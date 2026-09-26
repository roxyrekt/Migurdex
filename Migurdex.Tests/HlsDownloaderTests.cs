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
        private readonly Func<ProcessStartInfo, CancellationToken, Task<ExternalProcessResult>> _handler;

        public FakeProcessRunner(Func<ProcessStartInfo, CancellationToken, Task<ExternalProcessResult>> handler)
        {
            _handler = handler;
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, cancellationToken);
        }
    }
}
