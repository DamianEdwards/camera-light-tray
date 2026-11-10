using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace CameraLightTray.App.Diagnostics;

internal sealed record FileLogOptions
{
    internal long MaxFileBytes { get; init; } = 5 * 1024 * 1024;
    internal int MaxFiles { get; init; } = 20;
    internal int RetentionDays { get; init; } = 14;
    internal int QueueCapacity { get; init; } = 1024;
}

internal sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const string Prefix = "camera-light-tray-";
    private readonly string _directory;
    private readonly FileLogOptions _options;
    private readonly TimeProvider _clock;
    private readonly Action<string?> _reportFailure;
    private readonly Channel<Entry> _queue;
    private readonly Task _writer;
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private FileStream? _file;
    private string? _path, _failure;
    private DateOnly _day;
    private long _dropped, _writeFailures;
    private int _disposed, _overflow;
    private sealed record Entry(DateTimeOffset Time, byte[] Bytes);
    private Entry CreateEntry(DateTimeOffset time, string text) => new(time, Encode(text, (int)Math.Min(_options.MaxFileBytes, 64 * 1024)));

    internal FileLoggerProvider(string directory, Action<string?> reportFailure,
        FileLogOptions? options = null, TimeProvider? clock = null)
    {
        _directory = Path.GetFullPath(directory);
        _options = options ?? new();
        if (_options.MaxFileBytes < 512 || _options.MaxFiles < 2 ||
            _options.RetentionDays < 1 || _options.QueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _clock = clock ?? TimeProvider.System;
        _reportFailure = reportFailure;
        Directory.CreateDirectory(_directory);
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        _writer = Task.Run(WriteAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _writer.GetAwaiter().GetResult();
    }

    private void Enqueue<TState>(string category, LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var time = _clock.GetUtcNow();
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture,
            $"{time:O} [{level}] {category} event={eventId.Id}:{eventId.Name} pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} ");
        _scopes.ForEachScope((scope, builder) =>
        {
            builder.Append("scope=").Append(scope).Append(' ');
            if (scope is IEnumerable<KeyValuePair<string, object?>> scopeProperties)
                foreach (var property in scopeProperties.Where(p => p.Key != "{OriginalFormat}"))
                    builder.Append(property.Key).Append('=').Append(Convert.ToString(property.Value, CultureInfo.InvariantCulture)).Append(' ');
        }, text);
        text.Append(formatter(state, exception));
        if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            foreach (var property in properties.Where(p => p.Key != "{OriginalFormat}"))
                text.Append(" | ").Append(property.Key).Append('=').Append(Convert.ToString(property.Value, CultureInfo.InvariantCulture));
        if (exception is not null) text.Append(" | exception=").Append(exception);
        string line = text.ToString().Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        if (_queue.Writer.TryWrite(CreateEntry(time, line))) return;
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _dropped);
        if (Interlocked.Exchange(ref _overflow, 1) == 0)
            _reportFailure("Diagnostic log queue is full; some entries were dropped. A loss count will be written when logging catches up.");
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    long dropped = Interlocked.Read(ref _dropped);
                    if (dropped > 0)
                    {
                        Write(CreateEntry(_clock.GetUtcNow(), $"{_clock.GetUtcNow():O} [Warning] FileLogger droppedEntries={dropped} queueCapacity={_options.QueueCapacity}"));
                        Interlocked.Add(ref _dropped, -dropped);
                        Interlocked.Exchange(ref _overflow, 0);
                    }
                    long failed = Interlocked.Read(ref _writeFailures);
                    if (failed > 0)
                    {
                        Write(CreateEntry(_clock.GetUtcNow(), $"{_clock.GetUtcNow():O} [Warning] FileLogger recoveredAfterWriteFailure lostEntries={failed} lastFailure={_failure?.Replace("\r", "\\r").Replace("\n", "\\n")}"));
                        Interlocked.Exchange(ref _writeFailures, 0);
                    }
                    Write(entry);
                    if (Interlocked.Exchange(ref _failure, null) is not null || dropped > 0) _reportFailure(null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref _writeFailures);
                    _file?.Dispose();
                    _file = null;
                    string error = "Diagnostic log write failed: " + ex.Message;
                    if (Interlocked.Exchange(ref _failure, error) != error) _reportFailure(error);
                }
            }
        }
        finally { _file?.Dispose(); }
    }

    private void Write(Entry entry)
    {
        byte[] bytes = entry.Bytes;
        var day = DateOnly.FromDateTime(entry.Time.UtcDateTime);
        if (_file is null || _day != day || _file.Length + bytes.Length > _options.MaxFileBytes)
        {
            _file?.Dispose();
            _file = null;
            Directory.CreateDirectory(_directory);
            string start = Prefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-";
            var last = Directory.EnumerateFiles(_directory, start + "*.log")
                .Where(path => TryName(path, out _, out _)).OrderDescending(StringComparer.Ordinal).FirstOrDefault();
            int sequence = 1;
            if (last is not null)
            {
                TryName(last, out _, out sequence);
                if (new FileInfo(last).Length + bytes.Length > _options.MaxFileBytes) sequence++;
            }
            if (sequence > 9999) throw new IOException("Daily diagnostic log sequence exhausted.");
            _path = Path.Combine(_directory, start + sequence.ToString("D4", CultureInfo.InvariantCulture) + ".log");
            _file = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            _day = day;
            Prune();
        }
        _file.Write(bytes);
        _file.Flush();
    }

    private void Prune()
    {
        var cutoff = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-_options.RetentionDays + 1);
        var files = Directory.EnumerateFiles(_directory, Prefix + "*.log")
            .Where(path => TryName(path, out _, out _)).OrderDescending(StringComparer.Ordinal).ToArray();
        var keep = files.Where(path => path != _path && TryName(path, out var day, out _) && day >= cutoff)
            .Take(_options.MaxFiles - 1).Append(_path!).ToHashSet(StringComparer.Ordinal);
        foreach (string path in files)
            if (!keep.Contains(path)) File.Delete(path);
    }

    private static bool TryName(string path, out DateOnly day, out int sequence)
    {
        string name = Path.GetFileName(path);
        day = default; sequence = 0;
        return name.Length == Prefix.Length + 19 && name.StartsWith(Prefix, StringComparison.Ordinal) &&
            name.EndsWith(".log", StringComparison.Ordinal) && name[Prefix.Length + 10] == '-' &&
            DateOnly.TryParseExact(name.AsSpan(Prefix.Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) &&
            int.TryParse(name.AsSpan(Prefix.Length + 11, 4), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence > 0;
    }

    private static byte[] Encode(string text, int limit)
    {
        byte[] suffix = Encoding.UTF8.GetBytes(" [truncated]\n");
        if (Encoding.UTF8.GetByteCount(text) + 1 <= limit) return Encoding.UTF8.GetBytes(text + "\n");
        byte[] bytes = new byte[limit];
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), bytes.AsSpan(0, limit - suffix.Length),
            true, out _, out int written, out _);
        suffix.CopyTo(bytes, written);
        return bytes[..(written + suffix.Length)];
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Enqueue(category, logLevel, eventId, state, exception, formatter);
        }
    }
}
