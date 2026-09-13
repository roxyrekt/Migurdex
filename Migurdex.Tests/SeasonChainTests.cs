using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class SeasonChainTests
{
    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        public           string                            Name => "AniList";
        private readonly Dictionary<string, MediaMetadata> _byId;

        public FakeMetadataProvider(IEnumerable<MediaMetadata> data)
        {
            _byId = data.ToDictionary(m => m.ExternalId);
        }

        public Task<List<MediaMetadata>> SearchMetadataAsync(string title,
            ContentFormat                                           expectedFormat    = ContentFormat.Unknown,
            CancellationToken                                       cancellationToken = default)
            => Task.FromResult(_byId.Values.ToList());

        public Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(_byId.GetValueOrDefault(id));
    }

    private static MediaMetadata Meta(string id, string title, string? malId, int? eps)
    {
        return new MediaMetadata
        {
            ExternalId    = id,
            Source        = MetadataSource.AniList,
            Title         = title,
            AniListId     = id,
            MyAnimeListId = malId,
            TotalEpisodes = eps,
            Format        = ContentFormat.Tv,
            Synonyms      = []
        };
    }

    private static SeasonChainService Create(
        Dictionary<string, MediaMetadata> data,
        Dictionary<string, string?>       sequels)
    {
        return Create(data, sequels, new Dictionary<string, string?>());
    }

    private static SeasonChainService Create(
        Dictionary<string, MediaMetadata> data,
        Dictionary<string, string?>       sequels,
        Dictionary<string, string?>       prequels)
    {
        var edges = new Dictionary<string, List<RelationEdge>>();
        foreach (var (from, to) in sequels)
        {
            if (to is null)
            {
                continue;
            }

            edges.TryAdd(from, []);
            edges[from].Add(new RelationEdge { RelationType = "SEQUEL", Id = to });
        }

        foreach (var (from, to) in prequels)
        {
            if (to is null)
            {
                continue;
            }

            edges.TryAdd(from, []);
            edges[from].Add(new RelationEdge { RelationType = "PREQUEL", Id = to });
        }

        return new SeasonChainService(
            [new FakeMetadataProvider(data.Values)],
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SeasonChainService>.Instance,
            (id, _) => Task.FromResult<IReadOnlyList<RelationEdge>>(edges.GetValueOrDefault(id, [])));
    }

    private static Dictionary<string, MediaMetadata> JjkChainData() => new()
    {
        ["113415"] = Meta("113415", "Jujutsu Kaisen", "40748", 24),
        ["145064"] = Meta("145064", "Jujutsu Kaisen 2nd Season", "51009", 23),
        ["60683"]  = Meta("60683", "Jujutsu Kaisen S3", "57658", 12)
    };

    private static Dictionary<string, string?> JjkSequels() => new()
    {
        ["113415"] = "145064",
        ["145064"] = "60683",
        ["60683"]  = null
    };

    private static AnimeDetails Details(string title,           int seasons, int perSeason,
        List<SeasonMapping>?                   mappings = null, int startSeason = 1)
    {
        var d = new AnimeDetails { Title = title, SeasonMappings = mappings ?? [] };
        for (var s = 0; s < seasons; s++)
        {
            for (var n = 1; n <= perSeason; n++)
            {
                d.Episodes.Add(new Episode
                {
                    Id     = $"e{s}-{n}",
                    Title  = $"Bölüm {n}",
                    Number = n,
                    Season = startSeason + s
                });
            }
        }

        return d;
    }

    [Fact]
    public async Task Chain_WalksSequels()
    {
        var svc = Create(JjkChainData(), JjkSequels());

        var chain = await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal(3, chain.Entries.Count);
        Assert.Equal(["113415", "145064", "60683"], chain.Entries.Select(e => e.AniListId));
        Assert.Equal([1, 2, 3], chain.Entries.Select(e => e.SeasonNumber));
        Assert.False(chain.Truncated);
    }

    [Fact]
    public async Task Chain_UnknownRoot_ReturnsNull()
    {
        var svc = Create(JjkChainData(), JjkSequels());
        Assert.Null(await svc.GetSeasonChainAsync("yok", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Chain_Cycle_Stops()
    {
        var svc = Create(JjkChainData(), new Dictionary<string, string?>
        {
            ["113415"] = "145064",
            ["145064"] = "113415"
        });

        var chain = await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal(2, chain.Entries.Count);
    }

    [Fact]
    public async Task Chain_FromSeasonTwo_WalksBackToRoot()
    {
        var svc = Create(JjkChainData(), JjkSequels(), new Dictionary<string, string?>
        {
            ["145064"] = "113415",
            ["60683"]  = "145064"
        });

        var chain = await svc.GetSeasonChainAsync("145064", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal("113415", chain.RootAniListId);
        Assert.Equal(3, chain.Entries.Count);
        Assert.Equal([1, 2, 3], chain.Entries.Select(e => e.SeasonNumber));
        Assert.Equal("145064", chain.Entries[1].AniListId);
    }

    [Fact]
    public async Task Chain_MovieBridge_SkippedInNumbering()
    {
        var data = JjkChainData();
        data["131573"] = new MediaMetadata
        {
            ExternalId    = "131573",
            Source        = MetadataSource.AniList,
            Title         = "Jujutsu Kaisen 0",
            AniListId     = "131573",
            MyAnimeListId = "40748",
            TotalEpisodes = 1,
            Format        = ContentFormat.Movie,
            Synonyms      = []
        };
        var svc = Create(data, new Dictionary<string, string?>
        {
            ["113415"] = "145064",
            ["145064"] = "60683",
            ["131573"] = "113415",
            ["60683"]  = null
        }, new Dictionary<string, string?>
        {
            ["113415"] = "131573",
            ["145064"] = "113415",
            ["60683"]  = "145064"
        });

        var chain = await svc.GetSeasonChainAsync("145064", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal("113415", chain.RootAniListId);
        Assert.Equal(["131573", "113415", "145064", "60683"], chain.Entries.Select(e => e.AniListId));
        Assert.Equal([0, 1, 2, 3], chain.Entries.Select(e => e.SeasonNumber));
    }

    [Fact]
    public async Task Align_IdBased_MultiSeason()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 3, 0);
        details.Episodes.Clear();
        foreach (var (s, count) in new[] { (1, 24), (2, 23), (3, 12) })
        {
            for (var n = 1; n <= count; n++)
            {
                details.Episodes.Add(new Episode { Id = $"e{s}-{n}", Number = n, Season = s });
            }
        }

        details.SeasonMappings =
        [
            new SeasonMapping { SeasonNumber = 1, MyAnimeListId = "40748" },
            new SeasonMapping { SeasonNumber = 2, MyAnimeListId = "51009" },
            new SeasonMapping { SeasonNumber = 3, MyAnimeListId = "57658" }
        ];

        var align = svc.AlignEntry(details, chain);

        Assert.Equal(EntryNumberingMode.PerSeason, align.NumberingMode);
        Assert.Equal(3, align.Seasons.Count);
        Assert.Equal([1, 2, 3], align.Seasons.Select(s => s.CanonicalSeasonNumber));
        Assert.Empty(align.Warnings);
    }

    [Fact]
    public async Task Align_CountMismatch_Warns()
    {
        var svc   = Create(JjkChainData(), JjkSequels());
        var chain = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 1, 25,
                              [new SeasonMapping { SeasonNumber = 1, MyAnimeListId = "40748" }]);

        var align = svc.AlignEntry(details, chain);

        Assert.Single(align.Seasons);
        Assert.NotEmpty(align.Warnings);
        var ep = svc.TranslateToCanonical(align, 1, 25);
        Assert.NotNull(ep);
        Assert.True(ep.IsOverflow);
    }

    [Fact]
    public async Task Align_TitleFallback_NoIds()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 1, 24);

        var align = svc.AlignEntry(details, chain);

        Assert.Single(align.Seasons);
        Assert.Equal(1, align.Seasons[0].CanonicalSeasonNumber);
        var ep = svc.TranslateToCanonical(align, null, 12);
        Assert.NotNull(ep);
        Assert.Equal((1, 12), (ep.Season, (int)ep.Number));
    }

    [Fact]
    public async Task Align_TitleFallback_SeasonTwo()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen 2nd Season", 1, 23);

        var align = svc.AlignEntry(details, chain);

        Assert.Single(align.Seasons);
        Assert.Equal(2, align.Seasons[0].CanonicalSeasonNumber);
    }

    [Fact]
    public async Task Align_Absolute_MergedEntry()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 1, 59);
        foreach (var e in details.Episodes)
        {
            e.Season = null;
        }

        var align = svc.AlignEntry(details, chain);

        Assert.Equal(EntryNumberingMode.Absolute, align.NumberingMode);
        Assert.Equal(3, align.Seasons.Count);

        var ep24 = svc.TranslateToCanonical(align, null, 24);
        Assert.NotNull(ep24);
        Assert.Equal((1, 24), (ep24.Season, (int)ep24.Number));

        var ep25 = svc.TranslateToCanonical(align, null, 25);
        Assert.NotNull(ep25);
        Assert.Equal((2, 1), (ep25.Season, (int)ep25.Number));

        var ep48 = svc.TranslateToCanonical(align, null, 48);
        Assert.NotNull(ep48);
        Assert.Equal((3, 1), (ep48.Season, (int)ep48.Number));
    }

    [Fact]
    public async Task Translate_PerSeason_MapsSeason()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 2, 0);
        details.Episodes.Clear();
        foreach (var (s, count) in new[] { (1, 24), (2, 23) })
        {
            for (var n = 1; n <= count; n++)
            {
                details.Episodes.Add(new Episode { Id = $"e{s}-{n}", Number = n, Season = s });
            }
        }

        details.SeasonMappings =
        [
            new SeasonMapping { SeasonNumber = 1, MyAnimeListId = "40748" },
            new SeasonMapping { SeasonNumber = 2, MyAnimeListId = "51009" }
        ];

        var align = svc.AlignEntry(details, chain);
        var ep    = svc.TranslateToCanonical(align, 2, 5);

        Assert.NotNull(ep);
        Assert.Equal(2, ep.Season);
        Assert.Equal(5, ep.Number);
        Assert.False(ep.IsOverflow);
    }

    [Fact]
    public async Task Align_PartialIds_FillsRestByNumber()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 1, 0);
        details.Episodes.Clear();
        foreach (var (s, count) in new[] { (1, 25), (2, 23), (3, 12) })
        {
            for (var n = 1; n <= count; n++)
            {
                details.Episodes.Add(new Episode { Id = $"e{s}-{n}", Number = n, Season = s });
            }
        }

        details.SeasonMappings = [new SeasonMapping { SeasonNumber = 1, MyAnimeListId = "40748" }];

        var align = svc.AlignEntry(details, chain);

        Assert.Equal(3, align.Seasons.Count);
        Assert.Equal(
            [1, 2, 3], align.Seasons.OrderBy(s => s.ProviderSeasonNumber).Select(s => s.CanonicalSeasonNumber));
        Assert.Contains(align.Warnings, w => w.Contains("25"));
        Assert.Contains(align.Warnings, w => w.Contains("ID yok"));

        var ep = svc.TranslateToCanonical(align, 2, 5);
        Assert.NotNull(ep);
        Assert.Equal((2, 5), (ep.Season, (int)ep.Number));
    }

    [Fact]
    public async Task Chain_MiddleBridgeMovie_IsKept()
    {
        var data = JjkChainData();
        data["131573"] = new MediaMetadata
        {
            ExternalId    = "131573",
            Source        = MetadataSource.AniList,
            Title         = "Jujutsu Kaisen 0",
            AniListId     = "131573",
            MyAnimeListId = "40748",
            TotalEpisodes = 1,
            Format        = ContentFormat.Movie,
            Synonyms      = []
        };
        var svc = Create(data, new Dictionary<string, string?>
        {
            ["113415"] = "131573",
            ["131573"] = "145064",
            ["145064"] = "60683",
            ["60683"]  = null
        }, new Dictionary<string, string?>
        {
            ["131573"] = "113415",
            ["145064"] = "131573",
            ["60683"]  = "145064"
        });

        var chain = await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal(["113415", "131573", "145064", "60683"],
                     chain.Entries.Select(e => e.AniListId));
        Assert.Equal([1, 0, 2, 3], chain.Entries.Select(e => e.SeasonNumber));
    }

    [Fact]
    public async Task Chain_LeadingPrologue_KeptAsUnnumbered()
    {
        var data = new Dictionary<string, MediaMetadata>
        {
            ["167404"] = Meta("167404", "MONSTERS", null, 1),
            ["21"]     = Meta("21", "ONE PIECE", null, null)
        };
        data["167404"].Format = ContentFormat.Ova;
        var svc = Create(data, new Dictionary<string, string?> { ["167404"] = "21" },
                         new Dictionary<string, string?> { ["21"]           = "167404" });

        var chain = await svc.GetSeasonChainAsync("21", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal("21", chain.RootAniListId);
        Assert.Equal(2, chain.Entries.Count);
        Assert.Equal(0, chain.Entries[0].SeasonNumber);
        Assert.Equal("167404", chain.Entries[0].AniListId);
        Assert.Equal(1, chain.Entries[1].SeasonNumber);
        Assert.Equal("21", chain.Entries[1].AniListId);
    }

    [Fact]
    public async Task Chain_RequestedMovie_IsKept()
    {
        var data = new Dictionary<string, MediaMetadata>
        {
            ["167404"] = Meta("167404", "MONSTERS", null, 1),
            ["21"]     = Meta("21", "ONE PIECE", null, null)
        };
        data["167404"].Format = ContentFormat.Ova;
        var svc = Create(data, new Dictionary<string, string?> { ["167404"] = "21" },
                         new Dictionary<string, string?> { ["21"]           = "167404" });

        var chain = await svc.GetSeasonChainAsync("167404", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal(2, chain.Entries.Count);
        Assert.Equal("167404", chain.Entries[0].AniListId);
        Assert.Equal(0, chain.Entries[0].SeasonNumber);
        Assert.Equal("21", chain.Entries[1].AniListId);
    }

    [Fact]
    public void Translate_NoSeasons_ReturnsNull()
    {
        var svc   = Create(JjkChainData(), JjkSequels());
        var align = new EntryAlignment();

        Assert.Null(svc.TranslateToCanonical(align, 1, 1));
    }

    private static Dictionary<string, MediaMetadata> BleachChainData() => new()
    {
        ["269"]   = Meta("269", "BLEACH", "269", 366),
        ["16463"] = Meta("16463", "BLEACH S2", "5150", 13),
        ["16545"] = Meta("16545", "BLEACH S3", "5151", 13),
        ["16546"] = Meta("16546", "BLEACH S4", "5152", 14)
    };

    private static Dictionary<string, string?> BleachSequels() => new()
    {
        ["269"]   = "16463",
        ["16463"] = "16545",
        ["16545"] = "16546",
        ["16546"] = null
    };

    [Fact]
    public async Task Align_GroupSpanning_ExpandsToSlices()
    {
        var svc     = Create(BleachChainData(), BleachSequels());
        var chain   = (await svc.GetSeasonChainAsync("269", TestContext.Current.CancellationToken))!;
        var details = Details("Bleach", 1, 0);
        details.Episodes.Clear();
        foreach (var (s, count) in new[] { (1, 366), (2, 40) })
        {
            for (var n = 1; n <= count; n++)
            {
                details.Episodes.Add(new Episode { Id = $"e{s}-{n}", Number = n, Season = s });
            }
        }

        var align = svc.AlignEntry(details, chain);

        var p2 = align.Seasons.Where(s => s.ProviderSeasonNumber == 2).OrderBy(s => s.StartOffset).ToArray();
        Assert.Equal(3, p2.Length);
        Assert.Equal([2, 3, 4], p2.Select(s => s.CanonicalSeasonNumber));
        Assert.Equal([1, 14, 27], p2.Select(s => s.StartOffset));

        var ep20 = svc.TranslateToCanonical(align, 2, 20);
        Assert.NotNull(ep20);
        Assert.Equal((3, 7), (ep20.Season, (int)ep20.Number));
        Assert.False(ep20.IsOverflow);

        var ep40 = svc.TranslateToCanonical(align, 2, 40);
        Assert.NotNull(ep40);
        Assert.Equal((4, 14), (ep40.Season, (int)ep40.Number));

        var ep41 = svc.TranslateToCanonical(align, 2, 41);
        Assert.NotNull(ep41);
        Assert.True(ep41.IsOverflow);
    }

    [Fact]
    public async Task Align_SingleMapping_ExpandsWhenCountMatchesMultipleSeasons()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 1, 0);
        details.SeasonMappings = [new SeasonMapping { SeasonNumber = 1, AniListId = "113415" }];
        details.Episodes.Clear();
        for (var i = 1; i <= 47; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e{i}", Number = i, Season = 1 });
        }

        var align = svc.AlignEntry(details, chain);

        Assert.Equal(2, align.Seasons.Count);
        Assert.Equal([1, 2], align.Seasons.Select(s => s.CanonicalSeasonNumber));
        Assert.Equal([24, 23], align.Seasons.Select(s => s.ProviderEpisodeCount));

        var ep25 = svc.TranslateToCanonical(align, 1, 25);
        Assert.NotNull(ep25);
        Assert.Equal((2, 1), (ep25.Season, (int)ep25.Number));
        Assert.False(ep25.IsOverflow);
    }

    [Fact]
    public async Task Align_TryExpandAbsolute_AllocatesRemainingEpisodesToLastSlice()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 1, 0);
        details.Episodes.Clear();
        // 47 canonical (24 + 23), provider has 49 (2 extra recaps)
        for (var i = 1; i <= 49; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e{i}", Number = i, Season = 1 });
        }

        var align = svc.AlignEntry(details, chain);

        Assert.Equal(2, align.Seasons.Count);
        Assert.Equal(25, align.Seasons[1].ProviderEpisodeCount); // 23 + 2

        var ep49 = svc.TranslateToCanonical(align, 1, 49);
        Assert.NotNull(ep49);
        Assert.Equal((2, 25), (ep49.Season, (int)ep49.Number));
        Assert.True(ep49.IsOverflow);
    }

    [Fact]
    public async Task Align_UnmappedMultiSeason_DoesNotDuplicateConsumedSeasons()
    {
        var data = new Dictionary<string, MediaMetadata>
        {
            ["1"] = Meta("1", "Series S1", "1", 12),
            ["2"] = Meta("2", "Series S2", "2", 12),
            ["3"] = Meta("3", "Series S3", "3", 12)
        };
        var sequels = new Dictionary<string, string?>
        {
            ["1"] = "2",
            ["2"] = "3",
            ["3"] = null
        };
        var svc   = Create(data, sequels);
        var chain = (await svc.GetSeasonChainAsync("1", TestContext.Current.CancellationToken))!;

        var details = Details("Series", 2, 0);
        details.Episodes.Clear();
        // P1 has 24 eps (spans S1 + S2), P2 has 12 eps (should map to S3, not S2!)
        for (var i = 1; i <= 24; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e1-{i}", Number = i, Season = 1 });
        }

        for (var i = 1; i <= 12; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e2-{i}", Number = i, Season = 2 });
        }

        var align = svc.AlignEntry(details, chain);

        var canonicalSeasons = align.Seasons.Select(s => s.CanonicalSeasonNumber).ToList();
        Assert.Equal([1, 2, 3], canonicalSeasons);
        Assert.Equal(canonicalSeasons.Distinct().Count(), canonicalSeasons.Count);
    }

    [Fact]
    public async Task Align_MultiGroup_SubsequentSeasonMapsCleanly()
    {
        var data = BleachChainData();
        data["185874"] = Meta("185874", "BLEACH S5", "60636", 10);
        var sequels = BleachSequels();
        sequels["16546"]  = "185874";
        sequels["185874"] = null;

        var svc     = Create(data, sequels);
        var chain   = (await svc.GetSeasonChainAsync("269", TestContext.Current.CancellationToken))!;
        var details = Details("Bleach", 3, 0);
        details.Episodes.Clear();
        // P1 has 366 eps (S1), P2 has 40 eps (spans S2[13] + S3[13] + S4[14]), P3 has 10 eps (maps to S5!)
        for (var i = 1; i <= 366; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e1-{i}", Number = i, Season = 1 });
        }

        for (var i = 1; i <= 40; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e2-{i}", Number = i, Season = 2 });
        }

        for (var i = 1; i <= 10; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e3-{i}", Number = i, Season = 3 });
        }

        var align = svc.AlignEntry(details, chain);

        var p2Slices = align.Seasons.Where(s => s.ProviderSeasonNumber == 2).ToList();
        Assert.Equal(3, p2Slices.Count);
        Assert.Equal([2, 3, 4], p2Slices.Select(s => s.CanonicalSeasonNumber));

        var p3 = align.Seasons.FirstOrDefault(s => s.ProviderSeasonNumber == 3);
        Assert.NotNull(p3);
        Assert.Equal(5, p3.CanonicalSeasonNumber);
    }

    [Fact]
    public async Task Align_SingleMapping_NonFirstSeason_KeepsProviderSeason()
    {
        var svc     = Create(BleachChainData(), BleachSequels());
        var chain   = (await svc.GetSeasonChainAsync("269", TestContext.Current.CancellationToken))!;
        var details = Details("Bleach 2nd Season", 1, 0);
        details.SeasonMappings = [new SeasonMapping { SeasonNumber = 2, AniListId = "16463" }];
        details.Episodes.Clear();
        for (var i = 1; i <= 40; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e{i}", Number = i, Season = 2 });
        }

        var align = svc.AlignEntry(details, chain);

        Assert.Equal(3, align.Seasons.Count);
        Assert.All(align.Seasons, s => Assert.Equal(2, s.ProviderSeasonNumber));
        Assert.Equal([2, 3, 4], align.Seasons.Select(s => s.CanonicalSeasonNumber));

        var ep = svc.TranslateToCanonical(align, 2, 20);
        Assert.NotNull(ep);
        Assert.Equal((3, 7), (ep.Season, (int)ep.Number));
    }

    [Fact]
    public async Task Align_TitleFallback_PartTwo_MapsToSecondSeason()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen Part 2", 1, 0);
        details.Episodes.Clear();
        for (var i = 1; i <= 23; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e{i}", Number = i, Season = 1 });
        }

        var align = svc.AlignEntry(details, chain);

        var single = Assert.Single(align.Seasons);
        Assert.Equal(2, single.CanonicalSeasonNumber);
        Assert.Equal("145064", single.AniListId);
    }

    [Fact]
    public async Task Chain_LongPrequelRun_CappedAndTruncated()
    {
        var data     = new Dictionary<string, MediaMetadata>();
        var prequels = new Dictionary<string, string?>();
        for (var i = 1; i <= 10; i++)
        {
            data[i.ToString()]     = Meta(i.ToString(), $"Series Part {i}", i.ToString(), 12);
            prequels[i.ToString()] = i > 1 ? (i - 1).ToString() : null;
        }

        var svc   = Create(data, new Dictionary<string, string?>(), prequels);
        var chain = await svc.GetSeasonChainAsync("10", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.True(chain.Entries.Count <= 8);
        Assert.True(chain.Truncated);
    }

    [Fact]
    public async Task Align_ProviderSeasonZero_MapsToSpecialsWithoutShift()
    {
        var data = new Dictionary<string, MediaMetadata>
        {
            ["1"] = Meta("1", "Series S1", "1", 12),
            ["2"] = Meta("2", "Series OVA", "2", 3)
        };
        data["2"].Format = ContentFormat.Movie;
        var svc = Create(data, new Dictionary<string, string?> { ["1"] = "2", ["2"] = null });

        var chain = await svc.GetSeasonChainAsync("1", TestContext.Current.CancellationToken);
        Assert.NotNull(chain);

        var details = Details("Series", 1, 0);
        details.Episodes.Clear();
        for (var i = 1; i <= 3; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e0-{i}", Number = i, Season = 0 });
        }

        for (var i = 1; i <= 12; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e1-{i}", Number = i, Season = 1 });
        }

        var align = svc.AlignEntry(details, chain);

        var p0 = Assert.Single(align.Seasons, s => s.ProviderSeasonNumber == 0);
        Assert.Equal(0, p0.CanonicalSeasonNumber);
        var p1 = Assert.Single(align.Seasons, s => s.ProviderSeasonNumber == 1);
        Assert.Equal(1, p1.CanonicalSeasonNumber);
    }

    [Fact]
    public async Task Translate_Absolute_Overflow_MapsToLastSeason()
    {
        var svc     = Create(JjkChainData(), JjkSequels());
        var chain   = (await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken))!;
        var details = Details("Jujutsu Kaisen", 1, 0);
        details.Episodes.Clear();
        for (var i = 1; i <= 47; i++)
        {
            details.Episodes.Add(new Episode { Id = $"e{i}", Number = i, Season = 1 });
        }

        var align = svc.AlignEntry(details, chain);
        Assert.Equal(EntryNumberingMode.Absolute, align.NumberingMode);

        var ep = svc.TranslateToCanonical(align, 1, 49);
        Assert.NotNull(ep);
        Assert.Equal(2, ep.Season);
        Assert.Equal(25, (int)ep.Number);
        Assert.True(ep.IsOverflow);
    }

    [Fact]
    public async Task Translate_SpannedSeason_BelowRange_MapsToFirstSlice()
    {
        var svc     = Create(BleachChainData(), BleachSequels());
        var chain   = (await svc.GetSeasonChainAsync("269", TestContext.Current.CancellationToken))!;
        var details = Details("Bleach", 1, 0);
        details.Episodes.Clear();
        foreach (var (s, count) in new[] { (1, 366), (2, 40) })
        {
            for (var n = 1; n <= count; n++)
            {
                details.Episodes.Add(new Episode { Id = $"e{s}-{n}", Number = n, Season = s });
            }
        }

        var align = svc.AlignEntry(details, chain);

        var ep = svc.TranslateToCanonical(align, 2, 0);
        Assert.NotNull(ep);
        Assert.Equal(2, ep.Season);
        Assert.Equal(0, (int)ep.Number);
        Assert.False(ep.IsOverflow);
    }

    [Fact]
    public async Task Chain_RelationsError_MarksTruncated()
    {
        var edges = new Dictionary<string, List<RelationEdge>>
        {
            ["1"] = [new RelationEdge { RelationType = "SEQUEL", Id = "2" }],
            ["2"] = [new RelationEdge { RelationType = "SEQUEL", Id = "3" }]
        };
        var data = new Dictionary<string, MediaMetadata>
        {
            ["1"] = Meta("1", "Series S1", "1", 12),
            ["2"] = Meta("2", "Series S2", "2", 12),
            ["3"] = Meta("3", "Series S3", "3", 12)
        };

        Task<IReadOnlyList<RelationEdge>> Flaky(string id, CancellationToken _)
        {
            if (id == "2")
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult<IReadOnlyList<RelationEdge>>(edges.GetValueOrDefault(id, []));
        }

        var svc = new SeasonChainService(
            [new FakeMetadataProvider(data.Values)],
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SeasonChainService>.Instance,
            Flaky);

        var chain = await svc.GetSeasonChainAsync("1", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.True(chain.Truncated);
    }

    [Fact]
    public async Task Chain_RelationsMemoized_RootQueriedOnce()
    {
        var calls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var data  = JjkChainData();
        var sequels = JjkSequels();
        var edges = new Dictionary<string, List<RelationEdge>>();
        foreach (var (from, to) in sequels)
        {
            if (to is null)
            {
                continue;
            }

            edges.TryAdd(from, []);
            edges[from].Add(new RelationEdge { RelationType = "SEQUEL", Id = to });
        }

        Task<IReadOnlyList<RelationEdge>> Counting(string id, CancellationToken _)
        {
            calls[id] = calls.GetValueOrDefault(id) + 1;
            return Task.FromResult<IReadOnlyList<RelationEdge>>(edges.GetValueOrDefault(id, []));
        }

        var svc = new SeasonChainService(
            [new FakeMetadataProvider(data.Values)],
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SeasonChainService>.Instance,
            Counting);

        var chain = await svc.GetSeasonChainAsync("113415", TestContext.Current.CancellationToken);

        Assert.NotNull(chain);
        Assert.Equal(3, chain.Entries.Count);
        Assert.Equal(1, calls.GetValueOrDefault("113415"));
    }
}
