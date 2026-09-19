using Migurdex.Shared.Update;
using Xunit;

namespace Migurdex.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V2.0.0", "2.0.0")]
    [InlineData("  v1.0  ", "1.0")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeTag_StripsPrefix(string? input, string expected)
    {
        Assert.Equal(expected, AppInfo.NormalizeTag(input));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.2.4", "1.2.3", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("2.0", "1.9.9", 1)]
    [InlineData("1.2.3-beta", "1.2.3", -1)]
    [InlineData("1.2.3", "1.2.3-beta", 1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.2", -1)]
    [InlineData("1.2.3-1", "1.2.3-alpha", -1)]
    [InlineData("1.2.3+build.5", "1.2.3", 0)]
    [InlineData("", "1.0.0", -1)]
    [InlineData("1.0.0", "", 1)]
    public void CompareVersions_OrdersCorrectly(string a, string b, int expected)
    {
        Assert.Equal(Math.Sign(expected), Math.Sign(AppInfo.CompareVersions(a, b)));
    }

    private static GitHubRelease Release(string tag, bool pre = false, bool draft = false)
    {
        return new GitHubRelease(tag, pre, draft, $"https://example.com/{tag}", string.Empty, []);
    }

    [Fact]
    public void SelectRelease_StableSkipsPrerelease()
    {
        var releases = new[] { Release("v2.0.0-beta", pre: true), Release("v1.9.0"), Release("v1.8.0") };

        var picked = ReleaseSelector.SelectRelease(releases, false);

        Assert.NotNull(picked);
        Assert.Equal("1.9.0", picked.Version);
    }

    [Fact]
    public void SelectRelease_PrereleaseChannelPicksNewest()
    {
        var releases = new[] { Release("v2.0.0-beta", pre: true), Release("v1.9.0") };

        var picked = ReleaseSelector.SelectRelease(releases, true);

        Assert.NotNull(picked);
        Assert.Equal("2.0.0-beta", picked.Version);
    }

    [Fact]
    public void SelectRelease_SkipsDrafts()
    {
        var releases = new[] { Release("v3.0.0", draft: true), Release("v2.0.0") };

        var picked = ReleaseSelector.SelectRelease(releases, true);

        Assert.NotNull(picked);
        Assert.Equal("2.0.0", picked.Version);
    }

    [Fact]
    public void SelectRelease_EmptyReturnsNull()
    {
        Assert.Null(ReleaseSelector.SelectRelease([], true));
        Assert.Null(ReleaseSelector.SelectRelease([], false));
    }
}
