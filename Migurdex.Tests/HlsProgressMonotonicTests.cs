using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 23: HLS ilerlemesinde toplam (payda) grotesk titriyordu.
///
/// Canlı ölçüm, aynı indirme içinde:
/// <code>
/// 1 KiB → 7.81 MiB → 177.03 MiB → 434.27 MiB → 521.3 MiB → 362.26 MiB → 280.65 MiB
/// </code>
/// Kaynak: yt-dlp'nin <c>of ~Y</c> tahmini (<c>ProgressTotalRegex</c>).
/// Yüzde zaten [0,100] aralığına kırpılıyordu ama TOPLAM kırpılmadığı için
/// ilerleme çubuğu sürekli geri gidiyordu.
///
/// Düzeltme: tahmin edilen toplam faz içinde monoton artan yapılır. Gerçek iki
/// faz (segment ham → mux sonrası) için <c>frag</c> sayacı %100'e ulaşıp yeniden
/// başladığında sıfırlama serbest bırakılır.
/// </summary>
public sealed class HlsProgressMonotonicTests
{
    [Fact]
    public void Report_EstimatedTotalNeverGoesBackwards()
    {
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        // Canlı ölçümdeki titreme sırası, `of ~Y` biçiminde. frag sayacı hep
        // ilerliyor ve %100'e ulaşmıyor: saf tahmin titremesi, faz geçişi değil.
        heartbeat.Report("[download]   0.0% of ~1.00KiB at 585.91B/s ETA Unknown (frag 0/300)", progress);
        heartbeat.Report("[download]   0.0% of ~7.81MiB at 585.91B/s ETA Unknown (frag 5/300)", progress);
        heartbeat.Report("[download]  33.9% of ~177.03MiB at 12.00MiB/s ETA 00:12 (frag 100/300)", progress);
        heartbeat.Report("[download]  82.9% of ~434.27MiB at 12.00MiB/s ETA 00:02 (frag 250/300)", progress);
        heartbeat.Report("[download]  95.1% of ~521.30MiB at 12.00MiB/s ETA 00:01 (frag 285/300)", progress);
        // İşte regresyon satırları: tahmin küçülüyordu.
        heartbeat.Report("[download]  69.5% of ~362.26MiB at 12.00MiB/s ETA 00:11 (frag 288/300)", progress);
        heartbeat.Report("[download]  53.8% of ~280.65MiB at 12.00MiB/s ETA 00:15 (frag 290/300)", progress);

        var totals = progress.Items
                           .Where(item => item.Stage == DownloadStage.Downloading && item.TotalBytes is > 0)
                           .Select(item => item.TotalBytes!.Value)
                           .ToList();

        // Her satır kendi tahmini; ilk dördü büyüyor, son üçü küçülüyordu.
        Assert.Equal(7, totals.Count);
        Assert.Equal(1024L, totals[0]);          // 1.00KiB
        Assert.Equal(Mib(7.81), totals[1]);
        Assert.Equal(Mib(177.03), totals[2]);
        Assert.Equal(Mib(434.27), totals[3]);
        Assert.Equal(Mib(521.30), totals[4]);

        // ASIL SÖZLEŞME: küçülen tahminler yok sayılır, payda 521.30 MiB'de
        // kilitlenir. Önceki hâlde 362.26 ve 280.65 MiB'e düşüyordu.
        Assert.Equal(Mib(521.30), totals[5]);
        Assert.Equal(Mib(521.30), totals[6]);
        Assert.Equal(totals.Order(), totals);
    }

    private static long Mib(double value)
    {
        return (long)(value * 1024 * 1024);
    }

    [Fact]
    public void Report_SmallEstimatesDoNotShrinkTheTotal()
    {
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        heartbeat.Report("[download]  50.0% of ~100.00MiB at 1.00MiB/s ETA 00:50 (frag 50/100)", progress);
        heartbeat.Report("[download]  60.0% of ~  1.00MiB at 1.00MiB/s ETA 00:40 (frag 60/100)", progress);

        var last = progress.Items[^1];
        Assert.Equal(100L * 1024 * 1024, last.TotalBytes);
    }

    [Fact]
    public void Report_FirstEstimateIsAdoptedAsIs()
    {
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        heartbeat.Report("[download]   0.0% of ~324.23MiB at 585.91B/s ETA Unknown (frag 0/64)", progress);

        Assert.Equal(339979796L, progress.Items[^1].TotalBytes);
    }

    [Fact]
    public void Report_FragRestartAfterCompletionAllowsTotalToShrink()
    {
        // HLS'te iki GERÇEK faz var: segment ham ~277 MiB → mux sonrası ~260 MiB.
        // frag %100'e ulaşıp yeniden başladığında monotonluk kırılmalı, yoksa
        // ikinci fazın toplamı şişirilmiş bir değerle raporlanır.
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        heartbeat.Report("[download] 100.0% of ~277.00MiB at 12.00MiB/s ETA 00:00 (frag 900/900)", progress);
        heartbeat.Report("[download]   0.0% of ~260.00MiB at 12.00MiB/s ETA Unknown (frag 0/900)", progress);

        var totals = progress.Items
                           .Where(item => item.TotalBytes is > 0)
                           .Select(item => item.TotalBytes!.Value)
                           .ToList();

        Assert.Equal(2, totals.Count);
        Assert.True(totals[1] < totals[0],
                    $"Faz geçişinde toplam küçülebilmeli: {totals[0]} -> {totals[1]}");
        Assert.Equal(260L * 1024 * 1024, totals[1]);
    }

    [Fact]
    public void Report_FragCounterGoingBackwardsBeforeCompletionKeepsMonotonicTotal()
    {
        // %100'e hiç ulaşılmadı: sayaç sıfırlanması bir faz geçişi DEĞİL
        // (örn. track değişimi ya da yeniden başlatma). Monotonluk korunur.
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        heartbeat.Report("[download]  40.0% of ~300.00MiB at 1.00MiB/s ETA 01:00 (frag 120/300)", progress);
        heartbeat.Report("[download]   0.0% of ~280.00MiB at 1.00MiB/s ETA Unknown (frag 0/300)", progress);

        var totals = progress.Items.Where(item => item.TotalBytes is > 0)
                           .Select(item => item.TotalBytes!.Value)
                           .ToList();

        Assert.Equal(2, totals.Count);
        Assert.True(totals[1] >= totals[0], "Faz tamamlanmadan toplam küçülmemeli.");
    }

    [Fact]
    public void Report_NewTrackResetsMonotonicTotal()
    {
        // Farklı bir Destination satırı = ayrı indirilen dosya. Yeni track'in
        // toplamı öncekinden küçük olabilir ve OLMALIDIR.
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        heartbeat.Report("[download] Destination: /tmp/job/media.f137.mp4", progress);
        heartbeat.Report("[download] 100.0% of ~300.00MiB at 1.00MiB/s ETA 00:00 (frag 10/10)", progress);
        heartbeat.Report("[download] Destination: /tmp/job/media.f251.webm", progress);
        heartbeat.Report("[download]  10.0% of ~ 50.00MiB at 1.00MiB/s ETA 00:45 (frag 5/50)", progress);

        var last = progress.Items[^1];
        Assert.Equal(50L * 1024 * 1024, last.TotalBytes);
        Assert.Equal("media.f251.webm", last.Track);
    }

    [Fact]
    public void Report_ExactByteTotalIsNotInflatedByPreviousEstimate()
    {
        // `X / Y` biçimi yt-dlp'nin BİLİNEN gerçek toplamı, tahmin değil.
        // Monoton kural burada uygulanmamalı: şişirilmiş bir tahmin kesin
        // toplamı bozmamalı.
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        heartbeat.Report("[download]  50.0% of ~1000.00MiB at 1.00MiB/s ETA 08:20 (frag 50/100)", progress);
        heartbeat.Report("[download] 10.50MiB / 24.00MiB at 1.00MiB/s ETA 00:14", progress);

        Assert.Equal(24L * 1024 * 1024, progress.Items[^1].TotalBytes);
    }

    [Fact]
    public void Report_ReportedDenominatorNeverShrinksAcrossAWholeDownload()
    {
        // Uçtan uca: canlı ölçüm satırları. C2'nin sözleşmesi PAYDANIN
        // (toplamın) küçülmemesidir; oranın kendisi yt-dlp'nin bildirdiği
        // yüzdeye bağlıdır ve C2 kapsamında değiştirilmez.
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();
        var progress = new RecordingProgress();

        // Not: frag sayacı HEP ilerliyor ve %100'e ulaşmıyor. 300/300'den
        // sonra 200/300'e düşen yapay bir sayaç gerçek bir faz geçişidir ve
        // sıfırlamaya izin verilmesini gerektirir; titreme senaryosu değildir.
        foreach (var line in new[]
                 {
                     "[download]   0.0% of ~  1.00KiB at  585.91B/s ETA Unknown (frag 0/300)",
                     "[download]   0.0% of ~  7.81MiB at  585.91B/s ETA Unknown (frag 5/300)",
                     "[download]  33.9% of ~177.03MiB at 12.00MiB/s ETA 00:12 (frag 100/300)",
                     "[download]  82.9% of ~434.27MiB at 12.00MiB/s ETA 00:02 (frag 250/300)",
                     "[download]  95.1% of ~521.30MiB at 12.00MiB/s ETA 00:01 (frag 285/300)",
                     "[download]  69.5% of ~362.26MiB at 12.00MiB/s ETA 00:11 (frag 288/300)",
                     "[download]  53.8% of ~280.65MiB at 12.00MiB/s ETA 00:15 (frag 290/300)"
                 })
        {
            heartbeat.Report(line, progress);
        }

        var denominators = progress.Items
                                 .Where(item => item.Stage == DownloadStage.Downloading
                                                && item.TotalBytes is > 0)
                                 .Select(item => item.TotalBytes!.Value)
                                 .ToList();

        for (var index = 1; index < denominators.Count; index++)
        {
            Assert.True(denominators[index] >= denominators[index - 1],
                        $"Payda geri gitti: satır {index} -> {denominators[index]} < {denominators[index - 1]}");
        }

        // Son satırdaki 280.65 MiB tahmini 521.30 MiB'e kilitlenmiş olmalı.
        Assert.Equal(Mib(521.30), denominators[^1]);
    }

    [Fact]
    public void Report_NullProgressIsIgnored()
    {
        var heartbeat = new YtDlpHlsDownloader.HlsProgressHeartbeat();

        // Çağrının kendisi patlamamalı.
        heartbeat.Report("[download]  50.0% of ~10.00MiB at 1.00MiB/s ETA 00:05 (frag 5/10)", null);
        heartbeat.Report("", null);
        heartbeat.Report("   ", null);
    }

    [Fact]
    public async Task Download_EndToEndKeepsTotalMonotonicAcrossWobblingEstimates()
    {
        var root = Path.Combine(Path.GetTempPath(), "migurdex-hlsmono-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            Directory.CreateDirectory(destination.AnimeDirectory);
            var runner = new FakeProcessRunner((startInfo, onStandardOutput, _) =>
            {
                var jobDirectory = ValueAfter(startInfo.ArgumentList, "--paths");
                Directory.CreateDirectory(jobDirectory);
                File.WriteAllText(Path.Combine(jobDirectory, "media.mp4"), "hls-media");
                onStandardOutput?.Invoke("[download]   0.0% of ~  1.00KiB at  585.91B/s ETA Unknown (frag 0/300)");
                onStandardOutput?.Invoke("[download]  50.0% of ~300.00MiB at 12.00MiB/s ETA 00:12 (frag 150/300)");
                onStandardOutput?.Invoke("[download]  60.0% of ~120.00MiB at 12.00MiB/s ETA 00:10 (frag 180/300)");
                onStandardOutput?.Invoke("[download]  70.0% of ~ 90.00MiB at 12.00MiB/s ETA 00:08 (frag 210/300)");
                return Task.FromResult(new ExternalProcessResult(0));
            });
            var progress = new RecordingProgress();

            await new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                .DownloadAsync(new VideoSource
                    {
                        Url = "https://origin.example/master.m3u8",
                        Type = VideoType.M3U8
                    },
                    destination,
                    progress: progress,
                    cancellationToken: TestContext.Current.CancellationToken);

            // Yalnız tahmin satırları: MoveOutput sonda gerçek dosya boyutunu
            // (9 B) raporlar, o kesin toplamdır ve monoton kurala tabi değildir.
            // Filtre `IsEstimatedTotal` kullanır: tahminli satırlarda yüzde
            // bilinçli olarak null'dır (yanlış yüzde 100'ü önlemek için), bu
            // yüzden "Percent is not null" ölçütü satırların hepsini elerdi.
            var totals = progress.Items
                               .Where(item => item.Stage == DownloadStage.Downloading
                                              && item.TotalBytes is > 0
                                              && item.IsEstimatedTotal)
                               .Select(item => item.TotalBytes!.Value)
                               .ToList();

            Assert.Equal(4, totals.Count);
            Assert.Equal(1024L, totals[0]);
            Assert.Equal(300L * 1024 * 1024, totals[1]);
            Assert.Equal(300L * 1024 * 1024, totals[2]);
            Assert.Equal(300L * 1024 * 1024, totals[3]);

            // Monoton kural korunurken yüzde ve ETA tahminden türetilmez.
            Assert.All(progress.Items.Where(item => item.IsEstimatedTotal),
                       item =>
                       {
                           Assert.Null(item.Percent);
                           Assert.Null(item.Eta);
                       });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string ValueAfter(IReadOnlyList<string> values, string option)
    {
        var index = values.ToList().IndexOf(option);
        return index >= 0 && index + 1 < values.Count ? values[index + 1] : string.Empty;
    }

    private sealed class FakeProcessRunner : IExternalProcessRunner
    {
        private readonly Func<ProcessStartInfo, Action<string>?, CancellationToken, Task<ExternalProcessResult>> _handler;

        public FakeProcessRunner(
            Func<ProcessStartInfo, Action<string>?, CancellationToken, Task<ExternalProcessResult>> handler)
        {
            _handler = handler;
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, null, cancellationToken);
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            Action<string>?   onStandardOutput,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, onStandardOutput, cancellationToken);
        }
    }

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Items { get; } = [];

        public void Report(DownloadProgress value)
        {
            Items.Add(value);
        }
    }
}
