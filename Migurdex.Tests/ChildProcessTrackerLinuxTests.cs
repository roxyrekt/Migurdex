using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Utils;
using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// <c>ChildProcessTracker.Track</c>'in Linux yetim korumasını da kapsadığının
/// kanıtı.
///
/// <para><b>Düzeltilen hata.</b> <c>Track</c> baştan itibaren
/// <c>if (!OperatingSystem.IsWindows() || ...) return;</c> ile başlıyordu;
/// Linux'ta <b>hiçbir şey yapmıyordu</b>. Ölçülen sonuç: <c>Track</c> çağıran
/// iki yol (mpv oynatıcı, API daemon) Linux'ta korumasızdı —
/// <c>SIGKILL</c> sonrası yetim mpv ve oturum kapanana kadar yaşayan yetim
/// API daemon. Artık <c>Track</c>, Linux'ta
/// <see cref="LinuxOrphanGuard.Attach"/> çağırır; böylece <c>Track</c> çağıran
/// <b>her</b> yer (ve ileride eklenecek her yeni çağıran) kapsanır.</para>
///
/// <para><b>Windows değişmedi.</b> Job Object yolu aynen korunur ve bu testler
/// Windows CI'da da koşarak o sözleşmeyi doğrular. Linux'a özgü ölçümler
/// <c>if (!OperatingSystem.IsLinux()) return;</c> ile korunur.</para>
/// </summary>
public sealed class ChildProcessTrackerLinuxTests
{
    /// <summary>
    /// Sözleşme: <c>Track</c> <b>asla istisna atmaz</b>. Koruma bir iyileştirmedir;
    /// kurulamazsa oynatma/daemon/indirme normal devam etmelidir. Özellikle
    /// Linux'ta yeni eklenen <c>LinuxOrphanGuard.Attach</c> yolu, çağıranın
    /// oynatmayı/daemon'u düşürmemesi için güvenli olmak zorunda.
    /// </summary>
    [Fact]
    public void Track_NullProcess_DoesNotThrow()
    {
        ChildProcessTracker.Track(null!);
    }

    /// <summary>
    /// Gerçekten çalışmış (ve bitmiş) bir süreçte çağrı: istisna atmamalı,
    /// çağıran tarafın <c>WaitForExit</c> beklemesi engellenmemeli. Hem
    /// Windows (Job Object ataması) hem Linux (Attach'in <c>HasExited</c>
    /// erken çıkışı) yolunu gezer.
    /// </summary>
    [Fact]
    public void Track_AlreadyExitedProcess_DoesNotThrowAndStaysExited()
    {
        using var process = StartShortLived();
        Assert.True(process.WaitForExit(30_000), "Kısa ömürlü süreç başlatıldıktan sonra bitmeliydi.");

        ChildProcessTracker.Track(process);

        Assert.True(process.HasExited);
    }

    /// <summary>
    /// Hâlâ yaşayan bir süreçte çağrı: koruma kurulmaya çalışılır, istisna
    /// atmaz. Windows'ta Job Object, Linux'ta izleyici yolu çalışır.
    /// </summary>
    [Fact]
    public void Track_RunningProcess_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windows: kısa ömürlü süreç yeterli — Job Object ataması hata
            // verse bile `Track` yutmalı.
            using var shortLived = StartShortLived();
            ChildProcessTracker.Track(shortLived);
            Assert.True(shortLived.WaitForExit(30_000));
            return;
        }

        using var child = StartSleep();
        try
        {
            ChildProcessTracker.Track(child);
            Assert.False(child.HasExited, "Track çağrısı hedef süreci öldürmemeli.");
        }
        finally
        {
            KillQuietly(child);
        }
    }

    /// <summary>
    /// <b>Asıl düzeltmenin ölçümü.</b> Linux'ta <c>Track</c> çağrısından sonra
    /// hedef süreç için bir izleyici süreci <b>doğmalıdır</b>. Bu, eski
    /// davranışın (sessizce hiçbir şey yapmama) doğrudan tersidir ve
    /// <c>/proc/&lt;pid&gt;/cmdline</c> üzerinden ölçülür: izleyici, hedef PID'i
    /// argüman listesinde taşır. Aranan imza benzersizdir, bu yüzden aynı anda
    /// koşan diğer testlerin izleyicileriyle karışmaz.
    /// </summary>
    [Fact]
    public void Track_OnLinux_StartsWatcherProcessForTrackedChild()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var child = StartSleep();
        try
        {
            Assert.False(child.HasExited, "İzleyici doğmadan önce hedef süreç yaşıyor olmalı.");

            ChildProcessTracker.Track(child);

            // `Process.Start` senkron olduğu için süreç fork/exec sonrası
            // /proc'da anında görünür; yine de yeniden başlatma (fork/exec
            // yarışı) ve kısa süreli izleyici ömrü için kısa aralıklarla
            // yoklama yapılır. İzleyici hedef öldükçe en geç 500 ms içinde
            // kapanacağı için pencere dar ama yeterlidir.
            var deadline = Environment.TickCount64 + 10_000;
            var found    = false;
            while (Environment.TickCount64 < deadline && !found)
            {
                found = WatcherExistsFor(child.Id);
                if (!found)
                {
                    Thread.Sleep(10);
                }
            }

            Assert.True(found,
                        "Track, Linux'ta hedef süreç için LinuxOrphanGuard izleyicisi başlatmalıydı.");
        }
        finally
        {
            // Hedefi öldür: izleyici kendiliğinden çıksın. Kalan izleyici
            // süreçleri de temizle ki test, arkasında sürgüç bırakmasın.
            KillQuietly(child);
            KillWatchersFor(child.Id);
        }
    }

    /// <summary>
    /// Karşı ölçüm: bitmiş bir süreç için izleyici <b>doğmamalıdır</b>.
    /// <c>Attach</c> zaten <c>HasExited</c> kontrolü yapıyor; bu kontrol
    /// yanlışlıkla kalkarsa ölçümde ortaya çıkar (işe yaramayan izleyici, üst
    /// yaşam sınırına kadar boşuna yaşar). Ölçüm hedef PID'e özeldir; aynı anda
    /// koşan diğer testlerin izleyicileriyle karışmaz.
    /// </summary>
    [Fact]
    public void Track_OnLinux_DoesNotStartWatcherForExitedProcess()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var exited = StartShortLived();
        Assert.True(exited.WaitForExit(30_000));

        ChildProcessTracker.Track(exited);

        Thread.Sleep(500);

        Assert.False(WatcherExistsFor(exited.Id),
                     "Çıkmış bir süreç için izleyici başlatılmamalıydı.");
    }

    /// <summary>
    /// <b>Zincir riski ölçümü.</b> İzleyicinin <em>kendisi</em> dış süreç
    /// başlatmamalı / <c>Track</c> çağırmamalı; aksi halde her izleyici yeni bir
    /// izleyici doğurur. İzleyicinin giriş noktası (<c>RunWatcher</c>), hedef
    /// zaten ölmüşken çağrılır: <b>hiçbir izleyici doğurmadan</b> hızlıca
    /// dönmelidir. Ölçüm, bizim sürecimizin çocukları arasında izleyici imzalı
    /// süreç sayısıdır — <c>RunWatcher</c> doğurabilecek tek süreç budur.
    /// </summary>
    [Fact]
    public void RunWatcher_WithDeadChild_SpawnsNoFurtherWatcher()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var deadChild = FindUnusedPid();

        // Olcum **tum** izleyici cocuklarini degil, **yalnizca bu hedefi izleyenleri**
        // sayar.
        //
        // Neden: genel sayaç ("bizim cocugumuz olan izleyici sayisi") ayni siniftaki
        // diger testlerin izleyicilerini de kapsiyor. Onlar kendi sureclerini
        // biraktiginda bu testin taban cizgisi kendiliğinden degisiyor ve test
        // deterministik olarak kiriyordu. Olcum: 3 kosunun 3unde de
        // "Expected: 1, Actual: 0" - yani onceki testin izleyicisi taban cizgiden
        // sonra sonuyordu, bu testin hicbir seyi yanlisi degildi.
        //
        // Izleyicinin komut satirinda `--internal-watch-orphan <ebeveyn> <baslangic>
        // <hedef> <hedefBaslangic>` bulunur; yani dogru dogan izleyici **bu** hedef
        // PID'sini tasir. Hedef PID imzasi olcumu bu teste ozlestirir.
        var before = FindWatcherPids(deadChild).Count;

        var exitCode = LinuxOrphanGuard.RunWatcher(
        [
            LinuxOrphanGuard.WatcherArgument,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            LinuxOrphanGuard.ReadStartTime(Environment.ProcessId)
                .ToString(CultureInfo.InvariantCulture),
            deadChild.ToString(CultureInfo.InvariantCulture),
            "0"
        ]);

        // Hedef ölü olduğu için izleyici işini bitirip 0 döner; beklemez.
        Assert.Equal(0, exitCode);

        Thread.Sleep(500);

        Assert.Equal(before, FindWatcherPids(deadChild).Count);
    }

    // ---------------------------------------------------------------------
    // Yardımcılar
    // ---------------------------------------------------------------------

    private static Process StartShortLived()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit 0");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exit 0");
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Süreç başlatılamadı.");
        return process;
    }

    private static Process StartSleep()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName        = "/bin/sleep",
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        startInfo.ArgumentList.Add("60");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Süreç başlatılamadı.");
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.WaitForExit(10_000);
        }
        catch
        {
            // ignored
        }
    }

    private static void KillWatchersFor(int childPid)
    {
        foreach (var pid in FindWatcherPids(childPid))
        {
            try
            {
                using var watcher = Process.GetProcessById(pid);
                watcher.Kill();
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    /// Verilen çocuk PID'ini izleyen bir izleyici süreci var mı? İmza:
    /// komut satırında <c>--internal-watch-orphan</c> <b>ve</b> hedef PID geçiyor.
    /// </summary>
    private static bool WatcherExistsFor(int childPid)
    {
        return FindWatcherPids(childPid).Count > 0;
    }

    private static List<int> FindWatcherPids(int childPid)
    {
        var matches  = new List<int>();
        var childText = childPid.ToString(CultureInfo.InvariantCulture);

        foreach (var directory in EnumerateProcDirectories())
        {
            var pid = ParseProcDirectoryName(directory);
            var cmdline = ReadCmdline(directory);
            if (cmdline is null
                || !cmdline.Contains(LinuxOrphanGuard.WatcherArgument, StringComparison.Ordinal))
            {
                continue;
            }

            // İmza yalnız izleyici argümanıyla yetmez: aynı anda koşan başka
            // testlerin izleyicileri de o argümanı taşır. Hedef PID eşleşmesi
            // ölçümü bu testin izleyicisine özel kılar.
            if (cmdline.Split('\0').Contains(childText, StringComparer.Ordinal))
            {
                matches.Add(pid);
            }
        }

        return matches;
    }

    /// <summary>
    /// Bizim sürecimizin doğrudan çocukları arasında izleyici imzalı süreç
    /// sayısı. Bir izleyicinin doğurabileceği tek yeni süreç de bizim çocuğumuz
    /// olacağı için bu, "izleyici kendini izliyor mu" sorusunun doğrudan
    /// ölçümüdür.
    /// </summary>
    private static int CountOurWatcherChildren()
    {
        var count = 0;
        foreach (var directory in EnumerateProcDirectories())
        {
            if (LinuxOrphanGuard.ReadParentPid(ParseProcDirectoryName(directory)) != Environment.ProcessId)
            {
                continue;
            }

            var cmdline = ReadCmdline(directory);
            if (cmdline is not null
                && cmdline.Contains(LinuxOrphanGuard.WatcherArgument, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> EnumerateProcDirectories()
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories("/proc");
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (ParseProcDirectoryName(directory) > 0)
            {
                yield return directory;
            }
        }
    }

    private static int ParseProcDirectoryName(string directory)
    {
        return int.TryParse(Path.GetFileName(directory), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var pid)
               ? pid
               : 0;
    }

    private static string? ReadCmdline(string procDirectory)
    {
        try
        {
            // /proc/<pid>/cmdline NUL ile ayrılmış ham baytlardır; okuma
            // sırasında süreç ölmüş olabilir.
            return File.ReadAllText(Path.Combine(procDirectory, "cmdline"));
        }
        catch
        {
            return null;
        }
    }

    private static int FindUnusedPid()
    {
        for (var candidate = 2_000_000_000; candidate > 1_900_000_000; candidate--)
        {
            if (!Directory.Exists($"/proc/{candidate}"))
            {
                return candidate;
            }
        }

        return 2_000_000_000;
    }
}
