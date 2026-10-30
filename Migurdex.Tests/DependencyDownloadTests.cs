using Migurdex.Cli.Dependencies;
using Xunit;

namespace Migurdex.Tests;

public sealed class DependencyDownloadTests
{
    [Fact]
    public void ParseSha256Manifest_ReadsGnuStyleLine()
    {
        const string manifest =
            "e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3  yt-dlp\n"
            + "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d  yt-dlp.exe\n";

        Assert.Equal("f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d",
                     DependencyDownload.ParseSha256Manifest(manifest, "yt-dlp.exe"));
    }

    [Fact]
    public void ParseSha256Manifest_HandlesBsdStarPrefixAndComments()
    {
        const string manifest =
            "# checksums\n" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *ffmpeg.exe\n";

        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                     DependencyDownload.ParseSha256Manifest(manifest, "ffmpeg.exe"));
    }

    [Fact]
    public void ParseSha256Manifest_ReturnsNullForUnknownOrInvalidInput()
    {
        Assert.Null(DependencyDownload.ParseSha256Manifest(null, "yt-dlp.exe"));
        Assert.Null(DependencyDownload.ParseSha256Manifest("", "yt-dlp.exe"));
        Assert.Null(DependencyDownload.ParseSha256Manifest("not-a-hash  yt-dlp.exe", "yt-dlp.exe"));
        Assert.Null(DependencyDownload.ParseSha256Manifest(
                        "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d  yt-dlp.exe",
                        "ffmpeg.exe"));
    }

    [Fact]
    public void ExtractSha256_FindsHashInPlainText()
    {
        const string body = "4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077\n";

        Assert.Equal("4705843ccaaf54257c16ad90f3e952ece33c17df964ecf7bfdbb0f49c7171077",
                     DependencyDownload.ExtractSha256(body));
        Assert.Null(DependencyDownload.ExtractSha256("no hash here"));
        Assert.Null(DependencyDownload.ExtractSha256(null));
    }

    [Theory]
    [InlineData("sha256:4A355F3E89C83FABC730D33FB4E759DFEC747806ED2287B61A90D1681B83E8D3")]
    [InlineData("4a355f3e89c83fabc730d33fb4e759dfec747806ed2287b61a90d1681b83e8d3")]
    public void NormalizeSha256_AcceptsDigestAndBareHash(string value)
    {
        Assert.Equal("4a355f3e89c83fabc730d33fb4e759dfec747806ed2287b61a90d1681b83e8d3",
                     DependencyDownload.NormalizeSha256(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:xyz")]
    [InlineData("too-short")]
    public void NormalizeSha256_RejectsInvalidInput(string? value)
    {
        Assert.Null(DependencyDownload.NormalizeSha256(value));
    }
}

public sealed class DependencyDownloadParallelTests
{
    [Fact]
    public void ComputeChunks_SplitsEvenlyAndCoversWholeFile()
    {
        var chunks = DependencyDownload.ComputeChunks(10_000_000, 4);

        Assert.Equal(4, chunks.Count);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(9_999_999, chunks[^1].End);

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].End + 1, chunks[i].Start);
        }
    }

    [Fact]
    public void ComputeChunks_ReturnsEmptyForUnknownLengthOrSingleWorker()
    {
        Assert.Empty(DependencyDownload.ComputeChunks(-1, 4));
        Assert.Empty(DependencyDownload.ComputeChunks(0, 4));
        Assert.Empty(DependencyDownload.ComputeChunks(10_000_000, 1));
    }

    [Fact]
    public void ComputeChunks_SmallFileFallsBackToSingleChunk()
    {
        var chunks = DependencyDownload.ComputeChunks(1024, 8);

        Assert.Single(chunks);
        Assert.Equal((0L, 1023L), chunks[0]);
    }

    [Fact]
    public async Task DownloadFileAsync_UsesParallelRangesAndVerifiesHash()
    {
        var payload = CreatePayload(4 * 1024 * 1024 + 1234);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
                              .ToLowerInvariant();

        var       handler = new RangeHandler(payload);
        using var http    = new HttpClient(handler);

        var root = NewTempDir();
        try
        {
            var destination = Path.Combine(root, "tool.bin");

            await DependencyDownload.DownloadFileAsync(http,
                                                       "https://example.test/tool.bin",
                                                       destination,
                                                       expected,
                                                       progress: null,
                                                       TestContext.Current.CancellationToken,
                                                       maxParallelism: 4);

            Assert.Equal(payload,
                         await File.ReadAllBytesAsync(destination,
                                                      TestContext.Current.CancellationToken));
            Assert.True(handler.RangeRequestCount > 1, "parçalı indirme kullanılmadı");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_FallsBackWhenServerIgnoresRange()
    {
        var       payload = CreatePayload(3 * 1024 * 1024);
        var       handler = new RangeHandler(payload, supportsRanges: false);
        using var http    = new HttpClient(handler);

        var root = NewTempDir();
        try
        {
            var destination = Path.Combine(root, "tool.bin");

            await DependencyDownload.DownloadFileAsync(http,
                                                       "https://example.test/tool.bin",
                                                       destination,
                                                       expectedSha256: null,
                                                       progress: null,
                                                       TestContext.Current.CancellationToken,
                                                       maxParallelism: 4);

            Assert.Equal(payload,
                         await File.ReadAllBytesAsync(destination,
                                                      TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_ThrowsOnHashMismatchAndLeavesTargetUntouched()
    {
        var       payload = CreatePayload(64 * 1024);
        var       handler = new RangeHandler(payload);
        using var http    = new HttpClient(handler);

        var root = NewTempDir();
        try
        {
            var destination = Path.Combine(root, "tool.bin");
            await File.WriteAllTextAsync(destination,
                                         "previous",
                                         TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                               DependencyDownload.DownloadFileAsync(http,
                                                                   "https://example.test/tool.bin",
                                                                   destination,
                                                                   new string('a', 64),
                                                                   progress: null,
                                                                   TestContext.Current
                                                                              .CancellationToken,
                                                                   maxParallelism: 2));

            Assert.Equal("previous",
                         await File.ReadAllTextAsync(destination,
                                                     TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static byte[] CreatePayload(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);

        return bytes;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly bool   _supportsRanges;

        public RangeHandler(byte[] payload, bool supportsRanges = true)
        {
            _payload        = payload;
            _supportsRanges = supportsRanges;
        }

        public int RangeRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();

            if (range is not null && _supportsRanges)
            {
                RangeRequestCount++;

                var start = range.From ?? 0;
                var end   = Math.Min(range.To ?? _payload.Length - 1, _payload.Length - 1);
                var slice = _payload[(int) start..(int) (end + 1)];

                var partial = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                partial.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(start, end, _payload.Length);

                return Task.FromResult(partial);
            }

            if (range is not null)
            {
                RangeRequestCount++;
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
        }
    }
}
