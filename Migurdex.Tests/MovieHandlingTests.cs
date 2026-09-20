using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class MovieHandlingTests
{
    [Theory]
    [InlineData("One Piece Film: Red", true)]
    [InlineData("One Piece Movie 14: Stampede", true)]
    [InlineData("Kimetsu no Yaiba: Mugen Ressha-hen (Movie)", true)]
    [InlineData("Gekijouban Jujutsu Kaisen 0", true)]
    [InlineData("Evangelion: 3.0+1.0 Thrice Upon a Time (Film)", true)]
    [InlineData("Naruto Shippuden", false)]
    [InlineData("One Piece", false)]
    [InlineData("Bleach: Sennen Kessen-hen", false)]
    public void IsMovieTitle_Detects_Movies_Accurately(string title, bool expected)
    {
        var result = AnimeDetails.IsMovieTitle(title);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("One Piece serisinin dördüncü filmidir. Luffy ve tayfası...", true)]
    [InlineData("Bu anime bir film uyarlamasıdır.", true)]
    [InlineData("The third movie of the series.", true)]
    [InlineData("Luffy ve arkadaşları korsanlar kralı olmak için denize açılır.", false)]
    public void IsMovieSummary_Detects_Keywords_Accurately(string summary, bool expected)
    {
        var result = AnimeDetails.IsMovieSummary(summary);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_Promotes_Movie_When_Title_Has_No_Movie_Word_But_Summary_Says_Filmidir()
    {
        var details = new AnimeDetails
        {
            Title = "One Piece: Chopper's Kingdom on the Island of Strange Animals",
            Summary =
                "One Piece: Chopper's Kingdom on the Island of Strange Animals, One Piece serisinin dördüncü filmidir. Luffy ve tayfası...",
            Format = ContentFormat.Tv,
            Episodes =
            [
                new Episode
                {
                    Id     = "ep-1",
                    Number = 1,
                    Title  = "1. Bölüm",
                    Season = 1
                }
            ]
        };

        details.Normalize();

        Assert.Equal(ContentFormat.Movie, details.Format);
        Assert.Single(details.Episodes);
        Assert.Equal(1, details.Episodes[0].Number);
        Assert.Equal(1, details.Episodes[0].Season);
        Assert.Equal("Film", details.Episodes[0].Title);
    }

    [Fact]
    public void Normalize_Promotes_Movie_Title_And_Normalizes_Single_Episode()
    {
        var details = new AnimeDetails
        {
            Title  = "One Piece Film: Red",
            Format = ContentFormat.Tv,
            Episodes =
            [
                new Episode
                {
                    Id     = "ep-1",
                    Number = 1,
                    Title  = "1. Bölüm",
                    Season = 1
                }
            ]
        };

        details.Normalize();

        Assert.Equal(ContentFormat.Movie, details.Format);
        Assert.Single(details.Episodes);
        Assert.Equal(1, details.Episodes[0].Number);
        Assert.Equal(1, details.Episodes[0].Season);
        Assert.Equal("Film", details.Episodes[0].Title);
    }

    [Fact]
    public void Normalize_Adds_Movie_Episode_When_Empty()
    {
        var details = new AnimeDetails
        {
            Title    = "Kimi no Na wa.",
            Format   = ContentFormat.Movie,
            Episodes = []
        };

        details.Normalize();

        Assert.Single(details.Episodes);
        Assert.Equal(1, details.Episodes[0].Number);
        Assert.Equal(1, details.Episodes[0].Season);
        Assert.Equal("Film", details.Episodes[0].Title);
    }

    [Fact]
    public void Normalize_Leaves_Tv_Series_Untouched()
    {
        var details = new AnimeDetails
        {
            Title  = "Attack on Titan",
            Format = ContentFormat.Tv,
            Episodes =
            [
                new Episode
                {
                    Id     = "ep-1",
                    Number = 1,
                    Title  = "1. Bölüm",
                    Season = 1
                },
                new Episode
                {
                    Id     = "ep-2",
                    Number = 2,
                    Title  = "2. Bölüm",
                    Season = 1
                }
            ]
        };

        details.Normalize();

        Assert.Equal(ContentFormat.Tv, details.Format);
        Assert.Equal(2, details.Episodes.Count);
        Assert.Equal("1. Bölüm", details.Episodes[0].Title);
    }
}
