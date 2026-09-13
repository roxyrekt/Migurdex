using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Services;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

public sealed class WatchSyncServiceTests
{
    private const string SaveEntryJson = """
                                         {"data":{"SaveMediaListEntry":{"id":1,"progress":5,"status":"CURRENT"}}}
                                         """;

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static TrackerEpisodeMapping Mapping(double episode  = 5,
        int?                                            total    = 12,
        bool                                            overflow = false,
        string?                                         malId    = "40748")
    {
        return new TrackerEpisodeMapping
        {
            AniListId     = "113415",
            MyAnimeListId = malId,
            Season        = 1,
            Episode       = episode,
            TotalEpisodes = total,
            IsOverflow    = overflow
        };
    }

    private static SyncWatchEntry Entry(double episode = 5, bool completed = false)
    {
        return new SyncWatchEntry
        {
            Provider    = "AnimeciX",
            AnimeId     = "abc",
            Season      = 1,
            Episode     = episode,
            IsCompleted = completed
        };
    }

    private static (WatchSyncService Sync, Script Script, OAuthTokenStore Store) Build(string dir,
        Script                                                                                script,
        bool                                                                                  loggedIn = true)
    {
        var store = new OAuthTokenStore(dir);
        if (loggedIn)
        {
            store.Set(new OAuthToken
            {
                Provider     = "anilist",
                AccessToken  = "access-1",
                RefreshToken = "refresh-1",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
        }

        var list = new AniListListClient(new StubBridge(new HttpClient(new ScriptHandler(script))),
                                         new FakeFlow(),
                                         store);
        var sync = new WatchSyncService(list, store, script.Map, dir);
        return (sync, script, store);
    }

    private static HttpResponseMessage JsonOk()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SaveEntryJson, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task Sync_Pushes_Immediately_When_Logged_In()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping()
            }
        };
        script.PushResponses.Enqueue(JsonOk());
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        await sync.SyncAsync(Entry());

        Assert.Equal(1, script.MapCalls);
        Assert.Single(script.PushBodies);
        Assert.Contains("\"mediaId\":113415", script.PushBodies[0]);
        Assert.Contains("\"progress\":5", script.PushBodies[0]);
        Assert.Contains("\"status\":\"CURRENT\"", script.PushBodies[0]);
        Assert.Equal(0, sync.QueuedCount);
    }

    [Fact]
    public async Task Sync_Finale_Completed_Maps_To_Completed_Status()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping(12)
            }
        };
        script.PushResponses.Enqueue(JsonOk());
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        await sync.SyncAsync(Entry(12, true));

        var body = Assert.Single(script.PushBodies);
        Assert.Contains("\"progress\":12", body);
        Assert.Contains("\"status\":\"COMPLETED\"", body);
    }

    [Fact]
    public async Task Sync_MidSeason_Completed_Maps_To_Current_Status()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping()
            }
        };
        script.PushResponses.Enqueue(JsonOk());
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        await sync.SyncAsync(Entry(5, true));

        Assert.Contains("\"status\":\"CURRENT\"", Assert.Single(script.PushBodies));
    }

    [Fact]
    public async Task Sync_Unknown_Total_Completed_Maps_To_Current_Status()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping(5, null)
            }
        };
        script.PushResponses.Enqueue(JsonOk());
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        await sync.SyncAsync(Entry(5, true));

        Assert.Contains("\"status\":\"CURRENT\"", Assert.Single(script.PushBodies));
    }

    [Fact]
    public async Task Sync_Overflow_Completed_Maps_To_Current_Status()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping(15, 12, true)
            }
        };
        script.PushResponses.Enqueue(JsonOk());
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        await sync.SyncAsync(Entry(15, true));

        Assert.Contains("\"status\":\"CURRENT\"", Assert.Single(script.PushBodies));
    }

    [Fact]
    public async Task Sync_Ambiguous_Returns_Candidates_And_Enqueues()
    {
        var candidates = new List<TrackerCandidate>
        {
            new()
            {
                Metadata = new MediaMetadata
                {
                    AniListId = "21",
                    Title     = "ONE PIECE"
                },
                Score = 0.82
            }
        };
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Ambiguous  = true,
                Candidates = candidates
            }
        };
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        var outcome = await sync.SyncAsync(Entry());

        Assert.Equal(SyncOutcomeKind.Ambiguous, outcome.Kind);
        Assert.NotNull(outcome.Entry);
        Assert.Equal("abc", outcome.Entry.AnimeId);
        Assert.Single(outcome.Candidates);
        Assert.Empty(script.PushBodies);
        Assert.Equal(1, sync.QueuedCount);
    }

    [Fact]
    public async Task Sync_Plain_Failure_Returns_Queued_Without_Candidates()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult()
        };
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        var outcome = await sync.SyncAsync(Entry());

        Assert.Equal(SyncOutcomeKind.Queued, outcome.Kind);
        Assert.Empty(outcome.Candidates);
        Assert.Equal(1, sync.QueuedCount);
    }

    [Fact]
    public async Task Sync_Push_Returns_Pushed_Outcome()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping()
            }
        };
        script.PushResponses.Enqueue(JsonOk());
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        var outcome = await sync.SyncAsync(Entry());

        Assert.Equal(SyncOutcomeKind.Pushed, outcome.Kind);
    }

    [Fact]
    public async Task Sync_Without_Login_Enqueues_And_Flush_Pushes_After_Login()
    {
        var dir = NewTempDir("migurdex-synctest-");
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping()
            }
        };
        var (sync, _, store) = Build(dir, script, false);

        await sync.SyncAsync(Entry());

        Assert.Equal(0, script.MapCalls);
        Assert.Equal(1, sync.QueuedCount);

        store.Set(new OAuthToken
        {
            Provider     = "anilist",
            AccessToken  = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        script.PushResponses.Enqueue(JsonOk());

        Assert.Equal(1, await sync.FlushQueueAsync());
        Assert.Equal(0, sync.QueuedCount);
    }

    [Fact]
    public async Task Sync_Push_Failure_Enqueues_With_Backoff()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping()
            }
        };
        script.PushResponses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        await sync.SyncAsync(Entry());
        Assert.Equal(1, sync.QueuedCount);

        Assert.Equal(0, await sync.FlushQueueAsync());
        Assert.Equal(1, sync.QueuedCount);
    }

    [Fact]
    public async Task Sync_Dedupes_Same_Key_Keeping_Highest_Episode()
    {
        var dir    = NewTempDir("migurdex-synctest-");
        var script = new Script();
        var (sync, _, store) = Build(dir, script, false);

        await sync.SyncAsync(Entry(3));
        await sync.SyncAsync(Entry(7, true));

        Assert.Equal(1, sync.QueuedCount);

        store.Set(new OAuthToken
        {
            Provider     = "anilist",
            AccessToken  = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        script.OnMap = (_, _, _, _, _) => new EpisodeMappingResult
        {
            Mapping = Mapping(7)
        };
        script.PushResponses.Enqueue(JsonOk());

        Assert.Equal(1, await sync.FlushQueueAsync());
        Assert.Equal(0, sync.QueuedCount);

        var body = Assert.Single(script.PushBodies);
        Assert.Contains("\"progress\":7", body);
        Assert.Contains("\"status\":\"CURRENT\"", body);
    }

    [Fact]
    public async Task Sync_Unmappable_Enqueues_Without_Push()
    {
        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult()
        };
        var (sync, _, _) = Build(NewTempDir("migurdex-synctest-"), script);

        await sync.SyncAsync(Entry());

        Assert.Empty(script.PushBodies);
        Assert.Equal(1, sync.QueuedCount);
    }

    [Fact]
    public async Task Queue_Persists_Across_Instances()
    {
        var dir    = NewTempDir("migurdex-synctest-");
        var script = new Script();
        var (sync, _, _) = Build(dir, script, false);

        await sync.SyncAsync(Entry());

        var (reopened, _, _) = Build(dir, new Script(), false);
        Assert.Equal(1, reopened.QueuedCount);
    }

    [Fact]
    public async Task Flush_Drops_After_Max_Attempts()
    {
        var dir       = NewTempDir("migurdex-synctest-");
        var queueFile = Path.Combine(dir, "sync_queue.json");
        var items = new List<SyncQueueItem>
        {
            new()
            {
                Provider = "AnimeciX",
                AnimeId  = "abc",
                Season   = 1,
                Episode  = 5,
                Attempts = WatchSyncService.MaxAttempts - 1
            }
        };
        await File.WriteAllTextAsync(queueFile, JsonSerializer.Serialize(items));

        var script = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping()
            }
        };
        var (sync, _, _) = Build(dir, script);

        Assert.Equal(0, await sync.FlushQueueAsync());
        Assert.Equal(0, sync.QueuedCount);
    }

    private static (WatchSyncService Sync, Script AniListScript, Script MalScript, OAuthTokenStore Store) BuildWithMal(
        string dir,
        Script aniListScript,
        Script malScript,
        bool   aniListLoggedIn = true,
        bool   malLoggedIn     = true)
    {
        var store = new OAuthTokenStore(dir);
        if (aniListLoggedIn)
        {
            store.Set(new OAuthToken
            {
                Provider     = "anilist",
                AccessToken  = "access-1",
                RefreshToken = "refresh-1",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
        }

        if (malLoggedIn)
        {
            store.Set(new OAuthToken
            {
                Provider     = "mal",
                AccessToken  = "mal-access-1",
                RefreshToken = "mal-refresh-1",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
        }

        var aniList = new AniListListClient(new StubBridge(new HttpClient(new ScriptHandler(aniListScript))),
                                            new FakeFlow(),
                                            store);
        var mal = new MalListClient(new StubBridge(new HttpClient(new ScriptHandler(malScript))),
                                    new FakeFlow("mal"),
                                    store);
        var sync = new WatchSyncService(aniList, store, aniListScript.Map, dir, malClient: mal);
        return (sync, aniListScript, malScript, store);
    }

    [Fact]
    public async Task Sync_Pushes_To_Mal_When_Logged_In_To_Mal_Only()
    {
        var dir = NewTempDir("migurdex-synctest-");
        var aniListScript = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping()
            }
        };
        var malScript = new Script();
        malScript.PushResponses.Enqueue(JsonOk());

        var (sync, _, _, _) = BuildWithMal(dir, aniListScript, malScript, false);

        var outcome = await sync.SyncAsync(Entry());

        Assert.Equal(SyncOutcomeKind.Pushed, outcome.Kind);
        Assert.Empty(aniListScript.PushBodies);
        var body = Assert.Single(malScript.PushBodies);
        Assert.Contains("num_watched_episodes=5", body);
        Assert.Contains("status=watching", body);
        Assert.Equal(0, sync.QueuedCount);
    }

    [Fact]
    public async Task Sync_Pushes_To_Both_When_Logged_In_To_Both()
    {
        var dir = NewTempDir("migurdex-synctest-");
        var aniListScript = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping(12)
            }
        };
        aniListScript.PushResponses.Enqueue(JsonOk());

        var malScript = new Script();
        malScript.PushResponses.Enqueue(JsonOk());

        var (sync, _, _, _) = BuildWithMal(dir, aniListScript, malScript);

        var outcome = await sync.SyncAsync(Entry(12, true));

        Assert.Equal(SyncOutcomeKind.Pushed, outcome.Kind);

        var aniBody = Assert.Single(aniListScript.PushBodies);
        Assert.Contains("\"status\":\"COMPLETED\"", aniBody);

        var malBody = Assert.Single(malScript.PushBodies);
        Assert.Contains("status=completed", malBody);
        Assert.Contains("num_watched_episodes=12", malBody);
        Assert.Equal(0, sync.QueuedCount);
    }

    [Fact]
    public async Task Sync_Pushes_To_AniList_When_Logged_In_To_Both_And_MalId_Missing()
    {
        var dir = NewTempDir("migurdex-synctest-");
        var aniListScript = new Script
        {
            OnMap = (_, _, _, _, _) => new EpisodeMappingResult
            {
                Mapping = Mapping(malId: null)
            }
        };
        aniListScript.PushResponses.Enqueue(JsonOk());

        var malScript = new Script();

        var (sync, _, _, _) = BuildWithMal(dir, aniListScript, malScript);

        var outcome = await sync.SyncAsync(Entry());

        Assert.Equal(SyncOutcomeKind.Pushed, outcome.Kind);
        var aniBody = Assert.Single(aniListScript.PushBodies);
        Assert.Contains("\"progress\":5", aniBody);
        Assert.Empty(malScript.PushBodies);
        Assert.Equal(0, sync.QueuedCount);
    }

    private sealed class Script
    {
        public Queue<HttpResponseMessage>                                        PushResponses { get; } = new();
        public List<string>                                                      PushBodies    { get; } = [];
        public Func<string, string, int, double, string?, EpisodeMappingResult>? OnMap         { get; set; }
        public int                                                               MapCalls      { get; private set; }

        public Task<EpisodeMappingResult> Map(string provider,
            string                                   animeId,
            int                                      season,
            double                                   episode,
            string?                                  title,
            CancellationToken                        ct)
        {
            MapCalls++;
            return Task.FromResult(OnMap?.Invoke(provider, animeId, season, episode, title)
                                   ?? new EpisodeMappingResult());
        }
    }

    private sealed class ScriptHandler : HttpMessageHandler
    {
        private readonly Script _script;

        public ScriptHandler(Script script)
        {
            _script = script;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                           cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _script.PushBodies.Add(request.Content is not null
                                       ? await request.Content.ReadAsStringAsync(cancellationToken)
                                       : string.Empty);
            return _script.PushResponses.Count > 0
                       ? _script.PushResponses.Dequeue()
                       : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class FakeFlow : IOAuthFlow
    {
        public FakeFlow(string provider = "anilist")
        {
            Provider = provider;
        }

        public string Provider { get; }

        public string BuildAuthorizeUrl(string redirectUri)
        {
            return string.Empty;
        }

        public Task<OAuthToken?> ExchangeCodeAsync(string code,
            string                                        redirectUri,
            CancellationToken                             cancellationToken = default)
        {
            return Task.FromResult<OAuthToken?>(null);
        }

        public Task<OAuthToken?> RefreshAsync(string refreshToken,
            CancellationToken                        cancellationToken = default)
        {
            return Task.FromResult<OAuthToken?>(null);
        }
    }

    private sealed class StubBridge : ISharedBridge
    {
        private readonly HttpClient _client;

        public StubBridge(HttpClient client)
        {
            _client = client;
        }

        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();

        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public HttpClient CreateHttpClient(HttpClientOptions? options = null)
        {
            return _client;
        }

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
        {
            return _client;
        }

        public ILogger<T> CreateLogger<T>()
        {
            return NullLogger<T>.Instance;
        }
    }
}
