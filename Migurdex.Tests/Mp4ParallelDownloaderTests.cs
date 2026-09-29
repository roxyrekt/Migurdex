using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class Mp4ParallelDownloaderTests
{
    private const long ParallelSize = 9L * 1024 * 1024;

    [Fact]
    public async Task Download_LargeFile_UsesParallelRangesAndMerges()
    {
        var root = NewTempDir();
        try
        {
            var expected = BuildPattern(ParallelSize);
            var handler = new RangeAwareHandler(expected, ignoreRange: false);
            var target = Path.Combine(root, "episode.mp4");

            var result = await new Mp4Downloader(handler).DownloadAsync(
                Source("https://origin.example/big-video"),
                target,
                cancellationToken: TestContext.Current.CancellationToken);

            var actual = await File.ReadAllBytesAsync(result.OutputPath, TestContext.Current.CancellationToken);
            Assert.Equal(expected, actual);
            Assert.True(handler.SawInitialRequest);
            Assert.True(handler.RangedRequests >= 2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RangeUnsupported_FallsBackToSequential()
    {
        var root = NewTempDir();
        try
        {
            var expected = BuildPattern(ParallelSize);
            var handler = new RangeAwareHandler(expected, ignoreRange: true);
            var target = Path.Combine(root, "episode.mp4");

            var result = await new Mp4Downloader(handler).DownloadAsync(
                Source("https://origin.example/big-video"),
                target,
                cancellationToken: TestContext.Current.CancellationToken);

            var actual = await File.ReadAllBytesAsync(result.OutputPath, TestContext.Current.CancellationToken);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static VideoSource Source(string url)
    {
        return new VideoSource { Url = url, Type = VideoType.Mp4 };
    }

    private static byte[] BuildPattern(long size)
    {
        var data = new byte[size];
        for (long i = 0; i < size; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-mp4par-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RangeAwareHandler(byte[] data, bool ignoreRange) : HttpMessageHandler
    {
        private readonly Lock _sync = new();
        private int _requests;

        public bool SawInitialRequest { get; private set; }
        public int RangedRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? range = null;
            if (request.Headers.TryGetValues("Range", out var values))
            {
                range = string.Join(",", values);
            }

            lock (_sync)
            {
                _requests++;
                if (range is null)
                {
                    SawInitialRequest = true;
                }
                else
                {
                    RangedRequests++;
                }
            }

            if (range is null || ignoreRange)
            {
                var full = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(data)
                };
                full.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                full.Content.Headers.ContentLength = data.Length;
                return Task.FromResult(full);
            }

            var (start, end) = ParseRange(range, data.Length);
            var sliceLength = (int)(end - start + 1);
            var slice = new byte[sliceLength];
            Array.Copy(data, start, slice, 0, sliceLength);
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            partial.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            partial.Content.Headers.ContentLength = sliceLength;
            partial.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {start}-{end}/{data.Length}");
            return Task.FromResult(partial);
        }

        private static (long Start, long End) ParseRange(string range, int total)
        {
            var value = range.Trim();
            if (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                value = value["bytes=".Length..];
            }

            var dash = value.IndexOf('-');
            var start = long.Parse(value[..dash], System.Globalization.CultureInfo.InvariantCulture);
            var end = long.Parse(value[(dash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            return (start, end);
        }
    }
}
