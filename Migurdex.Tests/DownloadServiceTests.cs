using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadServiceTests
{
    [Fact]
    public void AddDownloadServices_RegistersDownloadStack()
    {
        var services = new ServiceCollection();
        services.AddDownloadServices();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDownloadService>());
        Assert.NotNull(provider.GetRequiredService<IMp4Downloader>());
        Assert.NotNull(provider.GetRequiredService<IHlsDownloader>());
        Assert.NotNull(provider.GetRequiredService<ISubtitleDownloader>());
    }

    [Fact]
    public async Task Download_UsesSafeMetadataStemInsteadOfSourceUrlBasename()
    {
        var root = NewTempDir();
        try
        {
            var handler = new OneResponseHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("video", Encoding.UTF8, "video/mp4")
                });
            var service = new DownloadService(
                new DownloadPathBuilder(),
                new Mp4Downloader(handler),
                new NeverHlsDownloader(),
                new SubtitleDownloader(handler));

            var result = await service.DownloadAsync(
                new DownloadRequest
                {
                    Source = new VideoSource
                    {
                        Url = "https://origin.example/secret-token.mp4",
                        Type = VideoType.Mp4
                    },
                    OutputDirectory = root,
                    AnimeTitle = "Anime",
                    EpisodeTitle = "Episode"
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.NotNull(result.MediaPath);
            Assert.DoesNotContain("secret-token", result.MediaPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RejectsEmbedBeforeDispatch()
    {
        var root = NewTempDir();
        try
        {
            var result = await new DownloadService().DownloadAsync(
                new DownloadRequest
                {
                    Source = new VideoSource
                    {
                        Url = "https://origin.example/embed",
                        Type = VideoType.Embed
                    },
                    OutputDirectory = root,
                    AnimeTitle = "Anime",
                    EpisodeTitle = "Episode"
                },
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("Embed", result.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_WhenVideoFails_DoesNotCreateFinalSubtitle()
    {
        var root = NewTempDir();
        try
        {
            var handler = new OneResponseHandler(
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("failure", Encoding.UTF8, "text/plain")
                });
            var source = new VideoSource
            {
                Url = "https://origin.example/video",
                Type = VideoType.Mp4,
                Subtitles =
                [
                    new Subtitle
                    {
                        Url = "data:text/srt;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("1\n"))
                    }
                ]
            };
            var service = new DownloadService(
                new DownloadPathBuilder(),
                new Mp4Downloader(handler),
                new NeverHlsDownloader(),
                new SubtitleDownloader(handler));

            var result = await service.DownloadAsync(
                new DownloadRequest
                {
                    Source = source,
                    OutputDirectory = root,
                    AnimeTitle = "Anime",
                    EpisodeTitle = "Episode"
                },
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_WhenSubtitleIsCancelledAfterVideoFinal_KeepsCompletedMediaResult()
    {
        var root = NewTempDir();
        try
        {
            var handler = new TwoResponseHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("video", Encoding.UTF8, "video/mp4")
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new WaitForCancellationStream())
                });
            var source = new VideoSource
            {
                Url = "https://origin.example/video",
                Type = VideoType.Mp4,
                Subtitles =
                [
                    new Subtitle
                    {
                        Url = "https://origin.example/subtitle.srt"
                    }
                ]
            };
            var service = new DownloadService(new DownloadPathBuilder(),
                                               new Mp4Downloader(handler),
                                               new NeverHlsDownloader(),
                                               new SubtitleDownloader(handler));
            using var cts = new CancellationTokenSource();
            var download = service.DownloadAsync(new DownloadRequest
            {
                Source = source,
                OutputDirectory = root,
                AnimeTitle = "Anime",
                EpisodeTitle = "Episode"
            }, cts.Token);
            await handler.SubtitleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            cts.Cancel();

            var result = await download;
            Assert.True(result.IsCancelled);
            Assert.True(result.Success);
            Assert.NotNull(result.MediaPath);
            Assert.True(File.Exists(result.MediaPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_WhenSubtitleTargetLockIsHeld_SkipsSubtitlesAndKeepsMedia()
    {
        var root = NewTempDir();
        FileStream? holder = null;
        try
        {
            var source = new VideoSource
            {
                Url = "https://origin.example/video",
                Type = VideoType.Mp4,
                Subtitles =
                [
                    new Subtitle
                    {
                        Url    = "data:text/srt;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\ntr\n")),
                        Format = "srt"
                    }
                ]
            };
            var service = new DownloadService(new DownloadPathBuilder(),
                                               new StubMp4Downloader(),
                                               new NeverHlsDownloader(),
                                               new SubtitleDownloader(new NeverCalledHandler()));
            var destination = new DownloadPathBuilder()
                               .Build(root, "Anime", "Episode", 1, 1, ".mp4");
            Directory.CreateDirectory(destination.AnimeDirectory);
            holder = new FileStream(Path.Combine(destination.AnimeDirectory,
                                                 destination.FileStem + ".migurdex.lock"),
                                    FileMode.OpenOrCreate,
                                    FileAccess.ReadWrite,
                                    FileShare.None);

            var result = await service.DownloadAsync(
                new DownloadRequest
                {
                    Source = source,
                    OutputDirectory = root,
                    AnimeTitle = "Anime",
                    EpisodeTitle = "Episode"
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.NotNull(result.MediaPath);
            Assert.True(File.Exists(result.MediaPath!));
            Assert.Empty(result.SubtitlePaths);
            Assert.Contains(result.Warnings, warning => warning.Contains("Altyaz", StringComparison.Ordinal)
                                                           && warning.Contains("atland", StringComparison.Ordinal));
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
        }
        finally
        {
            holder?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_WritesDataUriSubtitleBesideMediaWhenTargetIsFree()
    {
        var root = NewTempDir();
        try
        {
            var source = new VideoSource
            {
                Url = "https://origin.example/video",
                Type = VideoType.Mp4,
                Subtitles =
                [
                    new Subtitle
                    {
                        Url      = "data:text/srt;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\ntr\n")),
                        Format   = "srt",
                        Language = "tr"
                    }
                ]
            };
            var service = new DownloadService(new DownloadPathBuilder(),
                                               new StubMp4Downloader(),
                                               new NeverHlsDownloader(),
                                               new SubtitleDownloader(new NeverCalledHandler()));

            var result = await service.DownloadAsync(
                new DownloadRequest
                {
                    Source = source,
                    OutputDirectory = root,
                    AnimeTitle = "Anime",
                    EpisodeTitle = "Episode"
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            var subtitle = Assert.Single(result.SubtitlePaths);
            Assert.True(File.Exists(subtitle));
            Assert.Contains("00:00:01,000", await File.ReadAllTextAsync(subtitle,
                                                                            TestContext.Current.CancellationToken),
                            StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(root, "*.migurdex.lock", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubMp4Downloader : IMp4Downloader
    {
        public Task<MediaDownloadResult> DownloadAsync(
            VideoSource                  source,
            string                       destinationPath,
            bool                         overwrite           = false,
            bool                         resume              = true,
            IProgress<DownloadProgress>? progress           = null,
            CancellationToken             cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "video", Encoding.UTF8);
            progress?.Report(new DownloadProgress(DownloadStage.Completed, 5, 5));
            return Task.FromResult(new MediaDownloadResult(full, 5, 5, false));
        }
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("HTTP should not be called for data URI subtitles.");
        }
    }

    private sealed class OneResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public OneResponseHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response);
        }
    }

    private sealed class TwoResponseHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        private int _requests;

        public TwoResponseHandler(HttpResponseMessage video, HttpResponseMessage subtitle)
        {
            _responses = new Queue<HttpResponseMessage>([video, subtitle]);
        }

        public TaskCompletionSource<bool> SubtitleStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requests) == 2)
            {
                SubtitleStarted.TrySetResult(true);
            }

            return Task.FromResult(_responses.Count > 0
                                       ? _responses.Dequeue()
                                       : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class WaitForCancellationStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(WaitAsync(cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => WaitAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async Task<int> WaitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
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
            throw new InvalidOperationException("HLS should not be called.");
        }
    }
}
