namespace Migurdex.Shared.Interfaces;

public interface IAnimeProviderRegistry
{
    IReadOnlyList<IAnimeProvider> AnimeProviders { get; }
}
