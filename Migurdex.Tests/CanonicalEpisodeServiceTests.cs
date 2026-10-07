using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class CanonicalEpisodeServiceTests
{
    private static MediaMetadata FrierenMeta()
    {
        return new MediaMetadata
        {
            ExternalId = "154587",
            Source = MetadataSource.AniList,
            Title = "Sousou no Frieren",
            EnglishTitle = "Frieren: Beyond Journey's End",
            RomajiTitle = "Sousou no Frieren",
            AniListId = "154587",
            MyAnimeListId = "52991",
            Year = 2023,
            Format = ContentFormat.Tv,
            Synonyms = ["Frieren at the Funeral"],
        };
    }

    [Fact]
    public async Task StreamSources_MergesResolvedWithAttribution()
    {
        var a = new FakeAnimeProvider("A", "a-frieren", "Sousou no Frieren",
            [("a1", "Bölüm 1", 1)]);
        a.VideoResults =
        [
            new VideoSource { Url = "https://embed.site/v/1", Type = VideoType.Embed, Hoster = "Vidmoly" },
            new VideoSource { Url = "https://cdn.site/v.mp4", Type = VideoType.Mp4, Quality = "1080p" },
        ];
        var service = Create(new FakeRegistry(a), FrierenMeta(), new FakeExtractors());

        var list = new List<VideoSource>();
        await foreach (var src in service.StreamEpisodeSourcesAsync(
                           "anilist:154587", 1, cancellationToken: TestContext.Current.CancellationToken))
        {
            list.Add(src);
        }

        Assert.Equal(2, list.Count);
        var resolved = Assert.Single(list, s => s.Url == "https://cdn.embed.site/v.mp4");
        Assert.Equal("A", resolved.ProviderName);
        Assert.Equal("Vidmoly", resolved.Hoster);
        var direct = Assert.Single(list, s => s.Url == "https://cdn.site/v.mp4");
        Assert.Equal("A", direct.ProviderName);
        Assert.Equal("1080p", direct.Quality);
    }

    [Fact]
    public async Task StreamSources_MissingEpisode_ThrowsNotFound()
    {
        var a = new FakeAnimeProvider("A", "a-frieren", "Sousou no Frieren", [("a1", "Bölüm 1", 1)]);
        var service = Create(new FakeRegistry(a), FrierenMeta(), new FakeExtractors());

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            await foreach (var _ in service.StreamEpisodeSourcesAsync(
                               "anilist:154587", 99, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task StreamSources_WithoutExtractors_Throws()
    {
        var service = Create(new FakeRegistry(), FrierenMeta());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.StreamEpisodeSourcesAsync(
                               "anilist:154587", 1, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public void MergeSource_PrefersExtractedKeepsRawMeta()
    {
        var merged = CanonicalEpisodeService.MergeSource(
            new VideoSource { Url = "https://x/v.mp4", Quality = "720p", Type = VideoType.Mp4 },
            new VideoSource { Url = "https://embed", Hoster = "Voe", Group = "G" },
            "P");

        Assert.Equal("https://x/v.mp4", merged.Url);
        Assert.Equal("720p", merged.Quality);
        Assert.Equal("Voe", merged.Hoster);
        Assert.Equal("G", merged.Group);
        Assert.Equal("P", merged.ProviderName);
    }

    private sealed class FakeExtractors : IExtractorManager
    {
        public IReadOnlyList<IExtractor> Extractors => [];
        public void RegisterExtractor(IExtractor extractor) { }
        public bool CanExtract(string url) => url.Contains("embed.site");

        public Task<List<VideoSource>> ExtractAsync(string url,
            IDictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<VideoSource>
            {
                new() { Url = "https://cdn.embed.site/v.mp4", Type = VideoType.Mp4 }
            });
        }
    }

    private static CanonicalEpisodeService Create(
        IAnimeProviderRegistry registry,
        MediaMetadata? meta,
        IExtractorManager? extractors = null)
    {
        var tracker = new FakeTracker(meta);
        var canonical = new CanonicalResolver([], NullLogger<CanonicalResolver>.Instance);
        return new CanonicalEpisodeService(
            registry,
            tracker,
            canonical,
            new FakeSeasons(),
            extractors,
            NullLogger<CanonicalEpisodeService>.Instance);
    }

    [Fact]
    public async Task GroupsEpisodes_WithSourcesFromTwoProviders()
    {
        var a = new FakeAnimeProvider("A", "a-frieren", "Sousou no Frieren",
            [("a1", "1. Bölüm", 1), ("a2", "2. Bölüm", 2)]);
        var b = new FakeAnimeProvider("B", "b-frieren", "Frieren: Beyond Journey's End",
            [("b1", "Bölüm 1", 1), ("b2", "Bölüm 2", 2)]);
        var service = Create(new FakeRegistry(a, b), FrierenMeta());

        var result = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result.ProvidersQueried);
        Assert.Equal(2, result.ProvidersMatched);
        Assert.Empty(result.ProviderErrors);
        Assert.Equal(2, result.Episodes.Count);
        Assert.All(result.Episodes, e => Assert.Equal(2, e.Sources.Count));
        Assert.Equal("anilist:154587", result.Anime.CanonicalId);
    }

    [Fact]
    public async Task GetEpisodes_SlowProvider_TimesOutFast()
    {
        var slow = new FakeAnimeProvider("Slow", "slow-id", "Sousou no Frieren",
            [("s1", "Bölüm 1", 1)])
        {
            SearchDelayMs = 5000
        };
        var fast = new FakeAnimeProvider("Fast", "fast-id", "Sousou no Frieren",
            [("f1", "Bölüm 1", 1)]);
        var service = new CanonicalEpisodeService(
            new FakeRegistry(slow, fast),
            new FakeTracker(FrierenMeta()),
            new CanonicalResolver([], NullLogger<CanonicalResolver>.Instance),
            new FakeSeasons(),
            null,
            NullLogger<CanonicalEpisodeService>.Instance,
            providerTimeout: TimeSpan.FromMilliseconds(150));

        var result = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result.ProvidersQueried);
        Assert.Equal(1, result.ProvidersMatched);
        Assert.Equal(["Fast"], result.MatchedProviders);
        Assert.True(result.ProviderErrors.ContainsKey("Slow"));
        Assert.Single(result.Episodes);
    }

    [Fact]
    public async Task GetEpisodes_SecondCall_ServedFromCache()
    {
        var a = new FakeAnimeProvider("A", "a-frieren", "Sousou no Frieren",
            [("a1", "1. Bölüm", 1), ("a2", "2. Bölüm", 2)]);
        var service = Create(new FakeRegistry(a), FrierenMeta());

        var first = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);
        var second = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Episodes.Count, second.Episodes.Count);
        Assert.Equal(1, a.SearchCalls);
        Assert.Equal(1, a.DetailsCalls);
    }

    [Fact]
    public async Task GroupsEpisodes_ExposesMatchedProviders()
    {
        var a = new FakeAnimeProvider("A", "a-frieren", "Sousou no Frieren",
            [("a1", "1. Bölüm", 1), ("a2", "2. Bölüm", 2)]);
        var b = new FakeAnimeProvider("B", "b-frieren", "Frieren: Beyond Journey's End",
            [("b1", "Bölüm 1", 1), ("b2", "Bölüm 2", 2)]);
        var service = Create(new FakeRegistry(a, b), FrierenMeta());

        var result = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["A", "B"], result.MatchedProviders);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task AlignmentWarnings_SurfacedWithProviderPrefix()
    {
        var chain = new SeasonChain
        {
            RootAniListId = "154587",
            Entries =
            [
                new SeasonChainEntry { SeasonNumber = 1, AniListId = "154587", TotalEpisodes = 28 }
            ]
        };
        var a = new FakeAnimeProvider("A", "a-frieren", "Sousou no Frieren",
            [("a1", "Bölüm 1", 1), ("a2", "Bölüm 2", 2)]);
        var service = new CanonicalEpisodeService(
            new FakeRegistry(a),
            new FakeTracker(FrierenMeta()),
            new CanonicalResolver([], NullLogger<CanonicalResolver>.Instance),
            new WarningSeasons(chain),
            NullLogger<CanonicalEpisodeService>.Instance);

        var result = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["A"], result.MatchedProviders);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("[A]", warning);
    }

    [Fact]
    public async Task SeasonZero_NormalizedToOne()
    {
        var a = new FakeAnimeProvider("A", "a-frieren", "Sousou no Frieren",
            [("a0", "Özel", 0)]);
        a.EpisodesSeason = 0;
        var service = Create(new FakeRegistry(a), FrierenMeta());

        var result = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var single = Assert.Single(result.Episodes);
        Assert.Equal(1, single.Season);
    }

    [Fact]
    public async Task UnmatchedProvider_RecordedAsError()
    {
        var junk = new FakeAnimeProvider("Junk", "junk-id", "Completely Different Show", [("j1", "Ep 1", 1)]);
        var service = Create(new FakeRegistry(junk), FrierenMeta());

        var result = await service.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result.ProvidersMatched);
        Assert.Empty(result.Episodes);
        Assert.True(result.ProviderErrors.ContainsKey("Junk"));
    }

    [Fact]
    public async Task UnknownTrackerId_ReturnsNull()
    {
        var service = Create(new FakeRegistry(), meta: null);

        var result = await service.GetEpisodesAsync("anilist:999999", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEpisodes_MultiSeasonChain_OnlyReturnsEpisodesForTargetSeason()
    {
        var s2Meta = new MediaMetadata
        {
            ExternalId = "182255",
            Source = MetadataSource.AniList,
            Title = "Sousou no Frieren 2nd Season",
            AniListId = "182255",
            Year = 2026,
            Format = ContentFormat.Tv,
        };
        var chain = new SeasonChain
        {
            RootAniListId = "154587",
            Entries =
            [
                new SeasonChainEntry { SeasonNumber = 1, AniListId = "154587", TotalEpisodes = 28 },
                new SeasonChainEntry { SeasonNumber = 2, AniListId = "182255", TotalEpisodes = 10 }
            ]
        };

        var provider = new FakeAnimeProvider("MultiSeasonProvider", "frieren", "Sousou no Frieren",
            [("e1", "S1E01", 1), ("e2", "S2E01", 1)]);
        provider.EpisodesDetails = new AnimeDetails
        {
            Title = "Sousou no Frieren",
            Episodes =
            [
                new Episode { Id = "s1e1", Title = "S1E01", Number = 1, Season = 1 },
                new Episode { Id = "s2e1", Title = "S2E01", Number = 1, Season = 2 }
            ]
        };

        var seasons = new MultiSeasonSeasons(chain);
        var serviceS1 = new CanonicalEpisodeService(
            new FakeRegistry(provider),
            new FakeTracker(FrierenMeta()),
            new CanonicalResolver([], NullLogger<CanonicalResolver>.Instance),
            seasons,
            NullLogger<CanonicalEpisodeService>.Instance);

        var resultS1 = await serviceS1.GetEpisodesAsync("anilist:154587", TestContext.Current.CancellationToken);
        Assert.NotNull(resultS1);
        var s1Ep = Assert.Single(resultS1.Episodes);
        Assert.Equal(1, s1Ep.Number);
        Assert.Equal("s1e1", s1Ep.Sources[0].ProviderEpisodeId);

        var serviceS2 = new CanonicalEpisodeService(
            new FakeRegistry(provider),
            new FakeTracker(s2Meta),
            new CanonicalResolver([], NullLogger<CanonicalResolver>.Instance),
            seasons,
            NullLogger<CanonicalEpisodeService>.Instance);

        var resultS2 = await serviceS2.GetEpisodesAsync("anilist:182255", TestContext.Current.CancellationToken);
        Assert.NotNull(resultS2);
        var s2Ep = Assert.Single(resultS2.Episodes);
        Assert.Equal(1, s2Ep.Number);
        Assert.Equal("s2e1", s2Ep.Sources[0].ProviderEpisodeId);
    }

    [Theory]
    [InlineData("anilist:154587", "154587", null)]
    [InlineData("mal:52991", null, "52991")]
    [InlineData("154587", "154587", null)]
    [InlineData("legacy:x", null, null, false)]
    [InlineData("", null, null, false)]
    public void TryParseId_ParsesSchemes(string input, string? ani, string? mal, bool ok = true)
    {
        var parsed = CanonicalEpisodeService.TryParseId(input, out var anilistId, out var malId);
        Assert.Equal(ok, parsed);
        Assert.Equal(ani, anilistId);
        Assert.Equal(mal, malId);
    }

    private sealed class FakeRegistry(params IAnimeProvider[] providers) : IAnimeProviderRegistry
    {
        public IReadOnlyList<IAnimeProvider> AnimeProviders { get; } = providers;
    }

    private sealed class FakeAnimeProvider(
        string name,
        string animeId,
        string title,
        (string Id, string Title, double Number)[] episodes) : IAnimeProvider
    {
        public string Name => name;
        public string BaseUrl => "https://example.com";
        public ProviderType Type => ProviderType.Anime;
        public int? EpisodesSeason { get; set; } = 1;
        public List<VideoSource> VideoResults { get; set; } = [];
        public AnimeDetails? EpisodesDetails { get; set; }
        public int SearchCalls { get; private set; }
        public int DetailsCalls { get; private set; }
        public int SearchDelayMs { get; set; }

        public Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return SearchInnerAsync(query, cancellationToken);
        }

        private async Task<List<SearchResult>> SearchInnerAsync(string query, CancellationToken cancellationToken)
        {
            if (SearchDelayMs > 0)
            {
                await Task.Delay(SearchDelayMs, cancellationToken);
            }

            return new List<SearchResult>
            {
                new() { Id = animeId, Title = title, ProviderName = name }
            };
        }

        public Task<AnimeDetails> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
        {
            DetailsCalls++;
            if (EpisodesDetails is not null)
            {
                return Task.FromResult(EpisodesDetails);
            }

            return Task.FromResult(new AnimeDetails
            {
                Title = title,
                Episodes = episodes
                    .Select(e => new Episode { Id = e.Id, Title = e.Title, Number = e.Number, Season = EpisodesSeason })
                    .ToList(),
            });
        }

        public Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
            string? group = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(VideoResults);
        }
    }

    private sealed class FakeTracker(MediaMetadata? meta) : ITrackerIdResolver
    {
        public Task<TrackerResolveResult> ResolveFromProviderAsync(
            string providerName,
            string providerId,
            IEnumerable<string> titles,
            int? year = null,
            ContentFormat? format = null,
            IReadOnlyList<SeasonMapping>? seasonMappings = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MediaMetadata?> ResolveFromTrackerAsync(
            string? anilistId,
            string? malId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(meta);
        }
    }

    private sealed class FakeSeasons : ISeasonChainService
    {
        public Task<SeasonChain?> GetSeasonChainAsync(string anilistId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SeasonChain?>(null);
        }

        public EntryAlignment AlignEntry(AnimeDetails details, SeasonChain chain)
        {
            throw new NotSupportedException();
        }

        public CanonicalEpisode? TranslateToCanonical(EntryAlignment alignment, int? providerSeason, double number)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class MultiSeasonSeasons(SeasonChain chain) : ISeasonChainService
    {
        public Task<SeasonChain?> GetSeasonChainAsync(string anilistId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SeasonChain?>(chain);
        }

        public EntryAlignment AlignEntry(AnimeDetails details, SeasonChain c)
        {
            return new EntryAlignment();
        }

        public CanonicalEpisode? TranslateToCanonical(EntryAlignment alignment, int? providerSeason, double number)
        {
            return new CanonicalEpisode { Season = providerSeason ?? 1, Number = number };
        }
    }

    private sealed class WarningSeasons(SeasonChain chain) : ISeasonChainService
    {
        public Task<SeasonChain?> GetSeasonChainAsync(string anilistId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SeasonChain?>(chain);
        }

        public EntryAlignment AlignEntry(AnimeDetails details, SeasonChain c)
        {
            return new EntryAlignment
            {
                Warnings = ["S1: provider 3 bölüm veriyor, kanonik 2 (recap/özel karışmış olabilir)."]
            };
        }

        public CanonicalEpisode? TranslateToCanonical(EntryAlignment alignment, int? providerSeason, double number)
        {
            return new CanonicalEpisode { Season = 1, Number = number };
        }
    }
}
