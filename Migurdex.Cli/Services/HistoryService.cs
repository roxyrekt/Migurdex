using Migurdex.Cli.Configuration;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public class HistoryService : IHistoryService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly IConfigurationService _configService;

    private readonly List<FavoriteEntry> _favorites;
    private readonly string              _favoritesFilePath;
    private readonly string              _historyFilePath;
    private readonly string              _searchFilePath;
    private readonly List<string>        _searchHistory;

    private readonly List<WatchHistoryEntry> _watchHistory;

    public HistoryService(IConfigurationService configService)
    {
        _configService = configService;
        var dir = configService.ConfigDirectory;
        _historyFilePath   = Path.Combine(dir, "history.json");
        _searchFilePath    = Path.Combine(dir, "search_history.json");
        _favoritesFilePath = Path.Combine(dir, "favorites.json");

        _watchHistory  = LoadList<WatchHistoryEntry>(_historyFilePath);
        _searchHistory = LoadList<string>(_searchFilePath);
        _favorites     = LoadList<FavoriteEntry>(_favoritesFilePath);
    }

    public IReadOnlyList<WatchHistoryEntry> GetWatchHistory()
    {
        return [.. _watchHistory.OrderByDescending(x => x.LastWatchedAt)];
    }

    public void SaveWatchProgress(WatchHistoryEntry entry)
    {
        if (_configService.Config.EnableIncognitoMode)
        {
            return;
        }

        var existing = _watchHistory.FirstOrDefault(x =>
                                                        x.AnimeId.Equals(
                                                            entry.AnimeId,
                                                            StringComparison.OrdinalIgnoreCase)
                                                        && x.EpisodeId.Equals(
                                                            entry.EpisodeId,
                                                            StringComparison.OrdinalIgnoreCase)
                                                        && x.ProviderName.Equals(
                                                            entry.ProviderName,
                                                            StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.LastPositionSeconds  = entry.LastPositionSeconds;
            existing.TotalDurationSeconds = entry.TotalDurationSeconds;
            existing.IsCompleted          = entry.IsCompleted || entry.ProgressPercentage >= 90.0;
            existing.LastWatchedAt        = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(entry.PosterUrl))
            {
                existing.PosterUrl = entry.PosterUrl;
            }

            if (!string.IsNullOrWhiteSpace(entry.AnimeTitle))
            {
                existing.AnimeTitle = entry.AnimeTitle;
            }

            if (!string.IsNullOrWhiteSpace(entry.EpisodeTitle))
            {
                existing.EpisodeTitle = entry.EpisodeTitle;
            }
        }
        else
        {
            entry.IsCompleted   = entry.ProgressPercentage >= 90.0;
            entry.LastWatchedAt = DateTime.UtcNow;
            _watchHistory.Add(entry);
        }

        SaveList(_historyFilePath, _watchHistory);
    }

    public void DeleteWatchHistory(string animeId, string providerName)
    {
        _watchHistory.RemoveAll(x =>
                                    x.AnimeId.Equals(animeId, StringComparison.OrdinalIgnoreCase)
                                    && x.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));

        SaveList(_historyFilePath, _watchHistory);
    }

    public void ClearWatchHistory()
    {
        _watchHistory.Clear();
        SaveList(_historyFilePath, _watchHistory);
    }

    public IReadOnlyList<string> GetSearchHistory()
    {
        return [.. _searchHistory.Take(15)];
    }

    public void AddSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _configService.Config.EnableIncognitoMode)
        {
            return;
        }

        _searchHistory.RemoveAll(x => x.Equals(query, StringComparison.OrdinalIgnoreCase));
        _searchHistory.Insert(0, query);

        if (_searchHistory.Count > 15)
        {
            _searchHistory.RemoveRange(15, _searchHistory.Count - 15);
        }

        SaveList(_searchFilePath, _searchHistory);
    }

    public void DeleteSearchQuery(string query)
    {
        _searchHistory.RemoveAll(x => x.Equals(query, StringComparison.OrdinalIgnoreCase));
        SaveList(_searchFilePath, _searchHistory);
    }

    public void ClearSearchHistory()
    {
        _searchHistory.Clear();
        SaveList(_searchFilePath, _searchHistory);
    }

    public IReadOnlyList<FavoriteEntry> GetFavorites()
    {
        return [.. _favorites.OrderByDescending(x => x.AddedAt)];
    }

    public bool IsFavorite(string animeId, string providerName)
    {
        return _favorites.Any(x =>
                                  x.AnimeId.Equals(animeId, StringComparison.OrdinalIgnoreCase)
                                  && x.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));
    }

    public void ToggleFavorite(FavoriteEntry favorite)
    {
        var existing = _favorites.FirstOrDefault(x =>
                                                     x.AnimeId.Equals(favorite.AnimeId,
                                                                      StringComparison.OrdinalIgnoreCase)
                                                     && x.ProviderName.Equals(
                                                         favorite.ProviderName,
                                                         StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            _favorites.Remove(existing);
        }
        else
        {
            _favorites.Add(favorite);
        }

        SaveList(_favoritesFilePath, _favorites);
    }

    public void ClearFavorites()
    {
        _favorites.Clear();
        SaveList(_favoritesFilePath, _favorites);
    }

    private static List<T> LoadList<T>(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<T>>(json, _jsonOpts) ?? [];
        }
        catch (Exception ex) when (ex is IOException
                                         or JsonException
                                         or NotSupportedException
                                         or UnauthorizedAccessException)
        {
            BackupCorruptFile(filePath);
            return [];
        }
    }

    private static void SaveList<T>(string filePath, List<T> list)
    {
        try
        {
            var tmpPath = filePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(list, _jsonOpts));
            File.Move(tmpPath, filePath, true);
        }
        catch
        {
            // ignored
        }
    }

    private static void BackupCorruptFile(string filePath)
    {
        try
        {
            File.Copy(filePath, $"{filePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak", false);
        }
        catch
        {
            // ignored
        }
    }
}
