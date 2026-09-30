# Döngü 5 — OAuth token'ları dünya-okunur WAL dosyasında (güvenlik)

**Tarih:** 30 Eylül 2026 · **Taban:** `main` = `0604833` · **Dal:** `fix/oauth-token-file-permissions`

> Bu dal, döngü 3'ün SQLite incelemesinin **ölçüm sonucudur**. İki kalemden biri
> (BULGU 44) ölçümle çürütüldü ve **dokunulmadı**; diğeri (BULGU 45) doğrulandı.

---

## 0. İki kalem, iki farklı karar

| # | İddia | Sonuç | Karar |
|---|---|---|---|
| **44** | `busy_timeout` `Open()` sonrasında uygulanıyor, açılış 0 zaman aşımla çalışıyor olabilir | ❌ **ÇÜRÜTÜLDÜ** | Dokunulmadı |
| **45** | OAuth token'ları `migurdex.db-wal` içinde açık metin, izinler 0644 | ✅ **DOĞRULANDI** | Düzeltildi |

---

## 1. BULGU 44 — iddia çürütüldü, mekanizma tersti

**İddia:** `busy_timeout` bir bağlantı ayarı; `Open()`'dan *sonra* SQL olarak
gönderildiği için açılışın kendisi 0 zaman aşımla çalışıyor ve `SQLITE_BUSY`
fırlatabilir. Önerilen çözüm: `SqliteConnectionStringBuilder.DefaultTimeout = 5`.

### Ölçüm

`DefaultTimeout` **`sqlite3_busy_timeout`'a hiç gitmiyor.** Microsoft.Data.Sqlite
10.0.12 / SQLite 3.53.3, `SqliteConnectionInternal.cs`, tek `sqlite3_open_v2` yolu:

```csharp
var rc = sqlite3_open_v2(filename, out _db, flags, vfs: vfs);
SqliteException.ThrowExceptionForRC(rc, _db);
if (connectionOptions.Password.Length != 0) { ... }
if (connectionOptions.ForeignKeys.HasValue) { ... }
if (connectionOptions.RecursiveTriggers) { ... }
// sqlite3_busy_timeout HİÇ ÇAĞRILMIYOR
```

Bağlantı dizesi `Password`/`ForeignKeys`/`RecursiveTriggers` ayarlamadığı için `Open()`
**hiç SQL çalıştırmıyor**. `DefaultTimeout` bunun yerine `SqliteCommand.CommandTimeout`
üzerinden yönetilen `Thread.Sleep(150)` yeniden deneme döngüsüne gidiyor
(`SqliteDataReader.NextResult`).

| Şekil | `DefaultTimeout` | `Open()` sonrası `PRAGMA busy_timeout` |
|---|---|---|
| mevcut kod | (ayarlanmamış) | **0 ms** — `DefaultTimeout` **30** |
| önerilen "düzeltme" | 5 | **0 ms** — `DefaultTimeout` 5 |

Önerilen düzeltme `busy_timeout`'u **hiç değiştirmiyor**.

### Kilit altında ölçüm

Süreç A `BEGIN EXCLUSIVE` tutarken süreç B:

| | Sonuç |
|---|---|
| `Open()` tek başına — mevcut şekil | **OK**, 6 ms (kilit *alınmıyor*) |
| `Open()` tek başına — `DefaultTimeout=5` | **OK**, 8 ms |
| `Open()` tek başına — `DefaultTimeout=0` | **OK**, 7 ms |
| Tam PRAGMA batch — mevcut şekil (`DefaultTimeout` 30) | 30,0 sn sonra başarısız |
| Tam PRAGMA batch — `DefaultTimeout=5` | **5,2 sn** sonra başarısız |
| Tam PRAGMA batch — `DefaultTimeout=0` | 38,3 sn bloklanıp **başarılı** |

**Üçü de bekliyor ve mevcut kod en uzun bekleyeni.** Önerilen değişiklik 30 saniyeyi
**5 saniyeye düşürürdü** — yani kötüleştirirdi.

> `0` değeri "bekleme" değil **"sonsuza kadar bekle"**: `CommandTimeout != 0` koşulu
> döngüyü kırar, `0` ise kırmaz.

**Karar: dokunulmadı.** Ölçülmeden SQLite davranışı değiştirilmez. Bu kalem, 3. döngüdeki
`DownloadTargetLock` iddiasının da çürütülmesiyle birlikte ikinci kez: **tahminle
değiştirilmedi.**

---

## 2. BULGU 45 — OAuth token'ları dünya-okunur dosyada (DOĞRULANDI)

### Ölçüm — WSL2, `umask 022`, temiz kurulum

`CreateConnection()` `PRAGMA journal_mode = WAL` zorladığı için **her yazma önce
`migurdex.db-wal` dosyasına düşüyor**. `RestrictDbFilePermissions()` ise:

1. **yalnız `migurdex.db`**'ye `0600` uyguluyordu — `-wal` ve `-shm` kapsam dışıydı,
2. `InitializeDatabase()`'in **sonunda** çalışıyordu, yani yan dosyalar
   `CreateConnection` tarafından çoktan oluşturulduktan sonra.

İlk çalıştırmada ölçülen:

```
migurdex.db      mode=600
migurdex.db-shm  mode=644   ← dünya-okunur
migurdex.db-wal  mode=644   ← dünya-okunur
token occurrences in -wal: 2
```

**Token açık metin**, byte 16467'de bulundu. `kill -9` sonrasında da öyle kaldı:

```
migurdex.db      mode=600
migurdex.db-shm  mode=644
migurdex.db-wal  mode=644
token occurrences still in -wal: 2
```

### Yan dosyalar izin modunu nereden alıyor?

İzolasyon deneyi — `umask 022` sabit, **yalnız ana dosyanın modu** değiştirildi:

| `migurdex.db` modu | oluşan `-wal` / `-shm` |
|---|---|
| 600 | **600** |
| 644 | **644** |
| 666 | **666** |

Yani mod **umask'ten değil, ana veritabanından** geliyor. Bu yüzden ikinci çalıştırmada
(ana dosya zaten 0600) yan dosyalar 0600 doğuyor.

> **Sonuç: sorun kalıcı bir özellik değil, bir SIRALAMA hatası.** Yalnız chmod eklemek
> yetmez — dizin `CreateConnection`'den **önce** kısıtlanmalı.

Ayrıca `_configDirectory` düz `Directory.CreateDirectory` ile açılıyordu: **0755**.
Dizindeki `MigrateExistingJsonFiles()` `.bak` JSON'ları (izleme geçmişi) hiçbir zaman
chmod edilmiyordu.

### Düzeltme

```csharp
Directory.CreateDirectory(_configDirectory);
RestrictDirectoryPermissions();   // ← YENİ: 0700, hiçbir dosya oluşmadan ÖNCE
var dbPath = Path.Combine(_configDirectory, "migurdex.db");
```

```csharp
private void RestrictDbFilePermissions()
{
    RestrictDirectoryPermissions();

    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        foreach (var name in new[] { "migurdex.db", "migurdex.db-wal", "migurdex.db-shm" })
        { /* ... */ }
    }
}
```

**Dizin 0700 asıl koruyucu katmandır:** 0700 sınırı yan dosyaların modundan bağımsız
geçerli olur ve dizindeki diğer dosyaları (`.bak` JSON'lar) da kapsar.

### Doğrulama

| | |
|---|---|
| Düzeltilmiş, `umask 022` | **2/2 geçti** — dizin 0700, üç dosya 0600 |
| **Kontrol: düzeltme geri alındı** | **1 kırıldı** |

Kanıtlayıcı ikinci test, düz `Directory.CreateDirectory` ile açılan bir dizinin bu
ortamda gerçekten **0755** olduğunu ölçüyor — yani 0700 beklentisi her koşuda
"önceden geçer" değil, gerçekten bir kısıtlama.

---

## 3. Doğrulama

| | |
|---|---|
| Build Windows / Linux | **0 hata / 0 uyarı** |
| **Windows tam süit × 4** | **4/4 temiz** |
| **Linux tam süit × 4** | **4/4 temiz** — 602 test |
| İzin testleri (Linux, `umask 022`) | 2/2 — düzeltme geri alınınca 1 kırılıyor |

### Kapsam dışı bırakılan

`journal_mode = TRUNCATE`/`DELETE` seçeneği yan dosya modlarını tamamen devre dışı
bırakırdı, ama **tüm veritabanı için** WAL eşzamanlılığı kaybettirirdi. Düzeltilmiş
yol: 0700 dizin sınırı. Ölçüm notu olarak kayda geçti.
