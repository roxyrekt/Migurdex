using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Update;
using System.Net;
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

    [Fact]
    public void GetDisplayVersion_DevHidesPlaceholder()
    {
        Assert.StartsWith("dev", AppInfo.GetDisplayVersion());
        Assert.DoesNotContain("0.0.0", AppInfo.GetDisplayVersion());
    }

    [Theory]
    [InlineData("0.0.0+1ff11c0", "1ff11c0")]
    [InlineData("0.0.0+1ff11c06876e1590e5c27324bafce017cf5c295e", "1ff11c0")]
    [InlineData("1.2.3+a89ab71", "a89ab71")]
    [InlineData("0.0.0", "")]
    [InlineData("1.2.3", "")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("0.0.0+", "")]
    public void ParseCommit_ExtractsHash(string? input, string expected)
    {
        Assert.Equal(expected, AppInfo.ParseCommit(input));
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
        var releases = new[] { Release("v2.0.0-beta", true), Release("v1.9.0"), Release("v1.8.0") };

        var picked = ReleaseSelector.SelectRelease(releases, false);

        Assert.NotNull(picked);
        Assert.Equal("1.9.0", picked.Version);
    }

    [Fact]
    public void SelectRelease_PrereleaseChannelPicksNewest()
    {
        var releases = new[] { Release("v2.0.0-beta", true), Release("v1.9.0") };

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

    [Fact]
    public void SelectRelease_SkipsNightlyTag()
    {
        var releases = new[] { Release("nightly", true), Release("v1.0.0") };

        var picked = ReleaseSelector.SelectRelease(releases, true);

        Assert.NotNull(picked);
        Assert.Equal("1.0.0", picked.Version);
    }

    [Theory]
    [InlineData("nightly", true)]
    [InlineData("stable", false)]
    [InlineData("prerelease", false)]
    public void IsNightlyChannel_Classifies(string channel, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNightlyChannel(channel));
    }

    [Fact]
    public async Task NightlyChannel_NewMarker_IsAvailable()
    {
        var service = NightlyService("2026-10-08T12:00:00Z", skippedVersion: "nightly@20261001T000000");

        var result = await service.CheckForUpdatesAsync(true,
                                                        "nightly",
                                                        TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result!.IsUpdateAvailable);
        Assert.Equal("nightly@20261008T120000", result.LatestVersion);
    }

    [Fact]
    public async Task NightlyChannel_SameMarker_NotAvailable()
    {
        var service = NightlyService("2026-10-08T12:00:00Z", skippedVersion: "nightly@20261008T120000");

        var result = await service.CheckForUpdatesAsync(true,
                                                        "nightly",
                                                        TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.IsUpdateAvailable);
    }

    [Fact]
    public async Task NightlyChannel_MissingRelease_ReturnsNull()
    {
        var handler = new NightlyHandler(null);
        var service = new UpdateService(new HttpClient(handler), new NightlyConfig(null));

        var result = await service.CheckForUpdatesAsync(true,
                                                        "nightly",
                                                        TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    private static UpdateService NightlyService(string publishedAt, string? skippedVersion)
    {
        var json = "{\"tag_name\":\"nightly\",\"prerelease\":true,\"draft\":false,"
                   + "\"html_url\":\"https://example.com/nightly\",\"body\":\"nightly build\","
                   + "\"published_at\":\"" + publishedAt + "\","
                   + "\"assets\":[{\"name\":\"migurdex-linux-x64.tar.gz\","
                   + "\"browser_download_url\":\"https://example.com/f\",\"size\":1}]}";
        return new UpdateService(new HttpClient(new NightlyHandler(json)), new NightlyConfig(skippedVersion));
    }

    private sealed class NightlyHandler(string? json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            if (json is null
                || !request.RequestUri!.AbsolutePath.EndsWith("/releases/tags/nightly",
                                                              StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class NightlyConfig(string? skippedVersion) : IConfigurationService
    {
        public CliConfig Config { get; } = new() { UpdateCheckEnabled = true, SkippedVersion = skippedVersion };
        public string ConfigDirectory { get; } = Path.GetTempPath();

        public void Save()
        {
        }

        public void Reload()
        {
        }
    }
}
