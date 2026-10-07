using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class CanonicalAnimeTests
{
    [Fact]
    public void FromMetadata_MyAnimeListSource_MapsMalId()
    {
        var m = new MediaMetadata
        {
            ExternalId = "38000",
            Source = MetadataSource.MyAnimeList,
            Title = "Kimetsu no Yaiba",
        };

        var c = CanonicalAnime.FromMetadata(m);

        Assert.Equal("38000", c.MyAnimeListId);
        Assert.Null(c.AniListId);
        Assert.Equal("mal:38000", c.CanonicalId);
    }

    [Fact]
    public void FromMetadata_JikanSource_MapsMalId()
    {
        var m = new MediaMetadata
        {
            ExternalId = "13",
            Source = MetadataSource.Jikan,
            Title = "One Piece",
        };

        var c = CanonicalAnime.FromMetadata(m);

        Assert.Equal("13", c.MyAnimeListId);
        Assert.Equal("mal:13", c.CanonicalId);
    }

    [Fact]
    public void FromMetadata_AniListSource_PrefersAnilistId()
    {
        var m = new MediaMetadata
        {
            ExternalId = "21",
            Source = MetadataSource.AniList,
            Title = "One Piece",
            AniListId = "21",
            MyAnimeListId = "13",
        };

        var c = CanonicalAnime.FromMetadata(m);

        Assert.Equal("21", c.AniListId);
        Assert.Equal("13", c.MyAnimeListId);
        Assert.Equal("anilist:21", c.CanonicalId);
    }
}
