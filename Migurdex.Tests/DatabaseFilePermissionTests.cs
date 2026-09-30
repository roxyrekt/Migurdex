using Migurdex.Core.Database;
using Migurdex.Core.Services;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Yapılandırma dizini ve veritabanı dosyalarının izinleri (BULGU 45 — güvenlik).
///
/// <para><b>Ölçülen açık (WSL2, umask 022, temiz kurulum).</b> <c>journal_mode = WAL</c>
/// zorlandığı için her yazma önce <c>migurdex.db-wal</c> dosyasına düşüyor.
/// <c>RestrictDbFilePermissions</c> yalnız <c>migurdex.db</c>'ye <c>0600</c> uyguluyordu
/// <b>ve</b> <c>InitializeDatabase</c>'in sonunda, yani yan dosyalar
/// <c>CreateConnection</c> tarafından çoktan oluşturulduktan sonra çalışıyordu. İlk
/// çalıştırmada yan dosyalar umask'a göre <b>0644</b> doyuyor ve
/// <c>oauth_tokens.access_token</c> / <c>refresh_token</c> <b>açık metin</b> olarak
/// dünya-okunur dosyada kalıyordu. <c>kill -9</c> sonrasında da öyle kalıyordu.</para>
///
/// <para>Yan dosyalar izin modunu <b>ana veritabanından</b> miras alıyor (ölçüldü: ana
/// dosya 0600 → <c>-wal</c>/<c>-shm</c> 0600; 0644 → 0644; 0666 → 0666). Yani sorun
/// kalıcı bir özellik değil, <b>sıralama hatası</b>: yalnız chmod eklemek yetmez,
/// dizin <c>CreateConnection</c>'den <b>önce</b> kısıtlanmalı.</para>
/// </summary>
public sealed class DatabaseFilePermissionTests
{
    private static void AssertOwnerOnly(string path, UnixFileMode expected, string what)
    {
        var actual = File.GetUnixFileMode(path);
        Assert.True(expected == actual,
                    $"{what} izinleri {expected} olmalı, {actual} bulundu: {path}");
    }

    [Fact]
    public void ConfigDirectory_And_Database_Are_OwnerOnly()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var configDir = Path.Combine(Path.GetTempPath(), "migurdex-perm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);

        try
        {
            var db = new MigurdexDatabase(configDir);

            // Yan dosyaların doğması ve token'ın WAL'e düşmesi için gerçek bir yazma.
            // `-wal` dosyasında gömülen bu dize, ajanın ölçtüğü açığın doğrudan kanıtıydı.
            new OAuthTokenStore(db).Set(new OAuthToken
            {
                Provider         = "anilist",
                AccessToken      = "ya29.SECRET-ACCESS-0001",
                RefreshToken     = "ya29.SECRET-REFRESH-0001",
                ExpiresAtUtc     = DateTime.UtcNow.AddHours(1)
            });

            const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            const UnixFileMode dirMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            AssertOwnerOnly(configDir, dirMode, "Yapılandırma dizini");

            var dbPath = Path.Combine(configDir, "migurdex.db");
            Assert.True(File.Exists(dbPath), "Veritabanı oluşmamış.");
            AssertOwnerOnly(dbPath, fileMode, "Veritabanı");

            // Yan dosyalar WAL modunda mevcut olmalı; yoksa test bir şey ölçmez.
            foreach (var sidecar in new[] { "migurdex.db-wal", "migurdex.db-shm" })
            {
                var path = Path.Combine(configDir, sidecar);
                Assert.True(File.Exists(path), $"Yan dosya yok, test ölçüm yapamıyor: {sidecar}");
                AssertOwnerOnly(path, fileMode, sidecar);
            }

            Assert.NotNull(db);
        }
        finally
        {
            try
            {
                Directory.Delete(configDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    /// Kontrol: düzeltme geri alınırsa bu test <b>kırmızı</b> olmalı. Geçici dizin
    /// `Directory.CreateDirectory` ile açıldığı için modu umask'a göre <b>0755</b>'tir.
    /// </summary>
    [Fact]
    public void TempDirectoryCreatedPlainly_Is_0755_SoTheAssertionAboveIsMeaningful()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "migurdex-permctl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // Kanıtlayıcı ölçüm: umask'a açık bir dizin 0755 doğar, yani 0700 beklentisi
            // gerçekten bir kısıtlama (aksi hâlde test her koşuda önceden "geçerdi").
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                          | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                          | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            var actual = File.GetUnixFileMode(dir);
            Assert.True(expected == actual,
                        "Bu ortamda umask 022 değilse kanıtlayıcı ölçümün beklentisi güncellenmeli. " +
                        $"Beklenen {expected}, bulunan {actual}.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
