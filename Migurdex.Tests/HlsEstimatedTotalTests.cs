using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Nihai Windows doğrulama turunda bulundu: HLS segment fazında yt-dlp'nin
/// <c>of ~Y</c> **tahmini**, kesin toplam gibi sunuluyordu.
///
/// Canlı ölçüm (<c>one piece</c> 1. bölüm): segment fazı <c>1,25 GiB</c> derken
/// gerçek dosya <b>486,16 MiB</b> oldu — 2,6× sapma. Yüzde tahminden türetildiği
/// için ilerleme <b>yanlış bir %100</b> gösteriyor, buna dayalı ETA de yanlış.
///
/// Bu, BULGU 1'in (sahte bayt göstergesi) kardeşidir: gösterge gerçek değil.
/// Mux fazında yt-dlp gerçek boyutu verdiği için belirsizlik yalnız tahmin
/// satırlarında vardır ve <c>~</c> işaretiyle ayırt edilebilir.
/// </summary>
public sealed class HlsEstimatedTotalTests
{
    [Fact]
    public void Report_MarksTildeTotalAsEstimatedAndSuppressesPercent()
    {
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress   = new RecordingProgress();

        heartbeat.Report("[download]  50.0% of ~1.25GiB at 12.00MiB/s ETA 00:01 (frag 150/300)",
                         progress);

        var value = Assert.Single(progress.Reports);
        Assert.True(value.IsEstimatedTotal, "`of ~Y` satırı tahmin olarak işaretlenmeli.");
        // 1,25 GiB = 1,25 * 1073741824
        Assert.Equal(1_342_177_280L, value.TotalBytes);

        // Tahmine dayalı yüzde gösterilmez: yanlış "100%" üretmemek için.
        Assert.Null(value.Percent);

        // Parça sayacı hâlâ görünür — kullanıcı ilerlemeyi ondan okur.
        Assert.Equal(150, value.FragmentsDone);
        Assert.Equal(300, value.FragmentsTotal);
    }

    [Fact]
    public void Report_TotalWithoutTildeIsTreatedAsExact()
    {
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress   = new RecordingProgress();

        heartbeat.Report("[download]  50.0% of 486.16MiB at 12.00MiB/s ETA 00:01 (frag 150/300)",
                         progress);

        var value = Assert.Single(progress.Reports);
        Assert.False(value.IsEstimatedTotal);
        Assert.NotNull(value.Percent);
        Assert.Equal(50.0, value.Percent!.Value, 1);
    }

    [Fact]
    public void Report_EstimatedTotalSuppressesEta()
    {
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress   = new RecordingProgress();

        heartbeat.Report("[download]  50.0% of ~1.25GiB at 12.00MiB/s ETA 00:01 (frag 150/300)",
                         progress);

        // 1,25 GiB tahminine dayalı "kalan süre" de yanlış olurdu.
        Assert.Null(Assert.Single(progress.Reports).Eta);
    }

    [Fact]
    public void DownloadProgress_EstimatedFlagDefaultsToFalse()
    {
        // MP4 yolunda toplam gerçek bir Content-Length'dir; işaretlenmemeli.
        var progress = new DownloadProgress(DownloadStage.Downloading, 10, 100);
        Assert.False(progress.IsEstimatedTotal);
    }

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value) => Reports.Add(value);
    }
}
