# Migurdex İndirme Özelliği — Geliştirme Kaydı

> Bu dosya, anime indirme özelliğinin **başlangıçtan merge ve release'e kadar** tüm geliştirme,
> test, hata çözümü, paketleme ve sürüm uyumlama çalışmasını kronolojik ve teknik olarak kaydeder.
>
> Son güncelleme: 29 Eylül 2026 · Durum: **PR #1 merged**, **`v1.10.2` published**
> Aktif dal: **`main`** · Merge commit: `44f4010` (PR #1: `feature/download` → `main`)
> Temel: `v1.10.1` (`4ecd7d7`) · Sürüm: `v1.10.2` → `44f4010`
> PR (fork, merged): https://github.com/Nutaliaxd/Migurdex/pull/1
> Release: https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2
> CI run: https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972 (sonuç: **success**)
> Upstream: `roxyrekt/Migurdex` `main` hâlâ `v1.10.1` (`4ecd7d7`); upstream PR hedefi için aynı
> metin `PR_DESCRIPTION.md` dosyasında hazırdır.

> **Commit geçmişinde tek doğruluk kaynağı `git log`'dur** (bkz. bölüm 11). Bu dosyadaki commit
> listeleri okunabilirlik için anlatıcı kayıttır; güncel ve eksiksiz liste için
> `git log --oneline v1.10.1..main` çalıştırılmalıdır.

---

## İçindekiler

1. [Kısa özet](#1-kısa-özet)
2. [Başlangıç noktası ve hedef](#2-başlangıç-noktası-ve-hedef)
3. [Kronolojik geliştirme akışı](#3-kronolojik-geliştirme-akışı)
4. [Mimari çözüm](#4-mimari-çözüm)
5. [Karşılaşılan hatalar ve çözümler](#5-karşılaşılan-hatalar-ve-çözümler)
6. [Güvenlik ve veri bütünlüğü kararları](#6-güvenlik-ve-veri-bütünlüğü-kararları)
7. [Test ve doğrulama stratejisi](#7-test-ve-doğrulama-stratejisi)
8. [v1.10.0 sürüm uyumlama](#8-v1100-sürüm-uyumlama)
9. [Paketleme ve kök kurulum yükseltmesi](#9-paketleme-ve-kök-kurulum-yükseltmesi)
10. [Deokwave sağlayıcı testi](#10-deokwave-sağlayıcı-testi)
11. [Yapılan commit’ler](#11-yapılan-commitler)
12. [Dosya etkisi ve istatistikler](#12-dosya-etkisi-ve-istatistikler)
13. [Bilinen sınırlar](#13-bilinen-sınırlar)
14. [Kullanışlı dosyalar](#14-kullanışlı-dosyalar)
15. [PR #1 merge ve `v1.10.2` release](#15-pr-1-merge-ve-v1102-release)

---

## 1. Kısa özet

Migurdex’e anime indirme yeteneği eklendi.

Özellik iki yüz sunar:

- **CLI:** `migurdex download ...`
- **TUI:** bölüm kaynak ekranında `İndir` eylemi

Desteklenen medya türleri:

- Doğrudan `MP4`
- `M3U8/HLS` (harici `yt-dlp` süreciyle)
- Yan dosya olarak `.srt`, `.ass`, `.vtt`

Ana teknik hedefler:

- API tarafından çözülen doğrudan kaynakları diske kaydetmek
- İzleme geçmişi ve tracker senkronunu tetiklememek
- MP4 için gerçek streaming, resume ve atomik yazım
- HLS için yt-dlp/ffmpeg kullanımı
- Altyazıları güvenli biçimde yan dosya olarak indirmek
- Aynı hedefe eşzamanlı indirmeyi engellemek
- Yol güvenliği ve path traversal saldırılarını kapatmak
- URL, token, header ve cookie sızıntısını önlemek
- CLI ve TUI akışlarını bozmadan yeni davranışı eklemek

Sonuç:

- Offline testler: **275/275** (genel takım) · indirme/TUI kapsamı **81 metot / 110 çalışan case / 10 sınıf**
- Canlı API smoke: **21/21 endpoint** (26.09.2026, v1.10.0 öncesi koşum — 13 sağlayıcı)
- TUI canlı akış: **371.406.042 bayt** gerçek indirme
- CLI canlı akış: **437.668.399 bayt** gerçek MP4 indirmesi
- `v1.10.1` üzerine rebase (tek çakışma `Program.cs`, çözüldü)
- Kök kurulum **v1.10.0 + download** olarak yükseltildi
- **PR #1 merge edildi** (merge commit `44f4010`, 22 commit, 40 dosya, +11.559/−249)
- **`v1.10.2` yayınlandı** (fork `Nutaliaxd/Migurdex`; CI release koşusu başarılı) — bkz. bölüm 15

---

## 2. Başlangıç noktası ve hedef

### 2.1 Başlangıç

Çalışma, upstream projenin `v1.9.2` sürümü üzerinden başladı:

```text
v1.9.2 = 444a49e
```

Bu sürümde:

- Anime arama, detay, bölüm ve kaynak çözümleme vardı.
- MPV ile oynatma vardı.
- API ve plugin altyapısı vardı.
- Video kaynakları çözülüyordu ama diske kaydedilmiyordu.
- `Embed` kaynakları extractor’lar MP4/M3U8’e çeviriyordu.
- API ve CLI/TUI katmanları hazırdı.

### 2.2 Hedef

Yeni kullanıcı deneyimi:

```text
migurdex download "one piece" -e 1
```

veya TUI içinde:

```text
Kaynak ekranı → İndir
```

Beklenen davranış:

- Doğrudan MP4/HLS kaynağı seçilir.
- Video diske kaydedilir.
- Varsa altyazılar yan dosya olarak yazılır.
- MPV başlatılmaz.
- İzleme geçmişi yazılmaz.
- AniList/MAL senkronu tetiklenmez.
- İndirme iptal edilebilir.
- Yarım kalan MP4 devam ettirilebilir.

---

## 3. Kronolojik geliştirme akışı

### 3.1 Kaynak klonlama ve dal

Depo `roxyrekt/Migurdex` klonlandı:

```text
C:\Users\naton\OneDrive\Desktop\migu\source
```

Yeni dal açıldı:

```text
feature/download
```

İlk temel:

```text
444a49e (v1.9.2)
```

---

### 3.2 Temel indirme feature commit’i

İlk büyük commit:

```text
c3d307a feat: add anime download support
```

v1.10.0 rebase sonrası karşılığı:

```text
321fe38 feat: add anime download support
```

v1.10.1 rebase sonrası karşılığı:

```text
be85d61 feat: add anime download support
```

Bu commit şunları ekledi:

- `Migurdex.Cli/Services/Downloads/` altında çekirdek indirme yığını
- `DownloadCommand`
- `DownloadSourceResolver`
- TUI’de `İndir` eylemi
- API tarafında kaynak metadata birleştirme
- `M3U8PlaylistExtractor` güvenlik düzeltmeleri
- CLI config alanları
- Offline testler
- `DOWNLOAD.md`

#### Eklenen çekirdek dosyalar

| Dosya | Sorumluluk |
|---|---|
| `DownloadHttp.cs` | HTTP istemcisi, redirect zinciri, header allowlist’leri, kaynak fingerprint’i |
| `DownloadInterfaces.cs` | `IDownloadService`, `IMp4Downloader`, `IHlsDownloader`, `ISubtitleDownloader`, `IExternalProcessRunner` |
| `DownloadModels.cs` | `DownloadRequest`, `DownloadResult`, `DownloadProgress`, `DownloadPath`, `HlsDownloadOptions` |
| `DownloadPathBuilder.cs` | Güvenli dosya/klasör yolu üretimi, sanitizasyon, UTF-8 byte bütçesi |
| `DownloadService.cs` | MP4/HLS/altyazı orkestrasyonu, fallback, uyarı toplama |
| `DownloadServiceCollectionExtensions.cs` | DI kayıtları |
| `DownloadTargetLock.cs` | Süreç içi semafor + `.migurdex.lock` dosya kilidi |
| `ExternalProcessRunner.cs` | yt-dlp süreç yönetimi, çıktı sınırı, iptal |
| `Mp4Downloader.cs` | MP4 indirme, `Range` resume, 206/416 doğrulama |
| `Mp4ResumeMetadata.cs` | `.part.meta` dosyası, ETag/Last-Modified eşleşmesi |
| `SubtitleDownloader.cs` | HTTP ve `data:` URI altyazı indirme, biçim doğrulama |
| `YtDlpHlsDownloader.cs` | HLS indirme, yt-dlp sarmalayıcı |

#### CLI katmanı

| Dosya | Sorumluluk |
|---|---|
| `DownloadCommand.cs` | CLI argüman ayrıştırma, API çağrı zinciri, JSON çıktı, fallback |
| `DownloadSourceResolver.cs` | Sağlayıcı/grup/bölüm çözümleme, en fazla 3 aday seçimi |
| `SourceSelector.cs` | Kalite/format/hoster/grup sıralaması |

#### TUI katmanı

| Dosya | Değişiklik |
|---|---|
| `EpisodeSourcesView.cs` | Kaynak seçimi sonrası `Oynat / İndir / Geri` akışı eklendi |
| `FuzzyPrompt.cs` | Sonuç ekranı için filtresiz prompt desteği eklendi |
| `TuiApplicationCancellation.cs` | TUI içi iptal token’ı |

#### API/Core tarafı

| Dosya | Değişiklik |
|---|---|
| `AnimeEndpoints.cs` | Embed extractor sonuçlarıyla provider metadata’sı birleştirildi |
| `M3U8PlaylistExtractor.cs` | Header’ların yanlış taşınması ve `Range` çakışması önlendi |

---

### 3.3 TUI markup hatası

Kullanıcı, anime arattıktan sonra bölüm geçişinde şu hatayı aldı:

```text
System.InvalidOperationException: Unbalanced markup stack.
Did you forget to close a tag?
```

Kök neden:

- `EpisodeSourcesView.HandleSelectedSourceAsync` içinde Spectre.Console markup’u iki kez açılıyordu:

```text
[grey]Anime [grey]Bölüm[/]
```

Çözüm:

- Tek `[grey]...[/]` çiftine indirildi.
- Dinamik metinler `Markup.Escape` ile kaçırıldı.
- `FuzzyPrompt` footer’ı savunmacı hale getirildi.
- `TuiMarkupSafetyTests` eklendi.

Commit:

```text
e9e01e9 fix(tui): balance source selection markup
```

Rebase sonrası karşılığı:

```text
bd59ca4 fix(tui): balance source selection markup
```

v1.10.1 rebase sonrası karşılığı:

```text
6c366c9 fix(tui): balance source selection markup
```

---

### 3.4 İndirme sonrası gereksiz `Ara:` filtresi

Kullanıcı, indirme sonrası sonuç ekranında şunu sordu:

```text
İndirme tamamlandı

Ara:
› Geri
```

Bu `Ara:` alanı anime araması değil, genel `FuzzyPrompt` filtresiydi.

Kök neden:

- Sonuç ekranı tek seçenekli bir `FuzzyPrompt` kullanıyordu.
- Tek `Geri` seçeneği olduğu için filtre anlamsızdı.
- Kullanıcı yanlışlıkla harf yazarsa liste boşalabiliyordu.

Çözüm:

- `FuzzyPrompt.Show`’a `searchable` parametresi eklendi.
- Sonuç ekranı `searchable: false` ile çağrıldı.
- `Ara:` satırı tamamen kaldırıldı.
- Footer `Enter devam • Esc geri` olarak sadeleştirildi.
- `FuzzyPromptSearchableTests` eklendi.

Commit:

```text
4cd2ac8 fix(tui): hide search filter on download result
```

Rebase sonrası karşılığı:

```text
3a3cfc5 fix(tui): hide search filter on download result
```

v1.10.1 rebase sonrası karşılığı:

```text
b3dd5d5 fix(tui): hide search filter on download result
```

---

### 3.5 HLS progress güncellenmeme hatası

TUI canlı testinde 371 MB’lık bir HLS indirmesi başarıyla tamamlandı ama ilerleme satırı şöyle kaldı:

```text
Bağlanıyor • 0 B • Esc: iptal
```

Kök neden:

```text
--print after_move:filepath
```

yt-dlp’de `--quiet` davranışını ima ediyordu. Bu yüzden progress çıktısı türetilmiyordu.

Çözüm:

- yt-dlp argüman listesine `--progress` eklendi.
- `--print after_move:filepath` yerinde korundu.
- Yüzde ve `MiB/x MiB` kalıplarının `Downloading` aşamasına dönüştüğü test edildi.
- `HlsDownloaderTests` genişletildi.

Commit:

```text
dc344a0 fix(downloader): report live yt-dlp HLS progress
```

Rebase sonrası karşılığı:

```text
5014025 fix(downloader): report live yt-dlp HLS progress
```

v1.10.1 rebase sonrası karşılığı:

```text
00b2ee7 fix(downloader): report live yt-dlp HLS progress
```

Sonuç:

```text
İndiriliyor • <indirilen> / <toplam> • Esc: iptal
```

---

### 3.6 v1.10.0 rebase

Upstream v1.10.0 yayımlandı:

```text
4035f9a feat(providers): add Deokwave
8094425 fix(cli): altyazi ismi + link yerine indir
```

Bu sürüm:

- Sağlayıcı sayısını 13’ten 14’e çıkardı.
- MPV altyazı indirme davranışını düzeltti.
- API sözleşmesinde breaking change getirmedi.

Rebase kararı:

- `feature/download` lokal ve paylaşılmamıştı.
- Upstream delta küçüktü.
- Doğrusu temiz ve lineer tarih olduğu için rebase seçildi.

Yedek dal:

```text
backup/download-pre-v110
```

Rebase:

```bash
git rebase v1.10.0 feature/download
```

Sonuç:

- Çakışma çıkmadı.
- 10 feature commit’i yeni sürüm üzerine taşındı.
- `Deokwave` plugin’i için `dotnet restore` çalıştırıldı.
- Build ve test tekrar doğrulandı.

---

### 3.7 Paketleme ve kök kurulum yükseltmesi

Yeni Windows x64 paketi üretildi:

```text
build.ps1 -Publish
```

Paket içeriği:

| Dosya | Boyut |
|---|---:|
| `migurdex.exe` | 23.466.537 B |
| `api\Migurdex.Api.exe` | 108.374.872 B |
| `api\migurdex_native.dll` | 8.661.504 B |
| `api\Plugins\*` | 14 plugin DLL |
| `migurdex-win-x64.zip` | 61.605.017 B |

SHA-256:

| Dosya | SHA-256 |
|---|---|
| `migurdex.exe` | `DE9EA5634B16E07BC3B16B8C8C7EE07CB311FE489AF78E09AAD3E608C86E3E79` |
| `migurdex-win-x64.zip` | `BC17A9FFA03D0B9BE5AA9004C6E6E94DB35C469442BA1515B3090FB6C96DB5AA` |
| `api\migurdex_native.dll` | `91BA419E97AD5BC0F0CA1ADF9768CF60FDFAC72234AADE28AC02DB46C1B44627` |

Kök kurulum yükseltildi:

```text
C:\Users\naton\OneDrive\Desktop\migu\
```

Eski sürüm yedeği:

```text
C:\Users\naton\OneDrive\Desktop\migu\backup-v1.9.2\
```

Yeni sürüm köke yüklendi:

```text
migurdex.exe
api\
migurdex-win-x64.zip
```

Geri alma yolu:

- `backup-v1.9.2` içindeki `migurdex.exe` ve `api\` klasörünü köke geri kopyalamak.

---

### 3.8 v1.10.1 rebase

Upstream v1.10.1 yayımlandı:

```text
e4a32b4 fix: update database link for TurkAnime provider
914dfdd fix(anizm): handle unnamed anizm fansub groups
4ecd7d7 feat(appimage): zsync update info + AppRun locale wrapper
```

Bu sürüm:

- TurkAnime DB bağlantısını güncelledi (`mdexturkanime` → `roxyrekt` HuggingFace dataset'i).
- Anizm sağlayıcısında isimsiz fansub gruplarını ele aldı.
- AppImage güncelleme akışına zsync bilgisi ve AppRun locale sarmalayıcısı ekledi.
- `Program.cs`'te encoding guard'larını kaldırdı ve `!isOnline` bloğundaki `ReadKey`'yi
  `Console.IsInputRedirected` guard'ı + try/catch ile korudu.
- Sağlayıcı sayısını (14) ve API sözleşmesini değiştirmedi.

Yedek dal:

```text
backup/download-pre-v1101
```

Rebase:

```bash
git rebase v1.10.1 feature/download
```

Sonuç:

- 21 feature commit'i yeni tabana taşındı.
- Tek çakışma `Migurdex.Cli/Program.cs` (ana özellik commit'i):
  - Dal tarafının TUI iptal mimarisi korundu (`TuiApplicationCancellation`, `tuiToken`,
    `activeNavigator`, `catch (OperationCanceledException)`, `finally`).
  - Upstream'in guard'lı + try/catch `ReadKey` versiyonu alındı.
  - Encoding guard kaldırma otomatik merge ile geldi.
  - Çözüm, eski dal sürümüyle karşılaştırmalı diff ile doğrulandı: fark birebir
    upstream'in `Program.cs` değişiklikleri.
- `README.md` otomatik merge edildi; DB bağlantısı ve bölüm sırası gözle doğrulandı.
- Restore + Release build (19 proje, 0 uyarı / 0 hata) + offline test 275/275 doğrulandı.
- Tüm feature commit hash'leri yeniden yazıldı; eski hash'ler `backup/download-pre-v1101`
  dalında korunuyor.

> Proje sayısı notu: 19 proje, `v1.10.0` ve sonrası çözüm ağacıdır (upstream `Deokwave`
> plugin projesi dahil). `v1.9.2` tabanlı ağaç 18 projeydi; 26–27 Eylül tarihli kayıtlardaki
> "18 proje" ifadeleri o ağaca aittir.

---

### 3.9 PR #1 merge ve `v1.10.2` release

29.09.2026'da `feature/download` dalı fork (`Nutaliaxd/Migurdex`) `main` dalına PR #1 ile
birleştirildi (merge commit `44f4010`) ve aynı gün `v1.10.2` etiketiyle GitHub release'i
yayınlandı. Kanıtlar, asset listesi ve doğrulama komutları bölüm 15'tedir. Bu noktadan sonra
aktif dal **`main`**'dir.

---

## 4. Mimari çözüm

### 4.1 Genel akış

```text
DownloadCommand (CLI)  ─┐
EpisodeSourcesView (TUI) ─┴─> DownloadService
                               ├─ DownloadPathBuilder
                               ├─ DownloadTargetLock
                               ├─ Mp4Downloader
                               ├─ YtDlpHlsDownloader
                               └─ SubtitleDownloader
```

### 4.2 MP4 indirme

- Ayrı `HttpClient` kullanılır.
- `AllowAutoRedirect=false`.
- Yönlendirmeler elle, en fazla 5 adım izlenir.
- `VideoSource.Headers` uygulanır.
- `Range: bytes=N-` ile devam edilir.
- `206 Partial Content` doğrulanır.
- `200 OK` gelirse parça sıfırdan başlar.
- `416` ve doğru `Content-Range` gelirse dosya tamamlanmış kabul edilir.
- `.part` dosyası final adına ancak tam doğrulama sonrası taşınır.
- İptal durumunda `.part` korunur.

### 4.3 HLS indirme

- Harici `yt-dlp` süreci kullanılır.
- `ffmpeg` gerekiyorsa kullanıcı tarafından kurulur.
- URL argv yerine `--batch-file` ile geçilir.
- Yalnız güvenli header allowlist’i yt-dlp’ye taşınır.
- Geçici job dizini kullanılır.
- Çıktı gerçek uzantısıyla korunur.
- `--progress` ile canlı ilerleme sağlanır.
- İptal durumunda süreç ağacı sonlandırılır.

### 4.4 Altyazı indirme

- `VideoSource.Subtitles` kullanılır.
- `data:` URI desteklenir.
- HTTP/HTTPS altyazılar indirilir.
- Yalnız `.srt`, `.ass`, `.vtt` kabul edilir.
- 10 MiB sınır vardır.
- HTML/JSON hata sayfaları altyazı olarak yazılmaz.
- Altyazı hatası video sonucunu bozmaz, uyarı olur.

### 4.5 Hedef kilidi

- Süreç içi `SemaphoreSlim`
- Dosya bazlı `<stem>.migurdex.lock`
- Aynı hedefe iki eşzamanlı indirme engellenir.
- Kilit alınamazsa altyazılar atlanır, video sonucu korunur.

### 4.6 Yol güvenliği

- URL’den dosya adı üretilmez.
- Anime/section/episode metadata’sından dosya adı üretilir.
- Geçersiz karakterler temizlenir.
- Windows reserved names etkisizleştirilir.
- UTF-8 byte bütçesi uygulanır.
- `..` ve root dışına çıkış engellenir.

---

## 5. Karşılaşılan hatalar ve çözümler

### 5.1 Spectre markup hatası

**Hata:**

```text
Unbalanced markup stack
```

**Neden:** İki `[grey]`, tek `[/]`.

**Çözüm:** Dengeli markup + `Markup.Escape`.

---

### 5.2 Gereksiz `Ara:` filtresi

**Hata:** Tek seçenekli sonuç ekranında filtre alanı görünüyordu.

**Çözüm:** `searchable: false`.

---

### 5.3 HLS ilerlemesi sabit kalıyordu

**Hata:**

```text
Bağlanıyor • 0 B • Esc: iptal
```

**Neden:** `--print after_move:filepath` progress’i kapatıyordu.

**Çözüm:** `--progress` eklendi.

---

### 5.4 Adaylar arasında `.part` karışması riski

**Risk:** İlk adayın `.part` dosyası ikinci adaya append edilebilirdi.

**Çözüm:**

- Kaynak fingerprint’i eklendi.
- Aday başına ayrı `.part` adı kullanıldı.
- ETag/Last-Modified metadata’sı eklendi.
- Bayat parça asla append edilmez.

---

### 5.5 Cross-origin header sızıntısı

**Risk:** `Authorization`, `Cookie`, `X-Api-Key` gibi header’lar redirect sonrası başka origin’e gidebilirdi.

**Çözüm:**

- Yönlendirme zinciri elle izlendi.
- Origin değişince hassas header’lar düşürüldü.
- yt-dlp’ye yalnız güvenli allowlist geçti.
- Altyazı fallback’inde same-origin kontrolü yapıldı.

---

### 5.6 Süreç iptalinde takılma

**Risk:** yt-dlp/ffmpeg child process’leri pipe’ları açık tutarsa iptal sonsuza kadar bekleyebilirdi.

**Çözüm:**

- `TryKillProcessTree`
- Sınırlı çıkış beklemeleri
- 256 KiB çıktı sınırı
- Job Object / process tree kill

---

### 5.7 `Content-Range: bytes */*`

**Risk:** Toplam boyut bilinmediğinde eksik gövde final dosyası olabilirdi.

**Çözüm:**

- Toplam boyut doğrulanamıyorsa finalize edilmez.
- Kısa gövde reddedilir.

---

### 5.8 `.NET SDK` eksikliği

**Hata:**

```text
No .NET SDKs were found
```

**Çözüm:**

- Portatif .NET SDK 10.0.401 kuruldu.
- `DOTNET_ROOT` ve PATH ayarlandı.
- Restore/build/test bu SDK ile çalıştırıldı.

---

### 5.9 Rust native derleme araçları

**Hata:** `btls-sys`/BoringSSL derlemesi `cmake` ve `nasm` istedi.

**Çözüm:**

- CMake 4.4.3
- NASM 2.16.03
- LLVM/libclang
- MSVC BuildTools
- Rust 1.98.1

---

### 5.10 Deokwave Cloudflare engeli

**Hata:**

```text
HTTP 403 - Just a moment...
```

**Neden:** `deokwave.com` Cloudflare JS challenge kullanıyor.

**Durum:** Uygulama hatası değil.

**Çözüm:** Kod tarafında değişiklik gerekmedi. Temiz JSON hatası verildi, kullanıcı verisi değişmedi, sızıntı olmadı.

---

## 6. Güvenlik ve veri bütünlüğü kararları

- Yalnız HTTP/HTTPS kabul edilir.
- `Embed` ve `Unknown` kaynaklar indirilmez.
- MPV, izleme geçmişi ve tracker senkronu indirme sırasında çalışmaz.
- Hata mesajlarında URL/header maskelenir.
- `--debug` yalnız güvenli özet verir.
- Aynı hedefe lock alınır.
- Yol güvenliği katmanlıdır.
- HLS URL’i argv yerine batch dosyasına yazılır.
- yt-dlp’ye `--no-config` verilir.
- Dış süreç çıktısı sınırlı tutulur.
- Altyazı gövdesi imza ile doğrulanır.
- Medya olmayan Content-Type’lar reddedilir.
- Resume yalnız kaynak kimliği doğrulanırsa devam eder.

---

## 7. Test ve doğrulama stratejisi

### 7.1 Offline

```text
dotnet restore
dotnet build Migurdex.slnx -c Release
dotnet test ... --filter "FullyQualifiedName!~ExtractorSmokeTests"
```

Sonuç:

```text
275/275 geçti
```

Bu koşum 29 Eylül 2026 tarihli `v1.10.1` rebase ağacıdır (Release, 19 proje, 0 uyarı / 0 hata);
bu ağaç PR #1 ile `main`'e merge edilip `v1.10.2` olarak yayımlandı, merge sonrası kod değişmedi.

### 7.2 Test sınıfları

Bu tablo **8 sınıflık filtre** koşusunun sonucudur — `DownloadPathBuilderTests` (4) ve
`ExternalProcessRunnerTests` (3) dahil edilmediği için toplam 8 sınıf / 103 case:

| Sınıf | Sonuç |
|---|---:|
| `Mp4DownloaderTests` | 20/20 |
| `SubtitleDownloaderTests` | 10/10 |
| `HlsDownloaderTests` | 8/8 |
| `DownloadServiceTests` | 7/7 |
| `DownloadCommandTests` | 15/15 |
| `ApiClientServiceTests` | 2/2 |
| `TuiMarkupSafetyTests` | 23/23 |
| `FuzzyPromptSearchableTests` | 18/18 |
| **8 sınıflık filtre toplamı** | **103/103** |

**10 sınıflık tam kapsam: 110/110** — 103 + `DownloadPathBuilderTests` 4 +
`ExternalProcessRunnerTests` 3. Kanonik metrikler **81 metot / 110 çalışan case / 10 sınıf**
(`DOWNLOAD.md` → `Test kapsamı`). Tam sınıf/metod/case dökümü ve 103↔110 farkının kaydı için
`TEST_RESULTS.md` bölüm 1 ve `DOWNLOAD.md` → `Test kapsamı` bölümlerine bakın.

### 7.3 Canlı API

21 endpoint test edildi, hepsi `HTTP 200` (26 Eylül 2026 koşumu, v1.10.0 rebase'i öncesi:
o noktada 13 sağlayıcı; güncel sayı 14).

### 7.4 TUI canlı

Gerçek `naruto` akışı, gerçek indirme, `exit=0`.

### 7.5 CLI canlı

Gerçek `One Piece` MP4 indirmesi ve ffprobe doğrulaması.

### 7.6 Deokwave

Plugin yükleniyor, sağlayıcı çözülüyor; Cloudflare upstream engeli nedeniyle arama boş dönüyor.

---

## 8. v1.10.0 sürüm uyumlama

Upstream v1.10.0 ile birlikte:

- Deokwave provider eklendi.
- Sağlayıcı sayısı 14’e çıktı.
- Altyazı oynatma davranışı düzeltildi.
- API sözleşmesi değişmedi.

Rebase sonrası:

- Çakışma çıkmadı.
- Tüm feature commit’leri korundu.
- Build ve testler geçti.
- `feature/download`, `v1.10.0` üzerinde lineer hale getirildi.

---

## 9. Paketleme ve kök kurulum yükseltmesi

Detaylı paket bilgisi `TEST_RESULTS.md` içinde bulunur.

Kök kurulumda artık:

```text
v1.10.0 + download feature
```

çalışıyor.

Eski kurulum:

```text
backup-v1.9.2
```

klasöründe korunuyor.

---

## 10. Deokwave sağlayıcı testi

| Kontrol | Sonuç |
|---|---|
| Plugin pakette yüklü mü? | Evet |
| API tarafından tanınıyor mu? | Evet |
| `-p Deokwave` çözülüyor mu? | Evet |
| Arama sonuç dönüyor mu? | Hayır |
| Neden? | Cloudflare JS challenge / HTTP 403 |
| Uygulama hatası mı? | Hayır |
| `--debug` sızıntısı var mı? | Hayır |
| Diskte yan dosya kaldı mı? | Hayır |

---

## 11. Yapılan commit’ler

**Tek doğruluk kaynağı `git log`'dur.** Aşağıdaki listeler ve eşlemeler okunabilirlik için
anlatıcı kayıttır; commit sayısı, sırası ve içeriği için şunlar çalıştırılmalıdır:

```bash
git log --oneline v1.10.1..main     # güncel ve eksiksiz liste (22 commit + merge)
git rev-list --count v1.10.1..main   # dinamik commit sayısı
git log --oneline --graph --all -30 # dal ve merge grafiği
git describe --tags                 # HEAD'in etiketli sürümü (v1.10.2)
```

`v1.10.1..main` kapsamındaki commit'ler (PR #1, 22 commit) — `feature/download` üzerinde
geliştirildi, `main`'e merge commit `44f4010` ile birleştirildi:

| Commit | Konu |
|---|---|
| `be85d61` | `feat: add anime download support` |
| `6c366c9` | `fix(tui): balance source selection markup` |
| `320302e` | `docs: document download feature and operations` |
| `b3dd5d5` | `fix(tui): hide search filter on download result` |
| `6135f91` | `docs: update download history for result prompt fix` |
| `ed397f0` | `docs: clarify latest code commit in download guide` |
| `f19e2a4` | `docs: record offline validation results` |
| `fc6747f` | `docs: record live API smoke results` |
| `ba2665f` | `docs: add consolidated test results report` |
| `00b2ee7` | `fix(downloader): report live yt-dlp HLS progress` |
| `38972ce` | `docs: record v1.10.0 rebase and latest verification` |
| `07d8110` | `docs: document upstream v1.10.0 integration notes` |
| `7792f47` | `docs: record v1.10.0 packaged build` |
| `e3345a9` | `docs: record Deokwave smoke result` |
| `8226b95` | `docs: record root installation upgrade` |
| `e9d8276` | `docs: add development log and PR description` |
| `d6774a7` | `docs: record PR link in development log` |
| `4d56774` | `docs: update PR description with final diff stats` |
| `9a52271` | `docs: finalize PR commit count` |
| `8c58e2f` | `docs: make PR description commit count dynamic` |
| `7ca27de` | `docs: round PR diff stats` |
| `c4c06c8` | `docs: record v1.10.1 rebase and compatibility` |

Merge commit (PR dışı, ancak `v1.10.1..main` sayımına **dahildir**: 22 PR commit + 1 merge):

```text
44f4010 Merge pull request #1 from Nutaliaxd/feature/download
```

Bu kaydın kendi düzenleme commit'leri de `main` üzerine bu listeden sonra düşer; tam liste için
yukarıdaki `git log` komutunu kullanın.

Rebase karşılıkları:

| İlk yazılım (v1.9.2 tabanı) | v1.10.0 rebase sonrası | v1.10.1 rebase sonrası |
|---|---|---|
| `c3d307a` | `321fe38` | `be85d61` |
| `e9e01e9` | `bd59ca4` | `6c366c9` |
| `73535cb` | `81335bc` | `320302e` |
| `4cd2ac8` | `3a3cfc5` | `b3dd5d5` |
| `c6dfbaa` | `c8a7d75` | `6135f91` |
| `285efef` | `8ef695e` | `ed397f0` |
| `374e7cb` | `85f356a` | `f19e2a4` |
| `7ffa2c7` | `febe716` | `fc6747f` |
| `b95e97b` | `1f82f2d` | `ba2665f` |
| `dc344a0` | `5014025` | `00b2ee7` |
| — | `7763b2d` | `38972ce` |
| — | `21d687c` | `07d8110` |

v1.10.0 rebase sonrası oluşturulan commit'ler ilk yazılım sütununda yoktur (—);
v1.10.0 tabanlı hash'lerin tamamı `backup/download-pre-v1101` dalında korunmaktadır.

---

## 12. Dosya etkisi ve istatistikler

v1.10.1 base'ine göre (`DEVELOPMENT_LOG.md` ve `PR_DESCRIPTION.md` hariç):

```text
38 dosya
+10.181 satır
−249 satır
```

Tam diff (`git diff --shortstat v1.10.1..HEAD`, HEAD = merge commit `44f4010`):

```text
40 dosya
+11.559 satır
−249 satır
```

Bu değerler PR #1 istatistikleriyle birebir aynıdır (22 commit, 40 dosya, +11.559 / −249).
Doküman dosyaları da dahildir (`DOWNLOAD.md`, `TEST_RESULTS.md`, `README.md` ve bu log ile
`PR_DESCRIPTION.md`). Yeniden doğrulama: `git diff --shortstat v1.10.1..main`.

Ana alanlar:

- `Migurdex.Cli/Services/Downloads/`
- `Migurdex.Cli/Services/DownloadCommand.cs`
- `Migurdex.Cli/Services/DownloadSourceResolver.cs`
- `Migurdex.Cli/Tui/Views/EpisodeSourcesView.cs`
- `Migurdex.Cli/Tui/FuzzyPrompt.cs`
- `Migurdex.Api/Endpoints/AnimeEndpoints.cs`
- `Migurdex.Core/Extractors/M3U8PlaylistExtractor.cs`
- `Migurdex.Tests/`
- `DOWNLOAD.md`
- `TEST_RESULTS.md`
- `README.md`

---

## 13. Bilinen sınırlar

- HLS için `yt-dlp` ve genellikle `ffmpeg` gerekir.
- HLS resume desteklenmez.
- Altyazı içine gömme yapılmaz, yan dosya olarak iner.
- Sağlayıcı kaynakları upstream değişimlerine bağlıdır.
- `Deokwave` şu anda Cloudflare challenge nedeniyle erişilemiyor.
- Video hash doğrulaması yoktur; boyut/Range/Content-Type doğrulaması vardır.
- **Yerel** kök kurulumda `--version` hâlâ `v0.0.0` gösterir; `build.ps1` sürüm damgası basmıyor.
  CI release paketlerinde sürüm damgası `-p:Version` ile basıldığı için `v1.10.2` doğru
  görünür (bkz. bölüm 15).
- `Deokwave` dışındaki erişim sorunları ve upstream'e bağımlılıklar aynen geçerlidir.

---

## 14. Kullanışlı dosyalar

| Dosya | İçerik |
|---|---|
| `DOWNLOAD.md` | İndirme özelliğinin teknik kullanım ve mimari dokümanı |
| `TEST_RESULTS.md` | Offline/canlı test, paketleme, Deokwave, kök kurulum ve `v1.10.2` release raporu |
| `PR_DESCRIPTION.md` | Fork (merged) ve upstream hedefi için kullanılabilir PR metni |
| `DEVELOPMENT_LOG.md` | Bu dosya; kronolojik geliştirme, merge ve release kaydı |

---

## 15. PR #1 merge ve `v1.10.2` release

29 Eylül 2026'da `feature/download` dalı fork (`Nutaliaxd/Migurdex`) `main` dalına PR ile
birleştirildi ve aynı gün etiketli sürüm yayınlandı. **Aktif dal `main`**'dir; `feature/download`
artık ayrı bir geliştirme hedefi değildir.

### PR #1

| Alan | Değer |
|---|---|
| Başlık | `feat: add anime download support` |
| Link | https://github.com/Nutaliaxd/Migurdex/pull/1 |
| Dal yönü | `feature/download` → `main` |
| Durum | **merged** — 29.09.2026 10:05 UTC |
| Merge commit | `44f4010` — `Merge pull request #1 from Nutaliaxd/feature/download` |
| Ebeveynler | `4ecd7d7` (upstream `v1.10.1`) ve `c4c06c8` (dalın son commit'i) |
| İstatistik | 22 commit · 40 dosya · +11.559 / −249 |

Merge birleştirmeli (merge commit) gerçekleşti; `main` üzerindeki ağaç, upstream `v1.10.1`'in
üzerine dalın tamamının konduğu lineer bir geçmiş değil, iki ebeveynli bir birleşimdir. Yedek
dallar korunuyor: `backup/download-pre-v110` (v1.10.0 öncesi) ve `backup/download-pre-v1101`
(v1.10.1 öncesi).

### `v1.10.2` release

| Alan | Değer |
|---|---|
| Etiket | `v1.10.2` → `44f4010` |
| Release | https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2 (29.09.2026 11:41 UTC) |
| CI run | https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972 — **success** (`workflow_dispatch` @ `v1.10.2`) |
| Upstream taban | `v1.10.1` (`4ecd7d7`) |

Release asset'ları (üç platform matrisi + checksum'lar):

| Asset | Boyut (bayt) |
|---|---:|
| `migurdex-win-x64.zip` | 60.031.590 |
| `migurdex-linux-x64.tar.gz` | 61.318.906 |
| `migurdex-linux-arm64.tar.gz` | 58.679.919 |
| `migurdex-x86_64.AppImage` | 56.900.088 |
| `migurdex-x86_64.AppImage.zsync` | 194.707 |
| `migurdex-aarch64.AppImage` | 54.163.976 |
| `migurdex-aarch64.AppImage.zsync` | 185.357 |
| `sha256sums-win-x64.txt` | 87 |
| `sha256sums-linux-x64.txt` | 92 |
| `sha256sums-linux-arm64.txt` | 94 |

### Doğrulama

| Kontrol | Komut / sonuç |
|---|---|
| Commit sayısı | `git rev-list --count v1.10.1..main` → **23** (22 PR commit + merge `44f4010`) |
| Etiket çözümü | `git rev-list -n1 v1.10.2` → `44f4010…` |
| Sürüm damgası | `git describe --tags` → `v1.10.2` |
| Kaynak kod değişikliği | Merge sonrası kod değişmedi; bölüm 1'deki 275/275 ve 110/110 sonuçları geçerlidir |
| CI sonucu | `success` — linux-x64, linux-arm64, win-x64 |

### Kapsam notları

- `v1.10.2`, **fork'a** özel bir sürüm numarasıdır; upstream `roxyrekt/Migurdex` `main` dalı
  hâlâ `v1.10.1` (`4ecd7d7`) durumundadır ve orada bu çalışma için PR açılmamıştır. Upstream
  hedefi için aynı PR metni `PR_DESCRIPTION.md` içinde hazırdır (base: upstream `main`).
- Release paketi, bölüm 9'daki yerel `build.ps1` paketinden farklıdır: CI sürüm damgası basar
  (yerel pakette `--version` → `v0.0.0`, release paketinde → `v1.10.2`), üç platformu kapsar ve
  `sha256sums-*.txt` ile doğrulanabilir. Yerel `migurdex-win-x64.zip` 61.605.017 bayt,
  CI karşılığı 60.031.590 bayttır.
- Kök kurulum (`C:\Users\naton\OneDrive\Desktop\migu`) release paketiyle **yükseltilmedi**;
  hâlâ doğrulanmış yerel paketi çalıştırır. Karşılaştırma tablosu: `TEST_RESULTS.md` bölüm 9.
- Release notları yalnızca bu PR'ı içerir ("feat: add anime download support by @Nutaliaxd");
  arada başka commit yoktur, dolayısıyla sürüm atlaması veya atlama yapılmamıştır.
