using Microsoft.Data.Sqlite;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text.Json;

namespace Migurdex.Core.Database;

public class MigurdexDatabase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configDirectory;

    private readonly string _connectionString;
    private readonly Lock   _lock = new();

    public MigurdexDatabase(string? configDirectory = null)
    {
        _configDirectory = configDirectory
                           ?? Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".config",
                               "migurdex");

        Directory.CreateDirectory(_configDirectory);
        var dbPath = Path.Combine(_configDirectory, "migurdex.db");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Cache      = SqliteCacheMode.Shared
        };

        _connectionString = builder.ToString();

        InitializeDatabase();
        MigrateExistingJsonFiles();
    }

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();

        return connection;
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using var connection = CreateConnection();
            using var cmd        = connection.CreateCommand();

            cmd.CommandText = """
                                  CREATE TABLE IF NOT EXISTS watch_history (
                                      provider_name           TEXT NOT NULL,
                                      anime_id                TEXT NOT NULL,
                                      episode_id              TEXT NOT NULL,
                                      anime_title             TEXT NOT NULL,
                                      episode_title           TEXT,
                                      season                  INTEGER NOT NULL DEFAULT 1,
                                      episode_number          REAL NOT NULL DEFAULT 1,
                                      last_position_seconds   REAL NOT NULL DEFAULT 0,
                                      total_duration_seconds  REAL NOT NULL DEFAULT 0,
                                      is_completed            INTEGER NOT NULL DEFAULT 0,
                                      poster_url              TEXT,
                                      last_watched_at_utc     TEXT NOT NULL,
                                      PRIMARY KEY (provider_name, anime_id, episode_id)
                                  );
                                  CREATE INDEX IF NOT EXISTS idx_watch_history_last_watched ON watch_history(last_watched_at_utc DESC);
                                  CREATE INDEX IF NOT EXISTS idx_watch_history_anime ON watch_history(provider_name, anime_id);

                                  CREATE TABLE IF NOT EXISTS favorites (
                                      provider_name           TEXT NOT NULL,
                                      anime_id                TEXT NOT NULL,
                                      anime_title             TEXT NOT NULL,
                                      poster_url              TEXT,
                                      added_at_utc            TEXT NOT NULL,
                                      PRIMARY KEY (provider_name, anime_id)
                                  );
                                  CREATE INDEX IF NOT EXISTS idx_favorites_added_at ON favorites(added_at_utc DESC);

                                  CREATE TABLE IF NOT EXISTS search_history (
                                      query                   TEXT PRIMARY KEY,
                                      searched_at_utc         TEXT NOT NULL
                                  );
                                  CREATE INDEX IF NOT EXISTS idx_search_history_date ON search_history(searched_at_utc DESC);

                                  CREATE TABLE IF NOT EXISTS oauth_tokens (
                                      provider                TEXT PRIMARY KEY,
                                      access_token            TEXT NOT NULL,
                                      refresh_token           TEXT NOT NULL,
                                      expires_at_utc          TEXT NOT NULL,
                                      token_type              TEXT,
                                      scope                   TEXT,
                                      created_at_utc          TEXT NOT NULL
                                  );

                                  CREATE TABLE IF NOT EXISTS tracker_mappings (
                                      provider_name           TEXT NOT NULL,
                                      provider_id             TEXT NOT NULL,
                                      anilist_id              TEXT,
                                      mal_id                  TEXT,
                                      matched_title           TEXT NOT NULL DEFAULT '',
                                      score                   REAL NOT NULL DEFAULT 0,
                                      updated_at_utc          TEXT NOT NULL,
                                      PRIMARY KEY (provider_name, provider_id)
                                  );

                                  CREATE TABLE IF NOT EXISTS sync_queue (
                                      id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                                      provider_name           TEXT NOT NULL,
                                      anime_title             TEXT NOT NULL,
                                      anime_id                TEXT NOT NULL,
                                      season                  INTEGER NOT NULL DEFAULT 1,
                                      episode                 REAL NOT NULL DEFAULT 1,
                                      is_completed            INTEGER NOT NULL DEFAULT 0,
                                      attempts                INTEGER NOT NULL DEFAULT 0,
                                      next_attempt_at_utc     TEXT NOT NULL,
                                      enqueued_at_utc         TEXT NOT NULL,
                                      UNIQUE(provider_name, anime_id, season)
                                  );
                                  CREATE INDEX IF NOT EXISTS idx_sync_queue_key ON sync_queue(provider_name, anime_id, season);
                              """;

            cmd.ExecuteNonQuery();
            RestrictDbFilePermissions();
        }
    }

    private void RestrictDbFilePermissions()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var dbPath = Path.Combine(_configDirectory, "migurdex.db");
                if (File.Exists(dbPath))
                {
                    File.SetUnixFileMode(dbPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private void MigrateExistingJsonFiles()
    {
        lock (_lock)
        {
            try
            {
                // 1. migrate history.json
                var historyPath = Path.Combine(_configDirectory, "history.json");
                if (File.Exists(historyPath))
                {
                    try
                    {
                        var json    = File.ReadAllText(historyPath);
                        var entries = JsonSerializer.Deserialize<List<WatchHistoryEntry>>(json, JsonOpts);
                        if (entries is { Count: > 0 })
                        {
                            foreach (var entry in entries)
                            {
                                SaveWatchHistory(entry);
                            }
                        }

                        File.Move(historyPath, $"{historyPath}.migrated.bak", true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // 2. migrate favorites.json
                var favPath = Path.Combine(_configDirectory, "favorites.json");
                if (File.Exists(favPath))
                {
                    try
                    {
                        var json = File.ReadAllText(favPath);
                        var favs = JsonSerializer.Deserialize<List<FavoriteEntry>>(json, JsonOpts);
                        if (favs is { Count: > 0 })
                        {
                            foreach (var fav in favs)
                            {
                                AddFavorite(fav);
                            }
                        }

                        File.Move(favPath, $"{favPath}.migrated.bak", true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // 3. migrate search_history.json
                var searchPath = Path.Combine(_configDirectory, "search_history.json");
                if (File.Exists(searchPath))
                {
                    try
                    {
                        var json    = File.ReadAllText(searchPath);
                        var queries = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
                        if (queries is { Count: > 0 })
                        {
                            for (var i = queries.Count - 1; i >= 0; i--)
                            {
                                AddSearchQuery(queries[i]);
                            }
                        }

                        File.Move(searchPath, $"{searchPath}.migrated.bak", true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // 4. migrate tokens.json
                var tokensPath = Path.Combine(_configDirectory, "tokens.json");
                if (File.Exists(tokensPath))
                {
                    try
                    {
                        var json   = File.ReadAllText(tokensPath);
                        var tokens = JsonSerializer.Deserialize<Dictionary<string, OAuthToken>>(json, JsonOpts);
                        if (tokens is { Count: > 0 })
                        {
                            foreach (var kvp in tokens)
                            {
                                SetToken(kvp.Value);
                            }
                        }

                        File.Move(tokensPath, $"{tokensPath}.migrated.bak", true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // 5. migrate tracker_mappings.json
                var mappingsPath = Path.Combine(_configDirectory, "tracker_mappings.json");
                if (File.Exists(mappingsPath))
                {
                    try
                    {
                        var json = File.ReadAllText(mappingsPath);
                        var mappings =
                            JsonSerializer.Deserialize<Dictionary<string, TrackerMappingEntry>>(json, JsonOpts);
                        if (mappings is { Count: > 0 })
                        {
                            foreach (var kvp in mappings)
                            {
                                SetTrackerMapping(kvp.Value);
                            }
                        }

                        File.Move(mappingsPath, $"{mappingsPath}.migrated.bak", true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // 6. migrate sync_queue.json
                var queuePath = Path.Combine(_configDirectory, "sync_queue.json");
                if (File.Exists(queuePath))
                {
                    try
                    {
                        var json  = File.ReadAllText(queuePath);
                        var queue = JsonSerializer.Deserialize<List<SyncQueueItem>>(json, JsonOpts);
                        if (queue is { Count: > 0 })
                        {
                            foreach (var item in queue)
                            {
                                EnqueueSyncItem(item);
                            }
                        }

                        File.Move(queuePath, $"{queuePath}.migrated.bak", true);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }
        }
    }

#region Watch History

    public IReadOnlyList<WatchHistoryEntry> GetWatchHistory()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              SELECT provider_name, anime_id, episode_id, anime_title, episode_title,
                                     season, episode_number, last_position_seconds, total_duration_seconds,
                                     is_completed, poster_url, last_watched_at_utc
                              FROM watch_history
                              ORDER BY last_watched_at_utc DESC
                          """;

        var       list   = new List<WatchHistoryEntry>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add(ReadWatchHistory(reader));
        }

        return list;
    }

    public WatchHistoryEntry? GetWatchHistory(string providerName, string animeId, string episodeId)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              SELECT provider_name, anime_id, episode_id, anime_title, episode_title,
                                     season, episode_number, last_position_seconds, total_duration_seconds,
                                     is_completed, poster_url, last_watched_at_utc
                              FROM watch_history
                              WHERE provider_name = $provider AND anime_id = $animeId AND episode_id = $episodeId
                              LIMIT 1
                          """;

        cmd.Parameters.AddWithValue("$provider", providerName.Trim());
        cmd.Parameters.AddWithValue("$animeId", animeId.Trim());
        cmd.Parameters.AddWithValue("$episodeId", episodeId.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadWatchHistory(reader) : null;
    }

    public void SaveWatchHistory(WatchHistoryEntry entry)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO watch_history (
                                  provider_name, anime_id, episode_id, anime_title, episode_title,
                                  season, episode_number, last_position_seconds, total_duration_seconds,
                                  is_completed, poster_url, last_watched_at_utc
                              ) VALUES (
                                  $provider, $animeId, $episodeId, $animeTitle, $episodeTitle,
                                  $season, $episodeNumber, $lastPos, $totalDur,
                                  $completed, $posterUrl, $lastWatched
                              )
                              ON CONFLICT(provider_name, anime_id, episode_id) DO UPDATE SET
                                  anime_title            = excluded.anime_title,
                                  episode_title          = COALESCE(excluded.episode_title, watch_history.episode_title),
                                  season                 = excluded.season,
                                  episode_number         = excluded.episode_number,
                                  last_position_seconds  = excluded.last_position_seconds,
                                  total_duration_seconds = excluded.total_duration_seconds,
                                  is_completed           = excluded.is_completed,
                                  poster_url             = COALESCE(excluded.poster_url, watch_history.poster_url),
                                  last_watched_at_utc    = excluded.last_watched_at_utc;
                          """;

        cmd.Parameters.AddWithValue("$provider", entry.ProviderName.Trim());
        cmd.Parameters.AddWithValue("$animeId", entry.AnimeId.Trim());
        cmd.Parameters.AddWithValue("$episodeId", entry.EpisodeId.Trim());
        cmd.Parameters.AddWithValue("$animeTitle", entry.AnimeTitle.Trim());
        cmd.Parameters.AddWithValue("$episodeTitle",
                                    string.IsNullOrWhiteSpace(entry.EpisodeTitle)
                                        ? DBNull.Value
                                        : entry.EpisodeTitle.Trim());
        cmd.Parameters.AddWithValue("$season", entry.Season);
        cmd.Parameters.AddWithValue("$episodeNumber", entry.EpisodeNumber);
        cmd.Parameters.AddWithValue("$lastPos", entry.LastPositionSeconds);
        cmd.Parameters.AddWithValue("$totalDur", entry.TotalDurationSeconds);
        cmd.Parameters.AddWithValue("$completed", entry.IsCompleted || entry.ProgressPercentage >= 90.0 ? 1 : 0);
        cmd.Parameters.AddWithValue("$posterUrl",
                                    string.IsNullOrWhiteSpace(entry.PosterUrl) ? DBNull.Value : entry.PosterUrl.Trim());
        cmd.Parameters.AddWithValue("$lastWatched",
                                    entry.LastWatchedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

        cmd.ExecuteNonQuery();
    }

    public void DeleteWatchHistory(string animeId, string providerName)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM watch_history WHERE anime_id = $animeId AND provider_name = $provider";
        cmd.Parameters.AddWithValue("$animeId", animeId.Trim());
        cmd.Parameters.AddWithValue("$provider", providerName.Trim());
        cmd.ExecuteNonQuery();
    }

    public void ClearWatchHistory()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM watch_history;";
        cmd.ExecuteNonQuery();
    }

    private static WatchHistoryEntry ReadWatchHistory(SqliteDataReader reader)
    {
        return new WatchHistoryEntry
        {
            ProviderName         = reader.GetString(0),
            AnimeId              = reader.GetString(1),
            EpisodeId            = reader.GetString(2),
            AnimeTitle           = reader.GetString(3),
            EpisodeTitle         = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            Season               = reader.GetInt32(5),
            EpisodeNumber        = reader.GetDouble(6),
            LastPositionSeconds  = reader.GetDouble(7),
            TotalDurationSeconds = reader.GetDouble(8),
            IsCompleted          = reader.GetInt32(9) == 1,
            PosterUrl            = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            LastWatchedAt = DateTime.Parse(reader.GetString(11),
                                           CultureInfo.InvariantCulture,
                                           DateTimeStyles.AdjustToUniversal)
        };
    }

#endregion

#region Favorites

    public IReadOnlyList<FavoriteEntry> GetFavorites()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText =
            "SELECT provider_name, anime_id, anime_title, poster_url, added_at_utc FROM favorites ORDER BY added_at_utc DESC";

        var       list   = new List<FavoriteEntry>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add(new FavoriteEntry
            {
                ProviderName = reader.GetString(0),
                AnimeId      = reader.GetString(1),
                AnimeTitle   = reader.GetString(2),
                PosterUrl    = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                AddedAt = DateTime.Parse(reader.GetString(4),
                                         CultureInfo.InvariantCulture,
                                         DateTimeStyles.AdjustToUniversal)
            });
        }

        return list;
    }

    public bool IsFavorite(string animeId, string providerName)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "SELECT 1 FROM favorites WHERE anime_id = $animeId AND provider_name = $provider LIMIT 1";
        cmd.Parameters.AddWithValue("$animeId", animeId.Trim());
        cmd.Parameters.AddWithValue("$provider", providerName.Trim());

        return cmd.ExecuteScalar() != null;
    }

    public void AddFavorite(FavoriteEntry entry)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO favorites (provider_name, anime_id, anime_title, poster_url, added_at_utc)
                              VALUES ($provider, $animeId, $animeTitle, $posterUrl, $addedAt)
                              ON CONFLICT(provider_name, anime_id) DO UPDATE SET
                                  anime_title  = excluded.anime_title,
                                  poster_url   = COALESCE(excluded.poster_url, favorites.poster_url),
                                  added_at_utc = excluded.added_at_utc;
                          """;

        cmd.Parameters.AddWithValue("$provider", entry.ProviderName.Trim());
        cmd.Parameters.AddWithValue("$animeId", entry.AnimeId.Trim());
        cmd.Parameters.AddWithValue("$animeTitle", entry.AnimeTitle.Trim());
        cmd.Parameters.AddWithValue("$posterUrl",
                                    string.IsNullOrWhiteSpace(entry.PosterUrl) ? DBNull.Value : entry.PosterUrl.Trim());
        cmd.Parameters.AddWithValue("$addedAt",
                                    entry.AddedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

        cmd.ExecuteNonQuery();
    }

    public void RemoveFavorite(string animeId, string providerName)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM favorites WHERE anime_id = $animeId AND provider_name = $provider";
        cmd.Parameters.AddWithValue("$animeId", animeId.Trim());
        cmd.Parameters.AddWithValue("$provider", providerName.Trim());
        cmd.ExecuteNonQuery();
    }

    public void ClearFavorites()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM favorites;";
        cmd.ExecuteNonQuery();
    }

#endregion

#region Search History

    public IReadOnlyList<string> GetSearchHistory(int limit = 15)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "SELECT query FROM search_history ORDER BY searched_at_utc DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var       list   = new List<string>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public void AddSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO search_history (query, searched_at_utc)
                              VALUES ($query, $searchedAt)
                              ON CONFLICT(query) DO UPDATE SET
                                  searched_at_utc = excluded.searched_at_utc;
                          """;

        cmd.Parameters.AddWithValue("$query", query.Trim());
        cmd.Parameters.AddWithValue("$searchedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();

        using var pruneCmd = connection.CreateCommand();
        pruneCmd.CommandText = """
                                   DELETE FROM search_history
                                   WHERE query NOT IN (
                                       SELECT query FROM search_history ORDER BY searched_at_utc DESC LIMIT 50
                                   );
                               """;
        pruneCmd.ExecuteNonQuery();
    }

    public void DeleteSearchQuery(string query)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM search_history WHERE query = $query";
        cmd.Parameters.AddWithValue("$query", query.Trim());
        cmd.ExecuteNonQuery();
    }

    public void ClearSearchHistory()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM search_history;";
        cmd.ExecuteNonQuery();
    }

#endregion

#region OAuth Tokens

    public IReadOnlyList<string> GetTokenProviders()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "SELECT provider FROM oauth_tokens ORDER BY provider ASC";

        var       list   = new List<string>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public bool TryGetToken(string provider, out OAuthToken? token)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText =
            "SELECT provider, access_token, refresh_token, expires_at_utc FROM oauth_tokens WHERE provider = $provider LIMIT 1";
        cmd.Parameters.AddWithValue("$provider", provider.Trim().ToLowerInvariant());

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            token = new OAuthToken
            {
                Provider     = reader.GetString(0),
                AccessToken  = reader.GetString(1),
                RefreshToken = reader.GetString(2),
                ExpiresAtUtc = DateTime.Parse(reader.GetString(3),
                                              CultureInfo.InvariantCulture,
                                              DateTimeStyles.AdjustToUniversal)
            };
            return true;
        }

        token = null;
        return false;
    }

    public void SetToken(OAuthToken token)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO oauth_tokens (provider, access_token, refresh_token, expires_at_utc, created_at_utc)
                              VALUES ($provider, $accessToken, $refreshToken, $expiresAt, $createdAt)
                              ON CONFLICT(provider) DO UPDATE SET
                                  access_token   = excluded.access_token,
                                  refresh_token  = excluded.refresh_token,
                                  expires_at_utc = excluded.expires_at_utc;
                          """;

        cmd.Parameters.AddWithValue("$provider", token.Provider.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$accessToken", token.AccessToken);
        cmd.Parameters.AddWithValue("$refreshToken", token.RefreshToken);
        cmd.Parameters.AddWithValue("$expiresAt",
                                    token.ExpiresAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        cmd.ExecuteNonQuery();
        RestrictDbFilePermissions();
    }

    public bool RemoveToken(string provider)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM oauth_tokens WHERE provider = $provider";
        cmd.Parameters.AddWithValue("$provider", provider.Trim().ToLowerInvariant());

        return cmd.ExecuteNonQuery() > 0;
    }

#endregion

#region Tracker Mappings

    public bool TryGetTrackerMapping(string providerName, string providerId, out TrackerMappingEntry? entry)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              SELECT provider_name, provider_id, anilist_id, mal_id, matched_title, score, updated_at_utc
                              FROM tracker_mappings
                              WHERE provider_name = $provider AND provider_id = $providerId
                              LIMIT 1
                          """;

        cmd.Parameters.AddWithValue("$provider", providerName.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$providerId", providerId.Trim().ToLowerInvariant());

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            entry = new TrackerMappingEntry
            {
                ProviderName  = reader.GetString(0),
                ProviderId    = reader.GetString(1),
                AniListId     = reader.IsDBNull(2) ? null : reader.GetString(2),
                MyAnimeListId = reader.IsDBNull(3) ? null : reader.GetString(3),
                MatchedTitle  = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Score         = reader.GetDouble(5),
                UpdatedAt = DateTime.Parse(reader.GetString(6),
                                           CultureInfo.InvariantCulture,
                                           DateTimeStyles.AdjustToUniversal)
            };
            return true;
        }

        entry = null;
        return false;
    }

    public void SetTrackerMapping(TrackerMappingEntry entry)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO tracker_mappings (provider_name, provider_id, anilist_id, mal_id, matched_title, score, updated_at_utc)
                              VALUES ($provider, $providerId, $aniId, $malId, $matchedTitle, $score, $updatedAt)
                              ON CONFLICT(provider_name, provider_id) DO UPDATE SET
                                  anilist_id    = excluded.anilist_id,
                                  mal_id        = excluded.mal_id,
                                  matched_title = excluded.matched_title,
                                  score         = excluded.score,
                                  updated_at_utc = excluded.updated_at_utc;
                          """;

        cmd.Parameters.AddWithValue("$provider", entry.ProviderName.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$providerId", entry.ProviderId.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$aniId",
                                    string.IsNullOrWhiteSpace(entry.AniListId) ? DBNull.Value : entry.AniListId.Trim());
        cmd.Parameters.AddWithValue("$malId",
                                    string.IsNullOrWhiteSpace(entry.MyAnimeListId)
                                        ? DBNull.Value
                                        : entry.MyAnimeListId.Trim());
        cmd.Parameters.AddWithValue("$matchedTitle", entry.MatchedTitle ?? string.Empty);
        cmd.Parameters.AddWithValue("$score", entry.Score);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        cmd.ExecuteNonQuery();
    }

#endregion

#region Sync Queue

    public IReadOnlyList<SyncQueueItem> GetSyncQueue()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              SELECT id, provider_name, anime_title, anime_id, season, episode,
                                     is_completed, attempts, next_attempt_at_utc, enqueued_at_utc
                              FROM sync_queue
                              ORDER BY id ASC
                          """;

        var       list   = new List<SyncQueueItem>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add(new SyncQueueItem
            {
                Id          = reader.GetInt64(0),
                Provider    = reader.GetString(1),
                AnimeTitle  = reader.GetString(2),
                AnimeId     = reader.GetString(3),
                Season      = reader.GetInt32(4),
                Episode     = reader.GetDouble(5),
                IsCompleted = reader.GetInt32(6) == 1,
                Attempts    = reader.GetInt32(7),
                NextAttemptAtUtc = DateTime.Parse(reader.GetString(8),
                                                  CultureInfo.InvariantCulture,
                                                  DateTimeStyles.AdjustToUniversal),
                EnqueuedAtUtc = DateTime.Parse(reader.GetString(9),
                                               CultureInfo.InvariantCulture,
                                               DateTimeStyles.AdjustToUniversal)
            });
        }

        return list;
    }

    public int GetSyncQueueCount()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM sync_queue";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void EnqueueSyncItem(SyncQueueItem item)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO sync_queue (
                                  provider_name, anime_title, anime_id, season, episode,
                                  is_completed, attempts, next_attempt_at_utc, enqueued_at_utc
                              ) VALUES (
                                  $provider, $title, $animeId, $season, $episode,
                                  $completed, $attempts, $nextAttempt, $enqueued
                              )
                              ON CONFLICT(provider_name, anime_id, season) DO UPDATE SET
                                  anime_title         = excluded.anime_title,
                                  episode             = MAX(sync_queue.episode, excluded.episode),
                                  is_completed        = (sync_queue.is_completed OR excluded.is_completed),
                                  attempts            = 0,
                                  next_attempt_at_utc = excluded.next_attempt_at_utc,
                                  enqueued_at_utc     = excluded.enqueued_at_utc;
                          """;

        cmd.Parameters.AddWithValue("$provider", item.Provider.Trim());
        cmd.Parameters.AddWithValue("$title", item.AnimeTitle.Trim());
        cmd.Parameters.AddWithValue("$animeId", item.AnimeId.Trim());
        cmd.Parameters.AddWithValue("$season", item.Season);
        cmd.Parameters.AddWithValue("$episode", item.Episode);
        cmd.Parameters.AddWithValue("$completed", item.IsCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$attempts", item.Attempts);
        cmd.Parameters.AddWithValue("$nextAttempt",
                                    item.NextAttemptAtUtc.ToUniversalTime()
                                        .ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$enqueued",
                                    item.EnqueuedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

        cmd.ExecuteNonQuery();
    }

    public void RemoveSyncItem(string provider, string animeId, int season)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              DELETE FROM sync_queue
                              WHERE provider_name = $provider AND anime_id = $animeId AND season = $season
                          """;

        cmd.Parameters.AddWithValue("$provider", provider.Trim());
        cmd.Parameters.AddWithValue("$animeId", animeId.Trim());
        cmd.Parameters.AddWithValue("$season", season);

        cmd.ExecuteNonQuery();
    }

    public void RemoveSyncItemById(long id)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM sync_queue WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateSyncItem(SyncQueueItem item)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              UPDATE sync_queue
                              SET attempts = $attempts, next_attempt_at_utc = $nextAttempt, is_completed = $completed
                              WHERE id = $id
                          """;

        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$attempts", item.Attempts);
        cmd.Parameters.AddWithValue("$nextAttempt",
                                    item.NextAttemptAtUtc.ToUniversalTime()
                                        .ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$completed", item.IsCompleted ? 1 : 0);

        cmd.ExecuteNonQuery();
    }

    public void ClearSyncQueue()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM sync_queue;";
        cmd.ExecuteNonQuery();
    }

#endregion
}
