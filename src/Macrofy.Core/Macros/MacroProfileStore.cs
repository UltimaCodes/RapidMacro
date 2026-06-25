using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Macrofy.Core.Macros;

// Persists one macro profile per device to %AppData%/Macrofy/profiles/<deviceId>.json.
public sealed class MacroProfileStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public MacroProfileStore()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macrofy", "profiles");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string deviceId)
    {
        // Device ids are "VID:PID" or a raw path; keep the file name filesystem-safe.
        var safe = string.Concat(deviceId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_dir, safe + ".json");
    }

    public MacroProfile Load(string deviceId, string deviceName)
    {
        var path = PathFor(deviceId);
        if (File.Exists(path))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<MacroProfile>(File.ReadAllText(path), Options);
                if (profile is not null)
                {
                    profile.DeviceId = deviceId;
                    profile.Normalize(); // migrate legacy (pre-layers) profiles to a Base layer
                    return profile;
                }
            }
            catch { /* fall through to a fresh profile on a corrupt file */ }
        }
        var fresh = new MacroProfile { DeviceId = deviceId, DeviceName = deviceName };
        fresh.Normalize();
        return fresh;
    }

    public void Save(MacroProfile profile)
        => File.WriteAllText(PathFor(profile.DeviceId), JsonSerializer.Serialize(profile, Options));

    // Write a profile to an arbitrary path (export / share / back up).
    public void Export(MacroProfile profile, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(profile, Options));

    // Delete every saved profile (Reset all macros). Best effort.
    public void DeleteAll()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "*.json"))
                File.Delete(f);
        }
        catch { /* best effort */ }
    }

    // Read a profile from an arbitrary path. Returns null if the file isn't a valid profile.
    public MacroProfile? Import(string path)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<MacroProfile>(File.ReadAllText(path), Options);
            profile?.Normalize();
            return profile;
        }
        catch { return null; }
    }
}
