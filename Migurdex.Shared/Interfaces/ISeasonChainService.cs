using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface ISeasonChainService
{
    Task<SeasonChain?> GetSeasonChainAsync(string anilistId, CancellationToken cancellationToken = default);
    EntryAlignment     AlignEntry(AnimeDetails    details,   SeasonChain       chain);

    CanonicalEpisode? TranslateToCanonical(
        EntryAlignment alignment, int? providerSeason, double number);
}
