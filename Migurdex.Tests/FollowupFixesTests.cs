using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Windows doğrulama turunda bulunan iki yüksek ve dört düşük öncelikli kusurun
/// regresyon testleri.
/// </summary>
public sealed class FollowupFixesTests
{
    // ---------------------------------------------------------------------
    // 1) YÜKSEK: kaynak tarama zaman aşımı varsayılanı gerçek işin altındaydı
    // ---------------------------------------------------------------------

    [Fact]
    public void CliConfig_DownloadAutoSelectTimeout_DefaultHasHeadroom()
    {
        // Önceki varsayılan 5 sn idi. İlk turda "one piece" için 46,98 sn'lik bir
        // çözümleme ölçüldüğü için yükseltildi; ancak sonraki turda bu hata yeniden
        // ÜRETİLEMEDİ (gerçek zincir ~1,78 sn; 5 sn ve 0,2 sn zorlanınca da exit 0).
        // Yani 5 sn'in somut bir hata ürettiği kanıtlanmadı. Değer yine de yükseltildi:
        // ölçülen uç değerin çok altında kalmak üzere tasarlanmış bir sayıydı ve 60 sn
        // bu belirsizliği maliyetsiz ortadan kaldırıyor. Regresyon koruması: 60 sn'in
        // altına sessizce düşülmesin.
        var config = new CliConfig();
        Assert.True(config.DownloadAutoSelectTimeoutSeconds >= 60.0,
                    $"Varsayılan şu an {config.DownloadAutoSelectTimeoutSeconds}; "
                    + "ölçülen uç değere (46,98 sn) pay bırakmalı.");
    }

    [Fact]
    public void CliConfig_DownloadAutoSelectTimeout_IsIndependentFromTuiAutoSelect()
    {
        // TUI'nin "bekleme süresi" ile indirme kaynak taraması süresi farklı
        // ayarlardır; biri değişince diğeri değişmemeli.
        var config = new CliConfig { AutoSelectTimeoutSeconds = 3.5 };
        Assert.Equal(60.0, config.DownloadAutoSelectTimeoutSeconds);
    }

    // ---------------------------------------------------------------------
    // 2) Bayat .migurdex-partial temizliği
    // ---------------------------------------------------------------------

    [Fact]
    public void MoveOutput_RemovesStalePartialFileLeftByKilledProcess()
    {
        var root = NewTempDir();
        try
        {
            var output    = Path.Combine(root, "is.mp4");
            var destination = new DownloadPath(Path.Combine(root, "Bolum 1"), "S01E01", "Test");
            var finalPath = destination.GetMediaPath(".mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            // Önceki bir çalışma SIGKILL ile File.Copy ile File.Move arasında
            // yakalanmış: hedef klasörde yarım dosya kalmış.
            var partial = finalPath + ".migurdex-partial";
            File.WriteAllText(partial, "yarim");

            File.WriteAllText(output, "gercek veri");
            var result = YtDlpHlsDownloader.MoveOutputForTest(output, destination, overwrite: true);

            Assert.Equal(finalPath, result.OutputPath);
            Assert.True(File.Exists(finalPath));
            Assert.False(File.Exists(partial), "Bayat .migurdex-partial temizlenmedi.");
            Assert.Equal("gercek veri", File.ReadAllText(finalPath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveOutput_LeavesNoPartialFileBehindOnSuccess()
    {
        var root = NewTempDir();
        try
        {
            var output      = Path.Combine(root, "is.mp4");
            var destination = new DownloadPath(Path.Combine(root, "Bolum 2"), "S01E02", "Test");
            Directory.CreateDirectory(destination.Directory);

            File.WriteAllText(output, "veri");
            var result = YtDlpHlsDownloader.MoveOutputForTest(output, destination, overwrite: true);

            Assert.False(File.Exists(result.OutputPath + ".migurdex-partial"));
            Assert.False(File.Exists(output));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void MoveOutput_DoesNotTouchPartialFileThatIsInUse()
    {
        // Başka bir işlem tutuyorsa dokunulmamalı; taşıma yine de yapılır.
        var root = NewTempDir();
        try
        {
            var output      = Path.Combine(root, "is.mp4");
            var destination = new DownloadPath(Path.Combine(root, "Bolum 3"), "S01E03", "Test");
            var finalPath   = destination.GetMediaPath(".mp4");
            var partial     = finalPath + ".migurdex-partial";

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.WriteAllText(partial, "yarim");

            using var holder = new FileStream(partial,
                                             FileMode.Open,
                                             FileAccess.ReadWrite,
                                             FileShare.None);

            File.WriteAllText(output, "veri");
            YtDlpHlsDownloader.MoveOutputForTest(output, destination, overwrite: true);

            Assert.True(File.Exists(finalPath));
            Assert.True(File.Exists(partial), "Tutulan dosya silinmemeliydi.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ---------------------------------------------------------------------
    // 3) Semafor sızıntısı
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DownloadTargetLock_ProcessLocksDoNotAccumulate()
    {
        // Sözlük girdileri hiç silinmiyordu: uzun TUI oturumunda her farklı hedef
        // için bir SemaphoreSlim kalıcı oluyordu.
        //
        // Toplam sözlük boyutu ölçülmüyor: xUnit tam süitte testleri paralel
        // çalıştırdığı için diğer testlerin alımları karışır ve kararsız sonuç
        // verir (bu test ilk hâlinde tam sütte kırılmıştı). Bunun yerine yalnız
        // bu testin ürettiği anahtarlara bakılıyor.
        var root = NewTempDir();
        try
        {
            var targets = new List<string>();
            for (var index = 0; index < 25; index++)
            {
                var target = Path.Combine(root, "hedef-" + index + ".mp4");
                var handle = await DownloadTargetLock.AcquireAsync(target,
                                                                   TestContext.Current.CancellationToken);
                handle.Dispose();
                targets.Add(target);
            }

            var leaked = targets.Where(t => DownloadTargetLock.HasProcessLockForTest(t)).ToList();
            Assert.True(leaked.Count == 0,
                        $"{leaked.Count}/{targets.Count} hedef kaydı bırakıldı; "
                        + "sözlük girdileri birikiyor.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DownloadTargetLock_StillBlocksConcurrentUseOfSameTarget()
    {
        // Sızıntı düzeltmesi eşzamanlılık korumasını bozmamalı.
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "ayni.mp4");
            var first  = await DownloadTargetLock.AcquireAsync(target,
                                                               TestContext.Current.CancellationToken);

            var secondTask = DownloadTargetLock.AcquireAsync(target,
                                                              TestContext.Current.CancellationToken);
            var finishedEarly = await Task.WhenAny(secondTask,
                                                   Task.Delay(200, TestContext.Current.CancellationToken))
                                         == secondTask;

            Assert.False(finishedEarly, "İkinci alım kilidi geçmemeliydi.");

            first.Dispose();
            (await secondTask).Dispose();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ---------------------------------------------------------------------
    // 4) Linux yetim koruması: PID geri dönüşümü
    // ---------------------------------------------------------------------

    [Fact]
    public void ReadStartTime_ReturnsZeroForUnknownPid()
    {
        // 0 = "doğrulanamadı"; koruma eski davranışla devam eder, yanlış öldürmez.
        Assert.Equal(0, LinuxOrphanGuard.ReadStartTime(2_000_000_000));
    }

    [Fact]
    public void ReadStartTime_ReadsOwnProcessOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var start = LinuxOrphanGuard.ReadStartTime(Environment.ProcessId);
        Assert.True(start > 0, "Kendi sürecimizin başlangıç zamanı okunamadı.");
    }

    [Fact]
    public void ReadStartTime_DistinguishesDifferentProcessesOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var child = new System.Diagnostics.Process();
        child.StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/sleep")
        {
            UseShellExecute = false
        };
        child.StartInfo.ArgumentList.Add("30");
        child.Start();

        var self  = LinuxOrphanGuard.ReadStartTime(Environment.ProcessId);
        var other = LinuxOrphanGuard.ReadStartTime(child.Id);

        Assert.True(self > 0);
        Assert.True(other > 0);
        Assert.NotEqual(self, other);

        try
        {
            child.Kill();
        }
        catch
        {
            // ignored
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(),
                                "migurdex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // ignored
        }
    }
}
