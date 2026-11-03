using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Xaml.Controls;

namespace CameraLightTray.App.Platform;

internal sealed class CameraPreview(Action<string> failure)
{
    private readonly SemaphoreSlim _gate = new(1);
    private MediaCapture? _capture;
    private MediaPlayer? _player;
    private MediaSource? _source;
    private MediaPlayerElement? _element;
    private int _generation;
    internal bool Playing => _player is not null;

    internal void Attach(MediaPlayerElement element)
    {
        _element = element;
        element.AreTransportControlsEnabled = false;
        if (_player is not null) element.SetMediaPlayer(_player);
    }

    internal async Task StartAsync(string deviceId)
    {
        int generation = ++_generation;
        await _gate.WaitAsync();
        try
        {
            StopCore();
            if (_element is null) throw new InvalidOperationException("The camera preview surface is not visible.");
            _capture = new MediaCapture();
            _capture.Failed += CaptureFailed;
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Auto
            });
            if (generation != _generation) { StopCore(); return; }
            var source = _capture.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 1)
                .FirstOrDefault(s => s.Info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)
                ?? throw new NotSupportedException("This camera has no shared color preview stream.");
            _source = MediaSource.CreateFromMediaFrameSource(source);
            _player = new MediaPlayer { RealTimePlayback = true, AutoPlay = false, IsMuted = true, Source = _source };
            _player.MediaFailed += PlayerFailed;
            _element.SetMediaPlayer(_player);
            _player.Play();
        }
        catch { StopCore(); throw; }
        finally { _gate.Release(); }
    }

    internal async Task StopAsync()
    {
        ++_generation;
        await _gate.WaitAsync();
        try { StopCore(); }
        finally { _gate.Release(); }
    }

    private void StopCore()
    {
        var player = _player; var source = _source; var capture = _capture;
        _player = null; _source = null; _capture = null;
        try
        {
            if (player is not null)
            {
                player.MediaFailed -= PlayerFailed;
                try { player.Pause(); }
                finally
                {
                    try { _element?.SetMediaPlayer(null); }
                    finally
                    {
                        try { player.Source = null; }
                        finally { player.Dispose(); }
                    }
                }
            }
        }
        finally
        {
            try { source?.Dispose(); }
            finally
            {
                if (capture is not null)
                {
                    capture.Failed -= CaptureFailed;
                    capture.Dispose();
                }
            }
        }
    }

    private void CaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
        failure($"Camera preview failed (0x{args.Code:X8}): {args.Message}");
    private void PlayerFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        failure("Camera preview failed: " + args.ErrorMessage);
}
