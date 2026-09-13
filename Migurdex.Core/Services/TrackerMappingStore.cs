using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Core.Services;

public sealed class TrackerMappingStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly string                                  _filePath;
    private readonly Lock                                    _lock    = new();
    private          Dictionary<string, TrackerMappingEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public TrackerMappingStore(string? configDirectory = null)
    {
        var dir = configDirectory
                  ?? Path.Combine(
                      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                      ".config",
                      "migurdex");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "tracker_mappings.json");
        Load();
    }

    public static string Key(string providerName, string providerId)
    {
        return $"{providerName.Trim().ToLowerInvariant()}:{providerId.Trim().ToLowerInvariant()}";
    }

    public bool TryGet(string providerName, string providerId, out TrackerMappingEntry? entry)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(Key(providerName, providerId), out entry);
        }
    }

    public void Set(TrackerMappingEntry entry)
    {
        lock (_lock)
        {
            entry.UpdatedAt                                     = DateTime.UtcNow;
            _entries[Key(entry.ProviderName, entry.ProviderId)] = entry;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json   = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, TrackerMappingEntry>>(json, JsonOpts);
            if (loaded is not null)
            {
                _entries = new Dictionary<string, TrackerMappingEntry>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            _entries = new Dictionary<string, TrackerMappingEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, JsonOpts));
            File.Move(tmp, _filePath, true);
        }
        catch
        {
            // ignored
        }
    }
}
