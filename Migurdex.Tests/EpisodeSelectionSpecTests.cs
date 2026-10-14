using Migurdex.Cli.Services;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class EpisodeSelectionSpecTests
{
    [Theory]
    [InlineData("1,2,3")]
    [InlineData("1-12")]
    [InlineData("1-3,7,9-10")]
    [InlineData(" 12 ")]
    [InlineData("all")]
    [InlineData("tümü")]
    [InlineData("*")]
    public void TryParse_AcceptsSupportedSpecs(string spec)
    {
        Assert.True(EpisodeSelectionSpec.TryParse(spec, out var parsed, out var error), error);
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("1,x")]
    [InlineData("12-3")]
    [InlineData("1-")]
    [InlineData("-")]
    public void TryParse_RejectsInvalidSpecs(string spec)
    {
        Assert.False(EpisodeSelectionSpec.TryParse(spec, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_AllKeywordSetsTheIncludesAllFlag()
    {
        Assert.True(EpisodeSelectionSpec.TryParse("all", out var spec, out _));
        Assert.True(spec!.IncludesAll);
        Assert.Empty(spec.Ranges);

        Assert.True(EpisodeSelectionSpec.TryParse("1-12", out var rangeSpec, out _));
        Assert.False(rangeSpec!.IncludesAll);
        Assert.Single(rangeSpec.Ranges);
        Assert.Equal((1d, 12d), rangeSpec.Ranges[0]);
    }

    [Fact]
    public void TryResolve_ReturnsOnlyRequestedEpisodesInSeasonOrder()
    {
        var details = Details(
            new Episode { Id = "s1e3", Season = 1, Number = 3 },
            new Episode { Id = "s1e1", Season = 1, Number = 1 },
            new Episode { Id = "s1e2", Season = 1, Number = 2 });

        Assert.True(EpisodeSelectionSpec.TryParse("2-3", out var spec, out _));
        Assert.True(spec!.TryResolve(details.Episodes, null, out var chosen, out var error), error);

        Assert.Equal(new[] { "s1e2", "s1e3" }, chosen.Select(episode => episode.Id));
    }

    [Fact]
    public void TryResolve_HonoursTheSeasonFilter()
    {
        var details = Details(
            new Episode { Id = "s1e1", Season = 1, Number = 1 },
            new Episode { Id = "s2e1", Season = 2, Number = 1 },
            new Episode { Id = "s2e2", Season = 2, Number = 2 });

        Assert.True(EpisodeSelectionSpec.TryParse("1-2", out var spec, out _));
        Assert.True(spec!.TryResolve(details.Episodes, 2, out var chosen, out _));

        Assert.Equal(new[] { "s2e1", "s2e2" }, chosen.Select(episode => episode.Id));
    }

    [Fact]
    public void TryResolve_MultiSeasonNumberListWithoutSeason_IsRejectedInsteadOfGuessing()
    {
        var details = Details(
            new Episode { Id = "s1e1", Season = 1, Number = 1 },
            new Episode { Id = "s1e2", Season = 1, Number = 2 },
            new Episode { Id = "s2e1", Season = 2, Number = 1 },
            new Episode { Id = "s2e2", Season = 2, Number = 2 });

        Assert.True(EpisodeSelectionSpec.TryParse("1-2", out var spec, out _));

        Assert.False(spec!.TryResolve(details.Episodes, null, out var chosen, out var error));
        Assert.Empty(chosen);
        Assert.Contains("-s", error!, StringComparison.Ordinal);
        Assert.Contains("all", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_AllKeywordCoversEverySeasonWhenNoSeasonGiven()
    {
        var details = Details(
            new Episode { Id = "s1e1", Season = 1, Number = 1 },
            new Episode { Id = "s1e2", Season = 1, Number = 2 },
            new Episode { Id = "s2e1", Season = 2, Number = 1 },
            new Episode { Id = "s1e2-duplicate", Season = 1, Number = 2 });

        Assert.True(EpisodeSelectionSpec.TryParse("all", out var spec, out _));
        Assert.True(spec!.TryResolve(details.Episodes, null, out var chosen, out _));

        Assert.True(spec.IncludesAll);
        Assert.Equal(new[] { "s1e1", "s1e2", "s2e1" }, chosen.Select(episode => episode.Id));
    }

    [Fact]
    public void TryResolve_AllKeywordWithSeasonStaysInsideThatSeason()
    {
        var details = Details(
            new Episode { Id = "s1e1", Season = 1, Number = 1 },
            new Episode { Id = "s2e1", Season = 2, Number = 1 },
            new Episode { Id = "s2e2", Season = 2, Number = 2 });

        Assert.True(EpisodeSelectionSpec.TryParse("all", out var spec, out _));
        Assert.True(spec!.TryResolve(details.Episodes, 2, out var chosen, out _));

        Assert.Equal(new[] { "s2e1", "s2e2" }, chosen.Select(episode => episode.Id));
    }

    [Fact]
    public void TryResolve_NonMatchingNumbersReturnEmptyListInsteadOfThrowing()
    {
        var details = Details(new Episode { Id = "s1e1", Season = 1, Number = 1 });

        Assert.True(EpisodeSelectionSpec.TryParse("99-120", out var spec, out _));
        Assert.True(spec!.TryResolve(details.Episodes, null, out var chosen, out _));

        Assert.Empty(chosen);
        Assert.Equal("99-120", spec.Describe());
    }

    [Fact]
    public void TryResolve_SupportsFractionalEpisodeNumbers()
    {
        var details = Details(
            new Episode { Id = "e12", Season = 1, Number = 12 },
            new Episode { Id = "e125", Season = 1, Number = 12.5 });

        Assert.True(EpisodeSelectionSpec.TryParse("12.5", out var spec, out _));
        Assert.True(spec!.TryResolve(details.Episodes, null, out var chosen, out _));

        Assert.Equal("e125", Assert.Single(chosen).Id);
    }

    private static AnimeDetails Details(params Episode[] episodes)
    {
        return new AnimeDetails
        {
            Title    = "Anime",
            Episodes = [.. episodes]
        };
    }
}
