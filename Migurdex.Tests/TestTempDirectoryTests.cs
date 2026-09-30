using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// <see cref="TestTempDirectory"/> yardımcısının kendi testleridir. Amaç yalnızca
/// sızıntı önleme değil, temizliğin <b>testi düşürmemesi</b> ve
/// <b>eşzamanlı koşunun dizinlerini silmemesi</b> sözleşmelerini de
/// kilitlemektir.
/// </summary>
public sealed class TestTempDirectoryTests
{
    [Fact]
    public void Create_Places_Directory_Under_CurrentRun()
    {
        var dir = TestTempDirectory.Create("migurdex-tempdirtest-");

        try
        {
            Assert.True(Directory.Exists(dir));
            Assert.Equal(Path.GetFullPath(TestTempDirectory.RunRoot),
                         Path.GetFullPath(Path.GetDirectoryName(dir)!));
            Assert.StartsWith("migurdex-tempdirtest-",
                              Path.GetFileName(dir),
                              StringComparison.Ordinal);
        }
        finally
        {
            Assert.True(TestTempDirectory.TryDelete(dir));
        }
    }

    [Fact]
    public void Create_Is_Tracked_For_Exit_Cleanup()
    {
        var dir = TestTempDirectory.Create("migurdex-tempdirtest-");

        try
        {
            Assert.True(TestTempDirectory.IsTracked(dir));
        }
        finally
        {
            TestTempDirectory.TryDelete(dir);
        }
    }

    [Fact]
    public void Create_Produces_Unique_Directories()
    {
        var a = TestTempDirectory.Create("migurdex-tempdirtest-");
        var b = TestTempDirectory.Create("migurdex-tempdirtest-");

        try
        {
            Assert.NotEqual(a, b);
            Assert.True(Directory.Exists(a));
            Assert.True(Directory.Exists(b));
        }
        finally
        {
            TestTempDirectory.TryDelete(a);
            TestTempDirectory.TryDelete(b);
        }
    }

    [Fact]
    public void Create_Stays_Under_TempRoot()
    {
        // Kalıcı tek kök: `migurdex-<prefix>-<guid>` dizinleri doğrudan /tmp
        // altına yığılmaz, toplanıp süpürülebilir.
        var dir = TestTempDirectory.Create("migurdex-tempdirtest-");

        try
        {
            Assert.StartsWith(Path.GetFullPath(TestTempDirectory.Root),
                              Path.GetFullPath(dir),
                              StringComparison.Ordinal);
        }
        finally
        {
            TestTempDirectory.TryDelete(dir);
        }
    }

    [Fact]
    public void RunRoot_Is_Named_After_Live_Process()
    {
        var name = Path.GetFileName(TestTempDirectory.RunRoot);

        Assert.StartsWith(TestTempDirectory.RunPrefix, name, StringComparison.Ordinal);
        Assert.Contains("-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        name,
                        StringComparison.Ordinal);

        // Bu koşunun dizini canlı olduğu için "terk edilmiş" sayılmamalıdır.
        Assert.False(TestTempDirectory.IsAbandoned(TestTempDirectory.RunRoot));
    }

    [Fact]
    public void TryDelete_Removes_NonEmpty_Directory_Recursively()
    {
        var dir = TestTempDirectory.Create("migurdex-tempdirtest-");
        var sub = Path.Combine(dir, "alt", "daha-alt");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "veri.json"), "{}");

        Assert.True(TestTempDirectory.TryDelete(dir));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void TryDelete_Missing_Directory_Is_Success()
    {
        var missing = Path.Combine(TestTempDirectory.RunRoot,
                                   "yok-" + Guid.NewGuid().ToString("N"));

        Assert.True(TestTempDirectory.TryDelete(missing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryDelete_BlankPath_Is_Success(string? path)
    {
        Assert.True(TestTempDirectory.TryDelete(path));
    }

    [Fact]
    public void TryDelete_Failure_Is_Swallowed_Not_Thrown()
    {
        // Temizlik hatası testi düşürmemeli. Windows'ta paylaşım ihlali üreten
        // bir dosya taşıyıcısıyla hata üretilir.
        var blocker = Path.Combine(TestTempDirectory.RunRoot,
                                   "migurdex-blocker-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(blocker, "kapatik");
        using var handle = new FileStream(blocker,
                                          FileMode.Open,
                                          FileAccess.Read,
                                          FileShare.None,
                                          bufferSize: 1,
                                          FileOptions.None);

        var exception = Record.Exception(() =>
            TestTempDirectory.TryDelete(Path.Combine(blocker, "yok")));
        Assert.Null(exception);

        handle.Dispose();
        File.Delete(blocker);
    }

    [Fact]
    public void ReleaseDatabaseHandles_Does_Not_Throw()
    {
        // `CleanupAll` bunu çağırır ve süreç sonunda SQLite havuzlarını boşaltır.
        //
        // DİKKAT: `CleanupAll` burada bilinçli olarak ÇAĞRILMAZ. O, tüm kayıtlı
        // dizinleri siler ve bağlantı havuzlarını boşaltır; testler paralel
        // çalıştığı için bir testin ortasında çağırmak eşzamanlı başka testlerin
        // kullandığı dizinleri ve veritabanı bağlantılarını bozar. Bu yüzden
        // yalnızca `ProcessExit`/sinyal üzerinden, yani süreç sonunda çağrılır.
        var exception = Record.Exception(TestTempDirectory.ReleaseDatabaseHandles);

        Assert.Null(exception);
    }

    [Fact]
    public void SweepAbandonedRuns_Keeps_CurrentRun()
    {
        var dir = TestTempDirectory.Create("migurdex-tempdirtest-");

        try
        {
            TestTempDirectory.SweepAbandonedRuns();

            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            TestTempDirectory.TryDelete(dir);
        }
    }

    [Fact]
    public void SweepAbandonedRuns_Removes_Dead_Process_Run()
    {
        // Sahibi olmayan (PID'si çözülemeyen çok büyük PID) koşu dizini, çökmüş ya da
        // SIGKILL ile öldürülmüş bir koşudan kalmış gibidir ve toplanmalıdır.
        //
        // DİKKAT: dizin yaşlandırılır. Süpürücü artık yalnızca
        // `SweepGracePeriod` boyunca dokunulmamış dizinleri topluyor; aksi halde
        // "az önce ölmüş ama dosyaları hâlâ kullanılan" koşuların dizinleri de
        // toplanır ve canlı testlerin veritabanları yok edilir (Linux'ta ölçüldü:
        // koşu başına 8–17 hata). Gerçek bir çökme kalıntısı da zaten eski olduğu
        // için bu eşikle de toplanır.
        var stale = Path.Combine(TestTempDirectory.Root,
                                 "run-2147483647-1");
        Directory.CreateDirectory(Path.Combine(stale, "migurdex-dbtest-eski"));
        File.WriteAllText(Path.Combine(stale, "migurdex-dbtest-eski", "migurdex.db"), "x");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TestTempDirectory.SweepGracePeriod - TimeSpan.FromMinutes(5));

        try
        {
            TestTempDirectory.SweepAbandonedRuns();

            Assert.False(Directory.Exists(stale));
        }
        finally
        {
            TestTempDirectory.TryDelete(stale);
        }
    }

    /// <summary>
    /// <b>Regresyon testi — Linux CI kırmızısı.</b> Sahibi ölmüş görünen ama
    /// <b>taze</b> bir koşu dizini silinmemelidir.
    ///
    /// <para>Ölçülen hata: xUnit v3 tek <c>dotnet test</c> çağrısında birden çok
    /// işlem çalıştırıyor (PID 645 → 673 → 703 …). Her biri modül başlatıcısında
    /// kökü süpürdüğü için, bir işlem öldüğü anda <b>bir sonraki işlem onun hâlâ
    /// kullanılan dizinini siliyordu</b>. SQLite günlük dosyasını oluşturamadığı
    /// için <c>SQLite Error 10: disk I/O error</c> / <c>Error 14: unable to open
    /// database file</c> ile koşu başına 8–17 test düşüyordu.</para>
    ///
    /// <para>Bu test, düzeltme olmasaydı kırmızı olurdu: eski kod taze dizini de
    /// siliyordu.</para>
    /// </summary>
    [Fact]
    public void SweepAbandonedRuns_Keeps_Fresh_Run_Of_Dead_Process()
    {
        var fresh = Path.Combine(TestTempDirectory.Root,
                                 "run-2147483646-1");
        Directory.CreateDirectory(Path.Combine(fresh, "migurdex-dbtest-canli"));

        try
        {
            TestTempDirectory.SweepAbandonedRuns();

            Assert.True(Directory.Exists(fresh),
                        "Sahibi ölmüş görünen taze koşu dizini silinmemeli.");
        }
        finally
        {
            TestTempDirectory.TryDelete(fresh);
        }
    }


    [Fact]
    public void SweepAbandonedRuns_Keeps_Unparsable_Fresh_Directory()
    {
        // Ad çözülemiyorsa yaş ölçütüne düşülür; taze bir dizin silinmemelidir.
        var fresh = Path.Combine(TestTempDirectory.Root, "bilinmeyen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fresh);

        try
        {
            TestTempDirectory.SweepAbandonedRuns();

            Assert.True(Directory.Exists(fresh));
        }
        finally
        {
            TestTempDirectory.TryDelete(fresh);
        }
    }

    [Fact]
    public void SweepAbandonedRuns_Removes_Old_Unparsable_Directory()
    {
        var aged = Path.Combine(TestTempDirectory.Root, "bilinmeyen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(aged);
        Directory.SetLastWriteTimeUtc(aged, DateTime.UtcNow - TimeSpan.FromHours(2));

        TestTempDirectory.SweepAbandonedRuns();

        Assert.False(Directory.Exists(aged));
    }

    [Fact]
    public void SweepAbandonedRuns_Is_Idempotent()
    {
        // Süpürme iki kez çağrılabilir: modül başlatıcısı her yüklem bağlamında
        // çalışır. İkinci çağrı ne patlatmalı ne de canlı koşunun dizinlerine dokunmalı.
        var dir = TestTempDirectory.Create("migurdex-tempdirtest-");

        try
        {
            TestTempDirectory.SweepAbandonedRuns();
            TestTempDirectory.SweepAbandonedRuns();

            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            TestTempDirectory.TryDelete(dir);
        }
    }

    [Fact]
    public void IsAbandoned_NeverSweepsTheCurrentProcessRun()
    {
        // GUVENLIK KURALI: kendi PID'imize ait dizin, baslangic zamani tutmasa bile
        // ASLA terk edilmis sayilmaz.
        //
        // Neden sart: `BuildRunName` baslangic zamanini okuyamazsa `0` yaziyor ve
        // o dizin "baslangic zamani uyusmuyor" gerekcesiyle supuruluyordu - yani
        // **canli kosunun koku kendi kendini siliyordu**. Linux'ta bu sessizce
        // gerceklesiyordu (`unlink` acik dosyada da basarili): test ortasinda
        // `DirectoryNotFoundException`, CI'da kirmizi. Ayrica xUnit v3 derlemeyi
        // birden cok ALC'ye yukledigi icin (olculdu: 3) her biri kendi `RunRoot`
        // degerini kurabiliyor ve biri digerinin dizinini "kendi" diye atlayamiyor.
        var ownPidMismatched = Path.Combine(TestTempDirectory.Root,
                                            "run-" + Environment.ProcessId.ToString(
                                                             System.Globalization.CultureInfo.InvariantCulture)
                                                + "-1");

        Assert.False(TestTempDirectory.IsAbandoned(ownPidMismatched),
                     "Kendi surecimizin kosu dizini silinmemeli.");
    }


    [Fact]
    public void IsAbandoned_RejectsRecycledPidOfAnotherLiveProcess()
    {
        // PID YENIDEN KULLANIMI: süreç hâlâ yaşıyor ama dizindeki başlangıç zamanı
        // onunkiyle tutmuyor -> o numara artık başka bir sürece ait, dizin eski
        // koşudan kalmış ve toplanmalı.
        //
        // "Kendi PID'imiz asla terk edilmiş sayılmaz" kuralı yüzden burada
        // **başka bir canlı sürecin** PID'i kullanılır.
        //
        // DİKKAT: süreç mutlaka elle öldürülmeli. `Process.Dispose()` süreci
        // **öldürmez**, yalnız yönetilmeyen tanıtıcıyı serbest bırakır. Windows'ta
        // `cmd /c pause` stdin yönlendirilmediği için **sonsuza kadar** bloklandığından
        // (Linux'ta `sleep 30`) her `dotnet test` koşusu bir süreç sızdırıyordu.
        // Ölçüldü ve düzeltildi.
        using var other = new System.Diagnostics.Process();
        other.StartInfo = new System.Diagnostics.ProcessStartInfo(
            OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false
        };
        other.StartInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        other.StartInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "pause" : "sleep 30");
        other.Start();

        try
        {
            var recycled = Path.Combine(TestTempDirectory.Root,
                                        "run-" + other.Id.ToString(
                                                        System.Globalization.CultureInfo.InvariantCulture)
                                            + "-1");

            Assert.True(TestTempDirectory.IsAbandoned(recycled),
                        "Başka bir canlı sürecin numarası taşınmışsa dizin toplanmalı.");
        }
        finally
        {
            try
            {
                if (!other.HasExited)
                {
                    other.Kill();
                }

                other.WaitForExit(10_000);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void SharedRoot_Is_Named_And_Reapable()
    {
        Assert.Equal("migurdex-tests", TestTempDirectory.RootName);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "migurdex-tests"), TestTempDirectory.Root);
        Assert.True(TestTempDirectory.DeleteAttempts >= 2);
    }
}
