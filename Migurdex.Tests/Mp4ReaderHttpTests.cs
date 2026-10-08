using Microsoft.Extensions.Logging;
using Migurdex.Core.Utils;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using Xunit;

namespace Migurdex.Tests;

public sealed class Mp4ReaderHttpTests
{
    [Fact]
    public async Task Faststart_SingleRequest()
    {
        var file  = CannedMp4(moovAtEnd: false);
        var probe = new Probe(file, honorRange: true);

        var metadata = await probe.ReadAsync();

        Assert.Equal("720p", metadata.Quality);
        Assert.Equal(1280, metadata.Width);
        Assert.Equal(720, metadata.Height);
        Assert.Equal("H.264", metadata.VideoCodec);
        Assert.Equal("AAC", metadata.AudioCodec);
        Assert.Equal(600.0, metadata.DurationSeconds);
        Assert.Equal(file.Length, metadata.SizeBytes);
        Assert.Equal(Mp4MetadataReader.ComputeBitrate(file.Length, 600.0), metadata.Bitrate);
        Assert.Equal(1, probe.Requests);
    }

    [Fact]
    public async Task MoovAtEnd_FewRequests()
    {
        var file  = CannedMp4(moovAtEnd: true);
        var probe = new Probe(file, honorRange: true);

        var metadata = await probe.ReadAsync();

        Assert.Equal("720p", metadata.Quality);
        Assert.Equal(1280, metadata.Width);
        Assert.Equal(720, metadata.Height);
        Assert.Equal("H.264", metadata.VideoCodec);
        Assert.Equal("AAC", metadata.AudioCodec);
        Assert.Equal(600.0, metadata.DurationSeconds);
        Assert.Equal(file.Length, metadata.SizeBytes);
        Assert.NotNull(metadata.Bitrate);
        Assert.True(probe.Requests <= 3, $"requests: {probe.Requests}");
    }

    [Fact]
    public async Task RangeUnsupported_PartialRead()
    {
        var file  = CannedMp4(moovAtEnd: false);
        var probe = new Probe(file, honorRange: false);

        var metadata = await probe.ReadAsync();

        Assert.Equal("720p", metadata.Quality);
        Assert.Equal(1280, metadata.Width);
        Assert.Equal(file.Length, metadata.SizeBytes);
        Assert.NotNull(metadata.Bitrate);
        Assert.Equal(1, probe.Requests);
    }

    [Fact]
    public async Task RangeUnsupported_MoovAtEnd_NoGarbageParse()
    {
        var file  = CannedMp4(moovAtEnd: true, mdatPadding: 200_000);
        var probe = new Probe(file, honorRange: false);

        var metadata = await probe.ReadAsync();

        Assert.Equal("Auto", metadata.Quality);
        Assert.Equal(file.Length, metadata.SizeBytes);
        Assert.Null(metadata.Bitrate);
        Assert.Null(metadata.DurationSeconds);
    }

    [Fact]
    public async Task NonMp4Content_ReturnsAllNull()
    {
        var html  = System.Text.Encoding.ASCII.GetBytes("<html><body>unavailable</body></html>");
        var probe = new Probe(html, honorRange: false);

        var metadata = await probe.ReadAsync();

        Assert.Equal("Auto", metadata.Quality);
        Assert.Null(metadata.Bitrate);
        Assert.Null(metadata.SizeBytes);
        Assert.Null(metadata.DurationSeconds);
        Assert.Null(metadata.Width);
    }

    private static byte[] CannedMp4(bool moovAtEnd, int mdatPadding = 0)
    {
        var ftyp = Box("ftyp",
        [
            0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x00, 0x00, 0x69, 0x73, 0x6F, 0x6D, 0x61, 0x76, 0x63, 0x31
        ]);

        var mvhdPayload = new byte[100];
        mvhdPayload[0]  = 0;
        mvhdPayload[14] = 0x03;
        mvhdPayload[15] = 0xE8;
        mvhdPayload[17] = 0x09;
        mvhdPayload[18] = 0x27;
        mvhdPayload[19] = 0xC0;
        var mvhd = Box("mvhd", mvhdPayload);

        var tkhdPayload = new byte[84];
        tkhdPayload[0]  = 0;
        tkhdPayload[76] = 0x05;
        tkhdPayload[80] = 0x02;
        tkhdPayload[81] = 0xD0;
        var tkhdVideo = Box("tkhd", tkhdPayload);

        var tkhdAudio = Box("tkhd", new byte[84]);

        var videoTrak = Box("trak", Concat(tkhdVideo, MediaChain("avc1")));
        var audioTrak = Box("trak", Concat(tkhdAudio, MediaChain("mp4a")));
        var moov = Concat(mvhd, videoTrak, audioTrak);
        var moovBox = Box("moov", moov);

        return moovAtEnd ? Concat(ftyp, Box("mdat", new byte[mdatPadding]), moovBox) : Concat(ftyp, moovBox);
    }

    private static byte[] MediaChain(string fourcc)
    {
        return Box("mdia", Box("minf", Box("stbl", StsdBox(fourcc))));
    }

    private static byte[] StsdBox(string fourcc)
    {
        var payload = new byte[24];
        payload[7] = 1;
        payload[8]  = 0;
        payload[9]  = 0;
        payload[10] = 0;
        payload[11] = 16;
        payload[12] = (byte) fourcc[0];
        payload[13] = (byte) fourcc[1];
        payload[14] = (byte) fourcc[2];
        payload[15] = (byte) fourcc[3];

        return Box("stsd", payload);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var size = 8 + payload.Length;
        var box  = new byte[size];
        box[0] = (byte) (size >> 24);
        box[1] = (byte) (size >> 16);
        box[2] = (byte) (size >> 8);
        box[3] = (byte) size;
        box[4] = (byte) type[0];
        box[5] = (byte) type[1];
        box[6] = (byte) type[2];
        box[7] = (byte) type[3];
        Buffer.BlockCopy(payload, 0, box, 8, payload.Length);

        return box;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    private sealed class Probe
    {
        private readonly byte[] _file;
        private readonly bool   _honorRange;
        private          int    _requests;

        public Probe(byte[] file, bool honorRange)
        {
            _file       = file;
            _honorRange = honorRange;
        }

        public int Requests => _requests;

        public Task<Mp4Metadata> ReadAsync()
        {
            var bridge = new ProbeBridge(new CannedHandler(_file, _honorRange, () => Interlocked.Increment(ref _requests)));
            var reader = new Mp4MetadataReader(new ProbeProvider(bridge));

            return reader.GetVideoMetadataAsync("https://example.com/video.mp4",
                                                new Dictionary<string, string>(),
                                                TestContext.Current.CancellationToken);
        }
    }

    private sealed class CannedHandler(byte[] file, bool honorRange, Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            onRequest();

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (honorRange && range?.From is not null)
            {
                var from = (int) Math.Min(range.From.Value, file.Length);
                var to   = (int) Math.Min(range.To ?? file.Length - 1, file.Length - 1);
                if (to < from)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                }

                var slice = file[from..(to + 1)];
                var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from,
                                                                                                           to,
                                                                                                           file.Length);

                return Task.FromResult(partial);
            }

            var full = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(file)
            };
            full.Content.Headers.ContentLength = file.Length;

            return Task.FromResult(full);
        }
    }

    private sealed class ProbeBridge(HttpMessageHandler handler) : ISharedBridge
    {
        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();
        public ILoggerFactory LoggerFactory =>
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));

        public HttpClient CreateHttpClient(HttpClientOptions? options = null)
        {
            return new HttpClient(handler, disposeHandler: false);
        }

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
        {
            return new HttpClient(handler, disposeHandler: false);
        }

        public ILogger<T> CreateLogger<T>()
        {
            return LoggerFactory.CreateLogger<T>();
        }
    }

    private sealed class ProbeProvider(ISharedBridge bridge) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(ISharedBridge) ? bridge : null;
        }
    }
}
