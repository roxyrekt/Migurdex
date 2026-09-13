using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface ITrackerIdResolver
{
    Task<TrackerResolveResult> ResolveFromProviderAsync(
        string                        providerName,
        string                        providerId,
        IEnumerable<string>           titles,
        int?                          year              = null,
        ContentFormat?                format            = null,
        IReadOnlyList<SeasonMapping>? seasonMappings    = null,
        CancellationToken             cancellationToken = default);

    Task<MediaMetadata?> ResolveFromTrackerAsync(
        string?           anilistId,
        string?           malId,
        CancellationToken cancellationToken = default);
}
