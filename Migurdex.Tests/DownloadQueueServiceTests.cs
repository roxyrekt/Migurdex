using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Core.Database;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadQueueServiceTests
{
    [Fact]
    public void Database_PersistsAndRecoversInterruptedDownloadJobs()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = new MigurdexDatabase(tempDir);
            var job = new DownloadJobEntry
            {
                Id              = "job-1",
                ProviderName    = "AniHub",
                AnimeId         = "one-piece",
                AnimeTitle      = "One Piece",
                EpisodeId       = "ep-1",
                EpisodeTitle    = "Bölüm 1",
                Season          = 1,
                EpisodeNumber   = 1,
                OutputDirectory = tempDir,
                Status          = DownloadJobStatus.Downloading,
                BytesDownloaded = 1024,
                TotalBytes      = 4096,
                Source = new VideoSource
                {
                    Url     = "https://cdn.example/op-1.mp4",
                    Type    = VideoType.Mp4,
                    Quality = "1080p",
                    Hoster  = "Streamcash"
                }
            };

            db.UpsertDownloadJob(job);

            var loaded = db.GetDownloadJob("job-1");
            Assert.NotNull(loaded);
            Assert.Equal("One Piece", loaded.AnimeTitle);
            Assert.Equal(DownloadJobStatus.Downloading, loaded.Status);
            Assert.Equal(1024, loaded.BytesDownloaded);
            Assert.Equal("https://cdn.example/op-1.mp4", loaded.Source?.Url);

            var recoveredCount = db.RecoverInterruptedDownloadJobs(DownloadJobStatus.Queued);
            Assert.Equal(1, recoveredCount);

            var afterRecovery = db.GetDownloadJob("job-1");
            Assert.NotNull(afterRecovery);
            Assert.Equal(DownloadJobStatus.Queued, afterRecovery.Status);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task QueueService_ExecutesBatchAndDeduplicatesActiveEpisodes()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = new MigurdexDatabase(tempDir);
            var config = new FakeConfigurationService(tempDir)
            {
                MaxConcurrentDownloads = 2
            };
            var dlSvc = new FakeDownloadService(tempDir);

            await using var queue = new DownloadQueueService(db, dlSvc, config);

            var first = queue.Enqueue(new DownloadJobRequest
                                      {
                                          ProviderName  = "AniHub",
                                          AnimeId       = "naruto",
                                          AnimeTitle    = "Naruto",
                                          EpisodeId     = "ep-1",
                                          EpisodeTitle  = "Bölüm 1",
                                          Season        = 1,
                                          EpisodeNumber = 1,
                                          Source = new VideoSource
                                          {
                                              Url  = "https://cdn.example/naruto-1.mp4",
                                              Type = VideoType.Mp4
                                          }
                                      },
                                      out var existed1);
            Assert.False(existed1);

            var duplicate = queue.Enqueue(new DownloadJobRequest
                                          {
                                              ProviderName  = "AniHub",
                                              AnimeId       = "naruto",
                                              AnimeTitle    = "Naruto",
                                              EpisodeId     = "ep-1",
                                              EpisodeTitle  = "Bölüm 1",
                                              Season        = 1,
                                              EpisodeNumber = 1,
                                              Source = new VideoSource
                                              {
                                                  Url  = "https://cdn.example/naruto-1.mp4",
                                                  Type = VideoType.Mp4
                                              }
                                          },
                                          out var existed2);
            Assert.True(existed2);
            Assert.Equal(first.Id, duplicate.Id);

            var batch = queue.EnqueueBatch(
                [
                    new DownloadJobRequest
                    {
                        ProviderName  = "AniHub",
                        AnimeId       = "naruto",
                        AnimeTitle    = "Naruto",
                        EpisodeId     = "ep-1",
                        EpisodeTitle  = "Bölüm 1",
                        Season        = 1,
                        EpisodeNumber = 1
                    },
                    new DownloadJobRequest
                    {
                        ProviderName  = "AniHub",
                        AnimeId       = "naruto",
                        AnimeTitle    = "Naruto",
                        EpisodeId     = "ep-2",
                        EpisodeTitle  = "Bölüm 2",
                        Season        = 1,
                        EpisodeNumber = 2,
                        Source = new VideoSource
                        {
                            Url  = "https://cdn.example/naruto-2.mp4",
                            Type = VideoType.Mp4
                        }
                    }
                ],
                out var skippedCount);

            Assert.Equal(1, skippedCount);
            Assert.Single(batch);

            await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

            var summary = queue.GetSummary();
            Assert.Equal(2, summary.CompletedCount);
            Assert.Equal(0, summary.FailedCount);

            var cleared = queue.ClearCompleted();
            Assert.Equal(2, cleared);
            Assert.Empty(queue.GetSnapshots());
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task QueueService_ReResolvesFreshSource_WhenCachedSourceUrlExpired()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db     = new MigurdexDatabase(tempDir);
            var config = new FakeConfigurationService(tempDir);
            var dlSvc = new FakeDownloadService(tempDir)
            {
                FailingUrls =
                {
                    "https://cdn.example/expired-token.mp4"
                }
            };
            var api = new FakeQueueApiClient
            {
                SourcesToReturn =
                [
                    new VideoSource
                    {
                        Url     = "https://cdn.example/fresh-token.mp4",
                        Type    = VideoType.Mp4,
                        Quality = "1080p",
                        Hoster  = "Streamcash"
                    }
                ]
            };

            await using var queue = new DownloadQueueService(db, dlSvc, config, api);

            var job = queue.Enqueue(new DownloadJobRequest
                                    {
                                        ProviderName  = "AniHub",
                                        AnimeId       = "bleach",
                                        AnimeTitle    = "Bleach",
                                        EpisodeId     = "ep-5",
                                        EpisodeTitle  = "Bölüm 5",
                                        Season        = 1,
                                        EpisodeNumber = 5,
                                        Source = new VideoSource
                                        {
                                            Url     = "https://cdn.example/expired-token.mp4",
                                            Type    = VideoType.Mp4,
                                            Quality = "1080p"
                                        }
                                    },
                                    out _);

            await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

            var snapshot = queue.GetSnapshot(job.Id);
            Assert.NotNull(snapshot);
            Assert.Equal(DownloadJobStatus.Completed, snapshot.Job.Status);
            Assert.Equal("https://cdn.example/fresh-token.mp4", snapshot.Job.Source?.Url);
            Assert.Equal(1, api.ResolveCalls);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task QueueService_PauseAndResume_PreservesJobAndCompletesOnResume()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db     = new MigurdexDatabase(tempDir);
            var config = new FakeConfigurationService(tempDir);
            var gate   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dlSvc  = new FakeDownloadService(tempDir, gate.Task);

            await using var queue = new DownloadQueueService(db, dlSvc, config);

            var job = queue.Enqueue(new DownloadJobRequest
                                    {
                                        ProviderName  = "AniHub",
                                        AnimeId       = "gintama",
                                        AnimeTitle    = "Gintama",
                                        EpisodeId     = "ep-10",
                                        EpisodeTitle  = "Bölüm 10",
                                        Season        = 1,
                                        EpisodeNumber = 10,
                                        Source = new VideoSource
                                        {
                                            Url  = "https://cdn.example/gintama-10.mp4",
                                            Type = VideoType.Mp4
                                        }
                                    },
                                    out _);

            await dlSvc.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(queue.Pause(job.Id));

            var pausedSnap = queue.GetSnapshot(job.Id);
            Assert.NotNull(pausedSnap);
            Assert.Equal(DownloadJobStatus.Paused, pausedSnap.Job.Status);

            gate.SetResult();
            Assert.True(queue.Resume(job.Id));

            await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

            var finalSnap = queue.GetSnapshot(job.Id);
            Assert.NotNull(finalSnap);
            Assert.Equal(DownloadJobStatus.Completed, finalSnap.Job.Status);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void EpisodeSelector_ParsesRangesListsAndAll()
    {
        Assert.True(MediaSelection.TryParseEpisodeSelector("1-5", out var rangeSel, out var err1), err1);
        Assert.NotNull(rangeSel);
        Assert.True(rangeSel.IsBatch);
        Assert.True(rangeSel.Matches(1));
        Assert.True(rangeSel.Matches(3));
        Assert.True(rangeSel.Matches(5));
        Assert.False(rangeSel.Matches(6));

        Assert.True(MediaSelection.TryParseEpisodeSelector("1,3,5-7", out var multiSel, out var err2), err2);
        Assert.NotNull(multiSel);
        Assert.True(multiSel.IsBatch);
        Assert.True(multiSel.Matches(1));
        Assert.False(multiSel.Matches(2));
        Assert.True(multiSel.Matches(3));
        Assert.True(multiSel.Matches(6));

        Assert.True(MediaSelection.TryParseEpisodeSelector("all", out var allSel, out var err3), err3);
        Assert.NotNull(allSel);
        Assert.True(allSel.IsAll);
        Assert.True(allSel.IsBatch);

        var parsedCmd = DownloadCommand.TryParse(["naruto", "-e", "1-12", "-s", "2"], out var opts, out var cmdErr);
        Assert.True(parsedCmd, cmdErr);
        Assert.Null(opts.Episode);
        Assert.NotNull(opts.EpisodeSelector);
        Assert.True(opts.EpisodeSelector.IsBatch);
    }

    [Fact]
    public async Task QueueService_RespectsAutoResumeDownloadsOnStartup_And_AutoClearCompletedDownloads()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = new MigurdexDatabase(tempDir);
            db.UpsertDownloadJob(new DownloadJobEntry
            {
                Id              = "startup-job",
                ProviderName    = "AniHub",
                AnimeId         = "aot",
                AnimeTitle      = "Attack on Titan",
                EpisodeId       = "ep-1",
                EpisodeTitle    = "Bölüm 1",
                Season          = 1,
                EpisodeNumber   = 1,
                OutputDirectory = tempDir,
                Status          = DownloadJobStatus.Downloading,
                Source = new VideoSource
                {
                    Url  = "https://cdn.example/aot-1.mp4",
                    Type = VideoType.Mp4
                }
            });

            var config = new FakeConfigurationService(tempDir);
            config.Config.AutoResumeDownloadsOnStartup = false;
            config.Config.AutoClearCompletedDownloads  = true;

            var             dlSvc = new FakeDownloadService(tempDir);
            await using var queue = new DownloadQueueService(db, dlSvc, config);

            var initial = queue.GetSnapshot("startup-job");
            Assert.NotNull(initial);
            Assert.Equal(DownloadJobStatus.Paused, initial.Job.Status);

            Assert.Equal(1, queue.ResumeAll());
            await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

            Assert.Null(queue.GetSnapshot("startup-job"));
            Assert.Null(db.GetDownloadJob("startup-job"));
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public void CliConfig_ClampsNewDownloadSettingsBounds()
    {
        var config = new CliConfig
        {
            MaxConcurrentDownloads = 99,
            Mp4MaxSegments         = 99,
            HlsConcurrentFragments = 50,
            DownloadSpeedLimitKbps = -100
        };

        Assert.Equal(8, config.MaxConcurrentDownloads);
        Assert.Equal(16, config.Mp4MaxSegments);
        Assert.Equal(16, config.HlsConcurrentFragments);
        Assert.Equal(0, config.DownloadSpeedLimitKbps);
    }

    [Fact]
    public async Task QueueService_BoostPriority_MovesJobToTop()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db = new MigurdexDatabase(tempDir);
            var config = new FakeConfigurationService(tempDir)
            {
                MaxConcurrentDownloads = 1
            };
            var dlSvc = new FakeDownloadService(tempDir);

            await using var queue = new DownloadQueueService(db, dlSvc, config);

            var job1 = queue.Enqueue(new DownloadJobRequest
                                     {
                                         ProviderName = "AniHub",
                                         AnimeId      = "op",
                                         AnimeTitle   = "One Piece",
                                         EpisodeId    = "ep-1",
                                         Priority     = 0
                                     },
                                     out _);

            var job2 = queue.Enqueue(new DownloadJobRequest
                                     {
                                         ProviderName = "AniHub",
                                         AnimeId      = "op",
                                         AnimeTitle   = "One Piece",
                                         EpisodeId    = "ep-2",
                                         Priority     = 5
                                     },
                                     out _);

            Assert.True(queue.BoostPriority(job1.Id));

            var boosted = queue.GetSnapshot(job1.Id);
            Assert.NotNull(boosted);
            Assert.True(boosted.Job.Priority > 5);

            var dbJob = db.GetDownloadJob(job1.Id);
            Assert.NotNull(dbJob);
            Assert.True(dbJob.Priority > 5);
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Fact]
    public async Task QueueService_ClearFailed_And_ClearAll_RemovesMatchingJobs()
    {
        var tempDir = CreateTempDir();
        try
        {
            var db     = new MigurdexDatabase(tempDir);
            var config = new FakeConfigurationService(tempDir);
            var dlSvc = new FakeDownloadService(tempDir)
            {
                FailingUrls =
                {
                    "https://cdn.example/fail.mp4"
                }
            };

            await using var queue = new DownloadQueueService(db, dlSvc, config);

            var failJob = queue.Enqueue(new DownloadJobRequest
                                        {
                                            ProviderName = "AniHub",
                                            AnimeId      = "fail-anime",
                                            EpisodeId    = "ep-fail",
                                            Source = new VideoSource
                                            {
                                                Url  = "https://cdn.example/fail.mp4",
                                                Type = VideoType.Mp4
                                            }
                                        },
                                        out _);

            await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

            var failedSnap = queue.GetSnapshot(failJob.Id);
            Assert.NotNull(failedSnap);
            Assert.Equal(DownloadJobStatus.Failed, failedSnap.Job.Status);

            var clearedFailed = queue.ClearFailed();
            Assert.Equal(1, clearedFailed);
            Assert.Null(queue.GetSnapshot(failJob.Id));

            queue.Enqueue(new DownloadJobRequest
                          {
                              ProviderName = "AniHub",
                              AnimeId      = "any",
                              EpisodeId    = "ep-any",
                              Source = new VideoSource
                              {
                                  Url  = "https://cdn.example/ok.mp4",
                                  Type = VideoType.Mp4
                              }
                          },
                          out _);

            var clearedAll = queue.ClearAll(deletePartialFiles: false);
            Assert.Equal(1, clearedAll);
            Assert.Empty(queue.GetSnapshots());
            Assert.Empty(db.GetDownloadJobs());
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [Theory]
    [InlineData(0, 8, "[cyan][/][grey]░░░░░░░░[/]")]
    [InlineData(50, 8, "[cyan]████[/][grey]░░░░[/]")]
    [InlineData(100, 8, "[cyan]████████[/][grey][/]")]
    public void MiniProgressBar_RendersAccurately(double percent, int width, string expected)
    {
        var result = Migurdex.Cli.Tui.Views.DownloadsView.MiniProgressBar(percent, width);
        Assert.Equal(expected, result);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-dlqueue-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private sealed class FakeConfigurationService : IConfigurationService
    {
        public FakeConfigurationService(string dir)
        {
            ConfigDirectory = dir;
            Config = new CliConfig
            {
                DownloadDirectory = dir
            };
        }

        public CliConfig Config          { get; }
        public string    ConfigDirectory { get; }

        public int MaxConcurrentDownloads
        {
            get => Config.MaxConcurrentDownloads;
            set => Config.MaxConcurrentDownloads = value;
        }

        public void Save()   { }
        public void Reload() { }
    }

    private sealed class FakeDownloadService(string rootDir, Task? waitGate = null) : IDownloadService
    {
        public HashSet<string>      FailingUrls { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource Started     { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DownloadResult> DownloadAsync(
            DownloadRequest   request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            request.Progress?.Report(new DownloadProgress(DownloadStage.Downloading, 512, 1024));

            if (waitGate is not null)
            {
                await waitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (FailingUrls.Contains(request.Source.Url))
            {
                return DownloadResult.Failed("HTTP 403 Forbidden");
            }

            var filePath = Path.Combine(rootDir, $"{request.AnimeTitle}-S{request.Season}E{request.EpisodeNumber}.mp4");
            await File.WriteAllBytesAsync(filePath, [1, 2, 3, 4], cancellationToken).ConfigureAwait(false);
            return DownloadResult.Successful(filePath);
        }

        public Task<DownloadResult> DownloadAsync(
            VideoSource                  source,
            AnimeDetails                 details,
            Episode                      episode,
            string                       outputDirectory,
            bool                         overwrite         = false,
            bool                         resume            = true,
            IProgress<DownloadProgress>? progress          = null,
            CancellationToken            cancellationToken = default)
        {
            return DownloadAsync(new DownloadRequest
                                 {
                                     Source          = source,
                                     OutputDirectory = outputDirectory,
                                     AnimeTitle      = details.Title,
                                     EpisodeTitle    = episode.Title,
                                     Season          = episode.Season ?? 1,
                                     EpisodeNumber   = episode.Number,
                                     Overwrite       = overwrite,
                                     Resume          = resume,
                                     Progress        = progress
                                 },
                                 cancellationToken);
        }
    }

    private sealed class FakeQueueApiClient : IApiClientService
    {
        public List<VideoSource> SourcesToReturn { get; set; } = [];
        public int               ResolveCalls    { get; private set; }

        public Task<ApiResult<IReadOnlyList<VideoSource>>> GetVideoSourcesAsync(
            string            provider,
            string            episodeId,
            string?           group             = null,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Task.FromResult(ApiResult<IReadOnlyList<VideoSource>>.Ok(SourcesToReturn));
        }

        public Task<bool> IsApiOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> TryStartApiDaemonAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<ApiResult<IReadOnlyList<ProviderInfo>>> GetProvidersAsync(CancellationToken cancellationToken =
            default) =>
            throw new NotImplementedException();

        public Task<ApiResult<IReadOnlyList<SearchResult>>> SearchAnimeAsync(string query,
            string?                                                                 provider          = null,
            CancellationToken                                                       cancellationToken = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<StreamedSearchResult> SearchAnimeStreamAsync(string query,
            string?                                                                 provider          = null,
            CancellationToken                                                       cancellationToken = default,
            StreamScanStats?                                                        stats             = null) =>
            throw new NotImplementedException();

        public Task<ApiResult<AnimeDetails?>> GetAnimeDetailsAsync(string provider,
            string                                                        animeId,
            CancellationToken                                             cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ApiResult<IReadOnlyList<string>>> GetEpisodeGroupsAsync(string provider,
            string                                                                 episodeId,
            CancellationToken                                                      cancellationToken = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<VideoSource> GetVideoSourcesStreamAsync(string provider,
            string                                                             episodeId,
            string?                                                            group             = null,
            CancellationToken                                                  cancellationToken = default,
            StreamScanStats?                                                   stats             = null) =>
            throw new NotImplementedException();

        public Task<ApiResult<IReadOnlyList<string>>>
            GetExtractorsAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ApiResult<TrackerResolveResult?>> ResolveTrackerIdAsync(string provider,
            string                                                                 providerId,
            IReadOnlyList<string>                                                  titles,
            int?                                                                   year              = null,
            ContentFormat?                                                         format            = null,
            CancellationToken                                                      cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ApiResult<MediaMetadata?>> LookupTrackerAsync(string? anilistId,
            string?                                                       malId             = null,
            CancellationToken                                             cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ApiResult<TrackerEpisodeMapping?>> MapTrackerEpisodeAsync(string provider,
            string                                                                   providerId,
            int                                                                      season,
            double                                                                   episode,
            CancellationToken                                                        cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SaveTrackerMappingAsync(string provider,
            string                                       providerId,
            string                                       anilistId,
            string?                                      malId             = null,
            string?                                      matchedTitle      = null,
            CancellationToken                            cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ApiResult<BlameReport?>> GetBlameReportAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
