using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

public sealed class HlsDownloaderTests
{
    [Fact]
    public async Task Download_UsesYtDlpArgumentsHeadersAndPreservesOutputExtension()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root,
                                                                  "Anime",
                                                                  "Episode",
                                                                  1,
                                                                  1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var source = new VideoSource
            {
                Url = "https://origin.example/master.m3u8?token=secret",
                Type = VideoType.M3U8,
                Headers = new Dictionary<string, string>
                {
                    ["Referer"] = "https://origin.example/page",
                    ["Authorization"] = "Bearer secret",
                    ["X-Api-Key"] = "secret-key"
                }
            };
            ProcessStartInfo? captured = null;
            string? batchContents = null;
            var runner = new FakeProcessRunner((startInfo, _) =>
            {
                captured = startInfo;
                batchContents = File.ReadAllText(ValueAfter(startInfo.ArgumentList, "--batch-file"));
                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                File.WriteAllText(Path.Combine(jobDirectory, "media.webm"), "hls-media");
                return Task.FromResult(new ExternalProcessResult(0));
            });
            var downloader = new YtDlpHlsDownloader(runner,
                                                      "yt-dlp",
                                                      maxAttempts: 1,
                                                      retryDelay: TimeSpan.Zero);

            var result = await downloader.DownloadAsync(
                source,
                destination,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(captured);
            Assert.Contains("--no-config", captured!.ArgumentList);
            Assert.Contains("--no-playlist", captured.ArgumentList);
            Assert.Contains("--add-header", captured.ArgumentList);
            Assert.Contains("Referer: https://origin.example/page", captured.ArgumentList);
            Assert.Contains("--progress", captured.ArgumentList);
            // --print, [download] Destination satırlarını bastırıp track takibini kör eder.
            Assert.DoesNotContain("--print", captured.ArgumentList);
            Assert.Equal("5", ValueAfter(captured.ArgumentList, "--concurrent-fragments"));
            Assert.DoesNotContain(captured.ArgumentList,
                                  value => value.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                                           || value.Contains("X-Api-Key", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(source.Url, captured.ArgumentList);
            Assert.Equal(source.Url, batchContents?.Trim());
            Assert.EndsWith(".webm", result.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-token", result.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("hls-media", await File.ReadAllTextAsync(result.OutputPath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetDirectories(root, ".migurdex-job-*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ParsesYtDlpProgressLinesIntoDownloadingStage()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var runner = new FakeProcessRunner((startInfo, onStandardOutput, _) =>
            {
                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                File.WriteAllText(Path.Combine(jobDirectory, "media.mp4"), "hls-media");
                onStandardOutput?.Invoke("[download]  42.3% of   ~10.00MiB at  1.00MiB/s ETA 00:07");
                onStandardOutput?.Invoke("[download]   0.0% of ~ 324.23MiB at    585.91B/s ETA Unknown (frag 0/64)");
                onStandardOutput?.Invoke("[download]   9.0% of ~   1.19GiB at   12.67MiB/s ETA 01:24 (frag 12/142)");
                onStandardOutput?.Invoke("[download] 10.50MiB / 24.00MiB");
                return Task.FromResult(new ExternalProcessResult(0));
            });
            var progress = new RecordingProgress();

            var result = await new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                .DownloadAsync(new VideoSource
                    {
                        Url = "https://origin.example/master.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    progress: progress,
                    cancellationToken: TestContext.Current.CancellationToken);

            Assert.EndsWith(".mp4", result.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(progress.Items,
                            item => item.Stage == DownloadStage.Downloading
                                    && item.BytesDownloaded == 4435476
                                    && item.TotalBytes == 10485760
                                    && item.SpeedBytesPerSecond == 1048576
                                    && item.Eta == TimeSpan.FromSeconds(7));
            Assert.Contains(progress.Items,
                            item => item.Stage == DownloadStage.Downloading
                                    && item.BytesDownloaded == 11010048
                                    && item.TotalBytes == 25165824);
            Assert.Contains(progress.Items,
                            item => item.Stage == DownloadStage.Downloading
                                    && item.BytesDownloaded == 0
                                    && item.TotalBytes == 339979796
                                    && item.FragmentsDone == 0
                                    && item.FragmentsTotal == 64);
            Assert.Contains(progress.Items,
                            item => item.Stage == DownloadStage.Downloading
                                    && item.BytesDownloaded == 114997749
                                    && item.TotalBytes == 1277752770
                                    && item.SpeedBytesPerSecond == 13285457
                                    && item.FragmentsDone == 12
                                    && item.FragmentsTotal == 142
                                    && item.Percent == 9.0
                                    && item.Eta == TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(24));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RejectsHtmlOutputAsMedia()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var runner = new FakeProcessRunner((startInfo, _) =>
            {
                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                File.WriteAllText(Path.Combine(jobDirectory, "media.html"), "<html>error</html>");
                return Task.FromResult(new ExternalProcessResult(0));
            });

            await Assert.ThrowsAsync<HlsDownloadException>(() => new YtDlpHlsDownloader(runner,
                                                                                                maxAttempts: 1,
                                                                                                retryDelay: TimeSpan.Zero)
                .DownloadAsync(new VideoSource
                    {
                        Url = "https://origin.example/master.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.html", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_SameProcessTargetLockSerializesWritersEvenWithForce()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var runner = new FakeProcessRunner(async (startInfo, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                File.WriteAllText(Path.Combine(jobDirectory, "media.mp4"), "media-" + call);
                return new ExternalProcessResult(0);
            });
            var first = new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                .DownloadAsync(new VideoSource
                    {
                        Url = "https://origin.example/one.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    overwrite: true,
                    cancellationToken: TestContext.Current.CancellationToken);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            var second = new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                .DownloadAsync(new VideoSource
                    {
                        Url = "https://origin.example/two.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    overwrite: true,
                    cancellationToken: TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref calls));

            releaseFirst.TrySetResult(true);
            await first;
            await second;
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RetriesTransientProcessFailure()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var calls = 0;
            var runner = new FakeProcessRunner((startInfo, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new ExternalProcessResult(1));
                }

                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                File.WriteAllText(Path.Combine(jobDirectory, "media.mkv"), "retried");
                return Task.FromResult(new ExternalProcessResult(0));
            });

            var result = await new YtDlpHlsDownloader(runner,
                                                      maxAttempts: 2,
                                                      retryDelay: TimeSpan.Zero)
                .DownloadAsync(
                    new VideoSource
                    {
                        Url = "https://origin.example/master.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, calls);
            Assert.EndsWith(".mkv", result.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("retried", await File.ReadAllTextAsync(result.OutputPath,
                                                                  TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_CancellationStopsRunnerAndCleansTemporaryJob()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new FakeProcessRunner(async (_, cancellationToken) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ExternalProcessResult(0);
            });
            using var cts = new CancellationTokenSource();
            var download = new YtDlpHlsDownloader(runner,
                                                  maxAttempts: 1,
                                                  retryDelay: TimeSpan.Zero)
                .DownloadAsync(
                    new VideoSource
                    {
                        Url = "https://origin.example/master.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    cancellationToken: cts.Token);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
            Assert.Empty(Directory.GetDirectories(root, ".migurdex-job-*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ReportsFfmpegFailureWithoutLeakingProcessOutput()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            var calls = 0;
            var runner = new FakeProcessRunner((_, _) =>
            {
                calls++;
                return Task.FromResult(new ExternalProcessResult(
                    1,
                    standardError: "ffmpeg: command not found; https://secret-token.example"));
            });

            var exception = await Assert.ThrowsAsync<HlsDownloadException>(() =>
                new YtDlpHlsDownloader(runner, maxAttempts: 3, retryDelay: TimeSpan.Zero)
                    .DownloadAsync(
                        new VideoSource
                        {
                            Url = "https://origin.example/master.m3u8",
                            Type = VideoType.M3U8
                        },
                        destination,
                        cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(1, calls);
            Assert.Contains("ffmpeg", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-token", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_MissingYtDlpReturnsUnderstandableErrorWithoutUrl()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            var source = new VideoSource
            {
                Url = "https://origin.example/secret-token.m3u8",
                Type = VideoType.M3U8
            };
            var runner = new FakeProcessRunner((_, _) =>
                Task.FromException<ExternalProcessResult>(
                    new ExternalProcessStartException("process failed")));

            var exception = await Assert.ThrowsAsync<HlsDownloadException>(() =>
                new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                    .DownloadAsync(source,
                                    destination,
                                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("yt-dlp", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-token", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_FailedAttemptWarnsAndRetriesWithCause()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var calls = 0;
            var runner = new FakeProcessRunner((startInfo, onStandardOutput, _) =>
            {
                calls++;
                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                if (calls == 1)
                {
                    onStandardOutput?.Invoke("ERROR: https://cdn.example/frag12.ts: HTTP Error 403: Forbidden");
                    return Task.FromResult(new ExternalProcessResult(1));
                }

                File.WriteAllText(Path.Combine(jobDirectory, "media.mp4"), "hls-media");
                return Task.FromResult(new ExternalProcessResult(0));
            });

            var result = await new YtDlpHlsDownloader(runner, maxAttempts: 2, retryDelay: TimeSpan.Zero)
                .DownloadAsync(new VideoSource
                    {
                        Url = "https://origin.example/master.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, calls);
            Assert.EndsWith(".mp4", result.OutputPath, StringComparison.OrdinalIgnoreCase);
            var warning = Assert.Single(result.Warnings);
            Assert.Contains("Deneme 1/2", warning, StringComparison.Ordinal);
            Assert.Contains("403", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("cdn.example", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_DestinationLineSwitchesTrackAndResetsBytes()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var runner = new FakeProcessRunner((startInfo, onStandardOutput, _) =>
            {
                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                File.WriteAllText(Path.Combine(jobDirectory, "media.mp4"), "hls-media");
                onStandardOutput?.Invoke("[download] Destination: /home/roxy/Downloads/Migurdex/One Piece/.migurdex-job-abc123/master.f5471.mp4");
                onStandardOutput?.Invoke("[download]  50.0% of 10.00MiB at 1.00MiB/s ETA 00:05 (frag 5/10)");
                onStandardOutput?.Invoke("[download] Destination: /home/roxy/Downloads/Migurdex/One Piece/.migurdex-job-abc123/master.faud2-English.mp4");
                return Task.FromResult(new ExternalProcessResult(0));
            });
            var progress = new RecordingProgress();

            var result = await new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                .DownloadAsync(new VideoSource
                    {
                        Url = "https://origin.example/master.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    progress: progress,
                    cancellationToken: TestContext.Current.CancellationToken);

            Assert.EndsWith(".mp4", result.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(progress.Items,
                            item => item.Track == "master.f5471.mp4"
                                    && item.BytesDownloaded == 5242880);
            var reset = progress.Items.FirstOrDefault(item => item.Track == "master.faud2-English.mp4");
            Assert.NotNull(reset);
            Assert.Equal(0, reset!.BytesDownloaded);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_AllAttemptsFailingSurfacesLastCause()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var runner = new FakeProcessRunner((_, onStandardOutput, _) =>
            {
                onStandardOutput?.Invoke("ERROR: https://cdn.example/frag3.ts: HTTP Error 404: Not Found");
                return Task.FromResult(new ExternalProcessResult(1));
            });

            var exception = await Assert.ThrowsAsync<HlsDownloadException>(() =>
                new YtDlpHlsDownloader(runner, maxAttempts: 2, retryDelay: TimeSpan.Zero)
                    .DownloadAsync(new VideoSource
                        {
                            Url = "https://origin.example/master.m3u8",
                            Type = VideoType.M3U8
                        },
                        destination,
                        cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("404", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("cdn.example", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string ValueAfter(IReadOnlyList<string> values, string option)
    {
        var index = values.ToList().IndexOf(option);
        return index >= 0 && index + 1 < values.Count ? values[index + 1] : string.Empty;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-hls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeProcessRunner : IExternalProcessRunner
    {
        private readonly Func<ProcessStartInfo, Action<string>?, CancellationToken, Task<ExternalProcessResult>> _handler;

        public FakeProcessRunner(Func<ProcessStartInfo, CancellationToken, Task<ExternalProcessResult>> handler)
            : this((startInfo, _, cancellationToken) => handler(startInfo, cancellationToken))
        {
        }

        public FakeProcessRunner(
            Func<ProcessStartInfo, Action<string>?, CancellationToken, Task<ExternalProcessResult>> handler)
        {
            _handler = handler;
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, null, cancellationToken);
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            Action<string>?   onStandardOutput,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, onStandardOutput, cancellationToken);
        }
    }

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = [];

        public void Report(DownloadProgress value)
        {
            Items.Add(value);
        }
    }
}
