# Döngü 4 — Tanı Kaybı ve Sessiz Hata Düzeltmeleri

**Tarih:** 30 Eylül 2026 · **Taban:** `main` = `0604833` · **Dal:** `fix/subtitle-and-plugin-diagnostics`

> Bu dal, döngü 3'ün (PR #7) konusundan **ayrıdır**: PR #7 işletim/süreç yaşam döngüsüyle,
> bu dal tanı ve gözlemlenebilirlikle ilgilidir. İkisi birlikte gidiyorsa kayıtları
> karışır, diye ayrıldı.

---

## 0. Sonuç

| # | Konu | Etki | Durum |
|---|---|---|---|
| **42** | Altyazı `catch` bloğu 29 ayrı sebebi tek genel mesaja indiriyordu | kullanıcıya dönük | ✅ Düzeltildi |
| **46** | `PluginLoader` yapılandırılamayan plugin türlerini **hiçbir günlük kaydı olmadan** düşürüyordu | teşhis | ✅ Düzeltildi |
| **43** | `DownloadService` üst düzey `catch` her şeyi "Video indirilemedi" yapıyor | teşhis | 📋 Kayda geçti — nedeni aşağıda |
| **44** | `busy_timeout` `Open()` sonrasında uygulanıyor | olası kilitlenme | 🔬 **Ölçülüyor** |
| **45** | OAuth token'ları `migurdex.db-wal` içinde, izinler yalnız `migurdex.db`'ye | **güvenlik** | 🔬 **Ölçülüyor** |
| **41** | `EstimateRemaining` taşma riski | düşük | 📋 Ulaşılabilirliği kanıtlanamadı |

---

## 1. BULGU 42 — Altyazı hatalarının tamamı aynı görünüyordu

`DownloadService`, `SubtitleDownloader`'ı çağıran `try` bloğunun **dışında** bir
`catch` ile tüm istisnaları yakalıyordu:

```csharp
catch
{
    warnings.Add("Altyazı indirilemedi.");
}
```

`SubtitleDownloader` **29 ayrı, eyleme dönük** sebep üretiyor. Ölçülen liste:

```
Altyazı URL'si boş.                        Altyazı hedefi zaten var; overwrite kapalı.
Altyazı kaynağı HTML içerik döndürdü.       Altyazı dosyası taşınamadı.
WebVTT imzası bulunamadı.                   Altyazı HTTP isteği başarısız oldu.
SRT zamanlama imzası bulunamadı.            Altyazı MIME türü desteklenmiyor.
ASS blok imzası bulunamadı.                 Altyazı dosyası yazılamadı.
...                                         (29 adet)
```

Kullanıcı hangisinin olduğunu **hiçbir zaman** öğrenemiyordu. Ayrıca `catch` filtresiz
olduğu için `NullReferenceException` gibi programlama hataları da **günlüksüzce**
yutuluyordu — indiricinin teşhis katmanı tamamen atılıyordu.

### Düzeltme

```csharp
catch (SubtitleDownloadException exception)
{
    warnings.Add($"Altyazı indirilemedi: {exception.Message}");
}
catch (Exception exception)
{
    warnings.Add($"Altyazı indirilemedi ({exception.GetType().Name}).");
}
```

Programlama hatası ayrı tutuldu: kullanıcıya iç ayrıntı gösterilmiyor ama **sessizce de
yutulmuyor**.

---

## 2. BULGU 46 — Plugin'ler sessizce kayboluyordu

`PluginLoader` iki yerde (`provider` ve `extractor` döngüleri) tüm istisnaları
`catch { // ignored }` ile yutuyordu.

Bu, pratikte en sık görülen başarısızlık biçimini **tamamen görünmez** yapıyordu:
bağımlılık sürümü uyuşmayan bir plugin'in kurucusu çağrılamıyor, o tür atlanıyor ve
tek belirti `/health` çıktısında eksik sağlayıcı + TUI'de boş kaynak listesi.

Dış `catch` yalnız `assembly.GetTypes()` başarısızlığında tetiklendiği için — yani
**daha da nadir** — bu yol hiç tetiklenmiyordu.

### Düzeltme

İki noktaya da `logger.LogWarning(ex, "plugin type {Type} oluşturulamadı; atlanıyor", ...)`.

### Dikkat: üçüncü `catch` **korundu**

`PluginLoader` içinde üçüncü bir sessiz `catch` var:
`CreateInstanceWithBestConstructor`, `ctor.Invoke(args)`'ı dener. Burada yutmak
**tasarımın kendisi** — "en iyi eşleşen kurucu" aramasının başarısız adayları sessizce
geçmesi beklenen davranış. Loglansaydı her eşleşmeyen kurucu için gürültü üretirdi.

**Ölçümle ayrıldı:** ilk denemede üçüncüyü de değiştirmek üzereydim; satır bağlamını
okuyunca ayırdım ve **kasten dokunmadım**.

---

## 3. BULGU 43 — kayda geçti, bu turda yapılmadı

`DownloadService.DownloadAsync` en dıştaki `catch` bloğu her şeyi
`DownloadResult.Failed("Video indirilemedi.")` yapıyor ve **sınıf hiç `ILogger`
içermiyor** — özgün istisna tamamen kayboluyor.

**Neden şimdi değil:** `ILogger<DownloadService>` eklemek demek:

- 4 argümanlı kurucuyu değiştirmek,
- parametresiz kurucuyu (`new DownloadPathBuilder()`, …) güncellemek,
- tüm DI kayıtlarını ve testleri güncellemek.

Yani **geniş bir kırılma yüzeyi** olan bir değişiklik ve asıl kusur "hata gözlenmez"
değil, "hata kötü seçilir". Ajan izinde dört koşuda doğruladığı bir PR'da bu kapsam
değişikliğini yapmak yerine kayda geçirmek daha doğru. **Ayrı ve izlenebilir iş.**

---

## 4. BULGU 44 ve 45 — ölçüm sürüyor, tahminle değiştirilmeyecek

İkisi de `Migurdex.Core/Database/MigurdexDatabase.cs` içinde ve **tahminle
değiştirilmemeli**:

**44 — `busy_timeout` çok geç uygulanıyor.** `CreateConnection()` şunu yapıyor:

```csharp
connection.Open();
cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; ...";
```

`busy_timeout` bir **bağlantı** ayarı; `Open()`'dan *sonra* SQL olarak gönderildiği
için açılışın kendisi 0 zaman aşımla çalışıyor olabilir. Doğru yer
`SqliteConnectionStringBuilder.DefaultTimeout`. **Doğrulanmadan değiştirilmedi.**

**45 — OAuth token'ları WAL içinde, izinler yalnız ana dosyada.** `journal_mode = WAL`
zorlandığı için her yazma — `access_token` / `refresh_token` dahil — önce
`migurdex.db-wal` dosyasına düşüyor. `RestrictDbFilePermissions()` yalnız
`migurdex.db`'ye `0600` uyguluyor; `-wal` ve `-shm` dosyaları **umask'a** göre kalıyor.

Etkisi yüksek ama **tetikleyicisi belirsiz**: SQLite, ana veritabanı dosyasının
iznini yan dosyalara miras geçiriyor olabilir. Bu, ancak ölçümle yanıtlanabilir —
bu yüzden Linux'ta `umask 022` altında gerçek bir WAL veritabanı kurup dosya izinleri
ve token içeriği denetleniyor.

> **Not:** Bu iki kalem döngü 4'ün konusu değil; ölçüm sonucu ne çıkarsa çıksın
> **ayrı bir PR** olarak değerlendirilecek.

---

## 5. BULGU 41 — ulaşılabilirlik kanıtlanamadı

`EstimateRemaining`, `speed` için yalnız `> 0` kontrolü yapıyor ve
`TimeSpan.FromSeconds` 9,22e11 saniyenin üstünde taşıyor. Ajan **ulaşan bir girdi
yolu kuramadı**: `TryParseSize` alt-bayt değerleri `0`'a kırptığı için en küçük geçerli
override `1 B/s` ≈ 2e9 saniye — güvenli aralıkta.

**Karar:** dokunulmadı. Ulaşılabilirlik kanıtlanmadan savunma kodu eklemek, düzeltilmemiş
bir kusur varmış izlenimi yaratır.

---

## 6. Doğrulama

| | |
|---|---|
| Build Windows | **0 hata / 0 uyarı** |
| Build Linux | **0 hata / 0 uyarı** |
| **Windows tam süit × 4** | **4/4 temiz** (600 test) |
| **Linux tam süit × 4** | **4/4 temiz** (600 test) |
| İlgili testler (Subtitle/Plugin/DownloadService) | 20/20 |

### Yöntem notu

`PluginLoader`'daki üçüncü sessiz `catch`'i de değiştirmek üzereydim. Satır bağlamını
okuyunca bunun `CreateInstanceWithBestConstructor`'ın **tasarım gereği** yutması
olduğunu gördüm ve kasten korudum — bu, üçüncü `catch`'i de değiştirmiş olmaktan daha
doğru sonuç.
