using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class CanonicalResolverTests
{
    private static MediaMetadata AniMeta(string id, string title, string? malId = null, int? year = null)
    {
        return new MediaMetadata
        {
            ExternalId = id,
            Source = MetadataSource.AniList,
            Title = title,
            AniListId = id,
            MyAnimeListId = malId,
            Year = year,
            Format = ContentFormat.Tv,
            Synonyms = [],
        };
    }

    private static MediaMetadata JikanMeta(string malId, string title)
    {
        return new MediaMetadata
        {
            ExternalId = malId,
            Source = MetadataSource.Jikan,
            Title = title,
            MyAnimeListId = malId,
            Format = ContentFormat.Tv,
            Synonyms = [],
        };
    }

    private static CanonicalResolver Create(IMetadataProvider[] providers)
    {
        return new CanonicalResolver(providers, NullLogger<CanonicalResolver>.Instance);
    }

    private sealed class FakeProvider : IMetadataProvider
    {
        private readonly string _name;
        private readonly List<MediaMetadata> _data;
        public int Calls { get; private set; }

        public FakeProvider(string name, List<MediaMetadata> data)
        {
            _name = name;
            _data = data;
        }

        public string Name => _name;

        public Task<List<MediaMetadata>> SearchMetadataAsync(string title,
            ContentFormat expectedFormat = ContentFormat.Unknown,
            int limit = 10,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_data);
        }

        public Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_data.FirstOrDefault(m => m.ExternalId == id));
        }
    }

    [Fact]
    public async Task Resolve_OneAniList_OneJikan_ParallelSingleCallEach()
    {
        var ani = new FakeProvider("AniList", [AniMeta("21", "One Piece", "13", 1999)]);
        var jikan = new FakeProvider("Jikan", [JikanMeta("13", "One Piece")]);
        var resolver = Create([ani, jikan]);

        var result = await resolver.ResolveCanonicalAsync("One Piece", TestContext.Current.CancellationToken);

        var single = Assert.Single(result);
        Assert.Equal("anilist:21", single.CanonicalId);
        Assert.Equal("21", single.AniListId);
        Assert.Equal("13", single.MyAnimeListId);
        Assert.Equal(1, ani.Calls);
        Assert.Equal(1, jikan.Calls);
    }

    [Fact]
    public async Task Resolve_PrefersScraperOverJikan()
    {
        var ani = new FakeProvider("AniList", [AniMeta("21", "One Piece", "13", 1999)]);
        var malMeta = JikanMeta("13", "One Piece");
        malMeta.Source = MetadataSource.MyAnimeList;
        var mal = new FakeProvider("MyAnimeList", [malMeta]);
        var jikan = new FakeProvider("Jikan", [JikanMeta("13", "One Piece")]);
        var resolver = Create([ani, jikan, mal]);

        var result = await resolver.ResolveCanonicalAsync("One Piece", TestContext.Current.CancellationToken);

        var single = Assert.Single(result);
        Assert.Equal("anilist:21", single.CanonicalId);
        Assert.Equal(0, jikan.Calls);
        Assert.Equal(1, mal.Calls);
    }

    [Fact]
    public async Task Resolve_PrimaryTitle_BeatsSynonymOnlyMatch()
    {
        var onigiri = AniMeta("21612", "Onigiri");
        onigiri.Synonyms = ["Demon Slayer", "Demon Cutter"];
        onigiri.Score = 4.8;
        var kimetsu = AniMeta("101922", "Kimetsu no Yaiba", "38000", 2019);
        kimetsu.EnglishTitle = "Demon Slayer: Kimetsu no Yaiba";
        kimetsu.Score = 8.2;
        var ani = new FakeProvider("AniList", [onigiri, kimetsu]);
        var resolver = Create([ani]);

        var result = await resolver.ResolveCanonicalAsync("Demon Slayer", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("anilist:101922", result[0].CanonicalId);
    }

    [Fact]
    public async Task Resolve_SingleTokenQuery_PrefersPrimaryWordMatch()
    {
        var unmei = AniMeta("139074", "Unmei");
        unmei.Synonyms = ["Fate"];
        unmei.Score = 5.3;
        var fateZero = AniMeta("10087", "Fate/Zero", "10087", 2011);
        fateZero.Score = 8.1;
        var ani = new FakeProvider("AniList", [unmei, fateZero]);
        var resolver = Create([ani]);

        var result = await resolver.ResolveCanonicalAsync("fate", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("anilist:10087", result[0].CanonicalId);
    }

    [Fact]
    public async Task Resolve_TitleTie_PrefersEarlierYear()
    {
        var s1 = AniMeta("16498", "Shingeki no Kyojin", "16498", 2013);
        s1.Score = 8.0;
        var s3p2 = AniMeta("38524", "Shingeki no Kyojin", "38524", 2019);
        s3p2.Score = 9.05;
        var ani = new FakeProvider("AniList", [s1, s3p2]);
        var resolver = Create([ani]);

        var result = await resolver.ResolveCanonicalAsync(
            "Shingeki no Kyojin", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("anilist:16498", result[0].CanonicalId);
    }

    [Fact]
    public async Task Resolve_SeasonSuffixQuery_SearchesBaseTitleAndPrefersSeason()
    {
        var s1 = AniMeta("16498", "Shingeki no Kyojin", "16498", 2013);
        s1.EnglishTitle = "Attack on Titan";
        var s2 = AniMeta("20958", "Shingeki no Kyojin Season 2", "25777", 2017);
        s2.EnglishTitle = "Attack on Titan Season 2";
        var ani = new SeasonSuffixFakeProvider([s1, s2]);
        var resolver = Create([ani]);

        var result = await resolver.ResolveCanonicalAsync(
            "attack on titan 2. sezon", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("anilist:20958", result[0].CanonicalId);
    }

    [Fact]
    public async Task Resolve_RomanNumeralSeason_PreferredForDigitQuery()
    {
        var s1 = AniMeta("11741", "Sword Art Online", "11741", 2012);
        var s2 = AniMeta("20594", "Sword Art Online II", "21881", 2014);
        var ani = new FakeProvider("AniList", [s1, s2]);
        var resolver = Create([ani]);

        var result = await resolver.ResolveCanonicalAsync(
            "sword art online 2", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("anilist:20594", result[0].CanonicalId);
    }

    private sealed class SeasonSuffixFakeProvider(List<MediaMetadata> data) : IMetadataProvider
    {
        public string Name => "AniList";

        public Task<List<MediaMetadata>> SearchMetadataAsync(string title,
            ContentFormat expectedFormat = ContentFormat.Unknown,
            int limit = 10,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            if (title.Contains("sezon", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new List<MediaMetadata>());
            }

            return Task.FromResult(data);
        }

        public Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(data.FirstOrDefault(m => m.ExternalId == id));
        }
    }

    [Fact]
    public async Task Resolve_EmptyQuery_NoNetwork()
    {
        var ani = new FakeProvider("AniList", [AniMeta("1", "Naruto")]);
        var jikan = new FakeProvider("Jikan", [JikanMeta("20", "Naruto")]);
        var resolver = Create([ani, jikan]);

        var result = await resolver.ResolveCanonicalAsync("   ", TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(0, ani.Calls);
        Assert.Equal(0, jikan.Calls);
    }

    [Theory]
    [InlineData("Sousou no Frieren", "Frieren", true)]
    [InlineData("Sousou no Frieren", "Sousou no Frieren", true)]
    [InlineData("Jujutsu Kaisen 2nd Season", "Jujutsu Kaisen 2. Sezon", true)]
    [InlineData("Naruto Shippuuden", "Naruto Shippuden", true)]
    [InlineData("Cowboy Bebop", "Cowboy Bebop", true)]
    [InlineData("Demon Slayer", "Kimetsu no Yaiba", true)]
    [InlineData("Monster", "Monogatari Series Off Monster Season", false)]
    [InlineData("Monster", "Beheneko S-Ranked Monster", false)]
    [InlineData("Monster", "Re:Monster", false)]
    [InlineData("Sousou no Frieren", "Soukou Musume Senki", false)]
    [InlineData("Naruto", "Naruto x UT", false)]
    public void MatchProviders_Matrix(string canonicalTitle, string providerTitle, bool matched)
    {
        var ani = new FakeProvider("AniList", []);
        var jikan = new FakeProvider("Jikan", []);
        var resolver = Create([ani, jikan]);
        var canonical = CanonicalAnime.FromMetadata(new MediaMetadata
        {
            ExternalId = "1",
            Source = MetadataSource.AniList,
            Title = canonicalTitle,
            AniListId = "1",
            Format = ContentFormat.Tv,
            Synonyms = canonicalTitle == "Demon Slayer" ? ["Kimetsu no Yaiba"] : [],
        });

        var list = new List<SearchResult>
        {
            new() { Id = "x", Title = providerTitle, ProviderName = "P" },
        };

        var m = Assert.Single(resolver.MatchProviders(list, canonical));
        Assert.Equal(matched, m.Matched);
    }

    [Fact]
    public async Task MatchProviders_SubstringAbuse_Rejected()
    {
        var ani = new FakeProvider("AniList", []);
        var jikan = new FakeProvider("Jikan", []);
        var resolver = Create([ani, jikan]);
        var canonical = CanonicalAnime.FromMetadata(new MediaMetadata
        {
            ExternalId = "19",
            Source = MetadataSource.AniList,
            Title = "Monster",
            AniListId = "19",
            Format = ContentFormat.Tv,
            Synonyms = [],
        });

        var providers = new List<SearchResult>
        {
            new() { Id = "m1", Title = "Monogatari Series Off Monster Season", ProviderName = "TRAnimeci" },
            new() { Id = "b1", Title = "Beheneko S-Ranked Monster", ProviderName = "AsyaAnimeleri" },
            new() { Id = "real", Title = "Monster", ProviderName = "Anizm" },
        };

        var matches = resolver.MatchProviders(providers, canonical).ToArray();

        var real = matches.First(m => m.Result.Id == "real");
        Assert.True(real.Matched);
        Assert.DoesNotContain(matches, m => m.Result.Id != "real" && m.Matched);
    }

    [Fact]
    public async Task MatchProviders_LocalOnly_NoNetworkAfterSelect()
    {
        var ani = new FakeProvider("AniList", []);
        var jikan = new FakeProvider("Jikan", []);
        var resolver = Create([ani, jikan]);
        var canonical = CanonicalAnime.FromMetadata(AniMeta("154587", "Sousou no Frieren", year: 2023));

        var providers = new List<SearchResult>
        {
            new() { Id = "frieren", Title = "Frieren", ProviderName = "Deokwave" },
            new() { Id = "one-piece", Title = "One Piece", ProviderName = "Deokwave" },
        };

        var matches = resolver.MatchProviders(providers, canonical);

        Assert.Equal(2, matches.Count);
        Assert.True(matches[0].Matched);
        Assert.Contains("Frieren", matches[0].Result.Title, StringComparison.OrdinalIgnoreCase);
        Assert.False(matches[^1].Matched);
        Assert.Equal(0, ani.Calls);
        Assert.Equal(0, jikan.Calls);
    }

    [Fact]
    public async Task MatchProviders_SeasonMismatch_Penalized()
    {
        var resolver = Create([]);
        var canonical = CanonicalAnime.FromMetadata(AniMeta("145064", "Jujutsu Kaisen 2nd Season"));

        var providers = new List<SearchResult>
        {
            new() { Id = "s1", Title = "Jujutsu Kaisen", ProviderName = "A" },
            new() { Id = "s2", Title = "Jujutsu Kaisen 2. Sezon", ProviderName = "B" },
        };

        var matches = resolver.MatchProviders(providers, canonical);

        Assert.Equal("B", matches[0].Result.ProviderName);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact]
    public void MatchProviders_SeasonOne_PrefersSeasonOneOverSeasonTwo()
    {
        var resolver = Create([]);
        var canonical = CanonicalAnime.FromMetadata(AniMeta("154587", "Sousou no Frieren"));

        var providers = new List<SearchResult>
        {
            new() { Id = "s2", Title = "Sousou no Frieren 2nd Season", ProviderName = "TRAnimeci" },
            new() { Id = "s1", Title = "Sousou no Frieren", ProviderName = "TRAnimeci" },
        };

        var matches = resolver.MatchProviders(providers, canonical);

        Assert.Equal("s1", matches[0].Result.Id);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact]
    public async Task Resolve_Cancelled_Propagates()
    {
        var resolver = Create([new FakeProvider("AniList", [AniMeta("1", "Naruto")])]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveCanonicalAsync("Naruto", cts.Token));
    }
}
