using Migurdex.Core.Database;
using Migurdex.Shared.Models;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

public sealed class DatabaseMigrationAndConcurrencyTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "migurdex-dbtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WatchHistory_Crud_Roundtrip()
    {
        var dir = NewTempDir();
        var db  = new MigurdexDatabase(dir);

        var entry = new WatchHistoryEntry
        {
            ProviderName         = "AnimeciX",
            AnimeId              = "12345",
            EpisodeId            = "12345/1/1",
            AnimeTitle           = "Frieren: Beyond Journey's End",
            EpisodeTitle         = "The Journey's End",
            Season               = 1,
            EpisodeNumber        = 1,
            LastPositionSeconds  = 120.5,
            TotalDurationSeconds = 1440.0,
            IsCompleted          = false,
            PosterUrl            = "https://example.com/poster.jpg",
            LastWatchedAt        = DateTime.UtcNow
        };

        db.SaveWatchHistory(entry);

        var list = db.GetWatchHistory();
        Assert.Single(list);
        Assert.Equal("AnimeciX", list[0].ProviderName);
        Assert.Equal("12345", list[0].AnimeId);
        Assert.Equal("12345/1/1", list[0].EpisodeId);
        Assert.Equal("Frieren: Beyond Journey's End", list[0].AnimeTitle);
        Assert.Equal(120.5, list[0].LastPositionSeconds);

        var single = db.GetWatchHistory("AnimeciX", "12345", "12345/1/1");
        Assert.NotNull(single);
        Assert.Equal("The Journey's End", single!.EpisodeTitle);

        entry.LastPositionSeconds = 1400.0;
        entry.IsCompleted         = true;
        db.SaveWatchHistory(entry);

        var updated = db.GetWatchHistory("AnimeciX", "12345", "12345/1/1");
        Assert.NotNull(updated);
        Assert.Equal(1400.0, updated!.LastPositionSeconds);
        Assert.True(updated.IsCompleted);

        db.DeleteWatchHistory("12345", "AnimeciX");
        Assert.Empty(db.GetWatchHistory());
    }

    [Fact]
    public void Favorites_And_SearchHistory_Roundtrip()
    {
        var dir = NewTempDir();
        var db  = new MigurdexDatabase(dir);

        var fav = new FavoriteEntry
        {
            AnimeId      = "bleach-1",
            AnimeTitle   = "Bleach: Thousand-Year Blood War",
            ProviderName = "SonAnime",
            PosterUrl    = "https://example.com/bleach.jpg",
            AddedAt      = DateTime.UtcNow
        };

        Assert.False(db.IsFavorite("bleach-1", "SonAnime"));
        db.AddFavorite(fav);
        Assert.True(db.IsFavorite("bleach-1", "SonAnime"));
        Assert.Single(db.GetFavorites());

        db.RemoveFavorite("bleach-1", "SonAnime");
        Assert.False(db.IsFavorite("bleach-1", "SonAnime"));
        Assert.Empty(db.GetFavorites());

        db.AddSearchQuery("One Piece");
        db.AddSearchQuery("Naruto");
        db.AddSearchQuery("Bleach");

        var searches = db.GetSearchHistory(10);
        Assert.Equal(3, searches.Count);
        Assert.Equal("Bleach", searches[0]); // Most recent first

        db.DeleteSearchQuery("Naruto");
        searches = db.GetSearchHistory(10);
        Assert.Equal(2, searches.Count);
        Assert.DoesNotContain("Naruto", searches);
    }

    [Fact]
    public void TrackerMappings_And_SyncQueue_Roundtrip()
    {
        var dir = NewTempDir();
        var db  = new MigurdexDatabase(dir);

        var mapping = new TrackerMappingEntry
        {
            ProviderName  = "TRAnimeci",
            ProviderId    = "718-naruto",
            AniListId     = "20",
            MyAnimeListId = "20",
            MatchedTitle  = "Naruto",
            Score         = 0.99
        };

        db.SetTrackerMapping(mapping);
        Assert.True(db.TryGetTrackerMapping("TRAnimeci", "718-naruto", out var found));
        Assert.Equal("20", found!.AniListId);
        Assert.Equal("20", found.MyAnimeListId);
        Assert.Equal(0.99, found.Score);

        var queueItem = new SyncQueueItem
        {
            Provider         = "AnimeciX",
            AnimeTitle       = "Solo Leveling",
            AnimeId          = "10995",
            Season           = 1,
            Episode          = 12,
            IsCompleted      = true,
            Attempts         = 0,
            NextAttemptAtUtc = DateTime.UtcNow,
            EnqueuedAtUtc    = DateTime.UtcNow
        };

        db.EnqueueSyncItem(queueItem);
        Assert.Equal(1, db.GetSyncQueueCount());

        var queue = db.GetSyncQueue();
        Assert.Single(queue);
        Assert.Equal("Solo Leveling", queue[0].AnimeTitle);
        Assert.Equal(12, queue[0].Episode);

        db.RemoveSyncItem("AnimeciX", "10995", 1);
        Assert.Equal(0, db.GetSyncQueueCount());
    }

    [Fact]
    public void Automatic_Migration_From_Legacy_Json_Files()
    {
        var dir = NewTempDir();

        var history = new List<WatchHistoryEntry>
        {
            new()
            {
                ProviderName         = "Anizium",
                AnimeId              = "99",
                EpisodeId            = "99|1|1",
                AnimeTitle           = "Steins;Gate",
                EpisodeTitle         = "Prologue",
                Season               = 1,
                EpisodeNumber        = 1,
                LastPositionSeconds  = 600.0,
                TotalDurationSeconds = 1400.0,
                IsCompleted          = false,
                LastWatchedAt        = DateTime.UtcNow
            }
        };
        File.WriteAllText(Path.Combine(dir, "history.json"), JsonSerializer.Serialize(history));

        var favorites = new List<FavoriteEntry>
        {
            new()
            {
                AnimeId      = "99",
                AnimeTitle   = "Steins;Gate",
                ProviderName = "Anizium",
                AddedAt      = DateTime.UtcNow
            }
        };
        File.WriteAllText(Path.Combine(dir, "favorites.json"), JsonSerializer.Serialize(favorites));

        var searchHistory = new List<string>
        {
            "Steins;Gate",
            "Cowboy Bebop"
        };
        File.WriteAllText(Path.Combine(dir, "search_history.json"), JsonSerializer.Serialize(searchHistory));

        var tokens = new Dictionary<string, OAuthToken>
        {
            ["anilist"] = new()
            {
                Provider     = "anilist",
                AccessToken  = "token-abc",
                RefreshToken = "refresh-xyz",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(2)
            }
        };
        File.WriteAllText(Path.Combine(dir, "tokens.json"), JsonSerializer.Serialize(tokens));

        var mappings = new Dictionary<string, TrackerMappingEntry>
        {
            ["anizium:99"] = new()
            {
                ProviderName  = "Anizium",
                ProviderId    = "99",
                AniListId     = "9253",
                MyAnimeListId = "9253",
                MatchedTitle  = "Steins;Gate"
            }
        };
        File.WriteAllText(Path.Combine(dir, "tracker_mappings.json"), JsonSerializer.Serialize(mappings));

        var db = new MigurdexDatabase(dir);

        var migratedHistory = db.GetWatchHistory();
        Assert.Single(migratedHistory);
        Assert.Equal("Steins;Gate", migratedHistory[0].AnimeTitle);
        Assert.True(File.Exists(Path.Combine(dir, "history.json.migrated.bak")));

        Assert.True(db.IsFavorite("99", "Anizium"));
        Assert.True(File.Exists(Path.Combine(dir, "favorites.json.migrated.bak")));

        var migratedSearches = db.GetSearchHistory();
        Assert.Equal(2, migratedSearches.Count);
        Assert.True(File.Exists(Path.Combine(dir, "search_history.json.migrated.bak")));

        Assert.True(db.TryGetToken("anilist", out var token));
        Assert.Equal("token-abc", token!.AccessToken);
        Assert.True(File.Exists(Path.Combine(dir, "tokens.json.migrated.bak")));

        Assert.True(db.TryGetTrackerMapping("Anizium", "99", out var mapping));
        Assert.Equal("9253", mapping!.AniListId);
        Assert.True(File.Exists(Path.Combine(dir, "tracker_mappings.json.migrated.bak")));
    }

    [Fact]
    public async Task Concurrent_Reads_And_Writes_Do_Not_Lock()
    {
        var dir = NewTempDir();
        var db  = new MigurdexDatabase(dir);

        var tasks = Enumerable.Range(1, 20)
                              .Select(i => Task.Run(() =>
                              {
                                  var entry = new WatchHistoryEntry
                                  {
                                      ProviderName         = "AnimeciX",
                                      AnimeId              = $"anime-{i}",
                                      EpisodeId            = $"anime-{i}/1/1",
                                      AnimeTitle           = $"Anime {i}",
                                      Season               = 1,
                                      EpisodeNumber        = 1,
                                      LastPositionSeconds  = i * 10.0,
                                      TotalDurationSeconds = 1200.0,
                                      LastWatchedAt        = DateTime.UtcNow
                                  };

                                  db.SaveWatchHistory(entry);
                                  var list = db.GetWatchHistory();
                                  Assert.NotEmpty(list);
                              }));

        await Task.WhenAll(tasks);

        var finalHistory = db.GetWatchHistory();
        Assert.Equal(20, finalHistory.Count);
    }
}
