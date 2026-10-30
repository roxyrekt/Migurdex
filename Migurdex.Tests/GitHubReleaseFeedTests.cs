using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

public sealed class GitHubReleaseFeedTests
{
    private static Stream OpenFixture(string name)
    {
        return File.OpenRead(Path.Combine(AppContext.BaseDirectory, name));
    }

    [Fact]
    public async Task ParseAtom_ReadsTagsDatesAndNotes()
    {
        await using var fs = OpenFixture("releases.atom");

        var entries = await GitHubReleaseFeed.ParseAtomAsync(fs, TestContext.Current.CancellationToken);

        Assert.Equal(3, entries.Count);

        var stable = entries[0];
        Assert.Equal("v1.17.0", stable.Tag);
        Assert.False(stable.IsPrerelease);
        Assert.False(stable.IsNightly);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 4, 9, 44, TimeSpan.Zero), stable.UpdatedAt);
        Assert.StartsWith("https://github.com/roxyrekt/Migurdex/releases/tag/v1.17.0", stable.HtmlUrl);

        Assert.Contains("What's Changed", stable.Notes);
        Assert.Contains("- ", stable.Notes);
        Assert.DoesNotContain("<li>", stable.Notes);
        Assert.DoesNotContain("&lt;", stable.Notes);
    }

    [Fact]
    public async Task ParseAtom_IdentifiesNightlyEntry()
    {
        await using var fs = OpenFixture("releases.atom");

        var entries = await GitHubReleaseFeed.ParseAtomAsync(fs, TestContext.Current.CancellationToken);

        var nightly = Assert.Single(entries, e => e.IsNightly);
        Assert.Equal("nightly", nightly.Tag);

        Assert.True(nightly.UpdatedAt > new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseExpandedAssets_ReadsNamesSizesAndUrls()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "expanded_assets.html"));

        var assets = GitHubReleaseFeed.ParseExpandedAssets(html);

        Assert.Equal(4, assets.Count);

        var appImage = assets[0];
        Assert.Equal("migurdex-aarch64.AppImage", appImage.Name);
        Assert.Equal("https://github.com/roxyrekt/Migurdex/releases/download/v1.17.0/migurdex-aarch64.AppImage",
                     appImage.BrowserDownloadUrl);
        Assert.InRange(appImage.Size, 50L * 1024 * 1024, 60L * 1024 * 1024);

        var zsync = assets[1];
        Assert.Equal("migurdex-aarch64.AppImage.zsync", zsync.Name);
        Assert.InRange(zsync.Size, 100L * 1024, 500L * 1024);

        Assert.Contains(assets, a => a.Name == "migurdex-linux-x64.tar.gz");
        Assert.Contains(assets, a => a.Name == "migurdex-linux-arm64.tar.gz");
    }

    [Fact]
    public void ParseExpandedAssets_EmptyPageYieldsNothing()
    {
        Assert.Empty(GitHubReleaseFeed.ParseExpandedAssets("<html><body>Not Found</body></html>"));
    }

    [Fact]
    public void HtmlToText_StripsTagsAndDecodesEntities()
    {
        var html = "<h2>What&#39;s Changed</h2><ul><li>fix by <a href=\"x\">@user</a></li></ul>";

        var text = GitHubReleaseFeed.HtmlToText(html);

        Assert.Contains("What's Changed", text);
        Assert.Contains("- fix by @user", text);
        Assert.DoesNotContain("<", text);
    }
}
