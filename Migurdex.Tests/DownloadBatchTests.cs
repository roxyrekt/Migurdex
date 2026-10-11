using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Text;
using System.Runtime.CompilerServices;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadBatchTests
{
    [Fact]
    public async Task Batch_ContinuesAfterEpisodeFailure_AndReportsSummary()
    {
        using var root = new TempRoot();
        var       api  = new StubApi();
        api.FailSourcesFor.Add("ep1");
        var services = BuildServices(api, root.Path, maxEpisodes: 2, connections: 1);

        var exit = await DownloadCommand.RunAsync(["test anime", "-e", "1,2"], services);

        Assert.Equal(1, exit);
        Assert.Equal(3, api.ResolvedEpisodes.Count(e => e == "ep1"));
        Assert.Equal(1, api.ResolvedEpisodes.Count(e => e == "ep2"));
        var downloaded = Assert.Single(api.DownloadRecorder.DownloadedUrls);
        Assert.Contains("ep2.mp4", downloaded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_RetriesTransientResolve_ThenSucceeds()
    {
        using var root = new TempRoot();
        var       api  = new StubApi();
        api.FailSourcesFor.Add("ep1");
        api.TransientResolveFailures["ep2"] = 2;
        var services = BuildServices(api, root.Path, maxEpisodes: 1, connections: 1);

        var exit = await DownloadCommand.RunAsync(["test anime", "-e", "1,2"], services);

        Assert.Equal(1, exit);
        Assert.Equal(3, api.ResolvedEpisodes.Count(e => e == "ep1"));
        Assert.Equal(3, api.ResolvedEpisodes.Count(e => e == "ep2"));
        var downloaded = Assert.Single(api.DownloadRecorder.DownloadedUrls);
        Assert.Contains("ep2.mp4", downloaded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_HosterFilter_SelectsMatchingSource()
    {
        using var root     = new TempRoot();
        var       api      = new StubApi();
        var       services = BuildServices(api, root.Path, maxEpisodes: 1, connections: 1);

        var exit = await DownloadCommand.RunAsync(
                       ["test anime", "-e", "1", "--hoster", "FastHost"],
                       services);

        Assert.Equal(0, exit);
        var downloaded = Assert.Single(api.DownloadRecorder.DownloadedUrls);
        Assert.Contains("fast.mp4", downloaded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_ConnectionsPerFile_ReachesMp4Downloader()
    {
        using var root     = new TempRoot();
        var       api      = new StubApi();
        var       services = BuildServices(api, root.Path, maxEpisodes: 2, connections: 3);

        var exit = await DownloadCommand.RunAsync(["test anime", "-e", "2"], services);

        Assert.Equal(0, exit);
        Assert.Equal([3], api.DownloadRecorder.ReceivedMaxSegments.Distinct().ToList());
    }

    private static ServiceProvider BuildServices(StubApi api, string root, int maxEpisodes, int connections)
    {
        var config = new CliConfig
        {
            DownloadDirectory                = root,
            MaxConcurrentEpisodes            = maxEpisodes,
            ConnectionsPerFile               = connections,
            MaxSocketsPerHoster              = 8,
            DownloadSubtitles                = false,
            DownloadAutoSelectTimeoutSeconds = 5,
            DownloadRetryBaseDelaySeconds    = 0
        };
        var services = new ServiceCollection();
        services.AddSingleton<IApiClientService>(api);
        services.AddSingleton<IConfigurationService>(new StubConfig(config));
        services.AddSingleton<IDownloadPathBuilder, DownloadPathBuilder>();
        services.AddSingleton<IMp4Downloader>(api.DownloadRecorder);
        services.AddSingleton<IHlsDownloader>(new FailingHlsDownloader());
        services.AddSingleton<ISubtitleDownloader>(new NeverSubtitleDownloader());
        services.AddSingleton<IDownloadService, DownloadService>();
        return services.BuildServiceProvider();
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "migurdex-batch-" + Guid.NewGuid().ToString("N"));

        public TempRoot()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch { }
        }
    }

    private sealed class StubConfig(CliConfig config) : IConfigurationService
    {
        public CliConfig Config          { get; } = config;
        public string    ConfigDirectory => string.Empty;
        public void      Save()          { }
        public void      Reload()        { }
    }

    private sealed class RecordingMp4 : IMp4Downloader
    {
        public           List<string> DownloadedUrls      { get; } = [];
        public           List<int>    ReceivedMaxSegments { get; } = [];
        private readonly Lock         _gate = new();

        public Task<MediaDownloadResult> DownloadAsync(
            VideoSource                  source,
            string                       destinationPath,
            bool                         overwrite           = false,
            bool                         resume              = true,
            IProgress<DownloadProgress>? progress            = null,
            CancellationToken            cancellationToken   = default,
            int                          maxParallelSegments = 4)
        {
            lock (_gate)
            {
                DownloadedUrls.Add(source.Url);
                ReceivedMaxSegments.Add(maxParallelSegments);
            }

            var full = System.IO.Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "video", Encoding.UTF8);
            return Task.FromResult(new MediaDownloadResult(full, 5, 5, false));
        }
    }

    private sealed class FailingHlsDownloader : IHlsDownloader
    {
        public Task<MediaDownloadResult> DownloadAsync(
            VideoSource                  source,
            DownloadPath                 destination,
            bool                         overwrite         = false,
            IProgress<DownloadProgress>? progress          = null,
            CancellationToken            cancellationToken = default)
        {
            throw new DownloadException("HLS beklenmiyordu.");
        }
    }

    private sealed class NeverSubtitleDownloader : ISubtitleDownloader
    {
        public Task<SubtitleDownloadResult> DownloadAsync(
            Subtitle                             subtitle,
            string                               mediaPath,
            IReadOnlyDictionary<string, string>? sourceHeaders     = null,
            bool                                 overwrite         = false,
            int                                  index             = 0,
            IProgress<DownloadProgress>?         progress          = null,
            CancellationToken                    cancellationToken = default)
        {
            throw new InvalidOperationException("Altyazı beklenmiyordu.");
        }
    }

    private sealed class StubApi : IApiClientService
    {
        public           RecordingMp4            DownloadRecorder         { get; } = new();
        public           HashSet<string>         FailSourcesFor           { get; } = new(StringComparer.Ordinal);
        public           Dictionary<string, int> TransientResolveFailures { get; } = new(StringComparer.Ordinal);
        public           List<string>            ResolvedEpisodes         { get; } = [];
        private readonly Lock                    _gate = new();

        public Task<bool> IsApiOnlineAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> TryStartApiDaemonAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<ApiResult<IReadOnlyList<ProviderInfo>>> GetProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ApiResult<IReadOnlyList<ProviderInfo>>.Ok([]));
        }

        public Task<ApiResult<IReadOnlyList<SearchResult>>> SearchAnimeAsync(
            string            query,
            string?           provider          = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SearchResult> results =
            [
                new()
                {
                    Id           = "anime1",
                    Title        = "Test Anime",
                    ProviderName = "Stub"
                }
            ];
            return Task.FromResult(ApiResult<IReadOnlyList<SearchResult>>.Ok(results));
        }

        public async IAsyncEnumerable<StreamedSearchResult> SearchAnimeStreamAsync(
            string                                     query,
            string?                                    provider          = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            StreamScanStats?                           stats             = null)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<ApiResult<AnimeDetails?>> GetAnimeDetailsAsync(
            string            provider,
            string            animeId,
            CancellationToken cancellationToken = default)
        {
            AnimeDetails? details = new()
            {
                Title = "Test Anime",
                Episodes =
                [
                    new Episode
                    {
                        Id     = "ep1",
                        Title  = "Bolum 1",
                        Number = 1,
                        Season = 1
                    },
                    new Episode
                    {
                        Id     = "ep2",
                        Title  = "Bolum 2",
                        Number = 2,
                        Season = 1
                    }
                ]
            };
            return Task.FromResult(ApiResult<AnimeDetails?>.Ok(details));
        }

        public Task<ApiResult<IReadOnlyList<string>>> GetEpisodeGroupsAsync(
            string            provider,
            string            episodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ApiResult<IReadOnlyList<string>>.Ok((IReadOnlyList<string>) []));
        }

        public Task<ApiResult<IReadOnlyList<VideoSource>>> GetVideoSourcesAsync(
            string            provider,
            string            episodeId,
            string?           group             = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ResolvedEpisodes.Add(episodeId);
            }

            if (FailSourcesFor.Contains(episodeId))
            {
                return Task.FromResult(ApiResult<IReadOnlyList<VideoSource>>.Fail(
                                           (IReadOnlyList<VideoSource>) [],
                                           "upstream down"));
            }

            if (TransientResolveFailures.TryGetValue(episodeId, out var remaining) && remaining > 0)
            {
                TransientResolveFailures[episodeId] = remaining - 1;
                return Task.FromResult(ApiResult<IReadOnlyList<VideoSource>>.Fail(
                                           (IReadOnlyList<VideoSource>) [],
                                           "connection timeout"));
            }

            IReadOnlyList<VideoSource> sources = episodeId == "ep1"
                                                     ?
                                                     [
                                                         new VideoSource
                                                         {
                                                             Url     = "https://slow.example/ep1-slow.mp4",
                                                             Type    = VideoType.Mp4,
                                                             Quality = "720p",
                                                             Hoster  = "SlowHost"
                                                         },
                                                         new VideoSource
                                                         {
                                                             Url     = "https://fast.example/ep1-fast.mp4",
                                                             Type    = VideoType.Mp4,
                                                             Quality = "1080p",
                                                             Hoster  = "FastHost"
                                                         }
                                                     ]
                                                     :
                                                     [
                                                         new VideoSource
                                                         {
                                                             Url     = "https://fast.example/ep2.mp4",
                                                             Type    = VideoType.Mp4,
                                                             Quality = "1080p",
                                                             Hoster  = "FastHost"
                                                         }
                                                     ];
            return Task.FromResult(ApiResult<IReadOnlyList<VideoSource>>.Ok(sources));
        }

        public async IAsyncEnumerable<VideoSource> GetVideoSourcesStreamAsync(
            string                                     provider,
            string                                     episodeId,
            string?                                    group             = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            StreamScanStats?                           stats             = null)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<ApiResult<IReadOnlyList<string>>> GetExtractorsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ApiResult<IReadOnlyList<string>>.Ok((IReadOnlyList<string>) []));
        }

        public Task<ApiResult<TrackerResolveResult?>> ResolveTrackerIdAsync(
            string                provider,
            string                providerId,
            IReadOnlyList<string> titles,
            int?                  year              = null,
            ContentFormat?        format            = null,
            CancellationToken     cancellationToken = default)
        {
            return Task.FromResult(ApiResult<TrackerResolveResult?>.Ok(null));
        }

        public Task<ApiResult<MediaMetadata?>> LookupTrackerAsync(
            string?           anilistId,
            string?           malId             = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ApiResult<MediaMetadata?>.Ok(null));
        }

        public Task<ApiResult<TrackerEpisodeMapping?>> MapTrackerEpisodeAsync(
            string            provider,
            string            providerId,
            int               season,
            double            episode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ApiResult<TrackerEpisodeMapping?>.Ok(null));
        }

        public Task<bool> SaveTrackerMappingAsync(
            string            provider,
            string            providerId,
            string            anilistId,
            string?           malId             = null,
            string?           matchedTitle      = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<ApiResult<BlameReport?>> GetBlameReportAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ApiResult<BlameReport?>.Ok(null));
        }
    }
}
