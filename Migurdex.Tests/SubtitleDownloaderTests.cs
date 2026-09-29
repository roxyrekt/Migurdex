using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class SubtitleDownloaderTests
{
    [Fact]
    public async Task Download_DataUri_WritesSidecarWithVttExtension()
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "Anime", "S01E01 - Episode.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
            await File.WriteAllTextAsync(mediaPath, "video", TestContext.Current.CancellationToken);
            var subtitleText = "WEBVTT\n\n00:00.000 --> 00:01.000\nHello\n";
            var data = "data:text/vtt;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(subtitleText));
            var subtitle = new Subtitle
            {
                Url = data,
                Language = "en"
            };

            var result = await new SubtitleDownloader().DownloadAsync(
                subtitle,
                mediaPath,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.EndsWith(".en.vtt", result.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(subtitleText, await File.ReadAllTextAsync(result.OutputPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(result.OutputPath + ".part"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_HttpUsesOnlySafeSourceHeaderFallbacks()
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(mediaPath, "video", TestContext.Current.CancellationToken);
            var handler = new CapturingHandler(
            [
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nHello\n", Encoding.UTF8, "application/x-subrip")
                }
            ]);
            var sourceHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer source-secret",
                ["X-Api-Key"] = "source-secret",
                ["User-Agent"] = "safe-agent"
            };
            var subtitle = new Subtitle
            {
                Url = "https://cdn.example/subtitle.srt"
            };

            await new SubtitleDownloader(handler).DownloadAsync(
                subtitle,
                mediaPath,
                sourceHeaders,
                cancellationToken: TestContext.Current.CancellationToken);

            var request = Assert.Single(handler.Requests);
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.False(request.Headers.ContainsKey("X-Api-Key"));
            Assert.Equal("safe-agent", request.Headers["User-Agent"]);
            Assert.EndsWith(".srt", request.OutputPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_UsesSubtitleOwnHeadersIncludingAuthorization()
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(mediaPath, "video", TestContext.Current.CancellationToken);
            var handler = new CapturingHandler(
            [
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nHello\n", Encoding.UTF8, "application/x-subrip")
                }
            ]);
            var subtitle = new Subtitle
            {
                Url = "https://origin.example/subtitle.srt",
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer subtitle-secret"
                }
            };

            await new SubtitleDownloader(handler).DownloadAsync(
                subtitle,
                mediaPath,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("Bearer subtitle-secret", Assert.Single(handler.Requests).Headers["Authorization"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RejectsDataUriAboveSizeLimit()
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(mediaPath, "video", TestContext.Current.CancellationToken);
            var oversized = new string('x', SubtitleDownloader.MaxSubtitleBytes + 1);
            var subtitle = new Subtitle
            {
                Url = "data:text/srt," + oversized,
                Format = "srt"
            };

            await Assert.ThrowsAsync<SubtitleDownloadException>(() => new SubtitleDownloader()
                .DownloadAsync(subtitle,
                               mediaPath,
                               cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RejectsInvalidDataUriMime()
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(mediaPath, "video", TestContext.Current.CancellationToken);
            var subtitle = new Subtitle
            {
                Url = "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"error\":true}"))
            };

            await Assert.ThrowsAsync<SubtitleDownloadException>(() => new SubtitleDownloader()
                .DownloadAsync(subtitle,
                               mediaPath,
                               cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RejectsHttpSubtitleAboveSizeLimit()
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(mediaPath, "video", TestContext.Current.CancellationToken);
            var handler = new CapturingHandler(
            [
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[SubtitleDownloader.MaxSubtitleBytes + 1])
                }
            ]);

            await Assert.ThrowsAsync<SubtitleDownloadException>(() => new SubtitleDownloader(handler)
                .DownloadAsync(new Subtitle { Url = "https://origin.example/subtitle.srt" },
                               mediaPath,
                               cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("text/html", "<html>error</html>")]
    [InlineData("application/json", "{\"error\":\"not subtitle\"}")]
    public async Task Download_RejectsInvalidMimeAndBody(string mediaType, string body)
    {
        var root = NewTempDir();
        try
        {
            var mediaPath = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(mediaPath, "video", TestContext.Current.CancellationToken);
            var handler = new CapturingHandler(
            [
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, mediaType)
                }
            ]);

            await Assert.ThrowsAsync<SubtitleDownloadException>(() => new SubtitleDownloader(handler)
                .DownloadAsync(new Subtitle { Url = "https://origin.example/subtitle.srt" },
                               mediaPath,
                               cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadService_SubtitleFailureIsWarningAndLeavesNoFinalSidecar()
    {
        var root = NewTempDir();
        try
        {
            var handler = new CapturingHandler(
            [
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("video", Encoding.UTF8, "video/mp4")
                },
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("not a subtitle", Encoding.UTF8, "text/plain")
                }
            ]);
            var source = new VideoSource
            {
                Url = "https://origin.example/video",
                Type = VideoType.Mp4,
                Headers = new Dictionary<string, string>
                {
                    ["X-Source"] = "value"
                },
                Subtitles =
                [
                    new Subtitle
                    {
                        Url = "https://origin.example/subtitle.srt",
                        Language = "en"
                    }
                ]
            };
            var service = new DownloadService(
                new DownloadPathBuilder(),
                new Mp4Downloader(handler),
                new ThrowingHlsDownloader(),
                new SubtitleDownloader(handler));
            var result = await service.DownloadAsync(
                new DownloadRequest
                {
                    Source = source,
                    OutputDirectory = root,
                    AnimeTitle = "Anime",
                    EpisodeTitle = "Episode",
                    Season = 1,
                    EpisodeNumber = 1
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Single(result.Warnings);
            Assert.NotNull(result.MediaPath);
            Assert.True(File.Exists(result.MediaPath));
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadService_WhenSubtitlesDisabled_DoesNotCreateSidecar()
    {
        var root = NewTempDir();
        try
        {
            var handler = new CapturingHandler(
                [
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("video", Encoding.UTF8, "video/mp4")
                    }
                ]);
            var source = new VideoSource
            {
                Url = "https://origin.example/video",
                Type = VideoType.Mp4,
                Subtitles =
                [
                    new Subtitle
                    {
                        Url = "data:text/srt;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("1\n")),
                        Language = "en"
                    }
                ]
            };
            var service = new DownloadService(
                new DownloadPathBuilder(),
                new Mp4Downloader(handler),
                new ThrowingHlsDownloader(),
                new SubtitleDownloader(handler));

            var result = await service.DownloadAsync(
                new DownloadRequest
                {
                    Source = source,
                    OutputDirectory = root,
                    AnimeTitle = "Anime",
                    EpisodeTitle = "Episode",
                    DownloadSubtitles = false
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Empty(result.SubtitlePaths);
            Assert.Empty(Directory.GetFiles(root, "*.srt", SearchOption.AllDirectories));
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public CapturingHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage  request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(RequestSnapshot.Capture(request));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record RequestSnapshot(Uri? RequestUri, Dictionary<string, string> Headers)
    {
        public string? OutputPath => RequestUri?.AbsolutePath;

        public static RequestSnapshot Capture(HttpRequestMessage request)
        {
            return new RequestSnapshot(
                request.RequestUri,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed class ThrowingHlsDownloader : IHlsDownloader
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
