using Migurdex.Cli.Services.Downloads;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadSpeedometerTests
{
    [Fact]
    public void NoEstimate_BeforeTwoSamples()
    {
        var meter = new DownloadSpeedometer();
        var now   = DateTimeOffset.UtcNow;

        meter.Sample(500, now);

        Assert.Equal(0, meter.BytesPerSecond);
        Assert.Null(meter.EstimateRemaining(500, 1000));
    }

    [Fact]
    public void ConstantRate_MeasuresBytesPerSecond()
    {
        var meter = new DownloadSpeedometer();
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        meter.Sample(0, start);
        meter.Sample(2048, start.AddSeconds(2));

        Assert.Equal(1024, meter.BytesPerSecond);
    }

    [Fact]
    public void LaterSamples_SmoothWithEma()
    {
        var meter = new DownloadSpeedometer(alpha: 0.3);
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        meter.Sample(0, start);
        meter.Sample(1000, start.AddSeconds(1));
        meter.Sample(3000, start.AddSeconds(2));

        Assert.Equal(1300, meter.BytesPerSecond);
    }

    [Fact]
    public void EstimateRemaining_PrefersExplicitSpeed()
    {
        var meter = new DownloadSpeedometer();
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        meter.Sample(0, start);
        meter.Sample(100, start.AddSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(9), meter.EstimateRemaining(100, 1000, speedOverride: 100));
    }

    [Fact]
    public void EstimateRemaining_DividesWorkLeftBySpeed()
    {
        var meter = new DownloadSpeedometer();
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        meter.Sample(0, start);
        meter.Sample(1000, start.AddSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(9), meter.EstimateRemaining(1000, 10000));
    }

    [Theory]
    [InlineData(1000L, null)]
    [InlineData(1000L, 1000L)]
    [InlineData(1500L, 1000L)]
    public void EstimateRemaining_NullWithoutWorkLeft(long downloaded, long? total)
    {
        var meter = new DownloadSpeedometer();
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        meter.Sample(0, start);
        meter.Sample(1000, start.AddSeconds(1));

        Assert.Null(meter.EstimateRemaining(downloaded, total));
    }

    [Theory]
    [InlineData(31, "ETA 00:31")]
    [InlineData(3723, "ETA 1:02:03")]
    public void FormatEta_PrintsShortClock(int seconds, string expected)
    {
        Assert.Equal(expected, DownloadSpeedometer.FormatEta(TimeSpan.FromSeconds(seconds)));
    }
}
