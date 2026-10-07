using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface ICanonicalEpisodeService
{
    Task<CanonicalEpisodeResult?> GetEpisodesAsync(
        string canonicalId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<VideoSource> StreamEpisodeSourcesAsync(
        string canonicalId,
        double number,
        string? group = null,
        CancellationToken cancellationToken = default);
}
