using Migurdex.Cli.Services.Downloads;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Migurdex.Cli.Utils;

/// <summary>
/// Dış süreçleri Migurdex'in yaşamına bağlar: Migurdex kapanınca (ya da
/// <c>SIGKILL</c> ile öldürülünce) dış süreç de kapanır.
///
/// <para><b>Neden tek nokta?</b> Yetim süreç sorunu platforma göre iki farklı
/// mekanizmayla çözülür ve ikisi de "dış süreç başlatıldı" anında kurulmalıdır.
/// Bu kurulumu her çağırana ayrı ayrı yazmak, yeni bir dış süreç eklendiğinde
/// korumayı unutma riskini doğurur. Bu yüzden <see cref="Track"/> tek giriş
/// noktasıdır: dış süreç başlatan her yol buradan geçer, yeni çağıranlar
/// <b>kendiliğinden</b> kapsanır.</para>
///
/// <para><b>Windows.</b> Job Object
/// (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>): Migurdex'in işi kapanınca kernel
/// tüm alt süreçleri öldürür. Bu yol değişmemiştir.</para>
///
/// <para><b>Linux.</b> Job Object yoktur. Ölçüm: <c>Track</c> burada
/// <b>sessizce hiçbir şey yapmıyordu</b> ve yetim kalan iki yol vardı —
/// <c>SIGKILL</c> sonrası yetim <c>mpv</c> (MpvPlayerService) ve WSL oturumu
/// kapanana kadar yaşayan yetim API daemon (ApiClientService). Linux'ta
/// koruma <c>PR_SET_PDEATHSIG</c> ile yapılamaz (yalnız doğrudan çocuğu kapsar,
/// torunları kapsamaz ve <c>exec</c> ile değişen süreçte kaybolur); bu yüzden
/// <see cref="LinuxOrphanGuard"/> kullanılır: ayrı, bağımsız bir Migurdex
/// süreci başlatılır, ebeveyn öldüğünde hedefi ve tüm alt ağacını öldürür.
/// macOS'ta iki yol da no-op'tur.</para>
///
/// <para><b>Kapsanan çağıranlar.</b> <c>Track</c> çağıran her yer Linux'ta
/// yetim koruması kazanır:
/// <list type="bullet">
/// <item><description><c>MpvPlayerService</c> — mpv oynatıcı (satır 176).</description></item>
/// <item><description><c>ApiClientService</c> — API daemon (satır 195).</description></item>
/// <item><description><c>ExternalProcessRunner</c> — yt-dlp. Bu yol
/// <c>LinuxOrphanGuard.Attach</c> çağrısını zaten kendisi yapıyor; aşağıdaki
/// nottaki mükerrer izleyici bu yolda geçerlidir.</description></item>
/// </list></para>
///
/// <para><b>Ek süreç maliyeti.</b> Linux'ta her <c>Track</c> çağrısı bir
/// izleyici süreci doğurur: her mpv açılışında ve her API daemon
/// başlangıcında bir tane. İzleyici kısa ömürlüdür — hedef süreç bitince
/// (<= 500 ms) ya da en geç <c>LinuxOrphanGuard</c>'ın üst yaşam sınırında
/// kendiliğinden çıkar; kalıcı sürgüç değildir. Windows'ta izleyici
/// başlatılmaz.</para>
///
/// <para><b>Bilinçli olarak yapılmayan.</b> İzleyicinin <em>kendisi</em> hiçbir
/// dış süreç başlatmaz ve <c>Track</c> çağırmaz, dolayısıyla koruma zinciri
/// oluşmaz: <c>Program</c> izleyici argümanını en başta yakalayıp
/// <c>RunWatcher</c>'a döner ve <c>Watch</c> yalnızca <c>/proc</c> okur ve
/// <c>kill</c> çağırır.</para>
///
/// <para><b>Sözleşme.</b> <see cref="Track"/> <b>asla istisna atmaz</b>. Koruma
/// bir iyileştirmedir, oynatmanın/daemon'un/indirmenin çalışma önkoşulu
/// değildir; kurulamazsa iş akışı sessizce devam eder.</para>
/// </summary>
public static class ChildProcessTracker
{
    private static readonly IntPtr _jobHandle;

    static ChildProcessTracker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _jobHandle = CreateJobObject(IntPtr.Zero, null);

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };

        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info
        };

        var length          = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        var extendedInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

            if (!SetInformationJobObject(_jobHandle,
                                         JobObjectInfoClass.ExtendedLimitInformation,
                                         extendedInfoPtr,
                                         (uint) length))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    /// <summary>
    /// Bir dış süreci Migurdex'in yaşamına bağlar.
    ///
    /// <para>Windows'ta Job Object'a atanır; Linux'ta
    /// <see cref="LinuxOrphanGuard"/> izleyicisi başlatılır. macOS'ta iki yol
    /// da no-op'tur. Her iki platformda da <b>istisna atmaz</b>: koruma
    /// kurulamazsa oynatma/daemon/indirme normal devam eder.</para>
    /// </summary>
    public static void Track(Process process)
    {
        if (process is null)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Job Object yalnız Windows'ta var. Linux'ta aynı amacı ayrı bir
            // izleyici süreci karşılar: ebeveyn öldüğünde yaşamaya devam edebilmeli,
            // çünkü koruma `kill -9` sonrasında da çalışmak zorunda.
            //
            // `Attach` kendi içinde `IsSupported` ve null/eğilmiş-süreç kontrollerini
            // yapıyor ve kendi try/catch'i var; dıştaki catch ise bu sözleşmenin
            // (Track asla istisna atmaz) korunması için son savunmadır.
            try
            {
                LinuxOrphanGuard.Attach(process);
            }
            catch
            {
                // ignored
            }

            return;
        }

        if (_jobHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
            {
            }
        }
        catch
        {
            // ignored
        }
    }

#region Win32 API

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob,
        JobObjectInfoClass                                    jobObjectInfoClass,
        IntPtr                                                lpJobObjectInfo,
        uint                                                  cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long           PerProcessUserTimeLimit;
        public long           PerJobUserTimeLimit;
        public JOBOBJECTLIMIT LimitFlags;
        public nuint          MinimumWorkingSetSize;
        public nuint          MaximumWorkingSetSize;
        public uint           ActiveProcessLimit;
        public nuint          Affinity;
        public uint           PriorityClass;
        public uint           SchedulingClass;
    }

    [Flags]
    private enum JOBOBJECTLIMIT : uint
    {
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS                       IoCounters;
        public nuint                             ProcessMemoryLimit;
        public nuint                             JobMemoryLimit;
        public nuint                             PeakProcessMemoryLimit;
        public nuint                             PeakJobMemoryLimit;
    }

#endregion
}
