using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadCommandTests
{
    [Fact]
    public void TryParse_ReadsCompleteContractWithInvariantNumbers()
    {
        var parsed = DownloadCommand.TryParse(
            [
                "one", "piece", "-e", "12.5", "-s", "2", "-p", "TurkAnime", "-g", "Fansub",
                "-o", "D:\\Anime", "--format", "hls", "--no-subs", "--force", "--no-resume", "--debug", "--json"
            ],
            out var options,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal("one piece", options.Query);
        Assert.Equal(12.5, options.Episode);
        Assert.Equal(2, options.Season);
        Assert.Equal("TurkAnime", options.Provider);
        Assert.Equal("Fansub", options.Group);
        Assert.Equal("D:\\Anime", options.OutputDirectory);
        Assert.Equal(DownloadSourceFormat.Hls, options.Format);
        Assert.False(options.Subtitles);
        Assert.True(options.Force);
        Assert.True(options.NoResume);
        Assert.True(options.Debug);
        Assert.True(options.Json);
    }

    [Fact]
    public void TryParse_EpisodeIsOptionalAndDefaultsAreConservative()
    {
        var parsed = DownloadCommand.TryParse(["naruto", "--subs"], out var options, out var error);

        Assert.True(parsed, error);
        Assert.Equal("naruto", options.Query);
        Assert.Null(options.Episode);
        Assert.Null(options.Season);
        Assert.Equal(DownloadSourceFormat.Auto, options.Format);
        Assert.True(options.Subtitles);
        Assert.False(options.Force);
        Assert.False(options.NoResume);
    }

    [Theory]
    [InlineData("--format", "webm")]
    [InlineData("-e", "not-a-number")]
    [InlineData("-e", "0")]
    [InlineData("-s", "0")]
    public void TryParse_RejectsInvalidDownloadOptions(string first, string second)
    {
        var parsed = DownloadCommand.TryParse(["anime", first, second], out _, out var error);

        Assert.False(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_RejectsSubtitleContradiction()
    {
        var parsed = DownloadCommand.TryParse(["anime", "--subs", "--no-subs"], out _, out var error);

        Assert.False(parsed);
        Assert.Contains("--subs", error, StringComparison.Ordinal);
    }

    [Fact]
    public void IsDirectDownloadable_OnlyAcceptsHttpMp4AndM3u8()
    {
        Assert.True(DownloadSourceResolver.IsDirectDownloadable(
            Source("https://origin.example/video.mp4", VideoType.Mp4)));
        Assert.True(DownloadSourceResolver.IsDirectDownloadable(
            Source("https://origin.example/video.m3u8", VideoType.M3U8)));
        Assert.False(DownloadSourceResolver.IsDirectDownloadable(
            Source("https://origin.example/embed", VideoType.Embed)));
        Assert.False(DownloadSourceResolver.IsDirectDownloadable(
            Source("https://origin.example/unknown", VideoType.Unknown)));
        Assert.False(DownloadSourceResolver.IsDirectDownloadable(
            Source("file:///tmp/video.mp4", VideoType.Mp4)));
    }

    [Fact]
    public void SelectCandidates_FiltersDirectTypesAndCapsFallbackAtThree()
    {
        var sources = new List<VideoSource>
        {
            Source("https://origin.example/1080.mp4", VideoType.Mp4, "1080p", "A"),
            Source("https://origin.example/720.mp4", VideoType.Mp4, "720p", "B"),
            Source("https://origin.example/480.mp4", VideoType.Mp4, "480p", "C"),
            Source("https://origin.example/360.mp4", VideoType.Mp4, "360p", "D"),
            Source("https://origin.example/embed", VideoType.Embed, "2160p", "E"),
            Source("https://origin.example/unknown", VideoType.Unknown, "4320p", "F"),
            Source("file:///etc/passwd", VideoType.Mp4, "4320p", "G")
        };

        var candidates = DownloadSourceResolver.SelectCandidates(sources,
                                                                 DownloadSourceFormat.Mp4,
                                                                 new CliConfig());

        Assert.Equal(3, candidates.Count);
        Assert.Equal(new[] { "1080p", "720p", "480p" }, candidates.Select(source => source.Quality));
        Assert.All(candidates, source => Assert.Equal(VideoType.Mp4, source.Type));
    }

    [Fact]
    public void SelectCandidates_ExplicitHlsKeepsOnlyM3u8Sources()
    {
        var sources = new List<VideoSource>
        {
            Source("https://origin.example/mp4", VideoType.Mp4, "1080p", "A"),
            Source("https://origin.example/hls", VideoType.M3U8, "720p", "B")
        };

        var selected = Assert.Single(DownloadSourceResolver.SelectCandidates(sources,
                                                                               DownloadSourceFormat.Hls,
                                                                               new CliConfig()));

        Assert.Equal("https://origin.example/hls", selected.Url);
        Assert.Equal(VideoType.M3U8, selected.Type);
    }

    [Fact]
    public void SelectCandidates_AutoUsesFormatPriorityAndEligibility()
    {
        var config = new CliConfig();
        config.PreferredFormatOrder = ["M3U8", "Mp4"];
        var sources = new List<VideoSource>
        {
            Source("https://origin.example/mp4", VideoType.Mp4, "1080p", "A"),
            Source("https://origin.example/hls", VideoType.M3U8, "1080p", "B")
        };

        var candidates = DownloadSourceResolver.SelectCandidates(sources,
                                                                 DownloadSourceFormat.Auto,
                                                                 config);

        Assert.Equal(VideoType.M3U8, candidates[0].Type);
    }

    [Fact]
    public void SelectCandidates_AutoFallsBackFromFilteredPoolLikeSourceSelector()
    {
        var config = new CliConfig();
        config.AutoNeverTypes = ["M3U8"];
        var sources = new List<VideoSource>
        {
            Source("https://origin.example/hls", VideoType.M3U8, "1080p", "A"),
            Source("https://origin.example/mp4", VideoType.Mp4, "720p", "B")
        };

        var selected = Assert.Single(DownloadSourceResolver.SelectCandidates(sources,
                                                                               DownloadSourceFormat.Auto,
                                                                               config));
        Assert.Equal(VideoType.Mp4, selected.Type);
    }

    [Fact]
    public void PickEpisode_IsDeterministicAndDoesNotDependOnInputOrder()
    {
        var details = new AnimeDetails
        {
            Episodes =
            [
                new Episode { Id = "s2e1", Season = 2, Number = 1 },
                new Episode { Id = "s1e3", Season = 1, Number = 3 },
                new Episode { Id = "s1e1", Season = 1, Number = 1 },
                new Episode { Id = "s1e2", Season = 1, Number = 2 }
            ]
        };

        Assert.Equal("s1e1", MediaSelection.PickEpisode(details, null, null)?.Id);
        Assert.Equal("s2e1", MediaSelection.PickEpisode(details, 2, null)?.Id);
        Assert.Equal("s1e3", MediaSelection.PickEpisode(details, null, 3)?.Id);
        Assert.Null(MediaSelection.PickEpisode(details, 3, null));
    }

    [Fact]
    public void ProviderAndGroupValidation_ResolveKnownValuesAndRejectUnknown()
    {
        var providers = new List<ProviderInfo>
        {
            new() { Name = "TurkAnime" },
            new() { Name = "AniHub" }
        };

        Assert.True(MediaSelection.TryResolveProvider(providers,
                                                             "turkanime",
                                                             out var provider,
                                                             out var providerError));
        Assert.Equal("TurkAnime", provider);
        Assert.Null(providerError);
        Assert.True(MediaSelection.TryResolveProvider(providers,
                                                             "Turk",
                                                             out var partialProvider,
                                                             out _));
        Assert.Equal("TurkAnime", partialProvider);
        Assert.False(MediaSelection.TryResolveProvider(providers,
                                                              "xyz",
                                                              out _,
                                                              out _));

        Assert.True(MediaSelection.TryResolveGroup(["Fansub", "Duals"], "fansub", out var group));
        Assert.Equal("Fansub", group);
        Assert.False(MediaSelection.TryResolveGroup(["Fansub"], "dual", out _));
    }

    [Fact]
    public void CliConfig_OldOrNullDownloadValuesUseSafeDefaults()
    {
        var oldConfig = JsonSerializer.Deserialize<CliConfig>("{}");
        Assert.NotNull(oldConfig);
        Assert.Equal(CliConfig.DefaultDownloadDirectory, oldConfig.DownloadDirectory);
        Assert.Equal("yt-dlp", oldConfig.YtDlpPath);
        Assert.True(oldConfig.DownloadSubtitles);
        Assert.True(oldConfig.DownloadResume);
        Assert.False(oldConfig.DownloadOverwrite);

        var nullConfig = JsonSerializer.Deserialize<CliConfig>(
            "{\"DownloadDirectory\":null,\"YtDlpPath\":\" \"}");
        Assert.NotNull(nullConfig);
        Assert.Equal(CliConfig.DefaultDownloadDirectory, nullConfig.DownloadDirectory);
        Assert.Equal("yt-dlp", nullConfig.YtDlpPath);
    }

    private static VideoSource Source(string url,
        VideoType                  type,
        string                     quality = "1080p",
        string?                    hoster  = "Hoster")
    {
        return new VideoSource
        {
            Url     = url,
            Type    = type,
            Quality = quality,
            Hoster  = hoster
        };
    }
}
