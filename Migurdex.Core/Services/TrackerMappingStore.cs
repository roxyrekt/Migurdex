using Migurdex.Core.Database;
using Migurdex.Shared.Models;

namespace Migurdex.Core.Services;

public sealed class TrackerMappingStore
{
    private readonly MigurdexDatabase _db;

    public TrackerMappingStore(MigurdexDatabase db)
    {
        _db = db;
    }

    public TrackerMappingStore(string? configDirectory = null) : this(new MigurdexDatabase(configDirectory))
    {
    }

    public static string Key(string providerName, string providerId)
    {
        return $"{providerName.Trim().ToLowerInvariant()}:{providerId.Trim().ToLowerInvariant()}";
    }

    public bool TryGet(string providerName, string providerId, out TrackerMappingEntry? entry)
    {
        return _db.TryGetTrackerMapping(providerName, providerId, out entry);
    }

    public void Set(TrackerMappingEntry entry)
    {
        _db.SetTrackerMapping(entry);
    }
}
