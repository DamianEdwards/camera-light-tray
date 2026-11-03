using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CameraLightTray.Core;

public sealed class Storage(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public T? Load<T>(string file, JsonTypeInfo<T> type)
    {
        string path = Path.Combine(DirectoryPath, file);
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type)
            ?? throw new InvalidDataException($"Invalid {file}.") : default;
    }
    public void Save<T>(string file, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, file);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, type));
        File.Move(temporary, path, true);
    }
}
