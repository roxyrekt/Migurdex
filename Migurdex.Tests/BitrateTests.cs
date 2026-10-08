using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Core.Extractors;
using Migurdex.Core.PluginSystem;
using Migurdex.Core.Utils;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Migurdex.Tests;

public sealed class BitrateTests
{
    [Theory]
    [InlineData(6_200_000L, "6.2 Mbps")]
    [InlineData(8_000_000L, "8 Mbps")]
    [InlineData(800_000L, "800 kbps")]
    [InlineData(1500L, "1.5 kbps")]
    [InlineData(500L, "500 bps")]
    [InlineData(null, "")]
    [InlineData(0L, "")]
    [InlineData(-10L, "")]
    public void FormatBitrate_Formats(long? bitrate, string expected)
    {
        Assert.Equal(expected, VideoSource.FormatBitrate(bitrate));
    }

    [Theory]
    [InlineData("1080p", 6_200_000L, "1080p • 6.2 Mbps")]
    [InlineData("1080p", null, "1080p")]
    [InlineData("", 800_000L, "Çözülemedi • 800 kbps")]
    [InlineData("", null, "Çözülemedi")]
    public void DisplayLabel_CombinesQualityAndBitrate(string quality, long? bitrate, string expected)
    {
        var source = new VideoSource { Quality = quality, Bitrate = bitrate };

        Assert.Equal(expected, source.DisplayLabel);
    }

    [Fact]
    public void DisplayLabel_AppendsCodec()
    {
        var source = new VideoSource { Quality = "1080p", Bitrate = 6_200_000L, VideoCodec = "H.265" };

        Assert.Equal("1080p • 6.2 Mbps • H.265", source.DisplayLabel);
    }

    [Theory]
    [InlineData("avc1", "H.264")]
    [InlineData("hev1", "H.265")]
    [InlineData("hvc1", "H.265")]
    [InlineData("vp09", "VP9")]
    [InlineData("av01", "AV1")]
    [InlineData("mp4a", "AAC")]
    [InlineData("ec-3", "E-AC-3")]
    [InlineData("opus", "Opus")]
    [InlineData("unknown-codec", null)]
    public void MapCodecTag_Maps(string tag, string? expected)
    {
        Assert.Equal(expected, M3U8PlaylistExtractor.MapCodecTag(tag));
    }

    [Fact]
    public void ParseCodecs_SplitsVideoAndAudio()
    {
        var (video, audio) =
            M3U8PlaylistExtractor.ParseCodecs("#EXT-X-STREAM-INF:BANDWIDTH=2805419,CODECS=\"avc1.640028,mp4a.40.2\"");

        Assert.Equal("H.264", video);
        Assert.Equal("AAC", audio);
    }

    [Fact]
    public void ParseCodecs_MissingAttr_ReturnsNulls()
    {
        var (video, audio) = M3U8PlaylistExtractor.ParseCodecs("#EXT-X-STREAM-INF:BANDWIDTH=100");

        Assert.Null(video);
        Assert.Null(audio);
    }

    [Fact]
    public void ParseSegmentDuration_SumsExtinf()
    {
        var playlist = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:8
            #EXTINF:7.976,
            segment0.ts
            #EXTINF:8.008,title
            segment1.ts
            #EXTINF:-5.0,
            segment2.ts
            #EXTINF:abc,
            segment3.ts
            #EXT-X-ENDLIST
            """;

        Assert.Equal(15.984, M3U8PlaylistExtractor.ParseSegmentDuration(playlist)!.Value, 3);
    }

    [Fact]
    public void ParseSegmentDuration_RoundsToMilliseconds()
    {
        var playlist = "#EXTM3U\n#EXTINF:7.976000000001,\nseg0.ts\n#EXTINF:8.008000000001,\nseg1.ts\n";

        Assert.Equal(15.984, M3U8PlaylistExtractor.ParseSegmentDuration(playlist));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#EXTM3U\n#EXT-X-VERSION:3\n")]
    [InlineData("#EXTINF:-5.0,\nseg.ts\n")]
    public void ParseSegmentDuration_NoSegments_ReturnsNull(string playlist)
    {
        Assert.Null(M3U8PlaylistExtractor.ParseSegmentDuration(playlist));
    }

    [Fact]
    public void ParseResolutionFromTkhd_ReturnsDimensions()
    {
        var tkhd = new byte[104];
        tkhd[8]   = 0;
        tkhd[84]  = 0x07;
        tkhd[85]  = 0x80;
        tkhd[88]  = 0x02;
        tkhd[89]  = 0xD0;

        var (width, height) = Mp4MetadataReader.ParseResolutionFromTkhd(tkhd);

        Assert.Equal(1920, width);
        Assert.Equal(720, height);
    }

    [Fact]
    public void ParseResolutionFromTkhd_AudioTrack_ReturnsNulls()
    {
        var tkhd = new byte[104];
        tkhd[8] = 0;

        var (width, height) = Mp4MetadataReader.ParseResolutionFromTkhd(tkhd);

        Assert.Null(width);
        Assert.Null(height);
    }

    [Fact]
    public void ParseDurationFromMvhd_V0_ReturnsSeconds()
    {
        var mvhd = new byte[32];
        mvhd[8]  = 0;
        mvhd[20] = 0;
        mvhd[21] = 0;
        mvhd[22] = 0x03;
        mvhd[23] = 0xE8;
        mvhd[24] = 0;
        mvhd[25] = 0x09;
        mvhd[26] = 0x27;
        mvhd[27] = 0xC0;

        var duration = Mp4MetadataReader.ParseDurationFromMvhd(mvhd);

        Assert.NotNull(duration);
        Assert.Equal(600.0, duration.Value, 3);
    }

    [Fact]
    public void ParseDurationFromMvhd_V1_ReturnsSeconds()
    {
        var mvhd = new byte[40];
        mvhd[8]  = 1;
        mvhd[28] = 0;
        mvhd[29] = 0;
        mvhd[30] = 0x03;
        mvhd[31] = 0xE8;
        mvhd[36] = 0;
        mvhd[37] = 0x09;
        mvhd[38] = 0x27;
        mvhd[39] = 0xC0;

        var duration = Mp4MetadataReader.ParseDurationFromMvhd(mvhd);

        Assert.NotNull(duration);
        Assert.Equal(600.0, duration.Value, 3);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(31)]
    public void ParseDurationFromMvhd_ShortBuffer_ReturnsNull(int length)
    {
        Assert.Null(Mp4MetadataReader.ParseDurationFromMvhd(new byte[length]));
    }

    [Fact]
    public void ParseDurationFromMvhd_UnknownVersion_ReturnsNull()
    {
        var mvhd = new byte[40];
        mvhd[8] = 2;

        Assert.Null(Mp4MetadataReader.ParseDurationFromMvhd(mvhd));
    }

    [Theory]
    [InlineData(480_000_000L, 600.0, 6_400_000L)]
    [InlineData(null, 600.0, null)]
    [InlineData(480_000_000L, null, null)]
    [InlineData(0L, 600.0, null)]
    [InlineData(480_000_000L, 0.0, null)]
    public void ComputeBitrate_Computes(long? size, double? duration, long? expected)
    {
        Assert.Equal(expected, Mp4MetadataReader.ComputeBitrate(size, duration));
    }

    [Fact]
    public void ComputeBitrate_AbsurdValue_ReturnsNull()
    {
        Assert.Null(Mp4MetadataReader.ComputeBitrate(long.MaxValue, 0.001));
    }

    [Fact]
    public void SortVideoSources_SameQuality_PrefersHigherBitrate()
    {
        var config = new CliConfig();
        var low = new VideoSource
        {
            Quality = "1080p",
            Bitrate = 2_500_000,
            Hoster  = "A"
        };
        var high = new VideoSource
        {
            Quality = "1080p",
            Bitrate = 8_000_000,
            Hoster  = "A"
        };

        var sorted = SourceSelector.SortVideoSources([low, high], config);

        Assert.Same(high, sorted[0]);
    }

    [Fact]
    public void SortVideoSources_HigherQuality_WinsOverBitrate()
    {
        var config = new CliConfig();
        var lowRes = new VideoSource
        {
            Quality = "720p",
            Bitrate = 20_000_000,
            Hoster  = "A"
        };
        var highRes = new VideoSource
        {
            Quality = "1080p",
            Bitrate = 2_500_000,
            Hoster  = "A"
        };

        var sorted = SourceSelector.SortVideoSources([lowRes, highRes], config);

        Assert.Same(highRes, sorted[0]);
    }

    [Fact]
    public void SortVideoSources_MissingBitrate_SortsLast()
    {
        var config = new CliConfig();
        var unknown = new VideoSource
        {
            Quality = "1080p",
            Bitrate = null,
            Hoster  = "A"
        };
        var known = new VideoSource
        {
            Quality = "1080p",
            Bitrate = 1_000_000,
            Hoster  = "A"
        };

        var sorted = SourceSelector.SortVideoSources([unknown, known], config);

        Assert.Same(known, sorted[0]);
    }

    [Theory]
    [InlineData("bytes 0-100/1168798546", 1168798546L)]
    [InlineData("bytes 0-100/212244921", 212244921L)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("bytes 0-100/", null)]
    [InlineData("bytes 0-100/0", null)]
    public void ParseTotalSize_Parses(string? contentRange, long? expected)
    {
        Assert.Equal(expected, Mp4MetadataReader.ParseTotalSize(contentRange));
    }

    [Theory]
    [InlineData("ftyp", true)]
    [InlineData("moov", true)]
    [InlineData("mdat", true)]
    [InlineData("free", true)]
    [InlineData("wide", true)]
    [InlineData("skip", true)]
    [InlineData("<htm", false)]
    [InlineData("....", false)]
    public void IsMp4Start_Classifies(string type, bool expected)
    {
        var bytes = new byte[8];
        bytes[4] = (byte) type[0];
        bytes[5] = (byte) type[1];
        bytes[6] = (byte) type[2];
        bytes[7] = (byte) type[3];

        Assert.Equal(expected, Mp4MetadataReader.IsMp4Start(bytes));
    }

    [Fact]
    public void IsMp4Start_ShortBuffer_ReturnsFalse()
    {
        Assert.False(Mp4MetadataReader.IsMp4Start([0x00, 0x00, 0x00]));
    }

    [Fact]
    public async Task ExtractAsync_FillsMissingMp4Bitrate()
    {
        var (manager, _) = GapFillSetup(bitrate: 6_400_000L);
        manager.RegisterExtractor(new GapFillExtractor(
            new VideoSource { Url = "https://example.com/a.mp4", Quality = "720p", Type = VideoType.Mp4 },
            new VideoSource
            {
                Url = "https://example.com/b.m3u8",
                Quality = "1080p",
                Type = VideoType.M3U8
            }));

        var sources = await manager.ExtractAsync("https://example.com/embed",
                                                 cancellationToken: TestContext.Current.CancellationToken);

        var mp4 = sources.Single(s => s.Type == VideoType.Mp4);
        var hls = sources.Single(s => s.Type == VideoType.M3U8);

        Assert.Equal(6_400_000L, mp4.Bitrate);
        Assert.Equal(1280, mp4.Width);
        Assert.Equal(720, mp4.Height);
        Assert.Equal(480_000_000L, mp4.SizeBytes);
        Assert.Equal(600.0, mp4.DurationSeconds);
        Assert.Null(hls.Bitrate);
    }

    [Fact]
    public async Task ExtractAsync_DuplicateUrl_ProbedOnce()
    {
        var (manager, reader) = GapFillSetup(bitrate: 6_400_000L);
        manager.RegisterExtractor(new GapFillExtractor(
            new VideoSource { Url = "https://example.com/a.mp4", Quality = "720p", Type = VideoType.Mp4 },
            new VideoSource { Url = "https://example.com/a.mp4", Quality = "720p", Type = VideoType.Mp4 }));

        var sources = await manager.ExtractAsync("https://example.com/embed",
                                                 cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(6_400_000L, s.Bitrate));
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task ExtractAsync_FillsMissingQuality()
    {
        var (manager, _) = GapFillSetup(bitrate: 6_400_000L);
        manager.RegisterExtractor(new GapFillExtractor(
            new VideoSource { Url = "https://example.com/a.mp4", Type = VideoType.Mp4 }));

        var sources = await manager.ExtractAsync("https://example.com/embed",
                                                 cancellationToken: TestContext.Current.CancellationToken);

        var source = Assert.Single(sources);
        Assert.Equal("720p", source.Quality);
        Assert.Equal(6_400_000L, source.Bitrate);
    }

    [Fact]
    public async Task ExtractAsync_KeepsExistingBitrate()
    {
        var (manager, _) = GapFillSetup(bitrate: 9_000_000L);
        manager.RegisterExtractor(new GapFillExtractor(
            new VideoSource
            {
                Url = "https://example.com/a.mp4",
                Quality = "720p",
                Bitrate = 2_500_000L,
                Type = VideoType.Mp4
            }));

        var sources = await manager.ExtractAsync("https://example.com/embed",
                                                 cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2_500_000L, sources.Single().Bitrate);
    }

    [Fact]
    public void Merge_TransfersAllScalarProperties()
    {
        var extracted = new VideoSource
        {
            Url             = "https://example.com/v.mp4",
            Quality         = "1080p",
            Bitrate         = 6_400_000L,
            Width           = 1920,
            Height          = 1080,
            VideoCodec      = "H.264",
            AudioCodec      = "AAC",
            SizeBytes       = 480_000_000L,
            DurationSeconds = 600.0,
            Type            = VideoType.Mp4,
            Hoster          = "exth",
            Group           = "extg",
            Language        = "extl"
        };
        var raw = new VideoSource
        {
            Url             = "https://example.com/embed",
            Quality         = "Auto",
            Bitrate         = 1L,
            Width           = 1,
            Height          = 1,
            VideoCodec      = "rawvc",
            AudioCodec      = "rawac",
            SizeBytes       = 1L,
            DurationSeconds = 1.0,
            Type            = VideoType.Embed,
            Hoster          = "rawh",
            Group           = "rawg",
            Language        = "rawl"
        };

        var merged = VideoSourceMerger.Merge(extracted, raw);

        foreach (var prop in typeof(VideoSource).GetProperties(System.Reflection.BindingFlags.Public
                                                               | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.Name is nameof(VideoSource.Headers) or nameof(VideoSource.Subtitles))
            {
                continue;
            }

            Assert.Equal(prop.GetValue(extracted), prop.GetValue(merged));
        }
    }

    [Fact]
    public void Merge_FallsBackToRawMetadata()
    {
        var extracted = new VideoSource { Url = "https://example.com/v.mp4", Quality = "720p", Type = VideoType.Mp4 };
        var raw = new VideoSource
        {
            Url      = "https://example.com/embed",
            Hoster   = "rawh",
            Group    = "rawg",
            Language = "rawl",
            Headers  = new Dictionary<string, string> { { "Referer", "https://raw.example/" } }
        };

        var merged = VideoSourceMerger.Merge(extracted, raw);

        Assert.Equal("rawh", merged.Hoster);
        Assert.Equal("rawg", merged.Group);
        Assert.Equal("rawl", merged.Language);
        Assert.Equal("https://raw.example/", merged.Headers!["Referer"]);
        Assert.Equal("720p", merged.Quality);
    }

    [Fact]
    public void Merge_PrefersExtractedCollections()
    {
        var extracted = new VideoSource
        {
            Url       = "https://example.com/v.mp4",
            Headers   = new Dictionary<string, string> { { "Referer", "https://ext.example/" } },
            Subtitles = [new Subtitle { Url = "https://example.com/s.vtt", Language = "en" }]
        };
        var raw = new VideoSource
        {
            Url       = "https://example.com/embed",
            Headers   = new Dictionary<string, string> { { "Referer", "https://raw.example/" } },
            Subtitles = [new Subtitle { Url = "https://example.com/r.vtt", Language = "tr" }]
        };

        var merged = VideoSourceMerger.Merge(extracted, raw);

        Assert.Equal("https://ext.example/", merged.Headers!["Referer"]);
        Assert.Single(merged.Subtitles!);
        Assert.Equal("https://example.com/s.vtt", merged.Subtitles![0].Url);
    }

    [Theory]
    [InlineData("#EXT-X-STREAM-INF:BANDWIDTH=2805419,RESOLUTION=1920x1080", 2805419L)]
    [InlineData("#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=1200000,BANDWIDTH=2805419", 1200000L)]
    [InlineData("#EXT-X-STREAM-INF:BANDWIDTH=99999999999", null)]
    [InlineData("#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=99999999999,BANDWIDTH=2805419", 2805419L)]
    [InlineData("#EXT-X-STREAM-INF:RESOLUTION=1920x1080", null)]
    public void ParseBandwidth_CapsAbsurdValues(string line, long? expected)
    {
        Assert.Equal(expected, M3U8PlaylistExtractor.ParseBandwidth(line));
    }

    [Fact]
    public async Task ExtractAsync_DropsSizeOnlyMp4()
    {
        var (manager, _) = GapFillSetup(empty: true);
        manager.RegisterExtractor(new GapFillExtractor(
            new VideoSource { Url = "https://example.com/stub.mp4", SizeBytes = 946746L, Type = VideoType.Mp4 }));

        var sources = await manager.ExtractAsync("https://example.com/embed",
                                                 cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(sources);
    }

    [Fact]
    public async Task ExtractAsync_DropsUnprobableMp4()
    {
        var (manager, _) = GapFillSetup(empty: true);
        manager.RegisterExtractor(new GapFillExtractor(
            new VideoSource { Url = "https://example.com/dead.mp4", Type = VideoType.Mp4 }));

        var sources = await manager.ExtractAsync("https://example.com/embed",
                                                 cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(sources);
    }

    [Fact]
    public async Task ExtractAsync_KeepsLabeledSourceOnFillFailure()
    {
        var (manager, _) = GapFillSetup(fail: true);
        manager.RegisterExtractor(new GapFillExtractor(
            new VideoSource { Url = "https://example.com/a.mp4", Quality = "720p", Type = VideoType.Mp4 }));

        var sources = await manager.ExtractAsync("https://example.com/embed",
                                                 cancellationToken: TestContext.Current.CancellationToken);

        var source = Assert.Single(sources);
        Assert.Null(source.Bitrate);
        Assert.Equal("720p", source.Quality);
    }

    private static (ExtractorManager Manager, GapFillReader Reader) GapFillSetup(long? bitrate = null,
        bool                                                                            fail    = false,
        bool                                                                            empty   = false)
    {
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var loader = new PluginLoader(new GapFillBridge(factory),
                                       factory.CreateLogger<PluginLoader>(),
                                       factory);
        var reader = new GapFillReader(bitrate, fail, empty);
        return (new ExtractorManager(factory.CreateLogger<ExtractorManager>(), reader, loader), reader);
    }

    private sealed class GapFillExtractor(params VideoSource[] sources) : IExtractor
    {
        public string Name => "GapFill";

        public bool CanExtract(string url)
        {
            return true;
        }

        public Task<List<VideoSource>> ExtractAsync(string url,
            IDictionary<string, string>?               headers           = null,
            CancellationToken                          cancellationToken = default)
        {
            return Task.FromResult(sources.ToList());
        }
    }

    private sealed class GapFillBridge(ILoggerFactory factory) : ISharedBridge
    {
        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();
        public ILoggerFactory LoggerFactory => factory;

        public HttpClient CreateHttpClient(HttpClientOptions? options = null)
        {
            return new HttpClient();
        }

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
        {
            return new HttpClient();
        }

        public ILogger<T> CreateLogger<T>()
        {
            return factory.CreateLogger<T>();
        }
    }

    private sealed class GapFillReader(long? bitrate, bool fail, bool empty) : IMp4MetadataReader
    {
        public int Calls;
        public Task<string> GetVideoQualityAsync(string videoUrl,
            string?                                     referer           = null,
            string?                                     userAgent         = null,
            CancellationToken                           cancellationToken = default)
        {
            return Task.FromResult("Auto");
        }

        public Task<string> GetVideoQualityAsync(string videoUrl,
            Dictionary<string, string>                        headers,
            CancellationToken                                 cancellationToken = default)
        {
            return Task.FromResult("Auto");
        }

        public Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
            string?                                                          referer           = null,
            string?                                                          userAgent         = null,
            CancellationToken                                                cancellationToken = default)
        {
            return Metadata();
        }

        public Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
            Dictionary<string, string>                             headers,
            CancellationToken                                      cancellationToken = default)
        {
            return Metadata();
        }

        private Task<Mp4Metadata> Metadata()
        {
            Interlocked.Increment(ref Calls);

            if (fail)
            {
                return Task.FromException<Mp4Metadata>(new HttpRequestException("probe failed"));
            }

            return empty
                       ? Task.FromResult(new Mp4Metadata("Auto", null, null, null))
                       : Task.FromResult(new Mp4Metadata("720p", bitrate, 480_000_000L, 600.0, 1280, 720));
        }
    }
}
