using System.Collections.Concurrent;

namespace Migurdex.Cli.Services.Downloads;

internal static class DownloadTargetLock
{
    internal const string LockSuffix = ".migurdex.lock";

    private static readonly ConcurrentDictionary<string, ProcessLock> _processLocks =
        new(StringComparer.Ordinal);

    /// <summary>Sözlük boyutu: uzun oturumda sızmadığını doğrulamak için.</summary>
    internal static int ProcessLockCountForTest => _processLocks.Count;

    /// <summary>
    /// Verilen hedef için süreç içi kayıt hâlâ duruyor mu? Sızıntı testi,
    /// paralel çalışan diğer testlerden gürültü almamak için **kendi
    /// anahtarlarını** sayar; toplam boyut ölçümü başka testlerin alımlarıyla
    /// karışır ve kararsız sonuç verir.
    /// </summary>
    internal static bool HasProcessLockForTest(string targetIdentity)
    {
        return _processLocks.ContainsKey(PathKey(Path.GetFullPath(targetIdentity)));
    }

    public static async Task<IDisposable> AcquireAsync(
        string            targetIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetIdentity))
        {
            throw new ArgumentException("Hedef yolu boş olamaz.", nameof(targetIdentity));
        }

        var fullTarget = Path.GetFullPath(targetIdentity);
        var key         = PathKey(fullTarget);
        var parent      = Path.GetDirectoryName(fullTarget);
        var entry       = _processLocks.GetOrAdd(key, _ => new ProcessLock());
        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref entry.Acquisitions);

        FileStream? stream = null;
        var         lockPath = BuildLockPath(fullTarget);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadPathBuilder.EnsureFullPathBudget(lockPath, reservedSuffixBytes: 0);
            TryRemoveStaleLockFile(lockPath);
            stream = new FileStream(lockPath,
                                     FileMode.OpenOrCreate,
                                     FileAccess.ReadWrite,
                                     FileShare.None,
                                     bufferSize: 1,
                                     FileOptions.SequentialScan);
            return new LockHandle(key, entry, lockPath, stream);
        }
        catch (ConcurrentDownloadException)
        {
            stream?.Dispose();
            ReleaseEntry(entry, key);
            throw;
        }
        catch (DirectoryNotFoundException ex)
        {
            // `DirectoryNotFoundException` de bir `IOException` türevidir. Altındaki
            // genel `catch (IOException)` kolu onu "başka bir indirme kullanıyor"
            // diye raporlardı — **tamamen yanlış teşhis**. Linux CI'da ölçüldü:
            // hedef klasör silinmiş/silinmemişken kullanıcıya eşzamanlı indirme
            // varmış gibi görünüyordu.
            stream?.Dispose();
            ReleaseEntry(entry, key);
            throw new DownloadException(
                $"İndirme klasörü bulunamadı: {parent}",
                ex);
        }
        catch (IOException ex)
        {
            stream?.Dispose();
            ReleaseEntry(entry, key);
            throw new ConcurrentDownloadException(
                "Video hedefi başka bir indirme tarafından kullanılıyor.",
                ex);
        }
        catch
        {
            stream?.Dispose();
            ReleaseEntry(entry, key);
            throw;
        }
    }

    /// <summary>
    /// Semaforu bırakır ve artık kullanılmıyorsa sözlükten düşürür.
    ///
    /// Sözlük girdileri hiç silinmiyordu; uzun ömürlü bir TUI oturumunda her farklı
    /// hedef için bir <see cref="SemaphoreSlim"/> birikiyordu.
    ///
    /// <see cref="ProcessLock.Acquisitions"/> yalnızca tam olarak bu yöntem
    /// çağrıldığında (kendi eşzamanlılığını içermeyen anahtar için) silme yapılır;
    /// aynı hedef için eşzamanlı iki indirme varsa kayıt güvenli tarafta kalır —
    /// sızıntı, işlev kaybı değildir. Semafor bilerek <b>dispose edilmez</b>: silme
    /// ile yarışan bir alıcı onu tutuyor olabilir ve işlevsel kayıp yaratmayalım.
    /// Asıl sızıntı sözlüktü; semaforu çöp toplayıcıya bırakmak yeterlidir.
    /// </summary>
    private static void ReleaseEntry(ProcessLock entry, string key)
    {
        entry.Semaphore.Release();

        if (Interlocked.Decrement(ref entry.Acquisitions) != 0 || entry.Semaphore.CurrentCount != 1)
        {
            return;
        }

        _processLocks.TryRemove(new KeyValuePair<string, ProcessLock>(key, entry));
    }

    private sealed class ProcessLock
    {
        public SemaphoreSlim Semaphore    { get; } = new(1, 1);
        public int           Acquisitions;
    }

    /// <summary>
    /// BULGU 22: zorla sonlandırılan (SIGKILL, <c>taskkill /F</c>) bir indirmede
    /// <see cref="LockHandle.Dispose"/> hiç çalışmadığı için indirme klasöründe
    /// 0 B'lık <c>&lt;hedef&gt;.migurdex.lock</c> kalıyordu. Zararsız (OS handle'ı
    /// süreçle birlikte ölüyor, sonraki indirmeyi engellemiyor) ama kullanıcının
    /// klasöründe çöp biriktiriyor.
    ///
    /// "Başka bir indirme kullanıyor mu" ayrımı burada netleştiriliyor:
    /// <list type="bullet">
    ///   <item>Dosya yoksa → dokunulacak bir şey yok.</item>
    ///   <item>
    ///     <c>FileShare.None</c> ile açılamıyorsa (IOException) dosyayı **açık**
    ///     tutan başka bir indirme/süreç var → hiç dokunulmaz, akış normal
    ///     <see cref="ConcurrentDownloadException"/> yoluna devreder.
    ///   </item>
    ///   <item>
    ///     Açılabiliyorsa kimse tutmuyor demektir. Dosya 0 B ise (Migurdex
    ///     kilit dosyasına asla yazmaz, yalnız <c>FileMode.OpenOrCreate</c> ile
    ///     açar) kesin bayat kalıntıdır → silinir. Sıfır dışı boyutta bir dosya
    ///     bizim ürettiğimiz bir kilit değildir → yine de dokunulmaz.
    ///   </item>
    /// </list>
    /// Zaten süreç içi <see cref="SemaphoreSlim"/> alındığı için aynı süreçte
    /// eşzamanlı iki indirme bu noktaya gelemez; yalnız *başka* süreçler
    /// senaryodur.
    /// </summary>
    internal static void TryRemoveStaleLockFile(string lockPath)
    {
        try
        {
            if (!File.Exists(lockPath))
            {
                return;
            }

            // Silme önce **yoklama akışı açıkken** denenir.
            //
            // Önceden akış kapatılıp sonra `File.Delete` çağrılıyordu. Windows'ta
            // bu güvenli (açık dosya silinemez → `IOException`). Linux'ta ise
            // `unlink` açık dosyada da başarılı olduğu için, iki yabancı süreç
            // arasında kalan mikrosaniyelik pencerede biri diğerinin kilidini
            // silebilirdi: karşılıklı dışlama sessizce bozulurdu.
            //
            // Akış açıkken denendiğinde `FileShare.None` Unix'te `flock` ile
            // karşılıklı dışlama alır; penceredeki rakip süreç zaten açamaz ve
            // tek bir silme yeterlidir. Windows'ta bu deneme `IOException` ile
            // reddedilir, akış kapanır ve aynı silme bir kez daha denenir.
            var deletedWhileOpen = false;
            using (var probe = new FileStream(lockPath,
                                             FileMode.Open,
                                             FileAccess.ReadWrite,
                                             FileShare.None,
                                             bufferSize: 1,
                                             FileOptions.None))
            {
                if (probe.Length != 0)
                {
                    // Bizim kilit dosyamız daima 0 B. Dolu dosya elimizin değil.
                    return;
                }

                try
                {
                    File.Delete(lockPath);
                    deletedWhileOpen = true;
                }
                catch (IOException)
                {
                    // Windows'ta açık dosya silinemez; akış kapanınca tekrar denenir.
                }
            }

            if (!deletedWhileOpen)
            {
                File.Delete(lockPath);
            }
        }
        catch (IOException)
        {
            // Başka bir indirme kilidi tutuyor; silinmez, normal akış devreder.
        }
        catch (UnauthorizedAccessException)
        {
            // Klasör yazma izni yoksa zaten indirme yapılamaz; sessiz geç.
        }
    }

    private static string BuildLockPath(string targetIdentity)
    {
        var directory = Path.GetDirectoryName(targetIdentity);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return targetIdentity + LockSuffix;
        }

        var name = Path.GetFileName(targetIdentity);
        if (name.EndsWith(LockSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return targetIdentity;
        }

        return Path.Combine(directory, name + LockSuffix);
    }

    private static string PathKey(string path)
    {
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private sealed class LockHandle : IDisposable
    {
        private readonly string      _key;
        private readonly ProcessLock _entry;
        private readonly string      _lockPath;

        private FileStream? _stream;

        public LockHandle(string key, ProcessLock entry, string lockPath, FileStream stream)
        {
            _key      = key;
            _entry    = entry;
            _lockPath = lockPath;
            _stream   = stream;
        }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            catch
            {
                // ignored
            }

            for (var attempt = 0; attempt < 10 && File.Exists(_lockPath); attempt++)
            {
                try
                {
                    File.Delete(_lockPath);
                }
                catch
                {
                    // Windows'ta handle kapanması bir sonraki retry'a kalmayabilir.
                }

                if (File.Exists(_lockPath))
                {
                    Thread.Sleep(10);
                }
            }

            ReleaseEntry(_entry, _key);
        }
    }
}
