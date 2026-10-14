using Migurdex.Cli.Services.Downloads;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Bayt akmadığı süre hız değildir: eski ölçüm her boşlukta tahmini aşağı çekiyordu, bu yüzden
/// toplu indirmenin sonunda (birleştirme/duraklama sırasında) ekranda KB/s görünüyordu.
/// Bu testler boşluğun ölçümü düşürmediğini ve veri geri geldiğinde pencerenin tazelendiğini
/// kanıtlar.
/// </summary>
public sealed class DownloadSpeedometerIdleTests
{
    private const long MiB = 1024 * 1024;

    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IdleGap_DoesNotDecayMeasuredSpeed()
    {
        var meter = new DownloadSpeedometer();

        meter.Sample(0, Start);
        meter.Sample(MiB, Start.AddSeconds(1));

        Assert.Equal(MiB, meter.BytesPerSecond, 6);

        // Duraklama: bayt gelmiyor, ölçüm düşmemeli.
        meter.Sample(MiB, Start.AddSeconds(6));

        Assert.Equal(MiB, meter.BytesPerSecond, 6);
    }

    [Fact]
    public void IdleSampleKeepsWindowFresh_SoResumedBurstIsMeasuredOverBurstTime()
    {
        var meter = new DownloadSpeedometer();

        meter.Sample(0, Start);
        meter.Sample(MiB, Start.AddSeconds(1));
        meter.Sample(MiB, Start.AddSeconds(6));      // duraklama örneği pencereyi ilerletir
        meter.Sample(2 * MiB, Start.AddSeconds(7));  // 1 saniyede 1 MiB aktı

        Assert.Equal(MiB, meter.BytesPerSecond, 6);
    }

    [Fact]
    public void RepeatedIdleSamples_StillTrackBytes()
    {
        var meter = new DownloadSpeedometer();

        meter.Sample(0, Start);
        meter.Sample(1000, Start.AddSeconds(1));
        meter.Sample(1000, Start.AddSeconds(3));
        meter.Sample(1000, Start.AddSeconds(5));
        meter.Sample(2000, Start.AddSeconds(6));

        Assert.Equal(1000, meter.BytesPerSecond, 6);
    }

    [Fact]
    public void GapResetsEstimate_InsteadOfBlendingOldSpeed()
    {
        var meter = new DownloadSpeedometer();

        meter.Sample(0, Start);
        meter.Sample(MiB, Start.AddSeconds(1));      // 1 MiB/s

        // 4 saniyelik sessizlikten sonra 1 MiB: tahmin eski hıza yapışmaz, yeni akıştan başlar.
        meter.Sample(2 * MiB, Start.AddSeconds(5));

        Assert.Equal(MiB / 4.0, meter.BytesPerSecond, 6);
    }

    [Fact]
    public void IdleFor_ReportsTimeSinceLastByte()
    {
        var meter = new DownloadSpeedometer();

        meter.Sample(0, Start);
        meter.Sample(512, Start.AddSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(4), meter.IdleFor(Start.AddSeconds(5)));
        Assert.Null(new DownloadSpeedometer().IdleFor(Start));
    }
}
