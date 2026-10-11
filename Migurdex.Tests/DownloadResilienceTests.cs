using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadResilienceTests
{
    [Theory]
    [InlineData("404 not found")]
    [InlineData("Video indirilebilir değil: embed")]
    [InlineData("Uygun kaynak bulunamadı.")]
    [InlineData("Grup bulunamadı: Next.")]
    [InlineData("403 forbidden")]
    [InlineData("Hiçbir biçiminde uygun kaynak bulunamadı.")]
    public void IsTransient_PermanentErrors_ReturnsFalse(string error)
    {
        Assert.False(DownloadRetryPolicy.IsTransient(error));
    }

    [Theory]
    [InlineData("connection timeout")]
    [InlineData("Kaynak taraması zaman aşımına uğradı (5,0 sn).")]
    [InlineData("429 Too Many Requests")]
    [InlineData("502 Bad Gateway")]
    [InlineData("upstream down")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("beklenmedik bir şey oldu")]
    public void IsTransient_RetryableErrors_ReturnsTrue(string? error)
    {
        Assert.True(DownloadRetryPolicy.IsTransient(error));
    }

    [Theory]
    [InlineData(1, 2.0, 2.0, 2.5)]
    [InlineData(2, 2.0, 4.0, 5.0)]
    [InlineData(3, 2.0, 8.0, 10.0)]
    [InlineData(1, 0.0, 0.0, 0.0)]
    public void BackoffDelay_StaysWithinBounds(int attempt, double baseSeconds, double min, double max)
    {
        var delay = DownloadRetryPolicy.BackoffDelay(attempt, TimeSpan.FromSeconds(baseSeconds));

        Assert.InRange(delay.TotalSeconds, min, max);
    }

    [Fact]
    public void BackoffDelay_CapsAtSixtySeconds()
    {
        var delay = DownloadRetryPolicy.BackoffDelay(10, TimeSpan.FromSeconds(30));

        Assert.InRange(delay.TotalSeconds, 60, 75);
    }

    [Fact]
    public void NormalizeDownloadDirectory_ExpandsHomeAndVariables()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(
            Path.Combine(profile, "Anime"),
            CliConfig.NormalizeDownloadDirectory("~/Anime"));
        Assert.Equal(
            Path.Combine(profile, "Anime"),
            CliConfig.NormalizeDownloadDirectory("  \"~/Anime\"  "));

        Environment.SetEnvironmentVariable("MIGURDEX_TEST_DIR", "/tmp/migx");
        try
        {
            Assert.Equal("/tmp/migx/a", CliConfig.NormalizeDownloadDirectory("$MIGURDEX_TEST_DIR/a"));
            Assert.Equal("/tmp/migx/a", CliConfig.NormalizeDownloadDirectory("${MIGURDEX_TEST_DIR}/a"));
            Assert.Equal("/tmp/migx/a", CliConfig.NormalizeDownloadDirectory("%MIGURDEX_TEST_DIR%/a"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIGURDEX_TEST_DIR", null);
        }
    }

    [Fact]
    public void NormalizeDownloadDirectory_PreservesUnknownAndPlainPaths()
    {
        Assert.Equal("$NOPE_VAR_XYZ/a", CliConfig.NormalizeDownloadDirectory("$NOPE_VAR_XYZ/a"));
        Assert.Equal("/tmp/anime", CliConfig.NormalizeDownloadDirectory("/tmp/anime"));
        Assert.Equal(CliConfig.DefaultDownloadDirectory, CliConfig.NormalizeDownloadDirectory(null));
        Assert.Equal(CliConfig.DefaultDownloadDirectory, CliConfig.NormalizeDownloadDirectory("   "));
    }

    [Fact]
    public void StripVolatileQueryParameters_RemovesOnlySignatureParams()
    {
        Assert.Equal(
            "https://cdn.example/v/ep1.mp4",
            DownloadHttp.StripVolatileQueryParameters(
                "https://cdn.example/v/ep1.mp4?token=abc&expires=123"));
        Assert.Equal(
            "https://cdn.example/v/ep1.mp4?quality=1080p",
            DownloadHttp.StripVolatileQueryParameters(
                "https://cdn.example/v/ep1.mp4?quality=1080p&sig=xyz&token=abc"));
        Assert.Equal(
            "https://cdn.example/v/ep1.mp4",
            DownloadHttp.StripVolatileQueryParameters("https://cdn.example/v/ep1.mp4"));
        Assert.Equal(string.Empty, DownloadHttp.StripVolatileQueryParameters(null));
    }

    [Fact]
    public void CreateSourceFingerprint_IgnoresTokenRotation()
    {
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "https://site.example/"
        };
        var first = DownloadHttp.CreateSourceFingerprint(
            "https://cdn.example/v/ep1.mp4?token=aaa&expires=111",
            headers);
        var second = DownloadHttp.CreateSourceFingerprint(
            "https://cdn.example/v/ep1.mp4?token=bbb&expires=222",
            headers);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateSourceFingerprint_DistinguishesDifferentFiles()
    {
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "https://site.example/"
        };
        var first = DownloadHttp.CreateSourceFingerprint(
            "https://cdn.example/v/ep1.mp4?token=aaa",
            headers);
        var other = DownloadHttp.CreateSourceFingerprint(
            "https://cdn.example/v/ep2.mp4?token=aaa",
            headers);

        Assert.NotEqual(first, other);
    }

    [Fact]
    public void CliConfig_OldConfigs_KeepNewRetryDefaults()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<CliConfig>("{}");

        Assert.NotNull(config);
        Assert.Equal(3, config.DownloadMaxRetries);
        Assert.Equal(2, config.DownloadRetryBaseDelaySeconds);
        Assert.Equal(0, config.MaxDownloadSpeedKbps);
    }

    [Fact]
    public async Task Limiter_Unlimited_DoesNotWait()
    {
        var limiter = new BandwidthLimiter(() => 0);
        var sw      = System.Diagnostics.Stopwatch.StartNew();
        await limiter.ConsumeAsync(10 * 1024 * 1024, TestContext.Current.CancellationToken);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Limiter_ThrottlesToConfiguredRate()
    {
        var limiter = new BandwidthLimiter(() => 200_000);
        var sw      = System.Diagnostics.Stopwatch.StartNew();
        await limiter.ConsumeAsync(200_000, TestContext.Current.CancellationToken);
        var elapsed = sw.Elapsed;

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(500));
        Assert.True(elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Limiter_Activation_ScopesCurrent()
    {
        var limiter = new BandwidthLimiter(() => 1024);
        Assert.Null(BandwidthLimiter.Current);

        using (BandwidthLimiter.Activate(limiter))
        {
            Assert.Same(limiter, BandwidthLimiter.Current);
        }

        Assert.Null(BandwidthLimiter.Current);

        using (BandwidthLimiter.Activate(null))
        {
            Assert.Null(BandwidthLimiter.Current);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void YtDlp_FormatLimitRate_BuildsValidSuffix()
    {
        Assert.Null(YtDlpHlsDownloader.FormatLimitRate(0));
        Assert.Null(YtDlpHlsDownloader.FormatLimitRate(-5));
        Assert.Equal("500K", YtDlpHlsDownloader.FormatLimitRate(512_000));
        Assert.Equal("2M", YtDlpHlsDownloader.FormatLimitRate(2_097_152));
    }
}
