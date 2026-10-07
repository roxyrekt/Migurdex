using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface ICanonicalResolver
{
    Task<IReadOnlyList<CanonicalAnime>> ResolveCanonicalAsync(
        string query,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ProviderMatch> MatchProviders(
        IReadOnlyList<SearchResult> providerResults,
        CanonicalAnime canonical);
}
