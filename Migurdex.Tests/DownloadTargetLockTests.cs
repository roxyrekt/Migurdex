using Migurdex.Cli.Services.Downloads;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 22: zorla sonlandırılan (<c>taskkill /F</c>) bir HLS indirmesi
/// indirme klasöründe 0 B'lık <c>&lt;hedef&gt;.migurdex.lock</c> bırakıyordu.
/// <c>LockHandle.Dispose</c> dosyayı siliyor ama <c>TerminateProcess</c> bu
/// kodu çalıştırmıyor.
///
/// Zararsız (OS handle'ı süreçle birlikte ölüyor, sonraki indirmeyi
/// engellemiyor) ama kullanıcının klasöründe çöp birikiyor. Buradaki
/// düzeltme, yeni indirme başlarken bayat kilidi temizliyor.
/// </summary>
public sealed class DownloadTargetLockTests
{
    [Fact]
    public async Task Acquire_RemovesStaleZeroByteLockFileLeftByKilledProcess()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "S01E01 - Naruto 1. Bölüm.mkv");
            var lockPath = target + DownloadTargetLock.LockSuffix;
            // Zorla öldürülmüş indirmenin bıraktığı tam olarak bu: 0 B dosya,
            // açık handle YOK (OS handle'ı süreçle birlikte ölmüş).
            File.WriteAllBytes(lockPath, []);
            // Bayat dosyayı belirgin bir geçmişe tarihle: temizlik onu silip
            // yeniden oluşturuyorsa damga değişecek. Sadece "var" olması
            // temizliğin çalıştığını KANITLAMAZ (OpenOrCreate zaten var olan
            // dosyayı kullanır), bu yüzden damga üzerinden ölçüyoruz.
            var staleStamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(lockPath, staleStamp);
            Assert.Equal(staleStamp, File.GetLastWriteTimeUtc(lockPath));

            using (await DownloadTargetLock.AcquireAsync(target, TestContext.Current.CancellationToken))
            {
                Assert.True(File.Exists(lockPath));
                Assert.Equal(0, new FileInfo(lockPath).Length);
                Assert.NotEqual(staleStamp, File.GetLastWriteTimeUtc(lockPath));
            }

            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryRemoveStaleLockFile_DeletesOrphanedZeroByteFile()
    {
        var root = NewTempDir();
        try
        {
            var lockPath = Path.Combine(root, "video.mkv" + DownloadTargetLock.LockSuffix);
            File.WriteAllBytes(lockPath, []);

            DownloadTargetLock.TryRemoveStaleLockFile(lockPath);

            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryRemoveStaleLockFile_DoesNotTouchFileAnotherDownloadIsUsing()
    {
        var root = NewTempDir();
        try
        {
            var lockPath = Path.Combine(root, "video.mkv" + DownloadTargetLock.LockSuffix);
            File.WriteAllBytes(lockPath, []);

            // "Başka bir indirme kullanıyor" ayrımı: FileShare.None ile açık
            // tutulan handle. Temizlik bu dosyaya DOKUNMAMALI.
            using var held = new FileStream(lockPath,
                                           FileMode.Open,
                                           FileAccess.ReadWrite,
                                           FileShare.None,
                                           bufferSize: 1,
                                           FileOptions.None);

            DownloadTargetLock.TryRemoveStaleLockFile(lockPath);

            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TryRemoveStaleLockFile_LeavesNonEmptyFileAlone()
    {
        var root = NewTempDir();
        try
        {
            var lockPath = Path.Combine(root, "video.mkv" + DownloadTargetLock.LockSuffix);
            // Migurdex kilit dosyasına asla yazmaz; dolu dosya elimizin değil.
            await File.WriteAllTextAsync(lockPath, "baska bir sey", TestContext.Current.CancellationToken);

            DownloadTargetLock.TryRemoveStaleLockFile(lockPath);

            Assert.True(File.Exists(lockPath));
            Assert.Equal("baska bir sey", await File.ReadAllTextAsync(lockPath,
                                                                       TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryRemoveStaleLockFile_IsNoOpWhenFileDoesNotExist()
    {
        var root = NewTempDir();
        try
        {
            var lockPath = Path.Combine(root, "video.mkv" + DownloadTargetLock.LockSuffix);

            DownloadTargetLock.TryRemoveStaleLockFile(lockPath);

            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Acquire_StaleLockDoesNotBlockTheNextDownload()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "S01E01 - Test.mkv");
            File.WriteAllBytes(target + DownloadTargetLock.LockSuffix, []);

            // Bayat kilit varken indirme ENGELLENMEMELİ (bugün doğrulandı:
            // zararsız çöp). Bu regresyon koruması.
            using var handle = await DownloadTargetLock.AcquireAsync(target,
                                                                    TestContext.Current.CancellationToken);
            Assert.NotNull(handle);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Dispose_RemovesLockFileAndAllowsReacquire()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "S01E01 - Test.mkv");
            var lockPath = target + DownloadTargetLock.LockSuffix;

            var first = await DownloadTargetLock.AcquireAsync(target, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(lockPath));
            first.Dispose();
            Assert.False(File.Exists(lockPath));

            // Temiz çıkıştan sonra yeniden alınabilmeli.
            using var second = await DownloadTargetLock.AcquireAsync(target,
                                                                     TestContext.Current.CancellationToken);
            Assert.NotNull(second);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Acquire_FailsWhileAnotherHolderKeepsTheLockFile()
    {
        var root = NewTempDir();
        try
        {
            var target = Path.Combine(root, "S01E01 - Test.mkv");
            var lockPath = target + DownloadTargetLock.LockSuffix;
            File.WriteAllBytes(lockPath, []);

            // Süreç dışı "başka indirme" benzetmesi: handle dışarıda tutuluyor,
            // bu yüzden süreç içi SemaphoreSlim devreye girmiyor.
            using var held = new FileStream(lockPath,
                                           FileMode.Open,
                                           FileAccess.ReadWrite,
                                           FileShare.None,
                                           bufferSize: 1,
                                           FileOptions.None);

            await Assert.ThrowsAsync<ConcurrentDownloadException>(() =>
                DownloadTargetLock.AcquireAsync(target, TestContext.Current.CancellationToken));

            // Temizlik, kilidi kullanan süreci engellememeli.
            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
