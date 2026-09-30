using Migurdex.Cli.Utils;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Migurdex.Cli.Services.Downloads;

public sealed class ExternalProcessResult
{
    public ExternalProcessResult()
    {
    }

    public ExternalProcessResult(
        int                      exitCode,
        string                   standardOutput = "",
        string                   standardError  = "",
        IReadOnlyList<string>?   outputFiles    = null,
        bool                     outputTruncated = false)
    {
        ExitCode        = exitCode;
        StandardOutput  = standardOutput;
        StandardError   = standardError;
        OutputFiles     = outputFiles ?? [];
        OutputTruncated = outputTruncated;
    }

    public int                ExitCode        { get; init; }
    public string             StandardOutput  { get; init; } = string.Empty;
    public string             StandardError   { get; init; } = string.Empty;
    public IReadOnlyList<string> OutputFiles  { get; init; } = [];
    public string?            OutputPath      { get; init; }
    public bool               OutputTruncated { get; init; }

    public int    ReturnCode => ExitCode;
    public string StdOut    => StandardOutput;
    public string StdErr    => StandardError;
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public const int MaxCapturedCharactersPerStream = 256 * 1024;
    public const int MaxCapturedLineCharacters       = 16 * 1024;
    public const int MaxProgressCallbacks            = 4096;

    private static readonly TimeSpan OutputDrainTimeout   = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessStopTimeout   = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PipeStopTimeout      = TimeSpan.FromSeconds(2);

    public Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo  startInfo,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(startInfo, onStandardOutput: null, cancellationToken);
    }

    public Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo  startInfo,
        Action<string>?   onStandardOutput,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(startInfo, onStandardOutput, cancellationToken);
    }

    private static async Task<ExternalProcessResult> RunCoreAsync(
        ProcessStartInfo  startInfo,
        Action<string>?   onStandardOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        startInfo.UseShellExecute        = false;
        startInfo.CreateNoWindow         = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError  = true;

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                throw new ExternalProcessStartException("Dış araç başlatılamadı.");
            }
        }
        catch (Win32Exception)
        {
            throw new ExternalProcessStartException("Dış araç bulunamadı veya başlatılamadı.");
        }
        catch (FileNotFoundException)
        {
            throw new ExternalProcessStartException("Dış araç bulunamadı veya başlatılamadı.");
        }

        ChildProcessTracker.Track(process);
        LinuxOrphanGuard.Attach(process);
        using var cancellationRegistration = cancellationToken.Register(() => TryKillProcessTree(process));
        using var pipeCancellation          = new CancellationTokenSource();
        var standardOutputTask = CaptureAsync(process.StandardOutput,
                                              onStandardOutput,
                                              pipeCancellation.Token);
        var standardErrorTask = CaptureAsync(process.StandardError,
                                             onStandardOutput,
                                             pipeCancellation.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask)
                          .WaitAsync(OutputDrainTimeout)
                          .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await CleanupAfterTerminationAsync(process,
                                                   standardOutputTask,
                                                   standardErrorTask,
                                                   pipeCancellation)
                    .ConfigureAwait(false);
                throw new ExternalProcessStartException("Dış aracın çıktı akışları zamanında kapatılmadı.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError  = await standardErrorTask.ConfigureAwait(false);
            return new ExternalProcessResult(process.ExitCode,
                                              standardOutput.Text,
                                              standardError.Text,
                                              outputTruncated: standardOutput.Truncated
                                                                  || standardError.Truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupAfterTerminationAsync(process,
                                               standardOutputTask,
                                               standardErrorTask,
                                               pipeCancellation)
                .ConfigureAwait(false);
            throw;
        }
        catch (ExternalProcessStartException)
        {
            throw;
        }
        catch (IOException)
        {
            await CleanupAfterTerminationAsync(process,
                                               standardOutputTask,
                                               standardErrorTask,
                                               pipeCancellation)
                .ConfigureAwait(false);
            throw new ExternalProcessStartException("Dış araç çıktısı okunamadı.");
        }
        catch (Exception)
        {
            await CleanupAfterTerminationAsync(process,
                                               standardOutputTask,
                                               standardErrorTask,
                                               pipeCancellation)
                .ConfigureAwait(false);
            throw new ExternalProcessStartException("Dış araç çıktısı okunamadı.");
        }
    }

    private static async Task<CaptureResult> CaptureAsync(
        StreamReader     reader,
        Action<string>?  onLine,
        CancellationToken cancellationToken)
    {
        var text         = new StringBuilder();
        var line         = new StringBuilder();
        var buffer       = new char[4096];
        var callbackCount = 0;
        var lineTruncated = false;
        var truncated     = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                                   .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\r')
                {
                    continue;
                }

                if (character == '\n')
                {
                    EmitLine(line,
                             lineTruncated,
                             text,
                             ref truncated,
                             onLine,
                             ref callbackCount);
                    line.Clear();
                    lineTruncated = false;
                    continue;
                }

                if (line.Length < MaxCapturedLineCharacters)
                {
                    line.Append(character);
                }
                else
                {
                    lineTruncated = true;
                }

                if (text.Length < MaxCapturedCharactersPerStream)
                {
                    text.Append(character);
                }
                else
                {
                    truncated = true;
                }
            }
        }

        if (line.Length > 0 || lineTruncated)
        {
            EmitLine(line,
                     lineTruncated,
                     text,
                     ref truncated,
                     onLine,
                     ref callbackCount);
        }

        if (truncated)
        {
            const string marker = "\n[çıktı sınırına ulaşıldı]";
            if (text.Length > marker.Length)
            {
                text.Length = text.Length - marker.Length;
            }

            text.Append(marker);
        }

        return new CaptureResult(text.ToString(), truncated);
    }

    private static void EmitLine(
        StringBuilder    line,
        bool             lineTruncated,
        StringBuilder    text,
        ref bool         truncated,
        Action<string>?  onLine,
        ref int          callbackCount)
    {
        if (lineTruncated)
        {
            truncated = true;
        }

        if (text.Length < MaxCapturedCharactersPerStream)
        {
            text.Append('\n');
        }
        else
        {
            truncated = true;
        }

        if (onLine is not null && callbackCount < MaxProgressCallbacks)
        {
            var value = line.ToString();
            if (lineTruncated)
            {
                value += "…";
            }

            try
            {
                onLine(value);
            }
            catch
            {
                // İlerleme geri çağrısı süreç akışını bozamalıdır.
            }

            callbackCount++;
        }
    }

    private static async Task CleanupAfterTerminationAsync(
        Process                  process,
        Task<CaptureResult>      standardOutputTask,
        Task<CaptureResult>      standardErrorTask,
        CancellationTokenSource pipeCancellation)
    {
        TryKillProcessTree(process);

        using (var processStop = new CancellationTokenSource(ProcessStopTimeout))
        {
            try
            {
                await process.WaitForExitAsync(processStop.Token).ConfigureAwait(false);
            }
            catch
            {
                // bounded cleanup; süreç sonlanmasa bile sonsuza kadar beklenmez
            }
        }

        pipeCancellation.Cancel();
        using (var pipeStop = new CancellationTokenSource(PipeStopTimeout))
        {
            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask)
                          .WaitAsync(pipeStop.Token)
                          .ConfigureAwait(false);
            }
            catch
            {
                // Kalan pipe görevleri gözlemlenir; işlenmemiş exception bırakılmaz.
            }
        }

        ObserveFault(standardOutputTask);
        ObserveFault(standardErrorTask);
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(completed => _ = completed.Exception,
                              CancellationToken.None,
                              TaskContinuationOptions.OnlyOnFaulted
                              | TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private sealed record CaptureResult(string Text, bool Truncated);
}

/// <summary>
/// C4: Linux'ta zorla sonlandırılmış ana süreç yetim alt süreç bırakıyordu.
///
/// Windows'ta <see cref="ChildProcessTracker"/> bir Job Object
/// (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>) kullanıyor: job handle kapandığında
/// tüm alt süreçler de ölüyor. Linux'ta bu karşılığı yok ve
/// <c>PR_SET_PDEATHSIG</c> uygulanmadığı için Migurdex <c>SIGKILL</c> ile
/// öldürüldüğünde <c>yt-dlp</c>/<c>ffmpeg</c> yetim olarak kalmaya devam ediyor
/// (indirmeyi sürdürür, diski doldurur, bant genişliği çalar).
///
/// <para><b>Neden fork/exec sarmalayıcısı + prctl DEĞİL.</b> Tuzak şudur:
/// <c>PR_SET_PDEATHSIG</c> thread ölünce değil <em>process</em> ölünce tetiklenir
/// ve <c>prctl</c> çocuk süreçte, <c>fork</c> ile <c>exec</c> arasında
/// çağrılmalıdır. .NET'in <see cref="Process.Start(ProcessStartInfo)"/> yolu
/// fork+exec'i dışarıda, araya girebileceğimiz bir nokta bırakmadan yapar.
/// Bunu aşmanın tek yolu indirmeyi <c>/bin/sh -c '...'</c> sarmalayıcısına
/// sokmaktır — ki bu da "yanlış seçim gerçek indirmeyi bozar" riskinin tam
/// kendisidir:</para>
/// <list type="bullet">
///   <item>Argümanlar shell kaçış kurallarıyla bozulur (URL, başlık, yol).</item>
///   <item>Sarmalayıcı <c>exec</c> ile değiştirilmezse izlenen süreç shell'dir;
///         iptal/hata kodu ve sinyal semantiği kayar.</item>
///   <item>Ekstra süreç katmanı <c>entireProcessTree</c> öldürme ile
///         <c>--paths</c>/stdout eşlemesini değiştirir.</item>
///   <item>Yeni bir çalışma zamanı bağımlılığı (sh/dash/busybox farkları).</item>
/// </list>
///
/// <para><b>ÖLÇÜMLE DOĞRULANMIŞ TASARIM HATASI (düzeltildi).</b> İlk sürüm
/// izlemeyi ebeveyn sürecin <em>içinde</em> bir thread ile yapıyordu. Bu
/// yapısal olarak işe yaramaz: ana süreç <c>SIGKILL</c> ile öldüğünde thread de
/// anında ölür ve hedef hiç öldürülmez. Linux ölçümü bunu açıkça gösterdi —
/// guard thread'i gerçekten başlamıştı (<c>/proc/16767/task/16779</c> →
/// <c>comm = migurdex-linux-</c>) ama korumalı <b>4/4 sessiz alt süreç
/// <c>kill -9</c> sonrası yetim kaldı</b>. Gözlenen "temizlik" kırılan stdout
/// borusundan geliyordu; stdout'a yazan bir <c>bash</c> döngüsü 6 sn yaşamaya
/// devam etti. Yani düzeltme hiçbir işe yaramıyordu ve yorumu da yanlış güvence
/// veriyordu. İzleyici bu yüzden artık <b>ayrı, bağımsız bir Migurdex
/// sürecidir</b>: ebeveyn öldüğünde o yaşamaya devam eder.</para>
///
/// <para><b>Zombie tuzagi - olum degil, <c>wait()</c> ani.</b> Ilk duzeltmede
/// "ebeveyn oldu mu" sorusu <c>/proc/&lt;pid&gt;/stat</c>'in <em>baslangic zamanina</em>
/// (<c>kill(pid,0)</c> ve <c>ReadStartTime</c>) bakarak yanitlaniyordu. Her ikisi de
/// bir <b>zombie</b> surecte hala "yasiyor" sonucu verir: surec olmustur ama ebeveyni
/// onu reap etmeden <c>/proc</c> kaydi duruyordur. Olculen sonuc: hedef surec
/// <b>24 saniye</b> yasadi; ebeveyn <c>wait()</c> cagrisini yaptiktan <b>1 saniye</b>
/// icinde olduruldu. Yani Migurdex'i reap etmeyen her ebeveyn icin (python
/// <c>subprocess</c> <c>wait()</c>'siz, arka planda baslatip devam eden kabuk,
/// alismadik reaping yapan supervisor) koruma sifirdi.</para>
///
/// <para><b>Torun sorunu.</b> <c>ffmpeg</c> hicbir zaman Migurdex'in dogrudan
/// cocugu degildir - daima <c>yt-dlp</c>'nin cocugudur. Yalniz dogrudan cocugu
/// oldurmek, birlestirme gerektiren akislarda <c>ffmpeg</c>'i yetim birakiyordu.
/// Olculdu: korunan <c>bash</c> sarmalayici &lt;1 saniyede oldu, <c>torunu</c>
/// (<c>sleep 900</c>) 18 saniye yasamaya devam etti. Bu yuzden izleyici
/// <c>/proc</c>'yu tarayip dogrulanmis koku <b>tum torunlarini</b> oldurur.</para>
///
/// <para><b>Yanlis atif duzeltmesi.</b> Onceki incelemede "gozlenen temizlik kirilan
/// stdout borusundan geliyor" denmisti. Kontrollu deney bunu <em>curuttu</em>:
/// Python 8 KiB blok tamponladigi icin tampon dolmadan <c>write</c> olmuyor ve
/// <c>EPIPE</c> olusmuyor - boruya bagli <c>yt-dlp</c> de <c>kill -9</c> sonrasi
/// 12 saniye yasadi. Gercek akistaki temizlik guard'in isidir.</para>
///
/// <para><b>Kabul edilen risk ve maliyet.</b> (1) İzleyici ayrı bir .NET
/// .NET sürecidir; indirme boyunca ek bir çalışma zamanı örneği yaşar (kısa
/// ömürlü — hedef bitince ya da ebeveyn gidince hemen çıkar, ayrıca 6 saatlik
/// bir üst sınırı vardır). (2) Migurdex <c>exec</c> ile değiştirilirse (POSIX)
/// beklenen ebeveyn değişimi olmaz ve izleme işe yaramaz; bu yalnız "yetim kalır"
/// durumunu yeniden üretir, mevcut davranıştan kötü değildir. (3) Terminal
/// kapatılırsa izleyici <c>SIGHUP</c> alabilir — o durumda zaten graceful iptal
/// yolu çalışıyor ve korumaya gerek yok. (4) Ters yönlü hata (yaşayan sürece
/// zarar) PID geri dönüşümüyle mümkündür: hedef süreç kendiliğinden ölürse numara
/// başka bir sürece verilebilir. Bu yüzden koruma yalnız numarayı değil,
/// <c>/proc/&lt;pid&gt;/stat</c> başlangıç zamanını da doğrular ve ikisi
/// eşleşmiyorsa hiçbir şey öldürmez. <c>kill(pid, 0)</c> <c>EPERM</c> dönerse
/// süreç "yaşıyor" sayılır ve öldürülmez.</para>
///
/// <para><b>Seçilen yöntem: süreç-sonrası izleme.</b> Düşük riskli olmasının
/// nedenleri: (1) hiçbir komut satırı, argüman veya çalışma dizini
/// değişmez — indirme yolu birebir aynı kalır; (2) izleme kendi PID'mizi
/// bekler, ana süreç zorla öldürülünce ebeveyn değiştiği için "parent öldü"
/// sinyali PID yeniden kullanımından bağımsız gelir; (3) yalnız Linux'ta
/// aktiftir — Windows'ta Job Object zaten doğru davranışı verir, macOS'ta
/// <c>PR_SET_PDEATHSIG</c> yoktur.</para>
///
/// <para><b>Kabul edilen risk.</b> Migurdex <c>exec</c> ile değiştirilirse
/// (POSIX) beklenen ebeveyn değişimi olmaz ve izleme işe yaramaz; bu yalnız
/// "yetim kalır" durumunu yeniden üretir, mevcut davranıştan kötü değildir.
/// Ters yönde hata (yaşayan sürece zarar) PID geri dönüşümüyle mümkündü: hedef
/// süreç kendiliğinden ölürse numara başka bir sürece verilebilir. Bu yüzden
/// koruma artık yalnız numarayı değil, <c>/proc/&lt;pid&gt;/stat</c> başlangıç
/// zamanını da doğrular ve ikisi eşleşmiyorsa hiçbir şey öldürmez.</para>
/// </summary>
internal static class LinuxOrphanGuard
{
    /// <summary>İzleyici sürecinin kendi iç argümanı. Kullanıcıya açık değildir.</summary>
    internal const string WatcherArgument = "--internal-watch-orphan";

    private const int PollIntervalMilliseconds = 500;

    /// <summary>
    /// İzleyicinin en fazla yaşama süresi. Kalıcı sürgüç olmaması için sınırlı;
    /// iş dizini süpürmesiyle (6 saat) aynı mertebededir.
    /// </summary>
    private const long MaxWatchMilliseconds = 6L * 60 * 60 * 1000;

    private const int Sigkill = 9;
    private const int Esrch   = 3;

    // `/proc/<pid>/stat` alan numaraları. `comm` (2. alan) parantez içinde olduğu
    // için dizin 3. alandan başlar ve `N. alan -> N - 3` ile eşlenir.
    private const int StateFieldIndex      = 0;  // 3. alan  (R/S/D/Z/T/...)
    private const int ParentPidFieldIndex  = 1;  // 4. alan  (ppid)
    private const int StartTimeFieldIndex  = 19; // 22. alan (starttime)

    /// <summary>Yalnızca Linux'ta etkin; diğer platformlarda tüm API no-op'tur.</summary>
    internal static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Koruma için hedef PID geçerli mi? Birim testi ve teşhis için açık tutuldu.
    /// </summary>
    internal static bool ShouldGuard(int parentPid, int childPid)
    {
        return IsSupported && parentPid > 0 && childPid > 0 && parentPid != childPid;
    }

    /// <summary>
    /// Dış süreci Linux yetim korumasına bağlar. Kurulum başarısız olursa
    /// sessizce geçilir: koruma bir iyileştirmedir, indirmenin çalışma
    /// önkoşulu değildir.
    ///
    /// <b>Neden ayrı bir süreç?</b> Önceki sürüm izlemeyi ebeveyn sürecin İÇİNDE
    /// bir thread ile yapıyordu. Bu yapısal olarak işe yaramıyordu: ana süreç
    /// <c>SIGKILL</c> ile öldüğünde thread de anında ölür ve
    /// <see cref="KillIfAlive"/> hiç çalışamaz. Linux ölçümü: korumalı 4 sessiz
    /// alt süreç <c>kill -9</c> sonrası <b>4/4 yetim kaldı</b>; guard thread'i
    /// gerçekten başlıyordu (<c>/proc/16767/task/16779</c> →
    /// <c>comm = migurdex-linux-</c>), yani yanlış yerdeydi. Gözlenen temizlik
    /// kırılan stdout borusundan geliyordu, korumadan değil.
    ///
    /// Bu yüzden izleyici artık <b>ayrı, bağımsız bir Migurdex süreci</b> olarak
    /// başlatılır: ebeveyn öldüğünde o yaşamaya devam eder. Ek shell bağımlılığı
    /// (<c>sh</c>/<c>setsid</c>/<c>awk</c>) yoktur — PID ve başlangıç zamanı
    /// doğrulaması yine yönetilen kodda yapılır.
    /// </summary>
    public static void Attach(Process process)
    {
        if (!IsSupported || process is null)
        {
            return;
        }

        try
        {
            var childPid = process.Id;
            var parentPid = Environment.ProcessId;
            if (!ShouldGuard(parentPid, childPid) || process.HasExited)
            {
                return;
            }

            // PID tek başına kimlik değildir: hedef süreç kendiliğinden ölürse
            // Linux aynı numarayı başka bir sürece verebilir ve izleyici yanlış
            // sürececeği için ilgisiz bir sürece SIGKILL yollar. Başlangıç zamanı
            // (`/proc/<pid>/stat` 22. alan) PID ile birlikte kimlik oluşturur.
            var childStartTime = ReadStartTime(childPid);

            var startInfo = CreateWatcherStartInfo(parentPid, childPid, childStartTime);
            if (startInfo is null)
            {
                return;
            }

            var watcher = Process.Start(startInfo);
            if (watcher is null)
            {
                return;
            }

            // Çıktı yönlendirmesini boşalt; izleyici sessiz olmalı.
            watcher.BeginOutputReadLine();
            watcher.BeginErrorReadLine();
        }
        catch
        {
            // Koruma kurulamadı; indirme normal devam eder.
        }
    }

    /// <summary>
    /// İzleyicinin kendi çizelgesi. Ebeveyn süreç yeniden yorumlanmadan önce
    /// ölürse bu komut hiç çalışmaz; yalnız <c>Attach</c> tarafından üretilir.
    /// </summary>
    internal static int RunWatcher(string[] args)
    {
        if (!TryParseWatcherArguments(args, out var parentPid, out var parentStartTime,
                                       out var childPid, out var childStartTime))
        {
            return 2;
        }

        return Watch(parentPid, parentStartTime, childPid, childStartTime);
    }

    internal static bool TryParseWatcherArguments(
        string[] args,
        out int  parentPid,
        out long parentStartTime,
        out int  childPid,
        out long childStartTime)
    {
        parentPid = 0;
        parentStartTime = 0;
        childPid = 0;
        childStartTime = 0;

        if (args is null || args.Length != 5
            || !string.Equals(args[0], WatcherArgument, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid)
               && long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out parentStartTime)
               && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out childPid)
               && long.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out childStartTime)
               && ShouldGuard(parentPid, childPid);
    }

    private static ProcessStartInfo? CreateWatcherStartInfo(
        int  parentPid,
        int  childPid,
        long childStartTime)
    {
        var host = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute       = false,
            CreateNoWindow        = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        // Framework-dependent çalıştırmada host `dotnet`'tir ve DLL yolu ayrıca
        // verilmelidir; apphost (kendi çalıştırılabilir dosyası) kullanılıyorsa
        // gerekmez.
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet",
                          StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly))
            {
                return null;
            }

            startInfo.FileName = host;
            startInfo.ArgumentList.Add(entryAssembly);
        }
        else
        {
            startInfo.FileName = host;
        }

        var invariant = CultureInfo.InvariantCulture;
        startInfo.ArgumentList.Add(WatcherArgument);
        startInfo.ArgumentList.Add(parentPid.ToString(invariant));
        startInfo.ArgumentList.Add(ReadStartTime(parentPid).ToString(invariant));
        startInfo.ArgumentList.Add(childPid.ToString(invariant));
        startInfo.ArgumentList.Add(childStartTime.ToString(invariant));
        return startInfo;
    }

    private static int Watch(int parentPid, long parentStartTime, int childPid, long childStartTime)
    {
        try
        {
            var deadline = Environment.TickCount64 + MaxWatchMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                // Hedef normal şekilde bittiyse izlemenin işi yok: hemen çık.
                // Zombie da "bitti" sayılır — ölmüş, yalnız reap bekliyor.
                if (!IsProcessAlive(childPid))
                {
                    return 0;
                }

                // Ebeveyn hâlâ yaşıyor mu? `IsProcessAlive` zombie'ı da ölü sayar;
                // yalnız starttime'a bakmak **reap edilene kadar** yanlış olurdu
                // (ölçüm: 24 sn gecikme, `wait()` sonrası 1 sn).
                if (!IsProcessAlive(parentPid))
                {
                    // Numara geri dönmüş olabilir: başlangıç zamanı eşleşmiyorsa
                    // ebeveyn yaşıyor demektir, tetiklemeyelim.
                    var currentParentStartTime = ReadStartTime(parentPid);
                    if (currentParentStartTime != 0
                        && (parentStartTime == 0 || currentParentStartTime == parentStartTime))
                    {
                        break;
                    }
                }

                Thread.Sleep(PollIntervalMilliseconds);
            }

            if (!IsProcessAlive(childPid))
            {
                return 0;
            }

            // Numara artık beklediğimiz sürecin numarası olmayabilir; yalnız
            // başlangıç zamanı da eşleşiyorsa öldür.
            if (childStartTime != 0 && ReadStartTime(childPid) != childStartTime)
            {
                return 0;
            }

            // Ana süreç zorla öldürüldü: hâlâ yaşayan hedefi ve alt ağacını
            // yetim bırakma. `ffmpeg` doğrudan çocuk değil, `yt-dlp`'nin çocuğudur;
            // yalnız kökü öldürmek onu yetim bırakırdı.
            var descendants = KillProcessTree(childPid);
            KillIfAlive(childPid);

            // Torun taraması hiçbir şey bulamadıysa en azından doğrudan çocuğu
            // öldürmüş ol; başarısızsa da sessizce çık.
            _ = descendants;
        }
        catch
        {
            // İzleme sessizce sonlanır.
        }

        return 0;
    }

    /// <summary>
    /// <c>/proc/&lt;pid&gt;/stat</c> içindeki başlangıç zamanı (alan 22).
    /// Okunamazsa 0 döner; çağıran taraf 0'ı "doğrulanamadı" olarak ele alır.
    /// </summary>
    internal static long ReadStartTime(int pid)
    {
        return ReadStatField(pid, StartTimeFieldIndex, 0L);
    }

    /// <summary>
    /// Süreç gerçekten çalışıyor mu?
    ///
    /// <b>Zombie tuzağı.</b> Ölçümle bulundu: <c>ReadStartTime</c> 22. alanı okuyor ve
    /// bu alan <em>zombie süreçte de geçerlidir</em>; <c>kill(pid, 0)</c> de zombie'a
    /// <c>0</c> döner. Yani "ebeveyn öldü mü" sorusu yalnız PID/starttime'a bakarak
    /// **reap edilene kadar <c>false</c> kalıyordu</em>. Migurdex'i reap etmeyen her
    /// ebeveyn için (python <c>subprocess</c> <c>wait()</c>'siz, arka planda başlatıp
    /// devam eden kabuk, alışılmadık reaping yapan supervisor) koruma sıfırdı.
    ///
    /// Ölçülen: hedef süreç 24 saniye boyunca yaşadı, ebeveyn <c>wait()</c> çağrısını
    /// yaptıktan <b>1 saniye</b> içinde öldürüldü.
    ///
    /// Düzeltme: 3. alan (durum) okunur; <c>Z</c> (zombie) ve <c>X</c> (ölü) "ölü"
    /// sayılır.
    /// </summary>
    internal static bool IsProcessAlive(int pid)
    {
        // `kill` bir `libc` çağrısıdır; Windows'ta yoktur. İzleyici zaten yalnız
        // Linux'ta çalışır, ama metot dışarıdan çağrılabilir olduğu için burada
        // güvenli tarafta dönmek gerekir.
        if (!IsSupported || pid <= 0)
        {
            return false;
        }

        var state = ReadProcessState(pid);
        if (state != '\0')
        {
            // Z = zombie (öldü, reap bekliyor), X = tamamen ölü.
            return state is not ('Z' or 'X');
        }

        // /proc okunamadıysa sinyalle sor.
        try
        {
            if (kill(pid, 0) == 0)
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            return error != Esrch; // EPERM -> var ama başka kullanıcıya ait: yaşıyor
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>/proc/&lt;pid&gt;/stat</c> içindeki durum alanı (3. alan: <c>R</c>, <c>S</c>,
    /// <c>Z</c>, <c>X</c> ...). Okunamazsa <c>'\0'</c>.
    /// </summary>
    internal static char ReadProcessState(int pid)
    {
        return (char)ReadStatField(pid, StateFieldIndex, '\0');
    }

    /// <summary>
    /// <c>/proc/&lt;pid&gt;/stat</c> içindeki ebeveyn PID'i (4. alan). Okunamazsa 0.
    /// </summary>
    internal static int ReadParentPid(int pid)
    {
        return (int)ReadStatField(pid, ParentPidFieldIndex, 0L);
    }

    private static long ReadStatField(int pid, int fieldIndexAfterState, long fallback)
    {
        try
        {
            var text = File.ReadAllText($"/proc/{pid}/stat");
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            // 2. alan (`comm`) parantez içindedir ve boşluk ya da ')' içerebilir;
            // alan dizini bu yüzden **son** parantezden sonra başlar.
            var closing = text.LastIndexOf(')');
            if (closing < 0)
            {
                return fallback;
            }

            var fields = text[(closing + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // fields[0] = 3. alan (state). N. alan -> fields[N - 3].
            if (fields.Length <= fieldIndexAfterState)
            {
                return fallback;
            }

            var raw = fields[fieldIndexAfterState];
            if (fieldIndexAfterState == StateFieldIndex)
            {
                return raw.Length == 0 ? fallback : raw[0];
            }

            return long.TryParse(raw, out var value) ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Yalnız ebeveyn PID'i doğrulanmış sürecin **tüm torunlarını** öldürür.
    ///
    /// Gerekçe: <c>ffmpeg</c> hiçbir zaman Migurdex'in doğrudan çocuğu değildir —
    /// daima <c>yt-dlp</c>'nin çocuğudur. Yalnız doğrudan çocuğu öldürmek
    /// (eski davranış) birleştirme gerektiren akışlarda <c>ffmpeg</c>'i yetim
    /// bırakıyordu. Ölçüldü: korunan <c>bash</c> sarmalayıcı &lt;1 sn'de öldü,
    /// <c>torunu</c> (<c>sleep 900</c>) 18 sn yaşamaya devam etti.
    ///
    /// Uygulama: <c>/proc</c> taranır, PID -&gt; ebeveyn haritası kurulur, kökten
    /// genişlik öncelikli taranır. Her aday öldürülmeden hemen önce
    /// <c>/proc/&lt;pid&gt;/stat</c> yeniden okunur — PID geri dönüşümüne karşı son
    /// bir kontrol (harita milisaniyeler önce kurulmuş olsa da pencere kapatılır).
    ///
    /// Yalnız doğrulanmış kökün torunları hedeflenir; ebeveyn doğrulanamazsa
    /// hiçbir şey öldürülmez.
    /// </summary>
    private static int KillProcessTree(int rootPid)
    {
        var killed = 0;
        try
        {
            var children = BuildParentMap();
            if (children.Count == 0)
            {
                return 0;
            }

            var queue = new Queue<int>();
            queue.Enqueue(rootPid);

            var seen = new HashSet<int> { rootPid };
            var descendants = new List<int>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!children.TryGetValue(current, out var kids))
                {
                    continue;
                }

                foreach (var kid in kids)
                {
                    if (!seen.Add(kid))
                    {
                        continue;
                    }

                    descendants.Add(kid);
                    queue.Enqueue(kid);
                }
            }

            // Önce torunlar, sonra kök: kök öldürülünce çocuklar yeniden bağlanmasın.
            for (var index = descendants.Count - 1; index >= 0; index--)
            {
                var pid = descendants[index];
                if (ReadProcessState(pid) == '\0')
                {
                    continue; // arada çıkmış
                }

                KillIfAlive(pid);
                killed++;
            }
        }
        catch
        {
            // Torun taraması başarısız olursa yalnız kök öldürülür (aşağıda).
        }

        return killed;
    }

    private static Dictionary<int, List<int>> BuildParentMap()
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid <= 0)
            {
                continue;
            }

            var parent = ReadParentPid(pid);
            if (parent <= 0)
            {
                continue;
            }

            if (!map.TryGetValue(parent, out var kids))
            {
                kids = [];
                map[parent] = kids;
            }

            kids.Add(pid);
        }

        return map;
    }

    private static void KillIfAlive(int pid)
    {
        try
        {
            kill(pid, Sigkill);
        }
        catch
        {
            // Süreç zaten bitmiş olabilir.
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
