using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Macrofy.App;

// The chosen on-screen layout for a device: a preset, or "Custom" with the exact set of keys
// learned from the device.
public sealed class DeviceLayout
{
    public KeyboardLayoutKind Kind { get; set; } = KeyboardLayoutKind.Full;
    public List<int> Keys { get; set; } = new();   // virtual-key codes (only used for Custom)
}

// Persists per-device layouts to %AppData%/Macrofy/layouts.json (device id -> layout).
public sealed class DeviceLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<string, DeviceLayout> _map;

    public DeviceLayoutStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Macrofy");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "layouts.json");
        _map = Load();
    }

    public DeviceLayout Get(string id) => _map.TryGetValue(id, out var v) ? v : new DeviceLayout();

    public void Set(string id, DeviceLayout layout)
    {
        _map[id] = layout;
        Save();
    }

    private Dictionary<string, DeviceLayout> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, DeviceLayout>>(File.ReadAllText(_path), Options)
                    ?? new();
        }
        catch { /* corrupt file -> start fresh */ }
        return new();
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_map, Options)); }
        catch { /* best effort */ }
    }
}
