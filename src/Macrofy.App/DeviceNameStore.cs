using System.IO;
using System.Text.Json;

namespace Macrofy.App;

// Persists user-assigned keyboard names (device id -> custom name) to AppData.
public sealed class DeviceNameStore
{
    private readonly string _path;
    private Dictionary<string, string> _names;

    public DeviceNameStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Macrofy");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "devices.json");
        _names = Load();
    }

    public string? Get(string id) => _names.TryGetValue(id, out var name) ? name : null;

    public void Set(string id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            _names.Remove(id);
        else
            _names[id] = name.Trim();
        Save();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                    ?? new();
        }
        catch { /* corrupt file -> start fresh */ }
        return new();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_names,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
