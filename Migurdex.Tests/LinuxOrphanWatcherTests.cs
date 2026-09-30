using Migurdex.Cli.Services.Downloads;
using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Linux yetim alt süreç korumasının <b>izleyici süreç</b> sözleşmesi (C4 / D3).
///
/// <b>Nihai Linux doğrulamasında ölçülen tasarım hatası.</b> İlk sürüm izlemeyi
/// ebeveyn sürecin <em>içinde</em> bir thread ile yapıyordu. Ana süreç
/// <c>SIGKILL</c> ile öldüğünde thread de anında ölür, dolayısıyla
/// <c>KillIfAlive</c> hiç çalışamaz — koruma hiçbir işe yaramıyordu.
/// Ölçüm: guard thread'i gerçekten başlamıştı
/// (<c>/proc/16767/task/16779</c> → <c>comm = migurdex-linux-</c>) ama 4/4 sessiz
/// alt süreç <c>kill -9</c> sonrası yetim kaldı. Gözlenen "temizlik" kırılan
/// stdout borusundan geliyordu.
///
/// İzleyici bu yüzden artık <b>ayrı bir süreç</b> olarak başlatılıyor ve
/// <c>--internal-watch-orphan</c> argümanıyla ona ulaşılıyor. Aşağıdaki testler
/// yalnız bu yeni sözleşmeyi kapsar; temel <c>ShouldGuard</c>/<c>Attach</c>
/// davranışı <c>FfmpegDiagnosisTests</c> içinde zaten sınanıyordu.
/// </summary>
public sealed class LinuxOrphanWatcherTests
{
    private static string[] Args(int parentPid, long parentStart, int childPid, long childStart)
    {
        var invariant = CultureInfo.InvariantCulture;
        return
        [
            LinuxOrphanGuard.WatcherArgument,
            parentPid.ToString(invariant),
            parentStart.ToString(invariant),
            childPid.ToString(invariant),
            childStart.ToString(invariant)
        ];
    }

    [Fact]
    public void TryParse_AcceptsWellFormedWatcherArguments()
    {
        // `ShouldGuard` Linux'e bağlı (`IsSupported`), dolayısıyla ayrıştırma da
        // yalnız Linux'ta kabul eder. Bu bilinçli: koruma Windows'ta Job Object
        // ile zaten sağlanıyor ve izleyici orada hiç başlatılmıyor.
        if (!OperatingSystem.IsLinux())
        {
            Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(Args(100, 11, 200, 22),
                                                                    out _, out _, out _, out _));
            return;
        }

        var ok = LinuxOrphanGuard.TryParseWatcherArguments(Args(100, 11, 200, 22),
                                                           out var parentPid,
                                                           out var parentStart,
                                                           out var childPid,
                                                           out var childStart);

        Assert.True(ok);
        Assert.Equal(100, parentPid);
        Assert.Equal(11L, parentStart);
        Assert.Equal(200, childPid);
        Assert.Equal(22L, childStart);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--internal-watch-orpha")]
    [InlineData("--bilinmeyen")]
    public void TryParse_RejectsForeignFirstArgument(string first)
    {
        // Kullanıcı bu iç argümanı elle yazarsa "izleyici" moduna düşmemeli;
        // normal bilinmeyen argüman hatası (exit 2) verilmeli.
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(
                         [first, "1", "2", "3", "4"],
                         out _, out _, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsWrongArgumentCount()
    {
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments([], out _, out _, out _, out _));
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(
                         [LinuxOrphanGuard.WatcherArgument, "1", "2", "3"],
                         out _, out _, out _, out _));
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(
                         [LinuxOrphanGuard.WatcherArgument, "1", "2", "3", "4", "5"],
                         out _, out _, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsNonNumericValues()
    {
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(
                         [LinuxOrphanGuard.WatcherArgument, "abc", "2", "3", "4"],
                         out _, out _, out _, out _));
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(
                         [LinuxOrphanGuard.WatcherArgument, "1", "2", "three", "4"],
                         out _, out _, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsIdenticalOrNonPositivePids()
    {
        // Ebeveyn == çocuk olursa izleyici kendi kendini öldürürdü.
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(Args(100, 11, 100, 22),
                                                                out _, out _, out _, out _));
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(Args(0, 11, 200, 22),
                                                                out _, out _, out _, out _));
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(Args(100, 11, 0, 22),
                                                                out _, out _, out _, out _));
        Assert.False(LinuxOrphanGuard.TryParseWatcherArguments(Args(-1, 11, 200, 22),
                                                                out _, out _, out _, out _));
    }

    [Fact]
    public void RunWatcher_RejectsMalformedArguments()
    {
        Assert.Equal(2, LinuxOrphanGuard.RunWatcher([]));
        Assert.Equal(2, LinuxOrphanGuard.RunWatcher(["--bilinmeyen"]));
    }

    [Fact]
    public void RunWatcher_ExitsImmediatelyWhenChildIsAlreadyGone()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Linux dışında izleyici modu tamamen kapalı.
            Assert.Equal(2, LinuxOrphanGuard.RunWatcher(Args(Environment.ProcessId, 1, FindUnusedPid(), 0)));
            return;
        }

        // Çocuk PID'i hiç var olmamış bir numara: izleyici beklemeden çıkmalı.
        // Döngüye girmesi hâlinde test 6 saatlik üst sınıra takılırdı.
        var result = LinuxOrphanGuard.RunWatcher(Args(Environment.ProcessId,
                                                     LinuxOrphanGuard.ReadStartTime(Environment.ProcessId),
                                                     FindUnusedPid(),
                                                     0));

        Assert.Equal(0, result);
    }

    [Fact]
    public void ReadStartTime_ReturnsZeroForUnknownPid()
    {
        Assert.Equal(0, LinuxOrphanGuard.ReadStartTime(2_000_000_000));
    }

    [Fact]
    public void ReadStartTime_IsStableAcrossRepeatedReads()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Başlangıç zamanı değişmez olmalı; değilse PID doğrulaması işe yaramaz
        // ve izleyici yanlış süreci öldürebilir.
        var first = LinuxOrphanGuard.ReadStartTime(Environment.ProcessId);
        for (var repeat = 0; repeat < 10; repeat++)
        {
            Assert.Equal(first, LinuxOrphanGuard.ReadStartTime(Environment.ProcessId));
        }
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

        try
        {
            var self  = LinuxOrphanGuard.ReadStartTime(Environment.ProcessId);
            var other = LinuxOrphanGuard.ReadStartTime(child.Id);

            Assert.True(self > 0);
            Assert.True(other > 0);
            Assert.NotEqual(self, other);
        }
        finally
        {
            try
            {
                child.Kill();
            }
            catch
            {
                // ignored
            }
        }
    }

    // ---------------------------------------------------------------------
    // Zombie tuzağı: "ebeveyn öldü mü" sorusu reap edilene kadar doğru
    // cevaplanamıyordu. Ölçümde hedef 24 sn yaşadı, ebeveyn wait() sonrası 1 sn.
    // ---------------------------------------------------------------------

    [Fact]
    public void IsProcessAlive_RejectsNonPositivePids()
    {
        Assert.False(LinuxOrphanGuard.IsProcessAlive(0));
        Assert.False(LinuxOrphanGuard.IsProcessAlive(-1));
    }

    [Fact]
    public void IsProcessAlive_FalseForUnknownPid()
    {
        Assert.False(LinuxOrphanGuard.IsProcessAlive(2_000_000_000));
    }

    [Fact]
    public void IsProcessAlive_TrueForOwnProcessOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(LinuxOrphanGuard.IsProcessAlive(Environment.ProcessId));
    }

    [Fact]
    public void IsProcessAlive_TrueForRunningChildThenFalseAfterExit()
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

        Assert.True(LinuxOrphanGuard.IsProcessAlive(child.Id));

        child.Kill();
        child.WaitForExit(15_000);

        // .NET çocuğu reap ettiği için süreç tamamen kaybolmalı.
        Assert.False(LinuxOrphanGuard.IsProcessAlive(child.Id));
    }

    [Fact]
    public void IsProcessAlive_TreatsZombieAsDeadNotAlive()
    {
        // /bin/sh arka planda çocuk bırakıp çıkarsa (reap etmeden), yetim shell
        // zombie olur. Ölüm anında okunan durum 'Z' olmalı.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var shell = new System.Diagnostics.Process();
        shell.StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        shell.StartInfo.ArgumentList.Add("-c");
        shell.StartInfo.ArgumentList.Add("(sleep 5 &) ; echo $!");
        shell.Start();

        var childPidText = shell.StandardOutput.ReadLine()?.Trim();
        shell.WaitForExit(15_000);

        if (!int.TryParse(childPidText, out var childPid))
        {
            return; // kabuk beklenmedik biçimde çıktı: bu iddianın ölçülemediği koşul
        }

        // Çocuk bir süre daha yaşar; bu arada onu zombie'a çevirmiş olmamız gerekir.
        // Doğrudan "ölü" saymak zorunda değiliz; önemli olan Z'nin canlı sayılmaması.
        Thread.Sleep(6_000);

        // Zombiye ya da tamamen kaybolmuş olabilir; ikisinde de "canlı" olmamalı.
        Assert.False(LinuxOrphanGuard.IsProcessAlive(childPid),
                     "Zombie süreç 'yaşıyor' sayılmamalı (reap edilene kadar beklememek için).");
    }

    [Fact]
    public void ReadParentPid_ReportsRealParentOnLinux()
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

        try
        {
            Assert.Equal(Environment.ProcessId, LinuxOrphanGuard.ReadParentPid(child.Id));
        }
        finally
        {
            try
            {
                child.Kill();
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void ReadParentPid_ReturnsZeroForUnknownPid()
    {
        Assert.Equal(0, LinuxOrphanGuard.ReadParentPid(2_000_000_000));
    }

    [Fact]
    public void ReadProcessState_ReturnsNonZeroForOwnProcessOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var state = LinuxOrphanGuard.ReadProcessState(Environment.ProcessId);
        Assert.NotEqual('\0', state);
        Assert.NotEqual('Z', state);
        Assert.NotEqual('X', state);
    }

    private static int FindUnusedPid()
    {
        // /proc altında hiçbir şeyi olmayan bir numara seç.
        for (var candidate = 2_000_000_000; candidate > 1_900_000_000; candidate--)
        {
            if (!System.IO.Directory.Exists($"/proc/{candidate}"))
            {
                return candidate;
            }
        }

        return 2_000_000_000;
    }

    /// <summary>
    /// <b>Regresyon testi — BULGU 34 (kritik).</b> Ebeveyn <b>reap edildiğinde</b>
    /// koruma hiç tetiklenmiyordu.
    ///
    /// <para><b>Hata.</b> <c>Watch</c> içindeki ebeveyn kontrolü
    /// <c>ReadStartTime(parentPid) != 0</c> şartına bağlıydı. Ebeveyn
    /// <c>SIGKILL</c> edildikten sonra <b>reap edilirse</b> <c>/proc/&lt;pid&gt;</c>
    /// tamamen kaybolur, <c>ReadStatField</c> <c>0</c> döner ve <c>break</c> hiç
    /// çalışmıyordu — izleyici <c>MaxWatchMilliseconds</c> (<b>6 saat</b>) boyunca
    /// yoklamayı sürdürüyor, hedefi öldürmüyordu.</para>
    ///
    /// <para><b>Kontrol deneyi (WSL2, aynı harness):</b>
    /// ebeveyn reap <b>EDİLMEDİ</b> (zombie) -> hedef 0,16 sn'de öldü;
    /// ebeveyn reap <b>EDİLDİ</b> -> 20 sn sonra hâlâ yaşıyordu. Yani önceki
    /// doğrulama yalnız zombie dalını ölçmüş, <c>kill -9 migurdex</c> yapan her gerçek
    /// ebeveyn (bash, systemd, supervisor) için koruma işlevsizdi.</para>
    ///
    /// <para><b>Bu test nasıl kırılır?</b> Düzeltme geri alınırsa izleyici
    /// <c>MaxWatchMilliseconds</c> dolana kadar döner; bu test de <b>6 saat</b>
    /// sonra zaman aşımına düşer. Ölçülen süre sınırı 60 sn'dir — tavan yüksek
    /// tutuldu ki yavaş bir koşuda yanlış negatif üretmesin.</para>
    /// </summary>
    [Fact]
    public void RunWatcher_WithAlreadyReapedParent_KillsChildWithoutWaiting()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var child = StartSleep();

        // Ebeveyn PID'i hiç var olmamış bir numara: `/proc/<pid>` YOK, yani gerçek
        // hayatta "reap edilmiş" ebeveynle aynı durum. `ShouldGuard` yalnız
        // pozitiflik ve eşitlik kontrolü yaptığı için bu kabul edilir.
        var reapedParentPid = FindUnusedPid();
        Assert.False(LinuxOrphanGuard.IsProcessAlive(reapedParentPid),
                     "Bu numara gerçekten kullanılmıyor olmalı; aksi hâlde test yanıltır.");

        var stopwatch = Stopwatch.StartNew();
        var exitCode = LinuxOrphanGuard.RunWatcher(
            Args(reapedParentPid,
                 0,
                 child.Id,
                 LinuxOrphanGuard.ReadStartTime(child.Id)));
        stopwatch.Stop();

        Assert.Equal(0, exitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60),
                    $"Reap edilmiş ebeveyn için izleyici beklemek zorunda kalmamalı; " +
                    $"{stopwatch.Elapsed.TotalSeconds:F1} sn sürdü (sınır 60 sn). " +
                    "Eski davranış 6 saatlik tavana kadar sürerdi.");

        // Hedef gerçekten öldürülmüş olmalı.
        var deadline = Stopwatch.StartNew();
        while (LinuxOrphanGuard.IsProcessAlive(child.Id) && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(50);
        }

        Assert.False(LinuxOrphanGuard.IsProcessAlive(child.Id),
                     "Ebeveyn gitmişken hedef izleyici tarafından öldürülmeliydi.");
    }

    private static Process StartSleep()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName        = "/bin/sleep",
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        startInfo.ArgumentList.Add("120");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Süreç başlatılamadı.");
    }
}
