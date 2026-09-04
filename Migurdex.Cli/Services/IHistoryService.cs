using Migurdex.Cli.Configuration;

namespace Migurdex.Cli.Services;

public interface IHistoryService
{
    IReadOnlyList<WatchHistoryEntry> GetWatchHistory();
    void                             SaveWatchProgress(WatchHistoryEntry entry);
    void                             DeleteWatchHistory(string           animeId, string providerName);
    void                             ClearWatchHistory();
    IReadOnlyList<string>            GetSearchHistory();
    void                             AddSearchQuery(string    query);
    void                             DeleteSearchQuery(string query);
    void                             ClearSearchHistory();
    IReadOnlyList<FavoriteEntry>     GetFavorites();
    bool                             IsFavorite(string            animeId, string providerName);
    void                             ToggleFavorite(FavoriteEntry favorite);
    void                             ClearFavorites();
}
