using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Toplu indirme akışının uçtan uca testi: kuyruk -> gerçek <see cref="DownloadService"/> ->
/// gerçek MP4 indirici -> disk. HTTP taşıması sahte bir handler'dır; böylece aynı anda kaç
/// isteğin uçtuğu (yani eşzamanlılık) doğrudan gözlemlenebilir ve test ağ istemez.
/// </summary>
public sealed class BatchDownloadFlowTests
{
    [Fact]
    public async Task BatchDownload_WritesEveryEpisodeAndUsesTheConfiguredConcurrency()
    {
        var root = NewTempDir();
        try
        {
            var payload = new byte[96 * 1024];
            Random.Shared.NextBytes(payload);
            var handler = new SlowPayloadHandler(payload, TimeSpan.FromMilliseconds(60));
            var downloadService = new DownloadService(new DownloadPathBuilder(),
                                                      new Mp4Downloader(handler),
                                                      new NeverHlsDownloader(),
                                                      new SubtitleDownloader(handler));
            var items = new List<DownloadQueueItem>
            {
                EpisodeItem(downloadService, root, 1),
                EpisodeItem(downloadService, root, 2),
                EpisodeItem(downloadService, root, 3)
            };

            var parallelSummary = await new DownloadQueueService()
                                       .RunAsync(items,
                                                 DownloadQueueOptions.FromConfig(new CliConfig()),
                                                 cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(3, parallelSummary.Succeeded);
            Assert.Equal(0, parallelSummary.Failed);
            Assert.Equal(2, handler.PeakConcurrency);
            AssertAllEpisodesOnDisk(root, payload.Length);

            // Paralel kapalıyken aynı işler tek tek inmeli.
            Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            handler.ResetPeak();
            var sequentialConfig = new CliConfig
            {
                DownloadParallelEnabled = false,
                DownloadConcurrency     = 4
            };

            var sequentialSummary = await new DownloadQueueService()
                                        .RunAsync(items,
                                                  DownloadQueueOptions.FromConfig(sequentialConfig),
                                                  cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(3, sequentialSummary.Succeeded);
            Assert.Equal(1, handler.PeakConcurrency);
            AssertAllEpisodesOnDisk(root, payload.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task BatchDownload_OneBrokenEpisode_LeavesTheOthersComplete()
    {
        var root = NewTempDir();
        try
        {
            var payload = new byte[16 * 1024];
            var handler = new SlowPayloadHandler(payload, TimeSpan.Zero)
            {
                FailUriSuffix = "episode-2.mp4"
            };
            var downloadService = new DownloadService(new DownloadPathBuilder(),
                                                      new Mp4Downloader(handler),
                                                      new NeverHlsDownloader(),
                                                      new SubtitleDownloader(handler));

            var summary = await new DownloadQueueService()
                                .RunAsync([EpisodeItem(downloadService, root, 1),
                                           EpisodeItem(downloadService, root, 2),
                                           EpisodeItem(downloadService, root, 3)],
                                          DownloadQueueOptions.FromConfig(new CliConfig()),
                                          cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(3, summary.Total);
            Assert.Equal(2, summary.Succeeded);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(DownloadQueueItemState.Failed, summary.Results[1].State);
            Assert.False(summary.AllSucceeded);

            var builder = new DownloadPathBuilder();
            foreach (var number in new[] { 1, 3 })
            {
                var path = builder.Build(root, "Anime", $"Bölüm {number}", 1, number, ".mp4").MediaPath;
                Assert.True(File.Exists(path), $"Eksik dosya: {path}");
            }

            var failedPath = builder.Build(root, "Anime", "Bölüm 2", 1, 2, ".mp4").MediaPath;
            Assert.False(File.Exists(failedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Plan_BuildItem_ResolvesTheEpisodeAndWritesTheFileThroughTheRealDownloadStack()
    {
        var root = NewTempDir();
        try
        {
            var payload = new byte[24 * 1024];
            var handler = new SlowPayloadHandler(payload, TimeSpan.Zero);
            var downloadService = new DownloadService(new DownloadPathBuilder(),
                                                      new Mp4Downloader(handler),
                                                      new NeverHlsDownloader(),
                                                      new SubtitleDownloader(handler));
            var api    = new FakeApiClient("https://origin.example/episode-7.mp4");
            var config = new CliConfig();
            var episode = new Episode
            {
                Id     = "ep-7",
                Title  = "Yedinci",
                Number = 7,
                Season = 2
            };

            var item = BatchDownloadPlan.BuildItem(api,
                                                   downloadService,
                                                   "StubProvider",
                                                   DownloadSourceFormat.Auto,
                                                   null,
                                                   episode,
                                                   config,
                                                   source => new DownloadRequest
                                                   {
                                                       Source            = source,
                                                       OutputDirectory   = root,
                                                       AnimeTitle        = "Anime",
                                                       EpisodeTitle      = episode.Title,
                                                       Season            = episode.Season ?? 1,
                                                       EpisodeNumber     = episode.Number,
                                                       DownloadSubtitles = false
                                                   });

            Assert.Equal("S02E07 - Yedinci", item.DisplayName);

            var summary = await new DownloadQueueService()
                                .RunAsync([item],
                                          new DownloadQueueOptions { ParallelEnabled = false },
                                          cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, summary.Succeeded);
            Assert.Single(api.SourceRequests);
            var path = new DownloadPathBuilder().Build(root, "Anime", "Yedinci", 2, 7, ".mp4").MediaPath;
            Assert.True(File.Exists(path), $"Dosya yazılmadı: {path}");
            Assert.Equal(payload.Length, new FileInfo(path).Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Plan_BuildItem_WhenTheApiReturnsNoSource_FailsWithoutThrowing()
    {
        var root     = NewTempDir();
        var api      = new FakeApiClient(null);
        var config   = new CliConfig();
        var episode  = new Episode { Id = "ep-1", Title = "Birinci", Number = 1 };
        try
        {
            var item = BatchDownloadPlan.BuildItem(api,
                                                   new NeverCalledDownloadService(),
                                                   "StubProvider",
                                                   DownloadSourceFormat.Auto,
                                                   null,
                                                   episode,
                                                   config,
                                                   source => new DownloadRequest
                                                   {
                                                       Source          = source,
                                                       OutputDirectory = root
                                                   });

            var summary = await new DownloadQueueService()
                                .RunAsync([item],
                                          new DownloadQueueOptions { ParallelEnabled = false },
                                          cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, summary.Failed);
            Assert.Contains("MP4/HLS", summary.Results[0].Error!, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Plan_BuildItem_AnimeciXRetriesMissingDirectSourceAndExplainsTheResult()
    {
        var root    = NewTempDir();
        var api     = new FakeApiClient(null);
        var episode = new Episode { Id = "ep-1", Title = "Birinci", Number = 1 };
        try
        {
            var item = BatchDownloadPlan.BuildItem(api,
                                                   new NeverCalledDownloadService(),
                                                   "AnimeciX",
                                                   DownloadSourceFormat.Auto,
                                                   null,
                                                   episode,
                                                   new CliConfig(),
                                                   source => new DownloadRequest
                                                   {
                                                       Source          = source,
                                                       OutputDirectory = root
                                                   },
                                                   new AnimeciXBatchSourceResolver(TimeSpan.Zero, TimeSpan.Zero));

            var summary = await new DownloadQueueService()
                                .RunAsync([item],
                                          new DownloadQueueOptions { ParallelEnabled = false },
                                          cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, api.SourceRequests.Count);
            Assert.Equal(DownloadQueueItemState.Failed, summary.Results[0].State);
            Assert.Contains("AnimeciX", summary.Results[0].Error, StringComparison.Ordinal);
            Assert.Contains("bu denemede doğrudan MP4/HLS kaynak gelmedi",
                            summary.Results[0].Error,
                            StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Plan_OrderEpisodes_ScopesToTheChosenSeasonAndOrdersDeterministically()
    {
        var episodes = new List<Episode>
        {
            new() { Id = "s2e2", Season = 2, Number = 2 },
            new() { Id = "s1e2", Season = 1, Number = 2 },
            new() { Id = "s1e1", Season = 1, Number = 1 },
            new() { Id = "s2e1", Season = 2, Number = 1 }
        };

        Assert.Equal(new[] { "s2e1", "s2e2" },
                     BatchDownloadPlan.OrderEpisodes(episodes, 2).Select(episode => episode.Id));
        Assert.Equal(new[] { "s1e1", "s1e2", "s2e1", "s2e2" },
                     BatchDownloadPlan.OrderEpisodes(episodes, null).Select(episode => episode.Id));
        Assert.Empty(BatchDownloadPlan.OrderEpisodes(episodes, 3));

        var ordered = BatchDownloadPlan.OrderEpisodes(episodes, 2);
        Assert.Equal(new[] { "S02E01", "S02E02" }, BatchDownloadPlan.BuildLabels(ordered));
    }

    [Fact]
    public void Plan_FromSelection_MapsIndicesBackToEpisodesInOrder()
    {
        var episodes = new List<Episode>
        {
            new() { Id = "a", Season = 1, Number = 1 },
            new() { Id = "b", Season = 1, Number = 2 },
            new() { Id = "c", Season = 1, Number = 3 }
        };

        Assert.Equal(new[] { "a", "c" },
                     BatchDownloadPlan.FromSelection(episodes, [2, 0, 0, 99]).Select(episode => episode.Id));
        Assert.Empty(BatchDownloadPlan.FromSelection(episodes, []));
    }

    [Fact]
    public void Plan_Summary_MapsEveryStateToARowAndTitle()
    {
        var summary = new DownloadQueueSummary
        {
            Results =
            [
                Result(0, "S01E01", DownloadQueueItemState.Completed,
                       DownloadResult.Successful("/tmp/S01E01.mp4")),
                Result(1, "S01E02", DownloadQueueItemState.Failed,
                       DownloadResult.Failed("kaynak yok")),
                Result(2, "S01E03", DownloadQueueItemState.Cancelled, null)
            ]
        };
        var chosen = new List<Episode>
        {
            new() { Id = "e1", Season = 1, Number = 1, Title = "Bir" },
            new() { Id = "e2", Season = 1, Number = 2, Title = "İki" },
            new() { Id = "e3", Season = 1, Number = 3, Title = "Üç" }
        };

        var rows = BatchDownloadPlan.BuildSummaryRows(summary, chosen);

        Assert.Equal(3, rows.Count);
        Assert.Equal("S01E01 - Bir", rows[0].Label);
        Assert.Equal("/tmp/S01E01.mp4", rows[0].Detail);
        Assert.Equal("kaynak yok", rows[1].Detail);
        Assert.Equal("iptal edildi", rows[2].Detail);
        Assert.Equal("Toplu indirme kısmen tamamlandı", BatchDownloadPlan.SummaryTitle(summary));
        Assert.Equal("3 bölümden 1 indirildi, 1 hata, 1 iptal", BatchDownloadPlan.DescribeCounts(summary));

        var allDone = new DownloadQueueSummary
        {
            Results = [Result(0, "S01E01", DownloadQueueItemState.Completed, DownloadResult.Successful("/tmp/x.mp4"))]
        };
        Assert.Equal("Toplu indirme tamamlandı", BatchDownloadPlan.SummaryTitle(allDone));
        Assert.Equal("Toplu indirme boş",
                     BatchDownloadPlan.SummaryTitle(new DownloadQueueSummary { Results = [] }));
        // Toplam bağlantı bütçesi ekranda görünür: 8 dosya paralelken dosya başına 1 bağlantı,
        // 2 dosya paralelken dosya başına 4 parça. Sayı ile açılan bağlantı aynı olmalı.
        Assert.Equal("2 eşzamanlı (paralel) • dosya başına 4 parça",
                     BatchDownloadPlan.DescribeConcurrency(true, 2));
        Assert.Equal("8 eşzamanlı (paralel) • dosya başına 1 bağlantı",
                     BatchDownloadPlan.DescribeConcurrency(true, 8));
        Assert.Equal("sıralı (paralel kapalı)", BatchDownloadPlan.DescribeConcurrency(false, 2));
    }

    private static DownloadQueueItemResult Result(
        int                     index,
        string                  displayName,
        DownloadQueueItemState  state,
        DownloadResult?         result)
    {
        return new DownloadQueueItemResult
        {
            Item   = new DownloadQueueItem(displayName, _ => Task.FromResult(DownloadResult.Failed("x"))),
            Index  = index,
            State  = state,
            Result = result,
            Error  = state == DownloadQueueItemState.Failed ? result?.Error : null
        };
    }

    private sealed class FakeApiClient : IApiClientService
    {
        private readonly string? _mediaUrl;

        public FakeApiClient(string? mediaUrl)
        {
            _mediaUrl = mediaUrl;
        }

        public List<string> SourceRequests { get; } = [];

        public Task<ApiResult<IReadOnlyList<VideoSource>>> GetVideoSourcesAsync(
            string            provider,
            string            episodeId,
            string?           group             = null,
            CancellationToken cancellationToken = default)
        {
            SourceRequests.Add(episodeId);
            IReadOnlyList<VideoSource> sources = _mediaUrl is null
                                                     ? []
                                                     : [new VideoSource
                                                        {
                                                            Url     = _mediaUrl,
                                                            Type    = VideoType.Mp4,
                                                            Quality = "1080p",
                                                            Hoster  = "StubHoster"
                                                        }];
            return Task.FromResult(ApiResult<IReadOnlyList<VideoSource>>.Ok(sources));
        }

        public Task<bool> IsApiOnlineAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> TryStartApiDaemonAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<ApiResult<IReadOnlyList<ProviderInfo>>> GetProvidersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<ProviderInfo>>.Ok(
                                [new ProviderInfo { Name = "StubProvider" }]));

        public Task<ApiResult<IReadOnlyList<SearchResult>>> SearchAnimeAsync(
            string            query,
            string?           provider          = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<StreamedSearchResult> SearchAnimeStreamAsync(
            string            query,
            string?           provider          = null,
            CancellationToken cancellationToken = default,
            StreamScanStats?  stats             = null) => throw new NotSupportedException();

        public Task<ApiResult<AnimeDetails?>> GetAnimeDetailsAsync(
            string            provider,
            string            animeId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ApiResult<IReadOnlyList<string>>> GetEpisodeGroupsAsync(
            string            provider,
            string            episodeId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<VideoSource> GetVideoSourcesStreamAsync(
            string            provider,
            string            episodeId,
            string?           group             = null,
            CancellationToken cancellationToken = default,
            StreamScanStats?  stats             = null) => throw new NotSupportedException();

        public Task<ApiResult<IReadOnlyList<string>>> GetExtractorsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ApiResult<TrackerResolveResult?>> ResolveTrackerIdAsync(
            string                provider,
            string                providerId,
            IReadOnlyList<string> titles,
            int?                  year              = null,
            ContentFormat?        format            = null,
            CancellationToken     cancellationToken = default) => throw new NotSupportedException();

        public Task<ApiResult<MediaMetadata?>> LookupTrackerAsync(
            string?           anilistId,
            string?           malId             = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ApiResult<TrackerEpisodeMapping?>> MapTrackerEpisodeAsync(
            string            provider,
            string            providerId,
            int               season,
            double            episode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SaveTrackerMappingAsync(
            string            provider,
            string            providerId,
            string            anilistId,
            string?           malId             = null,
            string?           matchedTitle      = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        // Upstream `blame` özelliğiyle IApiClientService'e eklendi (89e8c0c). Toplu indirme akışı bu
        // üyeyi hiç çağırmaz; sahte sınıf sözleşmeyi karşılamak için nötr bir gövde taşır.
        public Task<ApiResult<BlameReport?>> GetBlameReportAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NeverCalledDownloadService : IDownloadService
    {
        public Task<DownloadResult> DownloadAsync(
            DownloadRequest   request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Kaynak yokken indirici çağrılmamalı.");

        public Task<DownloadResult> DownloadAsync(
            VideoSource                  source,
            AnimeDetails                 details,
            Episode                      episode,
            string                       outputDirectory,
            bool                         overwrite           = false,
            bool                         resume              = true,
            IProgress<DownloadProgress>? progress           = null,
            CancellationToken             cancellationToken = default) =>
            throw new InvalidOperationException("Kaynak yokken indirici çağrılmamalı.");
    }

    private static void AssertAllEpisodesOnDisk(string root, int expectedLength)
    {
        var builder = new DownloadPathBuilder();
        for (var number = 1; number <= 3; number++)
        {
            var path = builder.Build(root, "Anime", $"Bölüm {number}", 1, number, ".mp4").MediaPath;
            Assert.True(File.Exists(path), $"Dosya yazılmadı: {path}");
            Assert.Equal(expectedLength, new FileInfo(path).Length);
        }
    }

    private static DownloadQueueItem EpisodeItem(IDownloadService downloadService, string root, int number)
    {
        return new DownloadQueueItem($"S01E{number:00}", cancellationToken =>
            downloadService.DownloadAsync(new DownloadRequest
            {
                Source = new VideoSource
                {
                    Url  = $"https://origin.example/episode-{number}.mp4",
                    Type = VideoType.Mp4
                },
                OutputDirectory   = root,
                AnimeTitle        = "Anime",
                EpisodeTitle      = $"Bölüm {number}",
                Season            = 1,
                EpisodeNumber     = number,
                DownloadSubtitles = false
            },
            cancellationToken));
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class SlowPayloadHandler : HttpMessageHandler
    {
        private readonly byte[]   _payload;
        private readonly TimeSpan _delay;
        private          int      _active;
        private          int      _peak;

        public SlowPayloadHandler(byte[] payload, TimeSpan delay)
        {
            _payload = payload;
            _delay   = delay;
        }

        public string? FailUriSuffix { get; init; }

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public void ResetPeak()
        {
            Volatile.Write(ref _peak, 0);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken   cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            var seen   = Volatile.Read(ref _peak);
            while (active > seen && Interlocked.CompareExchange(ref _peak, active, seen) != seen)
            {
                seen = Volatile.Read(ref _peak);
            }

            try
            {
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken);
                }

                if (FailUriSuffix is not null
                    && request.RequestUri?.AbsoluteUri.EndsWith(FailUriSuffix, StringComparison.Ordinal) == true)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content        = new StringContent("upstream hatası"),
                        RequestMessage = request
                    };
                }

                var content = new ByteArrayContent(_payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content        = content,
                    RequestMessage = request
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class NeverHlsDownloader : IHlsDownloader
    {
        public Task<MediaDownloadResult> DownloadAsync(
            VideoSource                  source,
            DownloadPath                 destination,
            bool                         overwrite           = false,
            IProgress<DownloadProgress>? progress           = null,
            CancellationToken             cancellationToken = default)
        {
            throw new InvalidOperationException("Bu testte HLS kullanılmaz.");
        }
    }
}
