using Microsoft.Data.Sqlite;
using Migurdex.Core.Services.Turnstile;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text.Json;

namespace Migurdex.Core.Database;

public class MigurdexDatabase
{
    static MigurdexDatabase()
    {
        try
        {
            System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, libraryName) =>
            {
                if (!libraryName.Equals("e_sqlite3", StringComparison.OrdinalIgnoreCase)
                    && !libraryName.Equals("libe_sqlite3", StringComparison.OrdinalIgnoreCase)
                    && !libraryName.Equals("e_sqlite3.so", StringComparison.OrdinalIgnoreCase)
                    && libraryName.IndexOf("e_sqlite3", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return IntPtr.Zero;
                }

                var baseDir = AppContext.BaseDirectory ?? string.Empty;
                var exeDir  = string.Empty;
                try { exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty; }
                catch { }

                var candidates = new[]
                {
                    Path.Combine(baseDir, "api", "libe_sqlite3.so"),
                    Path.Combine(baseDir, "api", "e_sqlite3.so"),
                    Path.Combine(baseDir, "libe_sqlite3.so"),
                    Path.Combine(baseDir, "e_sqlite3.so"),
                    Path.Combine(exeDir, "api", "libe_sqlite3.so"),
                    Path.Combine(exeDir, "libe_sqlite3.so"),
                    Path.Combine(baseDir, "api", "e_sqlite3.dll"),
                    Path.Combine(baseDir, "e_sqlite3.dll"),
                    Path.Combine(baseDir, "api", "libe_sqlite3.dylib"),
                    Path.Combine(baseDir, "libe_sqlite3.dylib"),
                };

                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        continue;
                    }

                    if (File.Exists(candidate)
                        && System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        return handle;
                    }
                }

                return IntPtr.Zero;
            };
        }
        catch
        {
            // ignored
        }

        SQLitePCL.Batteries_V2.Init();
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
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
        cmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();

        return connection;
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using var connection = CreateConnection();
            using var cmd        = connection.CreateCommand();

            cmd.CommandText = "PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();

            EnsureDownloadJobsSchema(connection);

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

                                  CREATE TABLE IF NOT EXISTS cf_clearance (
                                      host                    TEXT PRIMARY KEY,
                                      value                   TEXT NOT NULL,
                                      expires_at_utc          TEXT NOT NULL,
                                      source                  TEXT NOT NULL DEFAULT '',
                                      updated_at_utc          TEXT NOT NULL
                                  );

                                  CREATE TABLE IF NOT EXISTS download_jobs (
                                      id                      TEXT PRIMARY KEY,
                                      provider_name           TEXT NOT NULL,
                                      anime_id                TEXT NOT NULL,
                                      anime_title             TEXT NOT NULL,
                                      episode_id              TEXT NOT NULL,
                                      episode_title           TEXT NOT NULL DEFAULT '',
                                      season                  INTEGER NOT NULL DEFAULT 1,
                                      episode_number          REAL NOT NULL DEFAULT 1,
                                      preferred_group         TEXT,
                                      preferred_format        TEXT NOT NULL DEFAULT 'Auto',
                                      source_json             TEXT,
                                      output_directory        TEXT NOT NULL,
                                      media_path              TEXT,
                                      subtitle_paths_json     TEXT,
                                      warnings_json           TEXT,
                                      status                  TEXT NOT NULL,
                                      bytes_downloaded        INTEGER NOT NULL DEFAULT 0,
                                      total_bytes             INTEGER,
                                      fragments_done          INTEGER,
                                      fragments_total         INTEGER,
                                      percent                 REAL,
                                      error_message           TEXT,
                                      overwrite               INTEGER NOT NULL DEFAULT 0,
                                      resume_enabled          INTEGER NOT NULL DEFAULT 1,
                                      download_subtitles      INTEGER NOT NULL DEFAULT 1,
                                      priority                INTEGER NOT NULL DEFAULT 0,
                                      created_at_utc          TEXT NOT NULL,
                                      updated_at_utc          TEXT NOT NULL
                                  );
                                  CREATE INDEX IF NOT EXISTS idx_download_jobs_status_priority ON download_jobs(status, priority DESC, created_at_utc ASC);
                                  CREATE INDEX IF NOT EXISTS idx_download_jobs_episode ON download_jobs(provider_name, anime_id, episode_id);
                              """;

            cmd.ExecuteNonQuery();
            RestrictDbFilePermissions();
        }
    }

    private static void EnsureDownloadJobsSchema(SqliteConnection connection)
    {
        try
        {
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info(download_jobs);";
            using var reader          = checkCmd.ExecuteReader();
            var       hasTable        = false;
            var       hasStatusColumn = false;
            while (reader.Read())
            {
                hasTable = true;
                var name = reader.GetString(1);
                if (string.Equals(name, "status", StringComparison.OrdinalIgnoreCase))
                {
                    hasStatusColumn = true;
                }
            }

            reader.Close();

            if (hasTable && !hasStatusColumn)
            {
                using var dropCmd = connection.CreateCommand();
                dropCmd.CommandText = "DROP TABLE IF EXISTS download_jobs;";
                dropCmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // ignored
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
                        List<WatchHistoryEntry>? entries;
                        using (var fs = File.OpenRead(historyPath))
                            entries = JsonSerializer.Deserialize<List<WatchHistoryEntry>>(fs, _jsonOpts);
                        if (entries is { Count: > 0 })
                        {
                            using var connection = CreateConnection();
                            using var tx         = connection.BeginTransaction();
                            using var cmd        = connection.CreateCommand();
                            cmd.Transaction = tx;
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
                                                      anime_title = excluded.anime_title,
                                                      episode_title = COALESCE(excluded.episode_title, watch_history.episode_title),
                                                      season = excluded.season,
                                                      episode_number = excluded.episode_number,
                                                      last_position_seconds = excluded.last_position_seconds,
                                                      total_duration_seconds = excluded.total_duration_seconds,
                                                      is_completed = excluded.is_completed,
                                                      poster_url = COALESCE(excluded.poster_url, watch_history.poster_url),
                                                      last_watched_at_utc = excluded.last_watched_at_utc;
                                              """;
                            foreach (var entry in entries)
                            {
                                if (entry is null)
                                    continue;
                                cmd.Parameters.Clear();
                                BindWatchHistory(cmd, entry);
                                cmd.ExecuteNonQuery();
                            }

                            tx.Commit();
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
                        List<FavoriteEntry>? favs;
                        using (var fs = File.OpenRead(favPath))
                            favs = JsonSerializer.Deserialize<List<FavoriteEntry>>(fs, _jsonOpts);
                        if (favs is { Count: > 0 })
                        {
                            using var connection = CreateConnection();
                            using var tx         = connection.BeginTransaction();
                            using var cmd        = connection.CreateCommand();
                            cmd.Transaction = tx;
                            cmd.CommandText = """
                                                  INSERT INTO favorites (provider_name, anime_id, anime_title, poster_url, added_at_utc)
                                                  VALUES ($provider, $animeId, $animeTitle, $posterUrl, $addedAt)
                                                  ON CONFLICT(provider_name, anime_id) DO UPDATE SET
                                                      anime_title = excluded.anime_title,
                                                      poster_url = COALESCE(excluded.poster_url, favorites.poster_url),
                                                      added_at_utc = excluded.added_at_utc;
                                              """;
                            foreach (var fav in favs)
                            {
                                if (fav is null)
                                    continue;
                                cmd.Parameters.Clear();
                                BindFavorite(cmd, fav);
                                cmd.ExecuteNonQuery();
                            }

                            tx.Commit();
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
                        List<string>? queries;
                        using (var fs = File.OpenRead(searchPath))
                            queries = JsonSerializer.Deserialize<List<string>>(fs, _jsonOpts);
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
                        Dictionary<string, OAuthToken>? tokens;
                        using (var fs = File.OpenRead(tokensPath))
                            tokens = JsonSerializer.Deserialize<Dictionary<string, OAuthToken>>(fs, _jsonOpts);
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
                        Dictionary<string, TrackerMappingEntry>? mappings;
                        using (var fs = File.OpenRead(mappingsPath))
                            mappings =
                                JsonSerializer.Deserialize<Dictionary<string, TrackerMappingEntry>>(fs, _jsonOpts);
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
                        List<SyncQueueItem>? queue;
                        using (var fs = File.OpenRead(queuePath))
                            queue = JsonSerializer.Deserialize<List<SyncQueueItem>>(fs, _jsonOpts);
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

        BindWatchHistory(cmd, entry);
        cmd.ExecuteNonQuery();
    }

    private static void BindWatchHistory(SqliteCommand cmd, WatchHistoryEntry entry)
    {
        cmd.Parameters.AddWithValue("$provider", entry.ProviderName?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$animeId", entry.AnimeId?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$episodeId", entry.EpisodeId?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$animeTitle", entry.AnimeTitle?.Trim() ?? string.Empty);
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

        BindFavorite(cmd, entry);
        cmd.ExecuteNonQuery();
    }

    private static void BindFavorite(SqliteCommand cmd, FavoriteEntry entry)
    {
        cmd.Parameters.AddWithValue("$provider", entry.ProviderName?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$animeId", entry.AnimeId?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$animeTitle", entry.AnimeTitle?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$posterUrl",
                                    string.IsNullOrWhiteSpace(entry.PosterUrl) ? DBNull.Value : entry.PosterUrl.Trim());
        cmd.Parameters.AddWithValue("$addedAt",
                                    entry.AddedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
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

#region Cf Clearance

    public bool TryGetCfClearance(string host, out string? value, out DateTime expiresAtUtc)
    {
        value        = null;
        expiresAtUtc = DateTime.MinValue;

        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "SELECT value, expires_at_utc FROM cf_clearance WHERE host = $host LIMIT 1";
        cmd.Parameters.AddWithValue("$host", normalized);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var expires = DateTime.Parse(reader.GetString(1),
                                     CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal);
        if (expires <= DateTime.UtcNow)
        {
            reader.Close();
            DeleteCfClearance(normalized);
            return false;
        }

        value        = reader.GetString(0);
        expiresAtUtc = expires;
        return !string.IsNullOrEmpty(value);
    }

    public void SetCfClearance(string host, string value, DateTime expiresAtUtc, string source = "")
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO cf_clearance (host, value, expires_at_utc, source, updated_at_utc)
                              VALUES ($host, $value, $expiresAt, $source, $updatedAt)
                              ON CONFLICT(host) DO UPDATE SET
                                  value          = excluded.value,
                                  expires_at_utc = excluded.expires_at_utc,
                                  source         = excluded.source,
                                  updated_at_utc = excluded.updated_at_utc;
                          """;

        cmd.Parameters.AddWithValue("$host", normalized);
        cmd.Parameters.AddWithValue("$value", value.Trim());
        cmd.Parameters.AddWithValue("$expiresAt",
                                    expiresAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$source", source ?? string.Empty);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        cmd.ExecuteNonQuery();
        RestrictDbFilePermissions();
    }

    public IReadOnlyList<(string Host, DateTime ExpiresAtUtc, string Source)> GetCfClearanceHosts()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "SELECT host, expires_at_utc, source FROM cf_clearance ORDER BY host ASC";

        var       list   = new List<(string Host, DateTime ExpiresAtUtc, string Source)>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            list.Add((reader.GetString(0),
                      DateTime.Parse(reader.GetString(1),
                                     CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal),
                      reader.GetString(2)));
        }

        return list;
    }

    public void DeleteCfClearance(string host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM cf_clearance WHERE host = $host";
        cmd.Parameters.AddWithValue("$host", normalized);
        cmd.ExecuteNonQuery();
    }

    public int ClearExpiredCfClearance()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM cf_clearance WHERE expires_at_utc <= $now";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return cmd.ExecuteNonQuery();
    }

    private static string NormalizeHost(string host)
    {
        return TurnstileDetector.NormalizeHost(host);
    }

#endregion

#region Download Jobs

    public void UpsertDownloadJob(DownloadJobEntry job)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              INSERT INTO download_jobs (
                                  id, provider_name, anime_id, anime_title, episode_id, episode_title,
                                  season, episode_number, preferred_group, preferred_format, source_json,
                                  output_directory, media_path, subtitle_paths_json, warnings_json,
                                  status, bytes_downloaded, total_bytes, fragments_done, fragments_total,
                                  percent, error_message, overwrite, resume_enabled, download_subtitles,
                                  priority, created_at_utc, updated_at_utc
                              ) VALUES (
                                  $id, $provider, $animeId, $animeTitle, $episodeId, $episodeTitle,
                                  $season, $episodeNumber, $preferredGroup, $preferredFormat, $sourceJson,
                                  $outputDir, $mediaPath, $subtitlePathsJson, $warningsJson,
                                  $status, $bytesDownloaded, $totalBytes, $fragmentsDone, $fragmentsTotal,
                                  $percent, $errorMessage, $overwrite, $resumeEnabled, $downloadSubtitles,
                                  $priority, $createdAt, $updatedAt
                              )
                              ON CONFLICT(id) DO UPDATE SET
                                  provider_name       = excluded.provider_name,
                                  anime_id            = excluded.anime_id,
                                  anime_title         = excluded.anime_title,
                                  episode_id          = excluded.episode_id,
                                  episode_title       = excluded.episode_title,
                                  season              = excluded.season,
                                  episode_number      = excluded.episode_number,
                                  preferred_group     = excluded.preferred_group,
                                  preferred_format    = excluded.preferred_format,
                                  source_json         = excluded.source_json,
                                  output_directory    = excluded.output_directory,
                                  media_path          = excluded.media_path,
                                  subtitle_paths_json = excluded.subtitle_paths_json,
                                  warnings_json       = excluded.warnings_json,
                                  status              = excluded.status,
                                  bytes_downloaded    = excluded.bytes_downloaded,
                                  total_bytes         = excluded.total_bytes,
                                  fragments_done      = excluded.fragments_done,
                                  fragments_total     = excluded.fragments_total,
                                  percent             = excluded.percent,
                                  error_message       = excluded.error_message,
                                  overwrite           = excluded.overwrite,
                                  resume_enabled      = excluded.resume_enabled,
                                  download_subtitles  = excluded.download_subtitles,
                                  priority            = excluded.priority,
                                  updated_at_utc      = excluded.updated_at_utc;
                          """;

        BindDownloadJob(cmd, job);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<DownloadJobEntry> GetDownloadJobs()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              SELECT
                                  id, provider_name, anime_id, anime_title, episode_id, episode_title,
                                  season, episode_number, preferred_group, preferred_format, source_json,
                                  output_directory, media_path, subtitle_paths_json, warnings_json,
                                  status, bytes_downloaded, total_bytes, fragments_done, fragments_total,
                                  percent, error_message, overwrite, resume_enabled, download_subtitles,
                                  priority, created_at_utc, updated_at_utc
                              FROM download_jobs
                              ORDER BY priority DESC, created_at_utc ASC
                          """;

        var       list   = new List<DownloadJobEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadDownloadJob(reader));
        }

        return list;
    }

    public DownloadJobEntry? GetDownloadJob(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              SELECT
                                  id, provider_name, anime_id, anime_title, episode_id, episode_title,
                                  season, episode_number, preferred_group, preferred_format, source_json,
                                  output_directory, media_path, subtitle_paths_json, warnings_json,
                                  status, bytes_downloaded, total_bytes, fragments_done, fragments_total,
                                  percent, error_message, overwrite, resume_enabled, download_subtitles,
                                  priority, created_at_utc, updated_at_utc
                              FROM download_jobs
                              WHERE id = $id
                              LIMIT 1
                          """;
        cmd.Parameters.AddWithValue("$id", id.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDownloadJob(reader) : null;
    }

    public DownloadJobEntry? FindActiveOrPendingDownloadJob(string providerName, string animeId, string episodeId)
    {
        if (string.IsNullOrWhiteSpace(providerName)
            || string.IsNullOrWhiteSpace(animeId)
            || string.IsNullOrWhiteSpace(episodeId))
        {
            return null;
        }

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              SELECT
                                  id, provider_name, anime_id, anime_title, episode_id, episode_title,
                                  season, episode_number, preferred_group, preferred_format, source_json,
                                  output_directory, media_path, subtitle_paths_json, warnings_json,
                                  status, bytes_downloaded, total_bytes, fragments_done, fragments_total,
                                  percent, error_message, overwrite, resume_enabled, download_subtitles,
                                  priority, created_at_utc, updated_at_utc
                              FROM download_jobs
                              WHERE provider_name = $provider
                                AND anime_id = $animeId
                                AND episode_id = $episodeId
                                AND status IN ('Queued', 'Resolving', 'Downloading', 'Subtitle', 'Finalizing', 'Paused')
                              ORDER BY created_at_utc DESC
                              LIMIT 1
                          """;
        cmd.Parameters.AddWithValue("$provider", providerName.Trim());
        cmd.Parameters.AddWithValue("$animeId", animeId.Trim());
        cmd.Parameters.AddWithValue("$episodeId", episodeId.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDownloadJob(reader) : null;
    }

    public bool DeleteDownloadJob(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM download_jobs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    public int ClearCompletedDownloadJobs()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM download_jobs WHERE status IN ('Completed', 'Cancelled')";
        return cmd.ExecuteNonQuery();
    }

    public int ClearFailedDownloadJobs()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM download_jobs WHERE status IN ('Failed', 'Cancelled')";
        return cmd.ExecuteNonQuery();
    }

    public int ClearAllDownloadJobs()
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM download_jobs;";
        return cmd.ExecuteNonQuery();
    }

    public int RecoverInterruptedDownloadJobs(DownloadJobStatus targetStatus = DownloadJobStatus.Queued)
    {
        using var connection = CreateConnection();
        using var cmd        = connection.CreateCommand();

        cmd.CommandText = """
                              UPDATE download_jobs
                              SET status = $targetStatus,
                                  updated_at_utc = $updatedAt
                              WHERE status IN ('Resolving', 'Downloading', 'Subtitle', 'Finalizing')
                          """;
        cmd.Parameters.AddWithValue("$targetStatus", targetStatus.ToString());
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return cmd.ExecuteNonQuery();
    }

    private static void BindDownloadJob(SqliteCommand cmd, DownloadJobEntry job)
    {
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$provider", job.ProviderName ?? string.Empty);
        cmd.Parameters.AddWithValue("$animeId", job.AnimeId ?? string.Empty);
        cmd.Parameters.AddWithValue("$animeTitle", job.AnimeTitle ?? string.Empty);
        cmd.Parameters.AddWithValue("$episodeId", job.EpisodeId ?? string.Empty);
        cmd.Parameters.AddWithValue("$episodeTitle", job.EpisodeTitle ?? string.Empty);
        cmd.Parameters.AddWithValue("$season", job.Season);
        cmd.Parameters.AddWithValue("$episodeNumber", job.EpisodeNumber);
        cmd.Parameters.AddWithValue("$preferredGroup",
                                    string.IsNullOrWhiteSpace(job.PreferredGroup)
                                        ? DBNull.Value
                                        : job.PreferredGroup.Trim());
        cmd.Parameters.AddWithValue("$preferredFormat",
                                    string.IsNullOrWhiteSpace(job.PreferredFormat) ? "Auto" : job.PreferredFormat);
        cmd.Parameters.AddWithValue("$sourceJson",
                                    job.Source is null
                                        ? DBNull.Value
                                        : JsonSerializer.Serialize(job.Source, _jsonOpts));
        cmd.Parameters.AddWithValue("$outputDir", job.OutputDirectory ?? string.Empty);
        cmd.Parameters.AddWithValue("$mediaPath",
                                    string.IsNullOrWhiteSpace(job.MediaPath) ? DBNull.Value : job.MediaPath);
        cmd.Parameters.AddWithValue("$subtitlePathsJson",
                                    job.SubtitlePaths is { Count: > 0 }
                                        ? JsonSerializer.Serialize(job.SubtitlePaths, _jsonOpts)
                                        : DBNull.Value);
        cmd.Parameters.AddWithValue("$warningsJson",
                                    job.Warnings is { Count: > 0 }
                                        ? JsonSerializer.Serialize(job.Warnings, _jsonOpts)
                                        : DBNull.Value);
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$bytesDownloaded", job.BytesDownloaded);
        cmd.Parameters.AddWithValue("$totalBytes", job.TotalBytes.HasValue ? job.TotalBytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$fragmentsDone",
                                    job.FragmentsDone.HasValue ? job.FragmentsDone.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$fragmentsTotal",
                                    job.FragmentsTotal.HasValue ? job.FragmentsTotal.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$percent", job.Percent.HasValue ? job.Percent.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$errorMessage",
                                    string.IsNullOrWhiteSpace(job.ErrorMessage) ? DBNull.Value : job.ErrorMessage);
        cmd.Parameters.AddWithValue("$overwrite", job.Overwrite ? 1 : 0);
        cmd.Parameters.AddWithValue("$resumeEnabled", job.ResumeEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$downloadSubtitles", job.DownloadSubtitles ? 1 : 0);
        cmd.Parameters.AddWithValue("$priority", job.Priority);
        cmd.Parameters.AddWithValue("$createdAt",
                                    job.CreatedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$updatedAt",
                                    job.UpdatedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
    }

    private static DownloadJobEntry ReadDownloadJob(SqliteDataReader reader)
    {
        var sourceJson        = reader.IsDBNull(10) ? null : reader.GetString(10);
        var subtitlePathsJson = reader.IsDBNull(13) ? null : reader.GetString(13);
        var warningsJson      = reader.IsDBNull(14) ? null : reader.GetString(14);
        var statusRaw         = reader.IsDBNull(15) ? "Queued" : reader.GetString(15);

        VideoSource? source = null;
        if (!string.IsNullOrWhiteSpace(sourceJson))
        {
            try
            {
                source = JsonSerializer.Deserialize<VideoSource>(sourceJson, _jsonOpts);
            }
            catch
            {
                // ignored
            }
        }

        List<string> subtitlePaths = [];
        if (!string.IsNullOrWhiteSpace(subtitlePathsJson))
        {
            try
            {
                subtitlePaths = JsonSerializer.Deserialize<List<string>>(subtitlePathsJson, _jsonOpts) ?? [];
            }
            catch
            {
                // ignored
            }
        }

        List<string> warnings = [];
        if (!string.IsNullOrWhiteSpace(warningsJson))
        {
            try
            {
                warnings = JsonSerializer.Deserialize<List<string>>(warningsJson, _jsonOpts) ?? [];
            }
            catch
            {
                // ignored
            }
        }

        if (!Enum.TryParse<DownloadJobStatus>(statusRaw, ignoreCase: true, out var status))
        {
            status = DownloadJobStatus.Queued;
        }

        return new DownloadJobEntry
        {
            Id                = reader.GetString(0),
            ProviderName      = reader.GetString(1),
            AnimeId           = reader.GetString(2),
            AnimeTitle        = reader.GetString(3),
            EpisodeId         = reader.GetString(4),
            EpisodeTitle      = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            Season            = reader.GetInt32(6),
            EpisodeNumber     = reader.GetDouble(7),
            PreferredGroup    = reader.IsDBNull(8) ? null : reader.GetString(8),
            PreferredFormat   = reader.IsDBNull(9) ? "Auto" : reader.GetString(9),
            Source            = source,
            OutputDirectory   = reader.GetString(11),
            MediaPath         = reader.IsDBNull(12) ? null : reader.GetString(12),
            SubtitlePaths     = subtitlePaths,
            Warnings          = warnings,
            Status            = status,
            BytesDownloaded   = reader.GetInt64(16),
            TotalBytes        = reader.IsDBNull(17) ? null : reader.GetInt64(17),
            FragmentsDone     = reader.IsDBNull(18) ? null : reader.GetInt32(18),
            FragmentsTotal    = reader.IsDBNull(19) ? null : reader.GetInt32(19),
            Percent           = reader.IsDBNull(20) ? null : reader.GetDouble(20),
            ErrorMessage      = reader.IsDBNull(21) ? null : reader.GetString(21),
            Overwrite         = reader.GetInt32(22) == 1,
            ResumeEnabled     = reader.GetInt32(23) == 1,
            DownloadSubtitles = reader.GetInt32(24) == 1,
            Priority          = reader.GetInt32(25),
            CreatedAtUtc = DateTime.Parse(reader.GetString(26),
                                          CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(27),
                                          CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal)
        };
    }

#endregion
}
