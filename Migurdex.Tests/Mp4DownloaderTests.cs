using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class Mp4DownloaderTests
{
    [Fact]
    public async Task Download_200_StreamsBodyAndMovesPartAtomically()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "video-body")]);
            var downloader = new Mp4Downloader(handler);
            var target = Path.Combine(root, "episode.mp4");

            var result = await downloader.DownloadAsync(
                Source("https://origin.example/video"),
                target,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("video-body", await File.ReadAllTextAsync(result.OutputPath,
                                                                      TestContext.Current.CancellationToken));
            Assert.True(File.Exists(target));
            Assert.Empty(PartPaths(target));
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ReportsBytesTotalAndStages()
    {
        var root = NewTempDir();
        try
        {
            var progress = new RecordingProgress();
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "12345")]);

            await new Mp4Downloader(handler).DownloadAsync(
                Source("https://origin.example/video"),
                Path.Combine(root, "episode.mp4"),
                progress: progress,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains(progress.Items, item => item.Stage == DownloadStage.Downloading
                                                          && item.BytesDownloaded == 5
                                                          && item.TotalBytes == 5);
            Assert.Equal(DownloadStage.Completed, progress.Items[^1].Stage);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_AppliesSourceHeaders()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "body")]);
            var source = Source("https://origin.example/video");
            source.Headers = new Dictionary<string, string>
            {
                ["X-Test"] = "header-value",
                ["Authorization"] = "Bearer secret"
            };

            await new Mp4Downloader(handler).DownloadAsync(
                source,
                Path.Combine(root, "episode.mp4"),
                cancellationToken: TestContext.Current.CancellationToken);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("header-value", request.Headers["X-Test"]);
            Assert.Equal("Bearer secret", request.Headers["Authorization"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RedirectDropsCustomAndSensitiveHeadersAcrossOrigin()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                Redirect("https://cdn.example/final"),
                Response(HttpStatusCode.OK, "body")
            ]);
            var source = Source("https://origin.example/video");
            source.Headers = new Dictionary<string, string>
            {
                ["Cookie"] = "session=secret",
                ["Cookie2"] = "session=secret",
                ["Authorization"] = "Bearer secret",
                ["Proxy-Authorization"] = "Basic secret",
                ["X-Api-Key"] = "secret",
                ["X-Referer"] = "drop-me",
                ["User-Agent"] = "safe-agent",
                ["Referer"] = "https://origin.example/page"
            };

            await new Mp4Downloader(handler).DownloadAsync(
                source,
                Path.Combine(root, "episode.mp4"),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("session=secret", handler.Requests[0].Headers["Cookie"]);
            Assert.Equal("Bearer secret", handler.Requests[0].Headers["Authorization"]);
            Assert.False(handler.Requests[1].Headers.ContainsKey("Cookie"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("Cookie2"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("Authorization"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("Proxy-Authorization"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("X-Api-Key"));
            Assert.False(handler.Requests[1].Headers.ContainsKey("X-Referer"));
            Assert.Equal("safe-agent", handler.Requests[1].Headers["User-Agent"]);
            Assert.Equal("https://origin.example/page", handler.Requests[1].Headers["Referer"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_KeepsSensitiveHeadersOnSameOriginRedirect()
    {
        var root = NewTempDir();
        try
        {
            var handler = new ScriptedHandler(
            [
                Redirect("https://origin.example/redirected"),
                Response(HttpStatusCode.OK, "body")
            ]);
            var source = Source("https://origin.example/video");
            source.Headers = new Dictionary<string, string>
            {
                ["Cookie"] = "session=secret",
                ["Authorization"] = "Bearer secret"
            };

            await new Mp4Downloader(handler).DownloadAsync(
                source,
                Path.Combine(root, "episode.mp4"),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("session=secret", handler.Requests[1].Headers["Cookie"]);
            Assert.Equal("Bearer secret", handler.Requests[1].Headers["Authorization"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_UsesDifferentPartForDifferentSourceCandidates()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var first = Source("https://origin.example/video-a");
            var second = Source("https://origin.example/video-b");
            var partA = Mp4Downloader.GetResumePartPath(target, first);
            var partB = Mp4Downloader.GetResumePartPath(target, second);
            var sameUrlDifferentHeaders = Source("https://origin.example/video-a");
            sameUrlDifferentHeaders.Headers = new Dictionary<string, string>
            {
                ["X-Api-Key"] = "different-source"
            };
            Assert.NotEqual(partA, partB);
            Assert.DoesNotContain("origin.example", partA, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(partA, Mp4Downloader.GetResumePartPath(target, sameUrlDifferentHeaders));

            await SeedPartAsync(first, target, "old-a", TestContext.Current.CancellationToken);
            await SeedPartAsync(second, target, "fresh-b", TestContext.Current.CancellationToken);
            Assert.Equal(2, PartPaths(target).Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_KeepsSeparatePartsForTwoSourceCandidates()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var first = Source("https://origin.example/candidate-a");
            var second = Source("https://origin.example/candidate-b");
            await SeedPartAsync(first, target, "aaaa", TestContext.Current.CancellationToken);
            await SeedPartAsync(second, target, "bbbb", TestContext.Current.CancellationToken);

            var parts = PartPaths(target);
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts, path => File.ReadAllText(path) == "aaaa");
            Assert.Contains(parts, path => File.ReadAllText(path) == "bbbb");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_StalePartFromPreviousCandidateIsNeverAppended()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var first = Source("https://origin.example/first");
            var second = Source("https://origin.example/second");
            var firstPart = Mp4Downloader.GetResumePartPath(target, first);
            Directory.CreateDirectory(Path.GetDirectoryName(firstPart)!);
            await File.WriteAllTextAsync(firstPart,
                                         "old-provider",
                                         TestContext.Current.CancellationToken);

            var result = await new Mp4Downloader(new ScriptedHandler(
                    [Response(HttpStatusCode.OK, "new-provider")]))
                .DownloadAsync(second, target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("new-provider", await File.ReadAllTextAsync(result.OutputPath,
                                                                         TestContext.Current.CancellationToken));
            Assert.Equal("old-provider", await File.ReadAllTextAsync(firstPart,
                                                                         TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_206_AppendsAtValidatedRangeAfterCancellation()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            var response = Response(HttpStatusCode.PartialContent, "def");
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 3-5/6");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var handler = new ScriptedHandler([response]);
            var downloader = new Mp4Downloader(handler);

            var result = await downloader.DownloadAsync(
                source,
                target,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("abcdef", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.Equal("bytes=3-", handler.Requests[0].Headers["Range"]);
            Assert.Empty(PartPaths(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_200_TruncatesPartWhenRangeIsIgnored()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "stale", TestContext.Current.CancellationToken);
            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "fresh")]);
            var downloader = new Mp4Downloader(handler);

            var result = await downloader.DownloadAsync(source,
                                                         target,
                                                         cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.Equal("bytes=5-", handler.Requests[0].Headers["Range"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ResumeFalseStartsFreshWithoutRange()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source,
                                target,
                                "stale",
                                TestContext.Current.CancellationToken);

            var handler = new ScriptedHandler([Response(HttpStatusCode.OK, "fresh")]);
            var result = await new Mp4Downloader(handler).DownloadAsync(source,
                                                                          target,
                                                                          resume: false,
                                                                          cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.False(handler.Requests[0].Headers.ContainsKey("Range"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_Invalid206SafelyRestartsInsteadOfAppending()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var invalid = Response(HttpStatusCode.PartialContent, "fresh");
            invalid.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 99-103/104");

            var result = await new Mp4Downloader(new ScriptedHandler(
                    [invalid, Response(HttpStatusCode.OK, "fresh")]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ContentRangeUnknownTotalAndShortBodyDoesNotFinalize()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var shortResponse = Response(HttpStatusCode.PartialContent, "d");
            shortResponse.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 3-8/*");

            await Assert.ThrowsAsync<DownloadException>(() => new Mp4Downloader(
                    new ScriptedHandler([shortResponse]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(File.Exists(target));
            Assert.Empty(PartPaths(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_ShortStandard206DoesNotFinalize()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var shortResponse = Response(HttpStatusCode.PartialContent, "d");
            shortResponse.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 3-8/20");

            await Assert.ThrowsAsync<DownloadException>(() => new Mp4Downloader(
                    new ScriptedHandler([shortResponse]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_416_CompletesOnlyWhenPartLengthMatchesTotal()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "done", TestContext.Current.CancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new StringContent(string.Empty)
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */4");
            var handler = new ScriptedHandler([response]);

            var result = await new Mp4Downloader(handler)
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("done", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
            Assert.Equal("bytes=4-", handler.Requests[0].Headers["Range"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_416WithWrongPartSizeRestartsInsteadOfFinalizing()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "abc", TestContext.Current.CancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new StringContent(string.Empty)
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */4");

            var result = await new Mp4Downloader(new ScriptedHandler(
                    [response, Response(HttpStatusCode.OK, "fresh")]))
                .DownloadAsync(source, target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await File.ReadAllTextAsync(result.OutputPath,
                                                                 TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_OverwriteReplacesExistingFinalOnlyAfterSuccessfulMove()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            await File.WriteAllTextAsync(target, "old", TestContext.Current.CancellationToken);
            var result = await new Mp4Downloader(new ScriptedHandler([Response(HttpStatusCode.OK, "new")]))
                .DownloadAsync(Source("https://origin.example/video"),
                               target,
                               overwrite: true,
                               cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("new", await File.ReadAllTextAsync(result.OutputPath,
                                                               TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_CancellationLeavesSourcePartForResume()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            await SeedPartAsync(source, target, "partial", TestContext.Current.CancellationToken);
            var part = Mp4Downloader.GetResumePartPath(target, source);
            using var cts = new CancellationTokenSource();
            var download = new Mp4Downloader(new ScriptedHandler(
                    [new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new DirectStreamContent(new PartialThenWaitStream("more"))
                    }]))
                .DownloadAsync(source, target, cancellationToken: cts.Token);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await download);
            Assert.True(File.Exists(part));
            Assert.NotEmpty(await File.ReadAllTextAsync(part, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task SeedPartAsync(
        VideoSource source,
        string target,
        string body,
        CancellationToken cancellationToken)
    {
        var response = Response(HttpStatusCode.OK, body);
        response.Content.Headers.ContentLength = body.Length + 1;
        var task = new Mp4Downloader(new ScriptedHandler([response]))
            .DownloadAsync(source, target, cancellationToken: cancellationToken);
        await Assert.ThrowsAsync<DownloadException>(async () => await task);
        Assert.True(File.Exists(Mp4Downloader.GetResumePartPath(target, source)));
    }

    private static string[] PartPaths(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        return Directory.Exists(directory)
                   ? Directory.GetFiles(directory, Path.GetFileName(target) + ".*.part")
                   : [];
    }

    [Fact]
    public async Task Download_RejectsNonMediaContentTypeWithoutWritingAnyFile()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var source = Source("https://origin.example/video");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>blocked</html>", Encoding.UTF8, "text/plain")
            };
            var handler = new ScriptedHandler([response]);

            var exception = await Assert.ThrowsAsync<DownloadException>(() => new Mp4Downloader(handler)
                                                      .DownloadAsync(source,
                                                                     target,
                                                                     cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("medya", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(Mp4Downloader.GetResumePartPath(target, source)));
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_KeepsValidMediaContentTypeWithoutExtraRangeRequest()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "episode.mp4");
            var handler = new ScriptedHandler(
                [new HttpResponseMessage(HttpStatusCode.OK)
                 {
                     Content = new StringContent("bytes", Encoding.UTF8, "application/octet-stream")
                 }]);

            var result = await new Mp4Downloader(handler)
                .DownloadAsync(Source("https://origin.example/video"),
                               target,
                               cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("bytes", await File.ReadAllTextAsync(result.OutputPath,
                                                              TestContext.Current.CancellationToken));
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static VideoSource Source(string url)
    {
        return new VideoSource
        {
            Url = url,
            Type = VideoType.Mp4
        };
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "video/mp4")
        };
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent(string.Empty)
        };
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-mp4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(IEnumerable<HttpResponseMessage> responses)
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
            return Task.FromResult(_responses.Count > 0
                                       ? _responses.Dequeue()
                                       : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed record RequestSnapshot(Uri? RequestUri, Dictionary<string, string> Headers)
    {
        public static RequestSnapshot Capture(HttpRequestMessage request)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            return new RequestSnapshot(request.RequestUri, headers);
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

    private sealed class DirectStreamContent : HttpContent
    {
        private readonly Stream _stream;

        public DirectStreamContent(Stream stream)
        {
            _stream = stream;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return _stream.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult(_stream);
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_stream);
        }
    }

    private sealed class PartialThenWaitStream : Stream
    {
        private readonly byte[] _initial;
        private          bool   _sent;

        public PartialThenWaitStream(string initial)
        {
            _initial = Encoding.UTF8.GetBytes(initial);
        }

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_sent)
            {
                _sent = true;
                _initial.CopyTo(buffer, offset);
                return _initial.Length;
            }

            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                _initial.AsMemory().CopyTo(buffer);
                return ValueTask.FromResult(_initial.Length);
            }

            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public override Task<int> ReadAsync(byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                _initial.CopyTo(buffer, offset);
                return Task.FromResult(_initial.Length);
            }

            return WaitForCancellationAsync(cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
