using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

public sealed class EpisodeRangeParserTests
{
    [Theory]
    [InlineData("12", new[] { 12.0 })]
    [InlineData("1,3,5", new[] { 1.0, 3.0, 5.0 })]
    [InlineData("1-3", new[] { 1.0, 2.0, 3.0 })]
    [InlineData("1..3", new[] { 1.0, 2.0, 3.0 })]
    [InlineData("1-3,5", new[] { 1.0, 2.0, 3.0, 5.0 })]
    [InlineData("3,1", new[] { 1.0, 3.0 })]
    [InlineData("1,1,2", new[] { 1.0, 2.0 })]
    public void TryParse_ExpandsListsAndRanges(string input, double[] expected)
    {
        Assert.True(EpisodeRangeParser.TryParse(input, out var episodes, out var selectAll));
        Assert.False(selectAll);
        Assert.Equal(expected, episodes);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("  All  ")]
    public void TryParse_RecognizesAllAlias(string input)
    {
        Assert.True(EpisodeRangeParser.TryParse(input, out _, out var selectAll));
        Assert.True(selectAll);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("5-1")]
    public void TryParse_RejectsInvalidInput(string input)
    {
        Assert.False(EpisodeRangeParser.TryParse(input, out _, out _));
    }

    [Fact]
    public void TryParse_DownloadCommandAcceptsNewFlags()
    {
        Assert.True(DownloadCommand.TryParse(
                        ["naruto", "-e", "1,3", "--hoster", "Tau Video", "-q", "1080p"],
                        out var options,
                        out var error));
        Assert.Equal(new List<double>
                     {
                         1,
                         3
                     },
                     options.Episodes);
        Assert.Equal("Tau Video", options.Hoster);
        Assert.Equal("1080p", options.Quality);

        Assert.True(DownloadCommand.TryParse(
                        ["naruto", "--all", "-s", "2"],
                        out var allOptions,
                        out _));
        Assert.True(allOptions.SelectAllEpisodes);
        Assert.Equal(2, allOptions.Season);
    }
}
