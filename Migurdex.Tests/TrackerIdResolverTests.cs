using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class TitleNormalizerTests
{
    [Theory]
    [InlineData("Naruto Shippuden izle", "naruto shippuden")]
    [InlineData("  One Piece  ", "one piece")]
    [InlineData("Attack on Titan: Final Season", "attack on titan final season")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_BasicCases(string? input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Fact]
    public void Ratio_Identical_IsOne()
    {
        Assert.Equal(1.0, TitleNormalizer.Ratio("Naruto", "naruto"));
    }

    [Fact]
    public void Ratio_Different_IsLow()
    {
        Assert.True(TitleNormalizer.Ratio("Naruto", "One Piece") < 0.5);
    }

    [Fact]
    public void Ratio_Typo_IsHigh()
    {
        Assert.True(TitleNormalizer.Ratio("Naruto Shippuden", "Naruto Shippuuden") > 0.8);
    }

    [Fact]
    public void SplitSeason_StripsSuffix()
    {
        var (baseTitle, season) = TitleNormalizer.SplitSeason("Naruto 2. Sezon izle");
        Assert.Equal(2, season);
        Assert.DoesNotContain("sezon", baseTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Jujutsu Kaisen 2nd Season", "jujutsu kaisen", 2)]
    [InlineData("Attack on Titan Final Season", "attack on titan final season", 1)]
    [InlineData("Fate Stay Night Part 2", "fate stay night", 2)]
    public void SplitSeason_Ordinals(string input, string expectedBase, int expectedSeason)
    {
        var (baseTitle, season) = TitleNormalizer.SplitSeason(input);
        Assert.Equal(expectedSeason, season);
        Assert.Equal(expectedBase, baseTitle);
    }

    [Fact]
    public void SplitSeason_Default_IsOne()
    {
        var (_, season) = TitleNormalizer.SplitSeason("One Piece");
        Assert.Equal(1, season);
    }
}

public sealed class TrackerIdResolverTests
{
    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        public           string              Name => "AniList";
        private readonly List<MediaMetadata> _data;

        public FakeMetadataProvider(List<MediaMetadata> data)
        {
            _data = data;
        }

        public Task<List<MediaMetadata>> SearchMetadataAsync(string title,
            ContentFormat                                           expectedFormat    = ContentFormat.Unknown,
            CancellationToken                                       cancellationToken = default)
        {
            return Task.FromResult(_data);
        }

        public Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_data.FirstOrDefault(m => m.ExternalId == id));
        }
    }

    private static TrackerIdResolver CreateResolver(List<MediaMetadata> data, string dir)
    {
        var store = new TrackerMappingStore(dir);
        return new TrackerIdResolver(
            [new FakeMetadataProvider(data)],
            store,
            NullLogger<TrackerIdResolver>.Instance);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "migurdex-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MediaMetadata Meta(string id, string title, string? malId = null, int? year = null)
    {
        return new MediaMetadata
        {
            ExternalId    = id,
            Source        = MetadataSource.AniList,
            Title         = title,
            AniListId     = id,
            MyAnimeListId = malId,
            Year          = year,
            Format        = ContentFormat.Tv,
            Synonyms      = []
        };
    }

    [Fact]
    public async Task Resolve_ExactTie_UniqueMaxEpisodes_Wins()
    {
        var series = Meta("269", "BLEACH");
        series.TotalEpisodes = 366;
        var pv = Meta("182561", "BLEACH 20th PV");
        pv.TotalEpisodes = 1;
        pv.Synonyms.Add("Bleach");
        var resolver = CreateResolver([pv, series], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("X", "bleach", ["Bleach"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.Equal("269", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_ExactTie_EqualEpisodes_StaysAmbiguous()
    {
        var a = Meta("1", "Same Title");
        a.TotalEpisodes = 12;
        var b = Meta("2", "Same Title");
        b.TotalEpisodes = 12;
        var resolver = CreateResolver([a, b], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("X", "same", ["Same Title"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Entry);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task Resolve_ExactMatch_ReturnsEntry()
    {
        var resolver = CreateResolver([Meta("21", "One Piece", "13", 1999)], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "one-piece", ["One Piece"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.False(result.Ambiguous);
        Assert.Equal("21", result.Entry.AniListId);
        Assert.Equal("13", result.Entry.MyAnimeListId);
    }

    [Fact]
    public async Task Resolve_BelowThreshold_IsAmbiguous()
    {
        var resolver = CreateResolver([Meta("1", "Completely Different Show")], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "naruto", ["Naruto"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Entry);
        Assert.True(result.Ambiguous);
    }

    [Fact]
    public async Task Resolve_CloseCandidates_IsAmbiguous()
    {
        var resolver = CreateResolver(
        [
            Meta("1", "Naruto"),
            Meta("2", "NARUTO")
        ], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "naruto", ["Naruto"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Ambiguous);
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public async Task Resolve_SecondCall_HitsCache()
    {
        var data     = new List<MediaMetadata> { Meta("21", "One Piece") };
        var dir      = NewTempDir();
        var resolver = CreateResolver(data, dir);

        var first = await resolver.ResolveFromProviderAsync("TurkAnime", "one-piece", ["One Piece"],
                                                            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(first.FromCache);

        data.Clear();
        var second = await resolver.ResolveFromProviderAsync("TurkAnime", "one-piece", ["One Piece"],
                                                             cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(second.FromCache);
        Assert.Equal("21", second.Entry!.AniListId);
    }

    [Fact]
    public async Task Resolve_SeasonMappingShortcut_SkipsSearch()
    {
        var data     = new List<MediaMetadata> { Meta("99", "Attack on Titan", "16498") };
        var resolver = CreateResolver(data, dir: NewTempDir());

        var mappings = new List<SeasonMapping>
        {
            new() { SeasonNumber = 1, AniListId = "99", MyAnimeListId = "16498" }
        };

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "xyz", ["xyz alakasız"],
                                                             seasonMappings: mappings,
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.Equal("99", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_YearBonus_PrefersCorrectYear()
    {
        var resolver = CreateResolver(
        [
            Meta("1", "Fate Stay Night", year: 2006),
            Meta("2", "Fate Stay Night", year: 2014)
        ], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "fate", ["Fate Stay Night"], year: 2014,
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.Equal("2", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_SeasonTwo_PrefersSecondSeason()
    {
        var s1 = Meta("16498", "Jujutsu Kaisen");
        var s2 = Meta("145064", "Jujutsu Kaisen 2nd Season");
        s2.EnglishTitle = "Jujutsu Kaisen Season 2";
        var resolver = CreateResolver([s1, s2], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "jjk-s2", ["Jujutsu Kaisen 2. Sezon"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.Equal("145064", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_ManySynonyms_DoesNotDiluteScore()
    {
        var s2 = Meta("145064", "Jujutsu Kaisen 2nd Season");
        s2.EnglishTitle = "Jujutsu Kaisen Season 2";
        s2.Synonyms.AddRange(
        [
            "Jujutsu Kaisen: Kaigyoku Gyokusetsu / Shibuya Jihen",
            "Jujutsu Kaisen: Hidden Inventory / Premature Death",
            "JJK2",
            "Jujutsu Kaisen: Shibuya Incident",
            "Jujutsu Kaisen: Shimetsu Kaiyuu"
        ]);
        var resolver = CreateResolver([s2], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "jjk-s2", ["Jujutsu Kaisen 2. Sezon"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.Equal("145064", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_ShortTitle_ContainedInCandidates_AsksUser()
    {
        var s1       = Meta("154587", "Sousou no Frieren");
        var s2       = Meta("209939", "Sousou no Frieren 3rd Season");
        var s3       = Meta("182255", "Sousou no Frieren 2nd Season");
        var resolver = CreateResolver([s1, s2, s3], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "frieren", ["Frieren"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Entry);
        Assert.True(result.Ambiguous);
        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c => Assert.True(c.Score >= 0.90));
    }

    [Fact]
    public async Task Resolve_ExactMatch_BeatsContainedCandidate()
    {
        var exact    = Meta("1", "Monster");
        var longer   = Meta("2", "Monster Musume no Iru Nichijou");
        var resolver = CreateResolver([exact, longer], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "monster", ["Monster"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.Equal("1", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_UnseasonedQuery_PrefersSeason1OverSeason2()
    {
        var s1 = Meta("101348", "Vinland Saga");
        var s2 = Meta("136430", "Vinland Saga Season 2");
        s1.TotalEpisodes = 24;
        s2.TotalEpisodes = 24;
        var resolver = CreateResolver([s1, s2], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "vinland", ["Vinland Saga"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.False(result.Ambiguous);
        Assert.Equal("101348", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_SingleTokenCandidate_NotContainedInMultiTokenQuery()
    {
        var hero = Meta("999", "Hero");
        var mha  = Meta("21459", "Boku no Hero Academia");
        mha.EnglishTitle = "My Hero Academia";
        var resolver = CreateResolver([hero, mha], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "mha", ["My Hero Academia"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.False(result.Ambiguous);
        Assert.Equal("21459", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_SeasonTwoQuery_DoesNotInflateSeasonOneViaContainment()
    {
        // Kısa sorgu adı S1'in tam adıyla birebir örtüşmüyor; S1 yalnızca
        // kapsama (containment) ile 0.90'a çıkabilirdi. Koruma kapalıyken
        // S1 haksız kazanırdı; açıkken sonuç en fazla belirsiz kalmalı.
        var s1 = Meta("101348", "Mob Psycho 100");
        var s2 = Meta("136430", "Mob Psycho 100 2nd Season");
        s1.TotalEpisodes = 25;
        s2.TotalEpisodes = 12;
        var resolver = CreateResolver([s1, s2], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "mob", ["Mob Psycho Season 2"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Ambiguous || result.Entry?.AniListId == "136430");
        if (result.Entry is not null)
        {
            Assert.NotEqual("101348", result.Entry.AniListId);
        }
    }

    [Fact]
    public async Task Resolve_ShortcutSkipsBadMapping_TriesNext()
    {
        var s1       = Meta("113415", "Jujutsu Kaisen");
        var resolver = CreateResolver([s1], NewTempDir());
        var mappings = new List<SeasonMapping>
        {
            new() { SeasonNumber = 1, AniListId = "no-such-id" },
            new() { SeasonNumber = 1, AniListId = "113415" }
        };

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "jjk", ["Jujutsu Kaisen"],
                                                             seasonMappings: mappings,
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Entry);
        Assert.False(result.Ambiguous);
        Assert.Equal("113415", result.Entry.AniListId);
    }

    [Fact]
    public async Task Resolve_JikanOnly_Unbridgeable_ReturnsAmbiguous()
    {
        var jikanOnly = new MediaMetadata
        {
            ExternalId    = "12345",
            Source        = MetadataSource.Jikan,
            Title         = "Some Obscure Anime",
            MyAnimeListId = "12345",
            Format        = ContentFormat.Tv,
            Synonyms      = []
        };
        var resolver = CreateResolver([jikanOnly], NewTempDir());

        var result = await resolver.ResolveFromProviderAsync("TurkAnime", "obscure", ["Some Obscure Anime"],
                                                             cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Entry);
        Assert.True(result.Ambiguous);
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public async Task Resolve_CancelledToken_PropagatesCancellation()
    {
        var store = new TrackerMappingStore(NewTempDir());
        var resolver = new TrackerIdResolver(
            [new CancellingMetadataProvider()],
            store,
            NullLogger<TrackerIdResolver>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveFromProviderAsync("TurkAnime", "x", ["Naruto"], cancellationToken: cts.Token));
    }

    private sealed class CancellingMetadataProvider : IMetadataProvider
    {
        public string Name => "AniList";

        public Task<List<MediaMetadata>> SearchMetadataAsync(string title,
            ContentFormat                                  expectedFormat    = ContentFormat.Unknown,
            CancellationToken                              cancellationToken = default)
        {
            return Task.FromException<List<MediaMetadata>>(new TaskCanceledException());
        }

        public Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromException<MediaMetadata?>(new TaskCanceledException());
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public           int                        Calls { get; private set; }

        public ScriptedHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            Calls++;
            return Task.FromResult(_responses.Count > 0
                                       ? _responses.Dequeue()
                                       : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class StubBridge : ISharedBridge
    {
        private readonly HttpClient _client;
        public StubBridge(HttpClient client) => _client = client;
        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();

        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory =>
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        public HttpClient CreateHttpClient(HttpClientOptions?        options = null) => _client;
        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)      => _client;

        public Microsoft.Extensions.Logging.ILogger<T> CreateLogger<T>() =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
    }

    private const string AniListPageJson = """
                                           {"data":{"Page":{"media":[{
                                             "id":21,"idMal":21,
                                             "title":{"romaji":"ONE PIECE","english":"ONE PIECE","native":"ONE PIECE"},
                                             "format":"TV","status":"RELEASING","seasonYear":1999,"averageScore":88,
                                             "episodes":null,"genres":[],"synonyms":[],
                                             "coverImage":{"extraLarge":"http://x"},"bannerImage":null
                                           }]}}}
                                           """;

    private static HttpResponseMessage TooManyRequests()
    {
        var r = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        return r;
    }

    [Fact]
    public async Task AniList_Relations_429_RetriesOnce()
    {
        const string relationsJson = """
                                     {"data":{"Media":{"relations":{"edges":[
                                       {"relationType":"SEQUEL","node":{"id":145064,"type":"ANIME","format":"TV"}}
                                     ]}}}}
                                     """;
        var handler = new ScriptedHandler(
        [
            TooManyRequests(),
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(relationsJson)
            }
        ]);
        var provider = new Migurdex.Core.Services.AniListProvider(
            new StubBridge(new HttpClient(handler)));

        var edges = await provider.QueryAnimeRelationsAsync("113415",
                                                            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        var edge = Assert.Single(edges);
        Assert.Equal("145064", edge.Id);
        Assert.Equal("SEQUEL", edge.RelationType);
    }

    [Fact]
    public async Task AniList_Relations_CancelledToken_PropagatesCancellation()
    {
        var handler = new ScriptedHandler([TooManyRequests()]);
        var provider = new Migurdex.Core.Services.AniListProvider(
            new StubBridge(new HttpClient(handler)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.QueryAnimeRelationsAsync("113415", cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AniList_LongCooldown_DoesNotRetry()
    {
        var throttled = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter =
            new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        var handler = new ScriptedHandler([throttled]);
        var provider = new Migurdex.Core.Services.AniListProvider(
            new StubBridge(new HttpClient(handler)));

        var list = await provider.SearchMetadataAsync("one piece",
                                                      cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Empty(list);
    }

    [Fact]
    public async Task AniList_NonNumericId_ReturnsNullWithoutRequest()
    {
        var handler = new ScriptedHandler([]);
        var provider = new Migurdex.Core.Services.AniListProvider(
            new StubBridge(new HttpClient(handler)));

        var meta = await provider.GetMetadataByIdAsync("not-a-number",
                                                       cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(meta);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AniList_429_RetriesOnce()
    {
        var handler = new ScriptedHandler(
        [
            TooManyRequests(),
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(AniListPageJson)
            }
        ]);
        var provider = new Migurdex.Core.Services.AniListProvider(
            new StubBridge(new HttpClient(handler)));

        var list = await provider.SearchMetadataAsync("one piece",
                                                      cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        var single = Assert.Single(list);
        Assert.Equal("21", single.ExternalId);
    }

    [Fact]
    public async Task AniList_Persistent429_ReturnsEmpty()
    {
        var handler = new ScriptedHandler([TooManyRequests(), TooManyRequests()]);
        var provider = new Migurdex.Core.Services.AniListProvider(
            new StubBridge(new HttpClient(handler)));

        var list = await provider.SearchMetadataAsync("one piece",
                                                      cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Calls);
        Assert.Empty(list);
    }
}
