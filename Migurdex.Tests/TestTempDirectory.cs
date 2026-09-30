using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Migurdex.Tests;

/// <summary>
/// Testlerin geçici dizinlerini tek bir yönetilebilir kök altında toplayan ortak
/// yardımcıdır.
///
/// <para><b>Ölçülen kök neden.</b> Altı test sınıfı
/// <c>Path.Combine(Path.GetTempPath(), "migurdex-xxx-" + Guid.NewGuid())</c> ile
/// kendi dizinini açıyordu ve <b>hiçbir temizleme kodu içermiyordu</b> — ne
/// <c>finally</c>, ne de <c>Directory.Delete</c>. Aynı işi yapan
/// <c>DownloadServiceTests</c> / <c>Mp4DownloaderTests</c> gibi sınıflarda
/// <c>finally</c> + <c>Directory.Delete(root, true)</c> mevcut; sızıntı yalnızca
/// temizleme eklenmemiş sınıflardaydı. Aynı makinede ölçülen birikim:
/// 12.567 dizin / ~1,4 GB (Linux turunda raporlanan: 241 dizin / 19 MB).</para>
///
/// <para><b>Engel.</b> Bu altı sınıfın tamamı SQLite kullanan mağazalar
/// (<c>OAuthTokenStore</c>, <c>TrackerMappingStore</c>, <c>MigurdexDatabase</c>)
/// açıyor ve bunlar <c>IDisposable</c> değil. Ölçüldü: aynı süreç içinde
/// <c>Directory.Delete</c> "migurdex.db ... used by another process" ile
/// başarısız oluyor, yani <b>süreç içi</b> temizlik bu sınıflar için Windows'ta
/// imkânsız. Bu yüzden temizlik iki katmanlıdır:</para>
///
/// <list type="number">
///   <item><description><b>Bu koşunun sonu (birincil).</b>
///   <see cref="AppDomain.ProcessExit"/> ve mümkünse SIGTERM/SIGINT tetiklendiğinde
///   SQLite bağlantı havuzları boşaltılır, ardından kayıtlı dizinlerin tümü
///   silinir. Bağlantı havuzları, <c>MigurdexDatabase</c> <c>IDisposable</c>
///   olmadığı için dosya tanıtıcılarını süreç boyunca açık tutar; boşaltılmadan
///   silme Windows'ta "migurdex.db ... used by another process" ile başarısız
///   olur (ölçüldü).</description></item>
///   <item><description><b>Bir sonraki koşu (garanti ağı).</b> Her koşu dizinlerini
///   <c>&lt;temp&gt;/migurdex-tests/run-&lt;pid&gt;-&lt;başlangıç&gt;/</c> altında açar.
///   Modül başlatıcısı, kök altındaki her koşu dizininin sahibi olan sürecin
///   <i>hala yaşadığını</i> denetler; ölmüş ya da PID yeniden kullanılmış süreçlerin
///   bıraktığı koşu dizinleri anında silinir. SIGKILL,
///   <c>Xunit.Sdk.TestPipelineException</c>, elektrik kesintisi gibi ilk katmanın
///   hiç çalışmadığı durumlar böylece bir koşu sonra toplanır — birikim sınırsız
///   büyümez.</description></item>
/// </list>
///
/// <para>Eşzamanlı iki <c>dotnet test</c> koşusunun birbirinin dizinlerini
/// silmesini önlemek için süpürme yalnızca sahibi ölmüş süreçlerin dizinlerini
/// hedefler; canlı bir koşunun dizini hiçbir zaman süpürülmez. PID yeniden
/// kullanımı, dizin adına yazılan süreç başlangıç zamanı karşılaştırılarak
/// elenir.</para>
///
/// <para>Hiçbir yol testi düşürmez: temizlik hataları yutulur, kök oluşturulamazsa
/// eski davranışa düşülür. Davranış testlerde değişmez, yalnızca dosya sistemi
/// hijyeni değişir.</para>
/// </summary>
internal static class TestTempDirectory
{
    /// <summary>Tüm test geçici dizinlerinin toplandığı sabit kök adı.</summary>
    internal const string RootName = "migurdex-tests";

    /// <summary>Koşu dizini ad ön eki: <c>run-&lt;pid&gt;-&lt;başlangıçTicks&gt;</c>.</summary>
    internal const string RunPrefix = "run-";

    /// <summary>
    /// Bir silme denemesi başarısız olursa bu kadar kez yeniden denenir. Windows'ta
    /// kapanmamış SQLite taşıyıcıları ilk denemede paylaşım ihlali üretir; kısa bir
    /// bekleme ve GC turu sonrasında yeniden denenir.
    /// </summary>
    internal const int DeleteAttempts = 3;

    /// <summary>
    /// Bir koşu dizini en az bu süre dokunulmamışsa "terk edilmiş" sayılabilir.
    ///
    /// <para><b>Neden şart — ölçüldü, varsayım değil.</b> xUnit v3 test derlemesini
    /// <b>birden çok işlemde</b> çalıştırıyor (ölçülen PID'ler: 645, 673, 703, 707,
    /// 719, 745 — hepsi <em>tek</em> <c>dotnet test</c> çağrısında). Her biri modül
    /// başlatıcısını çalıştırıp kökü süpürdüğü için, bir işlem bitip öldüğü anda
    /// <b>bir sonraki işlem onun dizinini siliyor</b> — oysa dosyalar hâlâ
    /// kullanılıyordu. Linux (WSL) kaydı:
    /// <c>RunRoot=run-673-… entry=run-645-… -&gt; SILDI</c>.</para>
    ///
    /// <para>Sonuç: SQLite günlük dosyasını oluşturamıyor,
    /// <c>SQLite Error 10: disk I/O error</c> ve
    /// <c>Error 14: unable to open database file</c> ile koşu başına 8–17 test
    /// düşüyordu. Ölçüm: süpürücü açıkken 3 koşuda 47 hata, kapalıyken <b>0</b>.</para>
    ///
    /// <para>Yalnız Windows'ta görünmüyordu: açık dosya varken
    /// <c>Directory.Delete</c> başarısız olur ve hata yutulur. Unix'te
    /// <c>unlink</c> açık dosyada da başarılıdır, yani silme gerçekten olur.</para>
    ///
    /// <para>Tolerans bu yarışı tamamen kapatır: yeni bitmiş bir koşu 10 dakika
    /// dokunulmaz, gerçekten eski kalıntılar toplanır. Birikim yine sınırsız
    /// büyümez — yalnız "bir sonraki koşuda" değil, "on dakika sonraki koşuda"
    /// toplanır. Doğruluk, daha agresif temizliğin önüne geçti.</para>
    /// </summary>
    internal static readonly TimeSpan SweepGracePeriod = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, byte> Tracked = new(StringComparer.Ordinal);

    /// <summary>Kökün tam yolu. Testler buraya yazmaya devam edebilir.</summary>
    internal static string Root { get; } = Path.Combine(Path.GetTempPath(), RootName);

    /// <summary>Bu koşunun kendi dizini: <c>kök/run-&lt;pid&gt;-&lt;başlangıç&gt;</c>.</summary>
    internal static string RunRoot { get; } = Path.Combine(Root, BuildRunName());

    /// <summary>Bu süreçte <see cref="Create"/> ile açılmış mı?</summary>
    internal static bool IsTracked(string path) => Tracked.ContainsKey(path);

    private static PosixSignalRegistration? _sigterm;
    private static PosixSignalRegistration? _sigint;

    /// <summary>
    /// Adı verilen geçici dizini açar ve temizlik kaydına ekler. Dizin adı ön eki
    /// korunur, yalnızca yer değiştirilir.
    /// </summary>
    internal static string Create(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var name = prefix + Guid.NewGuid().ToString("N");

        // Kök oluşturulamazsa (kısıtlı ortam) testleri patlatmamak adına eski
        // davranışa düşülür; hijyen kaybı testlerin doğruluğundan önemli değildir.
        var dir = TryCreateUnderRunRoot(name) ?? TryCreateUnderTemp(name);
        if (dir is null)
        {
            throw new IOException("Test geçici dizini oluşturulamadı: " + Path.GetTempPath());
        }

        Tracked.TryAdd(dir, 0);
        return dir;
    }

    private static string? TryCreateUnderRunRoot(string name)
    {
        try
        {
            var dir = Path.Combine(RunRoot, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryCreateUnderTemp(string name)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), name);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Dizini varsa siler. Hata olursa yutar; testin sonucunu düşürmez, çünkü
    /// temizlik başarısızlığı testin kendisiyle ilgisi yoktur.
    /// </summary>
    internal static bool TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch
            {
                if (attempt == DeleteAttempts)
                {
                    return false;
                }

                // Kapanmamış yönetilmeyen taşıyıcılar (ör. SQLite) ilk denemede
                // paylaşım ihlali üretir; kısa bekleyip yeniden denenir.
                //
                // DİKKAT: burada bilinçli olarak `GC.Collect()` /
                // `GC.WaitForPendingFinalizers()` ÇAĞRILMAZ. Bir dizin silme
                // yardımcısının süreç çapında sonlandırma tetiklemesi, paralel
                // çalışan başka testlerin nesnelerini beklenmedik anda sonlandırabilir.
                // Ölçülen Linux hatasında bu tam olarak tetikleniyordu: süpürücü canlı
                // bir koşu dizinini silemeyince yardımcı üç kez küresel GC zorluyordu.
                Thread.Sleep(20 * attempt);
            }
        }

        return false;
    }

    /// <summary>Bu koşuda oluşturulmuş tüm dizinleri siler, sonra kökü de kaldırır.</summary>
    internal static void CleanupAll()
    {
        ReleaseDatabaseHandles();

        foreach (var path in Tracked.Keys)
        {
            TryDelete(path);
        }

        // Kök ve koşu dizini boşsa onlar da kaldırılır; /tmp altında kalıcı bir
        // iskelet bırakılmaz. Eşzamanlı başka koşulara dokunmamak için yalnızca
        // boş olduklarında ve recursive olmadan silinirler.
        TryDeleteIfEmpty(RunRoot);
        TryDeleteEmptyRoot();
    }

    /// <summary>
    /// SQLite bağlantı havuzunu boşaltır. <c>MigurdexDatabase</c> ve türevleri
    /// <c>IDisposable</c> olmadığından kapatılan bağlantılar havuza döner ve
    /// <c>migurdex.db</c> tanıtıcısı süreç boyunca açık kalır; bu olmadan Windows'ta
    /// dizin silinemez. Havuz boşaltmak yalnızca boş bağlantıları kapatır, açık
    /// olanlara dokunmaz; sonraki kullanımda yeni bağlantı açılır.
    /// </summary>
    internal static void ReleaseDatabaseHandles()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // ignored — temizlik yine de denenecek
        }
    }

    /// <summary>
    /// Kökün altındaki, sahibi ölmüş süreçlere ait koşu dizinlerini siler. Önceki
    /// çalıştırmaların temizlenememiş dizinlerini toplar; süreç içi temizliğin
    /// SQLite kilidi yüzünden başarısız kaldığı ya da sürecin öldürüldüğü her
    /// durumda birikimi sınırlar.
    /// </summary>
    internal static void SweepAbandonedRuns()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            foreach (var entry in Directory.EnumerateDirectories(Root))
            {
                // `entry` bir TAM YOL, `RunRoot` de tam yoldur. Önceden
                // `Path.GetFileName(entry)` (dosya adı) ile karşılaştırılıyordu ve bu
                // hiç eşleşmediği için "kendi koşu dizinimi atla" güvencesi fiilen
                // yoktu — her karar `IsAbandoned`'e bağlıydı.
                //
                // Tolerans: yeni bitmiş bir koşunun dizini silinmez. Yukarıdaki
                // ölçülen yarışın kaynağı tam olarak budur.
                if (!string.Equals(entry, RunRoot, StringComparison.Ordinal)
                    && IsOlderThan(entry, SweepGracePeriod)
                    && IsAbandoned(entry))
                {
                    TryDelete(entry);
                }
            }

            // Kök, bu koşunun `RunRoot` dizinini içerdiği için normalde silinmez;
            // yalnızca tamamen boş kaldıysa kaldırılır. Koşu dizininin kendisi
            // burada silinmez: modül başlatıcısı birden çok yüklem bağlamında
            // çalışabiliyor ve diğerleri o anda dizin açıyor olabilir.
            TryDeleteEmptyRoot();
        }
        catch
        {
            // Süpürme hiçbir koşulda testleri etkilemez.
        }
    }

    /// <summary>
    /// Koşu dizininin sahibi olan süreç artık yaşamıyor mu? PID yeniden kullanımı,
    /// dizin adına yazılan süreç başlangıç zamanı karşılaştırılarak elenir.
    /// </summary>
    internal static bool IsAbandoned(string runDirectory)
    {
        var name  = Path.GetFileName(runDirectory);
        var parts = name.Split('-');
        if (parts.Length != 3
            || !string.Equals(parts[0], "run", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var startTicks))
        {
            // Ad çözülemiyorsa yalnızca gerçekten eskiyse silinir.
            return IsOlderThan(runDirectory, TimeSpan.FromHours(1));
        }

        if (pid <= 0)
        {
            return true;
        }

        // KENDİ koşu dizinimiz kesinlikle terk edilmiş olamaz — bu süreç yaşıyor
        // demektir. Bu kural iki gerçek açığı tek yerinde kapatır:
        //
        // 1) `BuildRunName` okuyamadığında `startTicks = 0` yazıyor. Böyle bir
        //    dizinde `owner.StartTime.Ticks != 0` olduğu için "terk edilmiş" sayılır
        //    ve **canlı koşunun kökü silinirdi**. Linux ölçümü: test ortasında
        //    `DirectoryNotFoundException` (kilit dosyasının yolu yok).
        //
        // 2) xUnit v3 derlemeyi birden çok AssemblyLoadContext'e yüklüyor (ölçüldü:
        //    3 ayrı yükleme). Her biri modül başlatıcısını çalıştırıp kendi
        //    `RunRoot` değerini kuruyor; bunlar farklıysa biri diğerinin dizinini
        //    "kendi" diye atlayamaz.
        //
        // Linux'a özgü görünmesinin nedeni: Unix'te `unlink`/`rmdir` **açık dosya
        //    bulunsa da** başarılıdır, yani silme gerçekten gerçekleşir. Windows'ta
        // açık kilit dosyası silmeyi engeller ve hata yutulur.
        if (pid == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var owner = Process.GetProcessById(pid);
            if (owner.HasExited)
            {
                return true;
            }

            // PID yeniden kullanılmış olabilir; başlangıç zamanı tutmuyorsa bu
            // dizin eski koşuya aittir.
            return owner.StartTime.ToUniversalTime().Ticks != startTicks;
        }
        catch (ArgumentException)
        {
            // Süreç yok.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            // Başlangıç zamanı okunamadı: yaş ölçütüne düş.
            return IsOlderThan(runDirectory, TimeSpan.FromHours(1));
        }
        catch (Win32Exception e)
        {
            // Erişilemiyorsa silmeyi dene; başarısız olursa TryDelete yutar.
            return e.NativeErrorCode == 87 /* ERROR_INVALID_PARAMETER: süreç yok */;
        }
    }

    private static bool IsOlderThan(string path, TimeSpan age)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path) < DateTime.UtcNow - age;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildRunName()
    {
        var pid = Environment.ProcessId;
        long startTicks;
        try
        {
            using var self = Process.GetCurrentProcess();
            startTicks = self.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                      or NotSupportedException
                                      or Win32Exception)
        {
            startTicks = 0;
        }

        return RunPrefix
               + pid.ToString(CultureInfo.InvariantCulture)
               + "-"
               + startTicks.ToString("x", CultureInfo.InvariantCulture);
    }

    private static void TryDeleteEmptyRoot()
    {
        TryDeleteIfEmpty(Root);
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        try
        {
            // Yalnızca boşsa silinir; yanlışlıkla eşzamanlı koşunun verisini
            // silmemek için recursive değil.
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // ignored
        }
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupAll();
        }
        catch
        {
            // ignored
        }

        RegisterSignal(PosixSignal.SIGTERM, r => _sigterm = r);
        RegisterSignal(PosixSignal.SIGINT, r => _sigint = r);

        SweepAbandonedRuns();
    }

    private static void RegisterSignal(PosixSignal signal, Action<PosixSignalRegistration> keep)
    {
        try
        {
            // Kayıt tutulmazsa çöp toplanınca kaldırılır; statik alanda saklanır.
            // Windows bu sinyallerin kaydını desteklemez; özel durum yutulur.
            keep(PosixSignalRegistration.Create(signal, ctx => CleanupAll()));
        }
        catch (PlatformNotSupportedException)
        {
            // ignored
        }
        catch (NotSupportedException)
        {
            // ignored
        }
        catch (ArgumentOutOfRangeException)
        {
            // ignored
        }
        catch (IOException)
        {
            // ignored
        }
    }
}