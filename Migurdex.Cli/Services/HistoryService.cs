using Migurdex.Cli.Configuration;
using Migurdex.Core.Database;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public class HistoryService : IHistoryService
{
    private readonly IConfigurationService _configService;
    private readonly MigurdexDatabase      _db;

    public HistoryService(IConfigurationService configService, MigurdexDatabase db)
    {
        _configService = configService;
        _db            = db;
    }

    public HistoryService(IConfigurationService configService)
        : this(configService, new MigurdexDatabase(configService.ConfigDirectory))
    {
    }

    public IReadOnlyList<WatchHistoryEntry> GetWatchHistory()
    {
        return _db.GetWatchHistory();
    }

    public void SaveWatchProgress(WatchHistoryEntry entry)
    {
        if (_configService.Config.EnableIncognitoMode)
        {
            return;
        }

        entry.IsCompleted   = entry.IsCompleted || entry.ProgressPercentage >= 90.0;
        entry.LastWatchedAt = DateTime.UtcNow;

        _db.SaveWatchHistory(entry);
    }

    public void DeleteWatchHistory(string animeId, string providerName)
    {
        _db.DeleteWatchHistory(animeId, providerName);
    }

    public void ClearWatchHistory()
    {
        _db.ClearWatchHistory();
    }

    public IReadOnlyList<string> GetSearchHistory()
    {
        return _db.GetSearchHistory(15);
    }

    public void AddSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _configService.Config.EnableIncognitoMode)
        {
            return;
        }

        _db.AddSearchQuery(query);
    }

    public void DeleteSearchQuery(string query)
    {
        _db.DeleteSearchQuery(query);
    }

    public void ClearSearchHistory()
    {
        _db.ClearSearchHistory();
    }

    public IReadOnlyList<FavoriteEntry> GetFavorites()
    {
        return _db.GetFavorites();
    }

    public bool IsFavorite(string animeId, string providerName)
    {
        return _db.IsFavorite(animeId, providerName);
    }

    public void ToggleFavorite(FavoriteEntry favorite)
    {
        if (_db.IsFavorite(favorite.AnimeId, favorite.ProviderName))
        {
            _db.RemoveFavorite(favorite.AnimeId, favorite.ProviderName);
        }
        else
        {
            _db.AddFavorite(favorite);
        }
    }

    public void ClearFavorites()
    {
        _db.ClearFavorites();
    }
}
