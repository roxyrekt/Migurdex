# Migurdex İndirme Özelliği — Geliştirme Kaydı

> Bu dosya, anime indirme özelliğinin **başlangıçtan merge ve release'e kadar** tüm geliştirme,
> test, hata çözümü, paketleme ve sürüm uyumlama çalışmasını kronolojik ve teknik olarak kaydeder.
>
> Son güncelleme: 29 Eylül 2026 · Durum: **fork PR #1, #2 ve #3 merged**, **`v1.10.2` published**,
> **upstream PR #2 `OPEN`**
> Aktif dal: **`main`** · Merge commit: `10db87d` (PR #3: `fix/source-stream-error-reporting`)
> Temel: `v1.10.1` (`4ecd7d7`) · Sürüm: `v1.10.2` → `44f4010`
> PR (fork, merged): https://github.com/Nutaliaxd/Migurdex/pull/1
> Release: https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2
> CI run: https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972 (sonuç: **success**)
> Upstream PR: https://github.com/roxyrekt/Migurdex/pull/2 (**OPEN**, `MERGEABLE`; temiz dal
> `upstream/download-clean`, 3 commit: `7f1e250` özellik, `f3aaad7` non-TTY help fix,
> `474a43d` dokümantasyon) — eski upstream
> https://github.com/roxyrekt/Migurdex/pull/1 **CLOSED** (duplicate; temiz PR #2 ile değiştirildi)
>
> **Yeni kalıcı süreç kuralı (29 Eylül 2026):** Bundan sonraki tüm yeni özellik ve dokümantasyon
> PR'ları **önce yalnız `Nutaliaxd/Migurdex` fork'una** açılacak ve test edilecek; upstream
> `roxyrekt/Migurdex` PR'ı yalnızca kullanıcı açıkça isterse açılacak. Ayrıntı: bölüm 17.2.
> Fork dokümantasyon PR'sı: [#2](https://github.com/Nutaliaxd/Migurdex/pull/2) **merged** (`62bda06`,
> `5d59491`, `API.md` + `README.md`) — ayrıntı bölüm 17. PR #3 de merged (`10db87d`).
> Birleştirme kararı ve kusur envanterinin güncel durumu: bölüm 18.
>
> **Test toplamları hangi ağaca ait:** bölüm 1'deki **275/275** ve **110/110** değerleri indirme
> özelliğinin merge'li halini (`44f4010`) temsil eder. **293/293** sonucu ise
> `upstream/download-clean` dalına (`f3aaad7`) aittir; Linux runtime doğrulaması ve non-TTY help
> fix bölüm 16'dadır. Branch birleştirme sonrası (`main` @ `10db87d`) genel offline takım
> **309/309**'dur; kırılım ve düzeltilmiş test sayıları bölüm 18.5'tedir.

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
16. [Linux runtime doğrulaması ve non-TTY help fix](#16-linux-runtime-doğrulaması-ve-non-tty-help-fix)
17. [API dokümantasyon çalışması ve fork-öncelikli süreç kuralı](#17-api-dokümantasyon-çalışması-ve-fork-öncelikli-süreç-kuralı)
18. [Branch birleştirme ve kusur envanteri kararı](#18-branch-birleştirme-ve-kusur-envanteri-kararı)

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
- **Upstream PR #2 açıldı** (temiz dal `upstream/download-clean`, 3 commit: `7f1e250` özellik,
  `f3aaad7` non-TTY help fix, `474a43d` dokümantasyon; 39 dosya, +10.173/−266, `OPEN` + `MERGEABLE`);
  önceki upstream PR #1 kapatıldı — bkz. bölüm 15 ve 16
- **Fork PR #2 ve PR #3 merge edildi** (`62bda06`, `10db87d`) ve help fix `main`'e cherry-pick
  edildi (`a7676fb`); `main` = `10db87d`, genel offline takım **309/309** — bkz. bölüm 18

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

### 5.11 Üst düzey `--help` TTY olmadan çöküyordu

**Hata:**

```text
$ migurdex --help
... yardım metni ...
Aborted (core dumped)   # exit 134 / SIGABRT
```

**Neden:** `Program.Main` üst düzey `--help` / `-h` / `help` argümanlarını yakalamıyordu.
Argümanlar düşüyor, TUI başlatma rotasına gidiyordu; yönlendirilmiş stdin'de `Console.ReadKey`
çalıştırıldığı için süreç çöküyordu. `MaybePromptForUpdateAsync` içindeki "bir tuşa basın"
beklemesi de aynı guard'dan yoksundu.

**Kapsam:** `--version` ve `migurdex download --help` zaten doğru çalışıyordu; sorun yalnızca
üst düzey help rotasındaydı.

**Bu Linux'a özgü değildi** — `stdin` yönlendirilmiş her ortamda geçerliydi (CI, Docker `CMD`,
`nohup`, `migurdex --help > dosya`, `$(...)`). Windows'ta yönlendirme yapılmadığı için görünmüyordu.

**Çözüm:** `f3aaad7` — yeni `HelpCommand` sınıfı (`IsHelpToken`, `IsTopLevelRequest`,
`PrintHelp(TextWriter)`, `Run()`), `Program.cs`'de TUI başlatılmadan önce top-level help kontrolü,
`NonInteractiveCommand.Help()` → yeniden kullanılabilir `PrintHelp(TextWriter?)` ayrıştırması ve
`ReadKey` guard'ı. Alt komut yardımı korunuyor; 18 yeni test. Ayrıntı: bölüm 16.

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

### 7.7 Linux runtime (WSL2, Ubuntu 24.04.5)

Gerçek Linux ortamında uçtan uca koşu: restore, build (19 proje / 0 uyarı / 0 hata), test
(275/275), CLI (`--version` → `v1.10.2`, `download --help` release binary'siyle byte-level aynı),
API (`/health` → 200, 14 sağlayıcı / 38 extractor / `rust=true`), AppImage (`--appimage-extract`,
zsync `updateinformation`, çalıştırma). Yerel build ile release binary'si aynı GNU BuildID'yi
taşıyor (`c36ad71424f1fa2ffd952574ab64dd0d952b101a`). arm64 statik doğrulandı, çalıştırılamadı
(x64 ortam, QEMU yok). Ayrıntı: bölüm 16.

### 7.8 Üst düzey yardım (non-TTY)

`f3aaad7` sonrası genel offline takım **293/293** (275 taban + 18 `TopLevelHelpTests`). Elle
kontroller: `migurdex --help`, `-h`, `help` → `exit 0`; `migurdex download --help` alt komut
yardımını koruyor; TUI açılmıyor.

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
- **Linux arm64 runtime doğrulanmadı.** arm64 paketi statik doğrulandı (ELF, `unsquashfs`) ancak
  x64 ortamda QEMU olmadığı için çalıştırılamadı; `--version`, `download --help`, `/health` ve canlı
  indirme arm64 üzerinde test edilmedi (bkz. bölüm 16.5).
- **AppImage checksum manifestinde yok.** Yayımlanan `sha256sums-*.txt` dosyaları yalnız `tar.gz`
  paketini kapsıyor; AppImage bütünlüğü release özetiyle doğrulanamıyor. Bu, indirme özelliğinin
  değil upstream `build-release.yml` iş akışının eksik adımıdır ve bu PR'da düzeltilmemiştir.

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

### Upstream PR

| Alan | Değer |
|---|---|
| Repo | `roxyrekt/Migurdex` |
| PR | https://github.com/roxyrekt/Migurdex/pull/2 |
| Başlık | `feat: add anime download support` |
| Head / base | `Nutaliaxd:upstream/download-clean` → `roxyrekt:main` |
| Durum | **OPEN**, `MERGEABLE` (`mergeable_state: unstable`; henüz check run üretilmedi) |
| Commit | 3 commit: `7f1e250` `feat: add anime download support` (29.09.2026 13:19 UTC) · `f3aaad7` `fix(cli): handle top-level help without tty` (29.09.2026 16:58 +0300) · `474a43d` `docs: record Linux runtime validation` |
| İstatistik | 3 commit · 39 dosya · +10.173 / −266 (kod commit'i sayılırsa: 2 kod + 1 docs) |
| Kapsam | kod + testler + `README.md` + `DOWNLOAD.md` |
| Önceki PR | https://github.com/roxyrekt/Migurdex/pull/1 — **CLOSED**, merge edilmedi (duplicate; temiz PR #2 ile değiştirildi) |

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
  hâlâ `v1.10.1` (`4ecd7d7`) durumundadır. Upstream entegrasyonu
  [PR #2](https://github.com/roxyrekt/Migurdex/pull/2) üzerinden yürüyor (`OPEN`, `MERGEABLE`, temiz
  dal `upstream/download-clean`); önceki upstream
  [PR #1](https://github.com/roxyrekt/Migurdex/pull/1) kapatıldı. PR #2 gövdesi kullanıcının
  saygı/niyet notuyla başlar ve kod + testler + `README.md` + `DOWNLOAD.md` içerir; bu üç internal
  belge (`DEVELOPMENT_LOG.md`, `TEST_RESULTS.md`, `PR_DESCRIPTION.md`) PR'ye **bilinçli olarak
  dahil edilmemiştir** ve yalnızca fork `main` dalında durur — PR #2 gövdesinde mutlak bağlantılarıyla
  işaret edilir. PR #2 gövdesi ayrıca 293/293 test sonucunu, Linux runtime kanıtını ve
  `f3aaad7` help fix'inin ayrıntılı teknik bölümünü içerir; kullanıcının saygı/niyet notu
  gövdenin en üstünde aynen korunur.
- Release paketi, bölüm 9'daki yerel `build.ps1` paketinden farklıdır: CI sürüm damgası basar
  (yerel pakette `--version` → `v0.0.0`, release paketinde → `v1.10.2`), üç platformu kapsar ve
  `sha256sums-*.txt` ile doğrulanabilir. Yerel `migurdex-win-x64.zip` 61.605.017 bayt,
  CI karşılığı 60.031.590 bayttır. Ancak Linux `sha256sums-*.txt` manifestleri **AppImage'leri
  kapsamaz** (bkz. bölüm 16.5).
- Kök kurulum (`C:\Users\naton\OneDrive\Desktop\migu`) release paketiyle **yükseltilmedi**;
  hâlâ doğrulanmış yerel paketi çalıştırır. Karşılaştırma tablosu: `TEST_RESULTS.md` bölüm 9.
- Release notları yalnızca bu PR'ı içerir ("feat: add anime download support by @Nutaliaxd");
  arada başka commit yoktur, dolayısıyla sürüm atlaması veya atlama yapılmamıştır.

---

## 16. Linux runtime doğrulaması ve non-TTY help fix

Bu bölüm, PR #1 merge edildikten ve `v1.10.2` yayınlandıktan **sonra** yürütülen çalışmanın
kronolojik kaydıdır. İki sonuç üretti: (a) özelliğin Linux'da gerçekten çalıştığının kanıtı,
(b) bir CLI yardım hatasının bulunup düzeltilmesi.

**Dal:** `upstream/download-clean` (tabandan `4ecd7d7` / v1.10.1). Bu dal `main`'den ayrıdır;
`main`'deki PR #1 kapsamı bu bölümdeki hiçbir değişikliği içermez.

### 16.1 Zaman çizelgesi

| # | Adım | Sonuç |
|---|---|---|
| 1 | Gerçek Linux ortamı kuruldu (WSL2, Ubuntu 24.04.5, x86_64) | hazır |
| 2 | `dotnet restore Migurdex.slnx` | başarılı |
| 3 | `dotnet build Migurdex.slnx -c Release --no-restore` | 19 proje, **0 uyarı, 0 hata** |
| 4 | `dotnet test ... --filter "FullyQualifiedName!~ExtractorSmokeTests"` | **275/275** (help fix'i öncesi ağaç) |
| 5 | `migurdex --version` | `migurdex v1.10.2` |
| 6 | `migurdex download --help` | release binary'siyle **byte-level aynı** |
| 7 | `migurdex --help` | **ÇÖKTÜ (exit 134)** → bulgu, bkz. 16.3 |
| 8 | `/health` (yerel API) | **200**; `providers=14`, `extractors=38`, `rust=true` |
| 9 | GNU BuildID karşılaştırması (yerel build ↔ release) | ikisi de `c36ad71424f1fa2ffd952574ab64dd0d952b101a` |
| 10 | AppImage: `--appimage-extract` | başarılı |
| 11 | AppImage: zsync `updateinformation` | gömülü ve doğru |
| 12 | AppImage: çalıştırma | başarılı (x86_64) |
| 13 | arm64 payload: derleme + ELF + `unsquashfs` | statik doğrulama başarılı |
| 14 | arm64 payload: çalıştırma | **yapılamadı** (x64 host, QEMU yok) |
| 15 | `sha256sums-*.txt` incelemesi | AppImage'ler manifestlerde **yok** → bkz. 16.5 |
| 16 | `f3aaad7` — non-TTY help fix | 18 yeni test, **293/293** |
| 17 | `474a43d` — dokümantasyon | `DOWNLOAD.md`, `README.md` |

### 16.2 Linux x64 sonuçlarının yorumu

**BuildID eşleşmesi.** Yerel Linux derlemesi ile yayınlanan release binary'si aynı GNU BuildID'yi
taşıyor. BuildID, kaynak kodu ve derleme parametrelerini özetleyen bir linker çıktısıdır; iki
çıktının aynı olması **paketleme adımının kaynak kodu değiştirmediğini** kanıtlar. Tek fark sürüm
damgası (`-p:Version` → `1.10.2` ↔ yerelde `0.0.0`) ve paket biçimidir. Pratik karşılığı: release
binary'si doğrudan çalıştırılabilir bir referanstır — Linux smoke'larında yerel build yerine
release paketi kullanılabilir ve `migurdex download --help` çıktısı byte-level karşılaştırılabilir
(bkz. adım 6).

**`rust=true`.** `/health` yanıtındaki `rust=true`, `migurdex_native.so` köprüsünün Linux'da
başarıyla yüklendiğini doğrular. Bu kritik: kaynak çözümleme Rust köprüsünden geçer ve
köprü yüklenemezse API kaynak çözümlemesini yapamaz (Windows'taki `migurdex_native.dll` ile
aynı durum). `rust=false` olsaydı Linux'daki indirme akışı çalışmazdı.

**Sağlayıcı sayısı.** `providers=14`, `extractors=38`. Bölüm 7.3'teki 13 sağlayıcı kaydı
v1.10.0 rebase'i öncesi bir koşuma aittir; `Deokwave` ile 14'e çıkmıştır. İki kayıt çelişmez.

### 16.3 Bulunan hata: non-TTY üst düzey yardım (bkz. 5.11)

Linux koşumu gerçek bir hata buldu. `migurdex --help` yönlendirilmiş stdin'de çöküyordu
(exit 134) çünkü `Program.Main` üst düzey yardım argümanlarını yakalamıyor, TUI rotasına
düşüyor ve `Console.ReadKey` yönlendirilmiş girdide hata veriyordu.

**Bu Linux'a özgü değildi.** `stdin` yönlendirilmiş her ortamda geçerliydi — CI job'ları, Docker
`CMD`/entrypoint, `nohup`, `migurdex --help > dosya`, `echo | migurdex --help`, `$(migurdex --help)`.
Windows'ta görünmüyordu çünkü orada yönlendirme yapılmıyordu. Bu yüzden bulgu "Linux hatası"
sanılmamalı, **etkileşimsiz ortam hatası** olarak kaydedilmiştir.

**Daraltma.** `--version` ve `migurdex download --help` zaten doğru çalışıyordu; sorun yalnızca
üst düzey help rotasındaydı. Alt komut yardımları hiç etkilenmedi ve düzeltmeden sonra da
korunuyor.

**Çözüm (`f3aaad7`).**

| Bileşen | Değişiklik |
|---|---|
| `Migurdex.Cli/Services/HelpCommand.cs` *(yeni, 71 satır)* | `IsHelpToken`, `IsTopLevelRequest`, `PrintHelp(TextWriter)`, `Run()` — TUI'siz, yalnız yazan yardım rotası |
| `Migurdex.Cli/Program.cs` (+10/−1) | `--version` rotasından sonra, TUI başlatılmadan önce top-level help kontrolü; `MaybePromptForUpdateAsync` içindeki `ReadKey` `Console.IsInputRedirected` ile korumaya alındı |
| `Migurdex.Cli/Services/NonInteractiveCommand.cs` (+25/−14) | `Help()` → yeniden kullanılabilir `internal static void PrintHelp(TextWriter?)`; metin `WriteCommandLines` + `WriteFlagLegend` olarak ayrıştırıldı — **tek kaynak** |
| `Migurdex.Tests/TopLevelHelpTests.cs` *(yeni, 89 satır)* | 18 test |

`IsTopLevelRequest`, ilk argüman devredilen bir komut (`version`, `update`, `auth`, `search`,
`play`, `continue`, `download`) olduğunda `false` döndürür — bu, `migurdex download --help`'in kendi
yardımını basmaya devam etmesini sağlar.

### 16.4 Test toplamları: 275 → 293

| Koşu | Ağaç | Sonuç |
|---|---|---:|
| v1.10.1 rebase sonrası | `main` @ `44f4010` | **275/275** |
| non-TTY help fix sonrası | `upstream/download-clean` @ `f3aaad7` | **293/293** |

Fark **tam olarak 18 yeni `TopLevelHelpTests` case'idir** (5 + 7 + 3 + 1 + 1 + 1). `TopLevelHelpTests`
CLI yardım yönlendirmesini kapsar, indirme kodunu değil; bu yüzden indirme/TUI grubu metrikleri
**değişmedi**: hâlâ **81 metot / 110 çalışan case / 10 sınıf**. 293 genel offline takımın
toplamıdır, 110 indirme/TUI kapsamının toplamıdır — çelişmez.

Elle kontroller (redirected stdin): `migurdex --help` / `-h` / `help` → `exit 0`;
`migurdex download --help` → alt komut yardımı korunuyor, `exit 0`; `migurdex --version` →
değişmedi; TUI açılmıyor.

### 16.5 Linux arm64 ve AppImage checksum boşluğu

**arm64.** Payload derlendi ve statik doğrulandı (ELF mimari = AArch64, `unsquashfs` ile AppImage
içeriğinin açılması, dosya bütünlüğü) ancak **çalıştırılamadı**: doğrulama ortamı x64 ve QEMU
kurulu değil. `--version`, `download --help`, `/health` ve canlı indirme arm64 üzerinde
test edilmedi. Bu bir ürün kusuru değil, ortam sınırıdır — arm64 paketi CI'da x64 ile aynı kaynak
koddan üretildiği için derleme düzeyinde sapma beklenmez. Yine de arm64 runtime kanıtı
doğrulanmadan "arm64 destekleniyor" denmemelidir.

**AppImage checksum manifesti.** `v1.10.2` release'indeki `sha256sums-linux-x64.txt` ve
`sha256sums-linux-arm64.txt` manifestleri **yalnız `tar.gz`** paketini kapsıyor; iki AppImage
manifestlerde **yer almıyor**. Boyutlarla bağımsız doğrulama: bir `sha256sum` satırı
`64 hex + 2 boşluk + ad + 1 satır sonu` = 67 + ad uzunluğu bayttur.

| Manifest | Kapsanan dosya | Beklenen | Yayımlanan | Yorum |
|---|---|---:|---:|---|
| `sha256sums-linux-x64.txt` | `migurdex-linux-x64.tar.gz` | 91 | 92 | tek dosya |
| `sha256sums-linux-arm64.txt` | `migurdex-linux-arm64.tar.gz` | 93 | 94 | tek dosya |
| `sha256sums-win-x64.txt` | `migurdex-win-x64.zip` | 86 | 87 | tek dosya |

Sabit 1 bayt farkı üç manifestte de aynıdır; önemli olan **sayıdır**: manifest başına tam olarak
tek dosya vardır. AppImage'ler de kapsansaydı boyutlar ~150 bayt daha büyük olurdu.

Sonuç: AppImage bütünlüğü release özetiyle **doğrulanamıyor**; yalnız `.zsync` dosyaları mevcut.
Bu bir indirme özelliği kusuru değil, upstream `build-release.yml` iş akışının eksik adımıdır —
checksum üretimine AppImage'lerin de eklenmesi gerekir. **Bu PR kapsamında düzeltilmemiştir.**

### 16.6 Risk ve uyumluluk

- `f3aaad7` yalnız yeni bir yönlendirme ve iki koruma guard'ı ekliyor; oynatma ve indirme akışı
  değişmedi.
- Etkilenen tek davranış: üst düzey yardım artık TUI'ye girmek yerine doğrudan basılıyor — bu,
  etkileşimli terminalde de istenen davranış.
- Windows ve Linux aynı şekilde fayda görüyor; platforma özgü kod veya `#if` yok.
- Yeni paket bağımlılığı, yeni `config.json` alanı, hedef çerçive veya `/api/v1` sözleşmesi
  değişikliği yok; breaking change yok.

## 17. API dokümantasyon çalışması ve fork-öncelikli süreç kuralı

Bu bölüm, `Migurdex.Api` için yazılan tam REST API referansının (`API.md`) kapsamını, okuma
adımlarını, canlı doğrulama koşumunu ve bu çalışma sırasında ortaya çıkan yedi bulguyu kaydeder.

**Dal:** `docs/api-reference` (tabandan `main` @ `83b9044`).
**Commit:** `5d59491` — `docs(api): add full REST API reference`
**Fork PR:** https://github.com/Nutaliaxd/Migurdex/pull/2 (base `main`, head `docs/api-reference`)

Bu çalışma **yalnız dokümantasyondur**: hiçbir `.cs`, `.csproj`, `.json` veya iş akışı dosyasına
dokunulmamıştır. `main` dalındaki bu kayıt da yalnız `.md` dosyalarını değiştirir.

### 17.1 Amaç ve kapsam kararı

| Karar | Değer | Gerekçe |
|---|---|---|
| Kapsam | Yalnız doküman (`API.md` + `README.md` bağlantısı) | Migurdex'in REST yüzeyi belgelenmemişti; `README.md` yalnız CLI/TUI akışını anlatıyordu. Harici istemci yazmak isteyen biri için sözleşme kaynağı yoktu |
| Kod değişikliği | **Yok** | Bulunan yedi kusurun her biri ayrı bir PR konusudur; dokümantasyon PR'ına kod katmak hem incelemeyi hem de fork→upstream aktarımını zorlaştırır |
| Hedef | **Yalnız fork PR** | Aşağıdaki süreç kuralı (17.2) gereği upstream'e PR açılmadı |
| Dil | Türkçe | Mevcut tüm belgeler (`README.md`, `DOWNLOAD.md`, `DEVELOPMENT_LOG.md`, `TEST_RESULTS.md`, `PR_DESCRIPTION.md`) Türkçe; tutarlılık korundu |
| Doğrulama | Canlı uç çağrısı | Kod okuması tek başına yetersizdi: hata biçimleri ve SSE teli yalnız gerçek yanıtlarla teyit edilebiliyordu |

### 17.2 Yeni kalıcı süreç kuralı

> **Kural (29 Eylül 2026, bu çalışmadan itibaren bağlayıcı):**
> Bundan sonraki **tüm yeni özellik ve dokümantasyon PR'ları önce yalnız `Nutaliaxd/Migurdex`
> fork'una** açılacak, orada test edilecek ve merge edilecek. Upstream `roxyrekt/Migurdex` PR'ı
> **yalnızca kullanıcı açıkça isterse** açılacak.

Uygulama:

- Fork PR açılır → merge edilir → release/doğrulama yapılır → **ancak o noktadan sonra** upstream
  hedefi konuşulur.
- Mevcut upstream PR #2 (`roxyrekt/Migurdex/pull/2`) bu kuralın öncesinde açılmıştır, hâlâ `OPEN`
  ve `action_required` durumundadır; **dokunulmadı ve değiştirilmedi.** `main`, `f3aaad7`'in
  cherry-pick karşılığını (`a7676fb`) içerir, ancak upstream dalı olduğu gibi bırakıldı
  (bkz. bölüm 18.3).
- Bu kayıt `main` dalındadır; fork PR #2 (`62bda06`) ve PR #3 (`10db87d`) merge edildi, bkz. bölüm 18.

Gerekçe: 29 Eylül'e kadar akış tersine çalışıyordu (önce upstream'e aç, sonra fork'ta geçiştir).
İlk upstream denemesi (`roxyrekt/Migurdex/pull/1`) duplicate olarak kapanmak zorunda kaldı ve
`pull/2` yeniden açıldı; bu tur iki kez iş yaratmıştı. Yeni kural, tek doğrulama noktası (fork)
bırakıp upstream'de yinelenen inceleme yükünü kaldırır.

### 17.3 Okunan kaynak dosyalar

`API.md` yalnızca tahminden yazılmadı; aşağıdaki dosyalar okunarak çıkarıldı.

| # | Dosya | Alınan bilgi |
|---:|---|---|
| 1 | `Migurdex.Api/Program.cs` | Uç kaydı sırası, OpenAPI üretimi, JSON serileştirme seçenekleri, `UseExceptionHandler`, host/port yapılandırması; **CORS / auth / rate limiting olmadığı** tespiti |
| 2 | `Migurdex.Api/Endpoints/AnimeEndpoints.cs` | `search`, `{provider}/{*animeId}`, `groups`, `sources` uçları; `AnimeDetails.Normalize()` dönüşüm kuralları; `MergeSourceMetadata` öncelik sırası; dedupe ve sessiz-atlanma davranışı |
| 3 | `Migurdex.Api/Endpoints/MetadataEndpoints.cs` | `metadata/search`, `metadata/{source}/{id}`; `anilist`/`jikan`/`mal` kaynak eşlemesi; `mal:`/`anilist:` önekli çapraz arama |
| 4 | `Migurdex.Api/Endpoints/ExtractorEndpoints.cs` | `GET /extractors`, `POST /extractors/resolve`; 9 adımlı doğrulama sırası; SSRF kontrolünün giriş noktası; 15 sn zaman aşımı |
| 5 | `Migurdex.Api/Endpoints/TrackerResolveEndpoints.cs` | `tracker/resolve`, `tracker/lookup`, `POST tracker/mapping`; `fromCache` / `ambiguous` / `candidates` semantiği; boş 200 gövde |
| 6 | `Migurdex.Api/Endpoints/TrackerSeasonEndpoints.cs` | `tracker/seasons`, `tracker/align`, `tracker/episode`; sezon zinciri, `numberingMode`, `isOverflow` |
| 7 | `Migurdex.Api/Common/ApiErrors.cs` | Biçim A (`{"error":"…"}`) ve Biçim B (RFC 7807 `Results.Problem`) ayrımı |
| 8 | `Migurdex.Api/Common/SseHelper.cs` | Tel kurgusu (`event:`/`data:` + `\n\n`), `EventSearchResult`/`EventSource`/`EventProviderError`/`EventDone`/`EventError` sabitleri, `X-Accel-Buffering: no` |
| 9 | `Migurdex.Api/Services/PluginWatcherService.cs` | Plugin yükleme/izleme, `/health` sayaçlarının kaynağı, sağlayıcı kayıt sırası |
| 10 | `Migurdex.Shared/Models/*` | 11 model: `AnimeDetails`, `Episode`, `SeasonMapping`, `SearchResult`, `VideoSource`, `Subtitle`, `MediaMetadata`, `SeasonChain`(+`SeasonChainEntry`), `EntryAlignment`(+`AlignedSeason`), `TrackerResolveResult`/`TrackerCandidate`/`TrackerMappingEntry`, `TrackerEpisodeMapping` |
| 11 | `Migurdex.Shared/Enums/*` | 6 enum: `ContentFormat`, `VideoType`, `ProviderType`, `ProviderCapabilities`, `MetadataSource`, `EntryNumberingMode` — **sayısal** karşılıkları dahil |
| 12 | `Migurdex.Cli/Services/ApiClientService.cs` | CLI'nin gerçekten çağırdığı uç kümesi → **CLI ↔ API eşleme tablosu** |
| 13 | `Migurdex.Cli/Configuration/CliConfig.cs` | `ApiBaseUrl` varsayılanı (`http://127.0.0.1:7045`), 500 ms health yoklaması, `PreferredHosterOrder` |
| 14 | `Migurdex.Shared/IProvider.cs`, `IAnimeProvider.cs` | Sağlayıcı sözleşmesi, `ProviderCapabilities` otomatik hesabı |
| 15 | `Migurdex.Shared/IExtractorManager.cs` | `CanExtract` + çözümleme sözleşmesi, `type == Embed` koşulu |
| 16 | `Migurdex.Api/Properties/launchSettings.json` | `dotnet run` profilinin `http://localhost:7045` olduğu |
| 17 | `Migurdex.Api/appsettings.json` | `ASPNETCORE_URLS` ve yapılandırma anahtarları |

### 17.4 Canlı doğrulama

Kod okuması tek başına yeterli görülmedi; yazılan her örnek ve her durum kodu gerçek yanıtla
teyit edildi.

**Çalıştırma.** `C:\Users\naton\OneDrive\Desktop\migu\api\Migurdex.Api.exe` süreci
`127.0.0.1:7099` üzerinde başlatıldı (CLI'nin kendi portu `7045`'tir; çakışma olmasın diye `7099`
seçildi), 28 uç çağrısı yapıldı, sonra sürec **temiz kapatıldı**.

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:7099"
.\Migurdex.Api.exe
```

Ortam: Windows 11 · `/health` → `{"status":"OK","version":"0.0.0","providers":14,"extractors":38,"rust":true}`
(`0.0.0` yerel build damgasıdır; release'de `1.10.2` görünür.)

| Uç | Sonuç |
|---|---|
| `GET /health` | 200 — 14 provider, 38 extractor, `rust: true` |
| `GET /openapi/v1.json` | 200 — OpenAPI 3.1.1, 16 yol, **gövde şemaları boş** |
| `GET /api/v1/providers` | 200 — 14 kayıt |
| `GET /api/v1/extractors` | 200 — 38 kayıt |
| `GET /api/v1/anime/search` (q yok) | 400 — **boş gövde** (ASP.NET model bağlama seviyesi) |
| `GET /api/v1/anime/search?q=test&provider=Yok` | 404 — `{"error":"Provider 'Yok' bulunamadı."}` |
| `GET /api/v1/anime/search?q=naruto&provider=Animexe` | 200 — 6513 bayt |
| aynı uç `&stream=true` | 200 — `searchResult` × n + `done` (`curl -N` ile doğrulandı) |
| `GET /api/v1/anime/Animexe/naruto` | 200 — 221 bölüm, 2 sezon eşlemesi |
| `GET /api/v1/anime/Animexe/groups?episodeId=naruto/1/1` | 200 — `["AniSekai","YuushaSubs"]` |
| `GET /api/v1/anime/Animexe/sources?episodeId=naruto/1/1` | 200 — 2 kaynak (`type: 1`, `480p`, Tau Video) |
| aynı uç `&stream=true` | 200 — `source` × 2 + `done {"succeeded":2,"failed":0,"errors":[],"totalItems":2}` |
| `GET /api/v1/metadata/search?q=naruto&source=anilist` | 200 — 11214 bayt |
| `GET /api/v1/metadata/anilist/21` | 200 — 2263 bayt, `source: 0` |
| `GET /api/v1/metadata/mal/20` | 200 — `source: 1` (Jikan) |
| `GET /api/v1/metadata/anilist/mal:20` | 200 — çapraz arama, `source: 0` (AniList) |
| `GET /api/v1/metadata/mal/anilist:21` | 200 — çapraz arama, `source: 1` (Jikan) |
| `GET /api/v1/metadata/bilinmeyen/1` | 404 |
| `GET /api/v1/tracker/seasons?anilistId=21` | 200 — 2 kalem, `seasonNumber` 0 ve 1 |
| `GET /api/v1/tracker/lookup?malId=20` | 200 |
| `GET /api/v1/tracker/resolve?provider=Animexe&id=naruto&title=Naruto&year=2002&format=TV` | 200 — `fromCache: true` |
| `GET /api/v1/tracker/align?provider=Animexe&id=naruto` | 200 — `numberingMode: 1`, 4 kalemli zincir |
| `GET /api/v1/tracker/episode?provider=Animexe&id=naruto&season=1&episode=5` | 200 — `{"season":1,"episode":5,"totalEpisodes":220,"isOverflow":false}` |
| `POST /api/v1/tracker/mapping` | 200 — **boş gövde** |
| `POST /api/v1/extractors/resolve` (localhost hedefi) | 400 — `{"error":"Bu host'a istek gönderilemez."}` (SSRF koruması) |
| `POST /api/v1/extractors/resolve` (`Host` başlığı) | 400 — `{"error":"Header 'Host' gönderilemez."}` |
| `POST /api/v1/extractors/resolve` (doğrudan `.mp4`) | 200 — `{"canExtract":false,"results":[]}` |
| `GET /api/v1/extractors/resolve` (yanlış yöntem) | 405 — boş gövde |

**Bu bir build/test koşumu değildir.** Kod derlenmedi, test takımı çalıştırılmadı; bölüm 1'deki
275/275 ve bölüm 16'daki 293/293 sonuçları **değişmedi**. Ayrıntı için bkz. `TEST_RESULTS.md`
→ bölüm 13.

### 17.5 Bulunan yedi kusur — düzeltilmedi, belgelendi

Yedi bulgunun hiçbiri bu çalışmada düzeltilmedi; tamamı `API.md` içinde belgelendi. Ortak gerekçe:
**her biri ayrı bir kod PR'ı ve ayrı bir test konusudur.** Dokümantasyon PR'ına karıştırılırsa
hem değişiklik kapsamı bulanıklaşır hem de fork→upstream aktarımında inceleyicinin odağı dağılır.

| # | Bulgu | Etki | Belgelendiği yer (`API.md`) |
|---:|---|---|---|
| 1 | **API'de CORS / auth / rate limiting yok.** `Program.cs` içinde `AddCors` / `UseAuthentication` / `UseRateLimiter` çağrısı yok. Servis `127.0.0.1`'e bağlanıyor ama `0.0.0.0`'e açılırsa ağdaki herkes `POST /api/v1/tracker/mapping` ile **kalıcı** eşleme tablosunu değiştirebilir | Güvenlik | *Güvenlik notları*, *Hızlı başlangıç* |
| 2 | **SSRF koruması yalnızca `/api/v1/extractors/resolve` ucunda.** Anime sağlayıcı uçlarında (search / groups / sources) koruma yok; koruma, alan adının plugin tarafında sabit kodlanmış olmasına dayanıyor | Güvenlik (derinlik) | *Güvenlik notları* |
| 3 | **SSE kaynak akışında hata sayımı tutarsız.** Extractor çözümlemesi çöken kaynaklar `providerError` üretmiyor, loglanıp atlanıyor; `done` özeti `failed: 0` veriyor. Upstream'dan 3 kaynak gelip hiçbiri çözülemezse `{"succeeded":0,"failed":0,"errors":[],"totalItems":0}` | Gözlemlenebilirlik | *SSE akışı*, *Bilinen sınırlar* |
| 4 | **Zorunlu parametre eksikliği boş 400 döndürüyor** (JSON gövde yok) — doğrulama ASP.NET Core model bağlama katmanında yapılıyor ve uç koduna ulaşmıyor. `q` gönderilip boş string verildiğinde ise `{"error":"Arama sorgusu ('q') boş olamaz."}` dönüyor | Sözleşme tutarlılığı; istemci iki biçimi de ele almalı | *Hata kodları* (Biçim C) |
| 5 | **`/openapi/v1.json` gövde şemaları boş** — modeller için şema üretimi tanımlanmadığı için `components/schemas` dolu değil | Geliştirici deneyimi | *Sağlık ve OpenAPI*, *Bilinen sınırlar* |
| 6 | **SSE `error` olayı hiç gönderilmiyor** — `SseHelper.EventError` sabiti tanımlı ama tek kullanım yeri tanımın kendisi | Sözleşme ölü kodu | *SSE akışı* (olay türleri tablosu) |
| 7 | **`502` (RFC 7807 `problem+json`) canlı gözlemlenemedi** — geçersiz bölüm kimlikleri bile 200 + boş liste döndürdü; biçim yalnızca koddan doğrulandı | Doğrulama boşluğu (belge kusuru değil) | *Bilinen sınırlar* (ilk madde) |

Nihai karar: **düzeltme yok, belgeleme var.** 1, 2 ve 5 ürün/kod değişikliği ister; 3, 4 ve 6
sözleşme/davranış değişikliği ister ve istemci uyumluluğu düşünülmelidir; 7 bir doğrulama
eksikliğidir ve tekrar koşumla kapatılabilir. Hiçbiri bu PR'ın kapsamına alınmadı.

> **Sonraki durum (bölüm 18):** 3 numaralı bulgu PR #3 ile **düzeltildi**; kalanlar `API.md`
> bölüm 19'da **tetik koşulu** formatında karar kaydına dönüştürüldü. Ayrıntı: bölüm 18.4.

### 17.6 Neden upstream'e şimdilik gönderilmedi

- 17.2'deki **yeni süreç kuralı**: önce fork, test, merge; upstream yalnızca açık istek üzerine.
- Upstream PR #2 (`roxyrekt/Migurdex/pull/2`) hâlâ `OPEN` ve `action_required` durumunda. Aynı
  anda ikinci bir PR açmak, inceleyicinin zaten kararsız duran bir PR'ın üstüne yeni yük bindirmesi
  anlamına gelir.
- `API.md` upstream'e bir **dokümantasyon katkısı** olarak kabul edilebilir; ancak yedi bulgunun
  bir kısmı (özellikle 1 ve 2) upstream'in kendi güvenlik borcunu ilgilendirir. Bunları düzeltmeden
  göndermek, dokümanın "bilinen sınırlar" bölümünü upstream bakım yükü haline getirir.
- Bu kayıt `main` dalındadır; fork PR #2 **merge edildi** (`62bda06`). Upstream'e gönderim kararı
  değişmedi: 17.2'deki kural gereği açık istek olmadan gönderilmeyecek (bölüm 18.2, 18.3).

### 17.7 `API.md` bölüm yapısı

1309 satır, 51 KB, 19 bölüm (bu değer PR #2'deki ilk sürüm içindir; güncel değer için dosyanın
kendisine bakın — bölüm 19'daki tetik koşulu ve doğrulama sınırı alt başlıkları eklendikten
sonra büyümüştür).

| # | Bölüm | Kapsam |
|---:|---|---|
| 1 | Genel bakış | Mimari, plugin yükleme, `rust` köprüsü |
| 2 | Hızlı başlangıç | Adres/port tablosu, auth yokluğu, `curl` örnekleri |
| 3 | Konvansiyonlar | Yöntem/rota, içerik tipi, **enum sayısal** serileştirme, adres kodlama, case-insensitivity |
| 4 | Sağlık ve OpenAPI | `/health` alanları, `/openapi/v1.json` ve 16 yolun parametre listesi |
| 5 | Sağlayıcı uçları | `/api/v1/providers`, `capabilities` otomatik hesabı |
| 6 | Anime uçları | `search` (sağlayıcı zarfı), `{*animeId}` + `Normalize()`, `groups`, `sources` + `MergeSourceMetadata` |
| 7 | **SSE akışı** | Yanıt başlıkları, tel kurgusu, 5 olay türü, `done` semantiği tablosu, iki örnek akış |
| 8 | Metadata | AniList/Jikan, `mal:`/`anilist:` önekli çapraz arama |
| 9 | Extractor uçları | `/extractors`, `/extractors/resolve` ve 9 adımlı doğrulama sırası |
| 10 | Tracker uçları | `resolve`, `lookup`, `mapping`, `seasons`, `align`, `episode` |
| 11 | **Veri modelleri** | 11 model, alan alan tablolarıyla |
| 12 | **Enum sözlüğü** | 6 enum'un sayısal karşılıkları |
| 13 | **Hata kodları** | Üç gövde biçimi (A/B/C) + "HTTP 200 dönen hatalar" |
| 14 | **Güvenlik notları** | Auth yokluğu, SSRF koruması ve engelli aralıklar, başlık enjeksiyonu engeli, URL sızıntısı, kalıcı veri |
| 15 | **Limit tablosu** | Uç × alan × limit × aşım davranışı |
| 16 | Uçtan uca akışlar | Arama→oynatma/indirme, kaynak çözümleme, izleme senkronu |
| 17 | **CLI ↔ API eşlemesi** | `ApiClientService` tablosu + CLI'nin çağırmadığı uçlar |
| 18 | Doğrulama kaydı | 29 Eylül 2026 koşumu, 28 uçluk tablo, başlatma komutu |
| 19 | Bilinen sınırlar | 19.1 tetik koşullu kararlar, 19.2 kapsam kayıtları, 19.3 doğrulama sınırı |
| — | İlgili belgeler | `DOWNLOAD.md`, `README.md`, `TEST_RESULTS.md`, `DEVELOPMENT_LOG.md` |

---

## 18. Branch birleştirme ve kusur envanteri kararı

Bu bölüm, 29 Eylül 2026'da fork'taki üç dalın (`docs/api-reference`,
`fix/source-stream-error-reporting`, `upstream/download-clean`) tek bir `main` altında toplanma
kararını, birleştirme sırasının **neden** bu şekilde kurulduğunu ve 17.5'teki kusur envanterinin
bugünkü durumunu kaydeder. Kod değişikliği yapılmamıştır; bu kayıt yalnız `.md` dosyalarını
günceller.

### 18.1 Aktif dal ve commit zinciri

| Dal | Durum | Son commit | Not |
|---|---|---|---|
| `main` | **aktif** | `10db87d` | PR #2 ve PR #3 merge edildi, help fix cherry-pick edildi |
| `docs/api-reference` | PR #2 ile merge | `5d59491` | Dokümantasyon dalı; artık ayrı geliştirme hedefi değil |
| `fix/source-stream-error-reporting` | PR #3 ile merge | `013c01d` | SSE hata bildirimi; artık ayrı hedef değil |
| `upstream/download-clean` | **dokunulmadı** | `474a43d` | Upstream PR #2'nin kaynağı; `roxyrekt/Migurdex/pull/2` açık kalıyor |
| `feature/download` | PR #1 ile merge | `c4c06c8` | v1.10.2 release'inin kaynağı |

`main` üzerindeki birleşim sırası:

```text
44f4010  Merge pull request #1 from Nutaliaxd/feature/download      (PR #1, indirme özelliği)
…
62bda06  Merge pull request #2 from Nutaliaxd/docs/api-reference   (PR #2, API.md)
a7676fb  fix(cli): handle top-level help without tty                (cherry-pick, upstream f3aaad7'den)
10db87d  Merge pull request #3 from Nutaliaxd/fix/source-stream-error-reporting  (PR #3, SSE hata bildirimi)
```

### 18.2 Birleştirme sırası ve nedensellik

Sıra **tesadüfi değil, `API.md` add/add çakışması tarafından belirlenmiştir:**

1. **PR #2 önce merge edildi** (`62bda06`). Bu PR `API.md` dosyasını **yeni** olarak ekliyordu
   (fork `main`'inde `API.md` henüz yoktu).
2. **PR #3 `main`'den açıldığı için** (`main` zaten `API.md` içeriyordu) aynı dosyayı getiriyordu.
   PR #3 önce merge edilseydi `main`'de `API.md` iki farklı kaynaktan gelmiş iki ayrı dosya olarak
   çakışırdı (add/add).
3. **Çözüm:** PR #3 `main` üzerine rebase edildi; çakışan `API.md` **branch tarafı (`theirs`)
   alınarak** çözüldü, çünkü branch'in `API.md`'si daha yeni kopyaydı — `main`'deki PR #2 kopyası
   üzerine PR #3'ün `docs(api): document extract scope and sse error reporting` değişikliği
   (SSE `extract` kapsamı ve hata bildirimi) işlenmişti. Eski kopyayı (`ours`) almak, yeni SSE
   hata yolunu belgelemeden geri gönderme anlamına gelirdi.
4. **`README.md` çakışması** aynı merge'de çözüldü: branch'teki **gerçek `API.md` bağlantısı**
   alındı; `main`'deki geçici "henüz merge edilmedi" notu atıldı. Gerekçe: branch, PR #2'nin
   merge olduğu `main`'den açıldığı için bağlantının hedefinin artık mevcut olduğunu doğru
   varsayıyordu; `main`'deki not ise merge öncesine ait geçici bir durumdu.
5. **Help fix cherry-pick edildi** (`a7676fb`, upstream `f3aaad7`'den) — bkz. 18.3.
6. **PR #3 merge edildi** (`10db87d`).

Rebase sonrası branch/`main` farkı doğrulandı: tam olarak **9 dosya, +337 / −47**.

### 18.3 `upstream/download-clean` neden merge edilmedi

Bu dal **dokunulmadan** bırakıldı (`474a43d`). Gerekçe:

- **Farklı taban.** `upstream/download-clean`, `main`'in PR #1 merge'li halinden değil, upstream
  `v1.10.1`'den (`4ecd7d7`) açılmış temiz bir daldır. Aynı özelliğin (anime indirme) **iki ayrı
  geçmişi** vardır: biri fork `main`'e PR #1 ile (commit `be85d61`), diğeri bu temiz dalda
  (`7f1e250`). İkisini birleştirmek aynı işi iki kez yazma/iki yerden bakım anlamına gelirdi.
- **Cherry-pick tercih edildi.** Bu dalın taşıdığı upstream'e özgü ve `main`'de eksik olan tek kod
  parçası, non-TTY üst düzey yardım düzeltmesi (`f3aaad7`) idi. Bu commit **`main`'e cherry-pick
  edildi** → `a7676fb`. Böylece `main` de düzeltmeyi içerirken `upstream/download-clean` dalına ve
  upstream PR #2'ye **hiç dokunulmamış** oldu.
- **Upstream PR #2 korunuyor.** `roxyrekt/Migurdex/pull/2` **dokunulmadı**, değiştirilmedi; hâlâ
  `OPEN` / `action_required` durumdadır ve `f3aaad7`'i **olduğu gibi** içerir. Yeni süreç kuralı
  (17.2) gereği upstream'e yeni bir PR açılmayacaktır.

### 18.4 Kusur envanterinin bugünkü durumu (17.5 güncellemesi)

17.5'teki yedi bulgunun durumu:

| # | Bulgu | Durum (29.09.2026 sonrası) |
|---:|---|---|
| 1 | CORS / auth / rate limiting yok | **Belgelendi, tetik koşulu eklendi** (`API.md` 19.1 → #2) |
| 2 | SSRF koruması tek uçla sınırlı | **Belgelendi, tetik koşulu eklendi** (`API.md` 19.1 → #1) |
| 3 | SSE kaynak akışında `failed` sayımı tutarsız | **DÜZELTİLDİ** (PR #3, `ab189b3` + `9aa8359`) |
| 4 | Zorunlu parametre eksikliği boş 400 | **Belgelendi, tetik koşulu eklendi** (`API.md` 19.1 → #4) |
| 5 | `/openapi/v1.json` gövde şemaları boş | **Belgelendi, tetik koşulu eklendi** (`API.md` 19.1 → #5) |
| 6 | SSE `error` olayı gönderilmiyor | Değişmedi; sözleşmenin bilinçli parçası |
| 7 | `502` (RFC 7807) canlı gözlemlenmedi | **Belgelendi, tetik koşulu eklendi** (`API.md` 19.1 → #7) |

Bugün için karar: **hiçbir dokümantasyon kusuru düzeltilmemiştir; hepsi tetik koşulu formatında
karar kaydına dönüştürülmüştür.** Düzeltme ancak tetik koşulu gerçekleşirse, ayrı bir kod PR'ı
olarak yapılacaktır. Ayrıntı: `API.md` → bölüm 19.

### 18.5 Test kırılımının düzeltilmiş hali

Bölüm 1 ve 7'deki 275/275 tabanı korunur. Branch birleştirme sonrası (`main` @ `10db87d`) genel
offline takım **309/309**'dur ve kırılım şöyledir:

```text
275   fork main tabanı (indirme özelliği merge'li hali)
 +18  Migurdex.Tests/TopLevelHelpTests.cs          (yeni dosya — non-TTY help fix, cherry-pick)
 +13  Migurdex.Tests/SourceExtractionReportTests.cs (yeni dosya — SSE hata bildirimi, PR #3)
  +3  Migurdex.Tests/DownloadCommandTests.cs        (mevcut dosya — PR #3, 3 yeni case)
=309  ✔ dosya bazında [Fact]+[InlineData] toplamı da tam 309
```

**Düzeltme notu:** Önceki ajanın verdiği "17 yeni test (14+3)" rakamı **yanlıştı**. Doğru rakamlar:

- `TopLevelHelpTests.cs` → **18** (3 `[Fact]` + 3 `[Theory]` + 15 `[InlineData]`), yeni dosya.
- `SourceExtractionReportTests.cs` → **13** (10 `[Fact]` + 1 `[Theory]` + 3 `[InlineData]`), yeni dosya.
- `DownloadCommandTests.cs` → **+3 case**, **yeni dosya değil**: bu dosya indirme özelliğinin
  `be85d61 feat: add anime download support` commit'inde zaten vardı; PR #3 yalnızca 20 satır /
  3 case ekledi.

Bu yüzden ajanın "3 yeni test" iddiası doğru, "14 yeni test" iddiası **1 fazlaydı**; doğrusu 18'dir
(18 + 13 + 3 = 34, taban 275 + 34 = 309).

### 18.6 Canlı doğrulama sınırı

Branch build'i ile `127.0.0.1:7099`'da canlı doğrulama yapıldı ve süreç **durduruldu** (port kapalı,
süreç yok). Doğrulanan:

- `event: done` → `{"succeeded":2,"failed":0,"errors":[],"totalItems":2}` (Animexe, Naruto 1. bölüm)
- Embed'li TurkAnime ile → `{"succeeded":75,"failed":0,...,"totalItems":75}`

**Sınır:** Yeni SSE hata yolunun (`providerError` `scope: "extract"`, `done.failed`) **canlı
kanıtı yok**. ~25 bölüm denendi, hiçbirinde extractor doğal olarak çökmedi; hata dalı yalnız
birim testiyle (`SourceExtractionReportTests`) kapsanıyor. Bu dal "tam test edildi" diye
sunulmamalıdır. Ayrıntı: `API.md` → bölüm 19.3 (*Doğrulama sınırı*) ve `TEST_RESULTS.md` → bölüm 14.

### 18.7 CI anomalisi (keşfedildi, çözülmedi)

Fork'ta GitHub Actions **otomatik tetikleyicileri hiç çalışmamış**. Kanıt:

```text
repos/Nutaliaxd/Migurdex/actions/permissions  → enabled=true, allowed_actions=all
workflow "Build and Release"                   → state=active
repos/Nutaliaxd/Migurdex/actions/runs          → total_count=1
   ve o tek koşu: 36562344972, event=workflow_dispatch, head=v1.10.2, success
kuyrukta bekleyen koşu                          → 0
```

Yani ne `push` ne `pull_request` tetikleyicisi fork'ta bir kez bile koşu üretmemiş. `v1.10.2`
release'i **elle tetiklenerek** yapılmış. Bugün `main`'e push edilen kod değişiklikleri için de
hiçbir koşu oluşmadı; PR #2 ve PR #3 hiç check almadı.

Bu nedenle CI **elle tetiklendi**:

```text
gh workflow run build-release.yml --repo Nutaliaxd/Migurdex --ref main
→ run 36589928053  (https://github.com/Nutaliaxd/Migurdex/actions/runs/36589928053)
```

`release` işi `if: startsWith(github.ref, 'refs/tags/v')` ile korumalı olduğu için bu koşu release
üretmez; yalnızca build'i doğrular.

**Bu anomali düzeltilmedi, yalnızca kayda geçirildi.** Olası nedenler (hangisi doğruysa, henüz
doğrulanmadı): hesap bazlı Actions harcama limiti, fork'a miras kalan Actions politikası veya
hesap düzeyinde bir kısıtlama. Kalıcı süreç kuralı olarak `PR_DESCRIPTION.md` → *Süreç kuralları*'na
eklendi: her kod değişikliğinden sonra CI elle tetiklenmeli, sonucu `TEST_RESULTS.md`'ye
yazılmalıdır.
