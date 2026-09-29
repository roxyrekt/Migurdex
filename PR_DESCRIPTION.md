# feat: add anime download support

> **Bu metin iki hedef için kullanılabilir.**
>
> | Hedef | Durum |
> |---|---|
> | Fork `Nutaliaxd/Migurdex` | [PR #1](https://github.com/Nutaliaxd/Migurdex/pull/1) **merge edildi** (29.09.2026 10:05 UTC) → merge commit `44f4010` → etiket `v1.10.2` → [release](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2) yayımlandı ([CI run](https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972), sonuç **success**) |
> | Upstream `roxyrekt/Migurdex` | [PR #2](https://github.com/roxyrekt/Migurdex/pull/2) **açık** ve `MERGEABLE` (temiz dal `upstream/download-clean`, tek commit `7f1e250`, 37 dosya, +9.879 / −253). Önceki [PR #1](https://github.com/roxyrekt/Migurdex/pull/1) kapatıldı (duplicate) |
>
> Fork dalı: `feature/download` → `main` · Base (upstream): `v1.10.1` (`4ecd7d7`) ·
> İstatistik — fork PR #1: 22 commit, 40 dosya, **+11.559 / −249** ·
> İstatistik — upstream PR #2: 1 commit, 37 dosya, **+9.879 / −253** (kod + testler + `README.md` + `DOWNLOAD.md`) ·
> Son kod commit'i: `00b2ee7` · Tam commit listesi: `git log --oneline v1.10.1..main`

## Durum ve release kanıtı (fork)

| Alan | Değer |
|---|---|
| PR | [#1 — feat: add anime download support](https://github.com/Nutaliaxd/Migurdex/pull/1) (`feature/download` → `main`) |
| Durum | **merged** — 29.09.2026 10:05 UTC |
| Merge commit | `44f4010` (`Merge pull request #1 from Nutaliaxd/feature/download`); ebeveynler `4ecd7d7` + `c4c06c8` |
| İstatistik | 22 commit · 40 dosya · +11.559 / −249 |
| Etiket | `v1.10.2` → `44f4010` |
| Release | https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2 (29.09.2026 11:41 UTC) |
| CI run | https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972 — **success** (3 build matrisi) |

**Release asset'ları** (CI üretimi; `sha256sums-*.txt` ile doğrulanabilir):

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

Doğrulama: `git rev-list --count v1.10.1..main` → **23** (22 PR commit + merge `44f4010`) ·
`git rev-list -n1 v1.10.2` → `44f4010` ·
`git describe --tags` → `v1.10.2`. Release paketinde `migurdex --version` gerçek sürümü
(`migurdex v1.10.2`) basar; yerel `build.ps1` paketi `v0.0.0` gösterir.

## Özet

Bu PR, Migurdex'e uçtan uca bir **anime indirme** özelliği ekler. API'nin çözdüğü doğrudan
`MP4` ve `M3U8/HLS` kaynakları iki giriş noktasından diske kaydedilir:

- **CLI:** `migurdex download "one piece" -e 12`
- **TUI:** bölüm kaynak ekranındaki **İndir** eylemi

İndirme; video dosyasını hedef klasöre yazar, yanına sidecar altyazıları (`.srt`/`.ass`/`.vtt`)
koyar. MP4'te yerleşik HTTP istemcisiyle **kaldığı yerden devam (resume)** desteklenir; HLS
harici `yt-dlp` süreciyle indirilir.

Kapsam dışı davranış bilinçli olarak korunur:

- `Embed` ve `Unknown` kaynaklar **indirilmez** (yalnız API'nin çözdüğü doğrudan medya URL'leri).
- İndirme **MPV açmaz**, izleme geçmişine **yazmaz**, tracker (AniList/MAL) senkronu **tetiklemez**.

## Motivasyon

- **Arşivleme ve çevrimdışı izleme:** Kullanıcılar yalnızca terminalden izlemek değil, bölümleri
  kalıcı olarak saklamak istiyor. Mevcut `search`/`play` akışının doğal uzantısı: arka uç zaten
  kaynakları çözüyordu; eksik olan diske yazan katmandı.
- **Betiklenebilirlik:** `--json` (stdout yalnız tek JSON belgesi; ilerleme stderr'de) ve
  tanımlı çıkış kodları (`0`/`1`/`2`/`130`) sayesinde indirme, cron/PowerShell/betik akışlarına
  eklenebilir.
- **Tutarlı tercih sistemi:** Kalite/hoster/fansub tercihleri mevcut `SourceSelector` kuralları
  (`SourceSortPriority`, `PreferredQualityOrder`, `PreferredHosterOrder`) üzerinden geçer;
  indirme için ayrı bir tercih mekanizması icat edilmez.
- **14 sağlayıcılı ekosistem:** Sağlayıcı listesi dinamik okunduğundan (bkz. Sürüm uyumluluğu),
  upstream'in v1.10.0'da eklediği `Deokwave` gibi yeni sağlayıcılar indirme akışına kod
  değişikliği gerektirmeden katılır.

## Mimari çözüm

```
DownloadCommand (CLI)  ─┐
EpisodeSourcesView (TUI) ─┴─> DownloadService
                               ├─ DownloadPathBuilder  → hedef yolu üretir (sanitizasyon)
                               ├─ DownloadTargetLock   → aynı hedefe eşzamanlı indirmeyi engeller
                               ├─ Mp4Downloader        → MP4 (yerleşik HTTP, resume destekli)
                               ├─ YtDlpHlsDownloader  → HLS (harici yt-dlp süreci)
                               └─ SubtitleDownloader  → .srt/.ass/.vtt sidecar altyazılar
```

**Katmanlar ve sorumluluklar:**

| Bileşen | Sorumluluk |
|---|---|
| `DownloadCommand` | CLI argüman ayrıştırma, sağlayıcı/grup/bölüm akışı, JSON çıktı, çıkış kodları |
| `DownloadSourceResolver` | Sağlayıcı/grup doğrulama; `--format auto` için en iyi 3 adayı sırayla deneme |
| `DownloadService` | Orkestrasyon: video + altyazı, uyarı toplama, hata mesajı sanitizasyonu |
| `DownloadHttp` | HTTP istemcisi, elle yönetilen redirect zinciri (maks. 5), header allowlist'leri, kaynak fingerprint'i |
| `Mp4Downloader` + `Mp4ResumeMetadata` | MP4 indirme; `Range` tabanlı resume, `ETag`/`Last-Modified` eşleşmesi |
| `YtDlpHlsDownloader` + `ExternalProcessRunner` | HLS; yt-dlp sarmalayıcı, çıktı ayrıştırma |
| `SubtitleDownloader` | Sidecar altyazı indirme, `data:` URI, biçim/imza doğrulama |
| `DownloadPathBuilder` | `<kök>/<anime>/SxxEyy - <bölüm>.<uzantı>` üretimi, sanitizasyon, byte bütçeleri |
| `DownloadTargetLock` | Süreç içi `SemaphoreSlim` + `<hedef>.migurdex.lock` dosya kilidi (`FileShare.None`) |

**API ve platform entegrasyonu:**

- `AnimeEndpoints`: kaynak metadata birleştirme yardımcıları (`MergeSourceMetadata`,
  `MergeSourceHeaders`) eklendi — indirme isteği için gerekli header/kalite bilgisini tek yanısta toplar.
- `M3U8PlaylistExtractor`: bağlantıya özgü header'ların medya isteğine taşınmasını engelleyen
  `_nonTransferableHeaders` seti ve kaynak header birleştirmesiyle güncellendi.
- `ApiClientService.TryStartApiDaemonAsync`: CLI, API ayakta değilse `api\` klasöründeki paket
  API'yi kendisi başlatır (`DownloadCommand.EnsureApiOnlineAsync`).
- `Program.ConfigureServices` → `AddDownloadServices` + `HlsDownloadOptions`
  (`Executable = YtDlpPath`, `MaxAttempts = 3`, `RetryDelay = 1 sn`).
- `NonInteractiveCommand`'a `download` alt komutu bağlandı.

**Ana tasarım kararları:**

1. **MP4 için saf C# HTTP istemcisi** — resume durum makinesine (`206`/`200`/`416`), header
   allowlist'lerine ve ilerleme raporuna tam kontrol sağlar.
2. **HLS için yt-dlp** — segment birleştirme, şifreleme ve sunucu varyantı karmaşıklığını
   kanıtlanmış harici araca devreder; Migurdex bunu otomatik kurmaz.
3. **URL'i yt-dlp'ye batch dosyasıyla geçirme** — komut satırı enjeksiyonuna kapanır.
4. **Çift katmanlı kilit** — süreç içi semaphore aynı process'i, `.migurdex.lock` dosya kilidi
   ayrı süreçleri kollar; video sonrası altyazı fazında kilit alınamazsa altyazılar atlanır,
   video sonucu korunur.
5. **Altyazı sidecar, mux yok** — medya dosyası yt-dlp/MP4 çıktısı olduğu gibi kalır; altyazı
   hatası video indirmesini bozmaz, uyarıya dönüşür.

## Kullanıcı deneyimi

### CLI

```bash
migurdex download "one piece"                                  # ilk bölüm, en iyi kaynak
migurdex download "one piece" -e 12                            # 12. bölüm (kesirli: -e 5.5 → E5.5)
migurdex download "one piece" -s 2 -e 3                        # 2. sezonun 3. bölümü
migurdex download "naruto" -p turkanime -s 2 -g FansubAdı      # sağlayıcı + fansub
migurdex download "one piece" -o "D:\Anime"                     # çıktı kökünü geçersiz kılar
migurdex download "bleach" --format mp4 --no-subs              # biçim kilitleme + altyazı kapalı
migurdex download "bleach" -e 1 --format hls --force --no-resume
migurdex download "one piece" -e 12 --json                     # stdout yalnız JSON; ilerleme stderr'de
migurdex download "bleach" -e 1 --debug                        # kuru çalıştırma: indirmez, güvenli özet
```

| Bayrak | Davranış |
|---|---|
| `-e/--episode`, `-s/--season` | Bölüm `double` olarak çözümlenir; kesirli bölüm dosya adına `E5.5` olarak yansır. Bölüm verilmezse deterministik ilk bölüm iner |
| `-p/--provider` | Ad büyük/küçük duyarsız + benzersiz alt dize eşleşir; bulunamazsa sağlayıcı listesi, belirsizse eşleşenler hatada listelenir |
| `-g/--group` | Fansub adı API'den gelen grup listesine göre doğrulanır; hata geçerli grupları listeler |
| `-o/--output` | Tek indirme için `DownloadDirectory`'i geçersiz kılar |
| `--format auto\|mp4\|hls` | `auto`: `IsAutoEligible` filtresi + `SourceSelector` sıralamasıyla en iyi 3 aday sırayla denenir; `m3u8` = `hls` takma adı |
| `--subs` / `--no-subs` | Config varsayılanı `DownloadSubtitles: true`; ikisi birlikte verilemez |
| `--force` | Hedef varsa üzerine yazar (config `DownloadOverwrite`, varsayılan `false`) |
| `--no-resume` | MP4'te `.part`'tan devam etmeyi kapatır |
| `--debug` | Kuru çalıştırma (`exit 0`): sağlayıcı/anime/bölüm/grup/hoster/kalite/tür/aday sayısı özeti |
| `--json` | camelCase + enum'lar metin olarak; üç sonuç şekli: başarı, hata (`{"success": false, "error": ...}`), video sonrası iptal |

**Çıkış kodları:** `0` başarı · `1` çalışma/sağlayıcı hatası · `2` kullanım hatası ·
`130` video tamamlandı, kullanıcı altyazı aşamasında iptal etti (`success: true` + `cancelled: true`).

### TUI

1. Arama → anime → bölüm seçimi mevcut akışta; kaynak ekranında (`EpisodeSourcesView`) eylem
   menüsüne **İndir** eklenir (yalnız doğrudan indirilebilir kaynaklarda görünür; `Embed`
   kaynaklar listeye zaten girmez).
2. İndirme sırasında durum satırı aşama + bayt sayacı gösterir (`FormatDownloadProgress`);
   `Esc` iptal eder — `.part` dosyası korunur, aynı kaynakla tekrar denendiğinde kaldığı yerden devam eder.
3. Sonuç ekranı (`ShowDownloadResultAsync`) video/altyazı yollarını ve uyarıları listeler;
   tek seçenekli bilgi ekranıdır — `FuzzyPrompt.Show(..., searchable: false)` ile açıldığından
   `Ara:` filtre satırı çizilmez (`b3dd5d5`).

### Çıktı düzeni

```
<DownloadDirectory>\            # varsayılan: <profil>\Downloads\Migurdex (Linux: XDG_DOWNLOAD_DIR tabanlı)
└─ <Anime adı>\                 # sanitizasyonlu klasör (fallback: "Anime")
   ├─ S01E05 - <Bölüm adı>.mp4                # bitmiş video
   ├─ S01E05 - <Bölüm adı>.tr.srt             # sidecar altyazı
   ├─ S01E05 - <Bölüm adı>.<fingerprint>.part       # MP4 indirme sırasında
   ├─ S01E05 - <Bölüm adı>.<fingerprint>.part.meta  # resume metadata
   └─ S01E05 - <Bölüm adı>.migurdex.lock            # hedef kilidi
```

- Sezon ve tam sayı bölüm iki haneli sıfırlı (`S02E05`); kesirli bölüm `E5.5` olarak kalır.
- MP4'te uzantı sabit `.mp4`; HLS'te gerçek uzantı yt-dlp çıktısından korunur (`.mp4`, `.mkv`, `.webm`, `.ts`, ...).
- `.part`, `.part.meta`, `.migurdex.lock` ve HLS'in `.migurdex-job-<guid>` geçici dizini indirme
  bitince ortadan kalkar.

## Teknik detaylar

### Aday seçimi

`--format auto` (varsayılan): `config.json` → `Auto*Hosters/Qualities/Types` listeleri
`IsAutoEligible` ile filtre uygular; `SourceSelector.SortVideoSources`, `SourceSortPriority`
(varsayılan `Quality → Format → Hoster → Group`) ve `PreferredQualityOrder`/`PreferredHosterOrder`
listeleriyle sıralar; en fazla 3 benzersiz aday (`MaxCandidates`) sırayla denenir, ilki başarısız
olursa sıradakine geçilir. `mp4`/`hls` verildiğinde adaylar türe sınırlanır; uygun kaynak yoksa
anlaşılır hata döner.

### MP4 indirme ve resume

- Kaynak yalnız HTTP/HTTPS kabul edilir (`DownloadHttp.ValidateHttpUri`).
- **Fingerprint:** URL + sıralı header'ların (Range hariç) SHA-256 özetinin ilk 24 hex karakteri;
  parça dosyası `<hedef>.<fingerprint>.part`, metadata `.part.meta` (sürüm 1: `ETag`/
  `Last-Modified` SHA-256 özetleri + `TotalBytes`). Fingerprint veya metadata eşleşmeyen parça
  kullanılmaz — değişmiş dosyaya yanlış append etme riski kapatılır. Eski, fingerprint'siz
  `.part` dosyaları silinir.
- **Resume durum makinesi:** mevcut parça boyutu kadar `Range: bytes=<n>-` gönderilir.
  - `206 Partial Content` → parçaya append; `Content-Range` tutarlılığı, `Content-Length` eşleşmesi
    ve metadata eşleşmesi doğrulanır.
  - `200 OK` (Range yok sayıldı) → parça sıfırlanıp dosya baştan iner.
  - `416` + `Content-Range: bytes */<total>` ve parça == total → dosya zaten tamamlanmış sayılır.
  - Uyuşmazlıkta en fazla 2 deneme (`MaxResumeAttempts`), sonra hata.
- İndirme sırasında HTML/metin/JSON/XML/görsel content-type geldiğinde hata verilir — sunucu hata
  sayfası medya dosyasına yazılmaz.
- İlerleme raporu ~150 ms'de bir güncellenir (`ProgressIntervalMilliseconds`).
- İptal durumunda `.part` + `.meta` korunur.

### HLS indirme (yt-dlp)

- `YtDlpPath` (varsayılan `yt-dlp`) config'ten çözülür; eksikse anlaşılır hata verilir. Segment
  birleştirme için çoğu durumda `ffmpeg` gerekir; yt-dlp çıktısından tespit edilip açık hata döner.
- yt-dlp argümanları:

  ```
  --no-config --no-playlist --no-part --newline --progress
  --batch-file <job/.migurdex-input.txt>   # URL enjeksiyonuna kapalı geçiş
  --paths <job> --output media.%(ext)s
  --print after_move:filepath
  --add-header <ad>: <değer>                # yalnız allowlist header'lar
  ```

- **`--progress` gerekçesi (canlı smoke bulgusu, düzeltme `00b2ee7`):** yt-dlp'de herhangi bir
  `--print` kullanımı `--quiet` davranışını ima eder; `--print after_move:filepath` tek başına
  verildiğinde progress satırı üretilmiyor, TUI durum satırı `Bağlanıyor • 0 B` değerinde
  kalıyordu. Argüman listesine `--progress` eklenince `--newline` ile satır satır akan progress
  çıktısı `Downloading` aşamasını besliyor; `--print` (çıktı yolunun öğrenilmesi) yerinde kaldı.
  `HlsDownloaderTests` bu birlikteliği ve yüzde (`42.3%`) / bayt (`10.50MiB / 24.00MiB`) kalıplarının
  çözümlenmesini doğrular.
- Her indirme `.migurdex-job-<guid>` geçici dizininde çalışır, bitince silinir. Varsayılan 3 deneme
  (1–5 aralığına sabitlenir), denemeler arası 1 sn.
- Çıktı dosyası `--print after_move:filepath` satırından veya job dizininden bulunur; yalnız medya
  uzantıları kabul edilir; HTML/JSON/altyazı görünümlü dosyalar reddedilir.

### Altyazı indirme

- Sidecar ad: `<video>.<dil|etiket>.<srt|ass|vtt>`; aynı etiketli ek altyazılara `.<sıra>` eklenir.
- Header önceliği: altyazıya özel header varsa o; yoksa video header'larından yalnız allowlist
  fallback'i (`Accept`, `Accept-Language`, `Origin`, `Referer`, `User-Agent`). `Referer`/`Origin`
  yalnız aynı origin'e taşınır. `Authorization`, `Cookie`, API-key benzeri header'lar hiçbir
  koşulda altyazı isteğine veya yt-dlp'ye taşınmaz.
- Yalnız HTTP/HTTPS, maks. 5 yönlendirme; `data:` URI altyazılar desteklenir.
- Boyut limiti 10 MiB (`MaxSubtitleBytes`); bildirilen boyutla eşleşmeyen gövde reddedilir.
- İmza doğrulaması zorunlu: `-->` (SRT), `[Script Info]` (ASS), `WEBVTT` (VTT); HTML/JSON
  görünümündeyse dosya reddedilir. Altyazı hatası video sonucunu bozmaz, uyarı listesine eklenir.

### Yapılandırma (`config.json`)

| Alan | Varsayılan | Açıklama |
|---|---|---|
| `DownloadDirectory` | Platform Downloads dizini altında `Migurdex` (Linux'ta `XDG_DOWNLOAD_DIR` tanımlıysa o kök) | Çıktı kökü; `-o` tek indirme için geçersiz kılar; boş değer varsayılana düşer |
| `YtDlpPath` | `yt-dlp` | HLS için çalıştırılabilir adı/yolu |
| `DownloadSubtitles` | `true` | Altyazılar varsayılan olarak iner (`--no-subs` kapatır) |
| `DownloadResume` | `true` | Kısmi MP4'ten devam (`--no-resume` kapatır) |
| `DownloadOverwrite` | `false` | Var olan hedefin üzerine yazma (`--force` açar) |

## Eklenen dosyalar

`git diff --stat v1.10.1..main` — **DEVELOPMENT_LOG.md** ve **PR_DESCRIPTION.md** hariç:
**38 dosya, +10.181 / −249**. Doküman dosyaları dahil tam diff (merge commit `44f4010` üzerinden,
PR #1 istatistiğiyle birebir aynı): **40 dosya, +11.559 / −249**.

**Yeni üretim kodu — `Migurdex.Cli` (14 dosya):**

| Dosya | Satır | Görev |
|---|---:|---|
| `Services/DownloadCommand.cs` | 740 | CLI komutu, argüman ayrıştırma, JSON çıktı |
| `Services/DownloadSourceResolver.cs` | 154 | Sağlayıcı/grup/bölüm çözümleme, aday denemesi |
| `Services/Downloads/DownloadHttp.cs` | 341 | HTTP istemcisi, redirect zinciri, allowlist'ler, fingerprint |
| `Services/Downloads/DownloadInterfaces.cs` | 159 | `IDownloadService`, `IMp4Downloader`, `IHlsDownloader`, `ISubtitleDownloader`, `IExternalProcessRunner` |
| `Services/Downloads/DownloadModels.cs` | 229 | `DownloadRequest/Result/Progress`, `DownloadPath`, `HlsDownloadOptions` |
| `Services/Downloads/DownloadPathBuilder.cs` | 388 | Yol üretimi, sanitizasyon, UTF-8 byte bütçesi |
| `Services/Downloads/DownloadService.cs` | 351 | Orkestrasyon: video + altyazı, uyarı toplama |
| `Services/Downloads/DownloadServiceCollectionExtensions.cs` | 25 | DI kayıtları |
| `Services/Downloads/DownloadTargetLock.cs` | 133 | Süreç içi semaphore + dosya kilidi |
| `Services/Downloads/ExternalProcessRunner.cs` | 363 | Harici süreç çalıştırma (yt-dlp), çıktı ayrıştırma |
| `Services/Downloads/Mp4Downloader.cs` | 613 | MP4 indirme, HTTP Range resume |
| `Services/Downloads/Mp4ResumeMetadata.cs` | 130 | `.meta` dosyası, ETag/Last-Modified eşleşmesi |
| `Services/Downloads/SubtitleDownloader.cs` | 529 | Altyazı indirme, `data:` URI, biçim doğrulama |
| `Services/Downloads/YtDlpHlsDownloader.cs` | 605 | HLS indirme, yt-dlp sarmalayıcı |

**Yeni testler — `Migurdex.Tests` (10 sınıf, 2.962 satır):**

| Dosya | Satır |
|---|---:|
| `Mp4DownloaderTests.cs` | 788 |
| `DownloadServiceTests.cs` | 439 |
| `HlsDownloaderTests.cs` | 424 |
| `SubtitleDownloaderTests.cs` | 407 |
| `DownloadCommandTests.cs` | 246 |
| `FuzzyPromptSearchableTests.cs` | 247 |
| `DownloadPathBuilderTests.cs` | 111 |
| `TuiMarkupSafetyTests.cs` | 151 |
| `ExternalProcessRunnerTests.cs` | 83 |
| `ApiClientServiceTests.cs` | 66 |

**Yeni dokümantasyon:** `DOWNLOAD.md` (özellik dokümanı, test kayıtları, çalışma günlüğü),
`TEST_RESULTS.md` (konsolide doğrulama raporu), `DEVELOPMENT_LOG.md` (geliştirme kaydı),
`PR_DESCRIPTION.md` (bu metin). Satır sayıları commit'le birlikte değişir; güncel değer için
`git show --stat <hash>` ya da dosyanın kendisi esas alınmalıdır.

**Değiştirilen mevcut dosyalar:** `AnimeEndpoints.cs` (metadata birleştirme),
`M3U8PlaylistExtractor.cs` (non-transferable header'lar), `CliConfig.cs` (5 yeni alan),
`Program.cs` (DI + `HlsDownloadOptions`), `ApiClientService.cs` (daemon otomatik başlatma),
`NonInteractiveCommand.cs` (`download` alt komutu), `EpisodeSourcesView.cs` (İndir eylemi +
sonuç ekranı), `FuzzyPrompt.cs` (`searchable: false` + markup kaçışlama),
`TuiApplicationCancellation.cs`, `Migurdex.Cli.csproj`, `Migurdex.Tests.csproj`, `README.md`.

## Güvenlik

- **Kaynak kısıtı:** Yalnız HTTP/HTTPS; `Embed`/`Unknown` kaynaklar hiç indirilmez.
  Kullanıcıya hatırlatma README'de korunur: yalnız erişimine izin verilen içerikler indirilmeli.
- **Redirect zinciri:** Elle yönetilir, en fazla `MaxRedirects = 5` adım. Cross-origin
  yönlendirmede `Authorization`/`Cookie` gibi hassas başlıklar düşürülür; üç ayrı header
  allowlist'i (cross-origin, yt-dlp, altyazı fallback) uygulanır.
- **yt-dlp sertleştirme:** `--no-config`; URL komut satırı yerine batch dosyasıyla geçer;
  yt-dlp'ye yalnız `Accept`, `Accept-Language`, `Origin`, `Referer`, `User-Agent` taşınır.
- **Yol güvenliği:** Sanitizasyon `<>:"|?*`, kontrol karakterleri ve Windows rezerve adlarını
  (`CON`, `NUL`, `COM1`, ...) temizler; bileşen başına 200, geçici son ek için 96 UTF-8 byte
  bütçesi; tam yol Windows 240 / Linux 1024 byte. Anime klasörü çıktı kökünün dışına çıkamaz.
- **Hata sanitizasyonu:** Hata mesajları kaynak URL'si veya header değerleri sızdırıyorsa genel
  metne düşürülür (`DownloadService.SafeFailureMessage`, `DownloadCommand.SanitizeFailure`).
- **`--debug`:** URL, header veya token hiçbir koşulda yazdırmaz; yalnız güvenli kaynak özeti
  (`dryRun: true` JSON etiketiyle birlikte kullanılabilir). Canlı denetimde 9 sızıntı deseni
  (`https://`, `Authorization`, `Bearer`, `Referer`, `Cookie`, `User-Agent`, `token`, `api_key`,
  `secret`) için **0 eşleşme** bulundu.
- **Eşzamanlılık:** Aynı hedefe çift katmanlı kilit (semaphore + `.migurdex.lock`,
  `FileShare.None`); ayrı süreçteki ikinci indirme kilidi açamayınca açık hata alır.
- **TUI markup güvenliği:** Dinamik her metin (anime adı, fansub, hoster, kalite, sorgu)
  `Markup.Escape` ile kaçırılır; `[1080p]`, `[SubsPlease]` gibi veriler sahte renk etiketi
  olamaz. `TuiMarkupSafetyTests` tüm prompt/header/choice satırının dengeli olduğunu doğrular
  (düzeltme: `6c366c9`; v1.10.0 tabanındaki karşılığı `bd59ca4`).

## Test ve doğrulama

**Birim testleri:** indirme/TUI kapsamında **81 test metodu / 110 çalışan case, 10 sınıf**
(`[Fact]`+`[Theory]` metod; Theory `[InlineData]` satırları açılarak çalışan case):

| Test sınıfı | Metod | Çalışan case | Sonuç |
|---|---:|---:|---:|
| `Mp4DownloaderTests` | 20 | 20 | 20/20 |
| `SubtitleDownloaderTests` | 9 | 10 | 10/10 |
| `HlsDownloaderTests` | 8 | 8 | 8/8 |
| `DownloadServiceTests` | 7 | 7 | 7/7 |
| `DownloadCommandTests` | 12 | 15 | 15/15 |
| `DownloadPathBuilderTests` | 4 | 4 | 4/4 |
| `ExternalProcessRunnerTests` | 3 | 3 | 3/3 |
| `ApiClientServiceTests` | 2 | 2 | 2/2 |
| `TuiMarkupSafetyTests` | 8 | 23 | 23/23 |
| `FuzzyPromptSearchableTests` | 8 | 18 | 18/18 |
| **Toplam** | **81** | **110** | **110/110** |

> 110, indirme/TUI kapsamındaki **10** sınıfın toplamıdır. `TEST_RESULTS.md` bölüm 1'deki
> **103** değeri aynı sınıfların yalnızca **8** tanesiyle koşulan filtrenin sonucudur
> (103 + `DownloadPathBuilderTests` 4 + `ExternalProcessRunnerTests` 3 = 110); kapsam farkıdır,
> metrik çelişkisi değil.

**Offline doğrulama** (29 Eylül 2026, `v1.10.1` rebase sonrası, temiz ağaç, Release; bu ağaç
`44f4010` ile merge edilip `v1.10.2` olarak yayımlandı — merge sonrası kod değişmedi):

- `dotnet restore Migurdex.slnx`: başarılı.
- `dotnet build Migurdex.slnx -c Release --no-restore`: 19 proje, **0 uyarı / 0 hata**.
  (19 proje = `v1.10.0` ve sonrası ağaç, `Deokwave` plugin projesi dahil; `v1.9.2` tabanı 18
  projeydi — 26–27 Eylül tarihli kayıtlardaki "18 proje" o ağaca aittir.)
- `dotnet test ... --filter "FullyQualifiedName!~ExtractorSmokeTests"`: **275/275 geçti**
  (ağ erişimi gerektiren `ExtractorSmokeTests` bilinçli olarak hariç).

**Canlı API smoke** (26 Eylül 2026, v1.10.0 rebase'i **öncesi** koşum): 21 endpoint test edildi,
**21/21 HTTP 200** ve geçerli JSON. `/health`: 13 sağlayıcı / 38 extractor / Rust hazır;
`q=one piece` araması 13/13 sağlayıcıda başarılı, 153 sonuç. API loglarında 0 exception /
0 stack trace / 0 serialization hatası. İki upstream notu (uygulama hatası değil):
`TrAnimeIzle` captcha challenge → boş liste; `VidmolyExtractor` reklam ağına 302 → kaynağı
atlayıp diğer hoster'lardan devam. (13 → 14 sağlayıcı geçişi v1.10.0 ile `Deokwave`'ta oldu;
bkz. Sürüm uyumluluğu.)

**TUI canlı smoke** (ConPTY, gerçek `naruto` sorgusu): arama → seçim → kaynak → İndir akışı
uçtan uca koştu; **371.406.042 bayt** video indirildi, `exit=0`. Bu smoke gerçek bir hata buldu —
HLS progress güncellenmiyordu (bkz. Teknik detaylar → `--progress` gerekçesi) — `00b2ee7` ile
düzeltildi ve testlerle sabitlendi. Düzeltme sonrası: genel 275/275, `HlsDownloaderTests` 8/8,
indirme/TUI sınıf filtreleri 110/110.

**CLI canlı smoke:**

- `migurdex download "one piece" -e 1 --debug --json` → `EXIT=0`, 42,6 sn, AnimeciX / Tau Video /
  1080p / MP4 / AoiSubs, `candidateCount: 3`, `dryRun: true`.
- Sağlayıcı bazında: `TurkAnime` 37 kaynak (GoogleDrive 1080p MP4), `AnimeciX` 9 (Tau Video),
  `OpenAnime` 6 — hata yok.
- **Gerçek MP4 indirmesi:** `migurdex download "one piece" -e 1 -p AnimeciX --json` → `EXIT=0`,
  10,1 sn, **437.668.399 bayt**, `S01E01 - Ben Luffy! Korsanlar Kralı Olacak Adam!.mp4`,
  geçerli MP4 (ffprobe 1500,14 sn, ~2,33 Mbps), boş uyarı listesi.

**Deokwave sağlayıcı smoke:** plugin pakette yüklü, `-p Deokwave` çözümlemesi çalışıyor;
`deokwave.com` tüm uç noktalarında Cloudflare JS challenge (HTTP 403) döndürdüğünden aramalar
boş liste dönüyor ve akış arama adımında **temiz `exit 1` JSON hatasıyla** duruyor. Diskte
kısmi dosya yok, kullanıcı veritabanı değişmedi. Kontrol grubu `TurkAnime` aynı akışta sorunsuz
— bulgu yalnızca Deokwave upstream erişimine özgü; **uygulama hatası değil**.

**Ortam notu:** Sistem dotnet'inde ASP.NET Core runtime yoktu; geçici SDK kurulunca aşıldı.
Kod hatası değildir, kalıcı etki yoktur.

## Sürüm uyumluluğu

- Dal önce `v1.10.0` (`4035f9a`) üzerine **çakışmasız** rebase edildi (yedek dal:
  `backup/download-pre-v110`), ardından `v1.10.1` (`4ecd7d7`) üzerine rebase edildi (yedek
  dal: `backup/download-pre-v1101`). v1.10.1 rebase'sinde 21 feature commit'i taşındı ve tek
  çakışma `Program.cs`'ti: dal tarafının TUI iptal mimarisi (`tuiToken`, `activeNavigator`,
  `catch (OperationCanceledException)`, `finally`) korunurken upstream'in
  `Console.IsInputRedirected` guard'lı + try/catch `ReadKey` versiyonu alındı; encoding guard
  kaldırma otomatik merge ile geldi. Rebase sonrası restore başarılı, Release 0 uyarı / 0 hata,
  offline takım 275/275.
- **Merge ve release:** Fork `Nutaliaxd/Migurdex` `main` dalına [PR #1](https://github.com/Nutaliaxd/Migurdex/pull/1)
  ile birleştirildi (29.09.2026 10:05 UTC, merge commit `44f4010`, 22 commit / 40 dosya /
  +11.559 / −249) ve `v1.10.2` etiketiyle
  [release](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2) yayımlandı
  ([CI run](https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972) — `success`).
  Upstream `roxyrekt/Migurdex` `main` dalı hâlâ `v1.10.1` (`4ecd7d7`); upstream hedefi için bu
  metin aynı base ile kullanılabilir ve açık upstream PR
  [PR #2](https://github.com/roxyrekt/Migurdex/pull/2) üzerinden yürüyor (`OPEN`, `MERGEABLE`, dal
  `upstream/download-clean`, tek commit `7f1e250`). Ayrıntı: `Durum ve release kanıtı` bölümü.
- Upstream v1.10.0 iki commit içerir: `8094425` (altyazı oynatmada isim + link yerine indirme)
  ve `4035f9a` (`feat(providers): add Deokwave`) — sağlayıcı sayısı 13 → 14. Upstream v1.10.1
  üç commit içerir: `e4a32b4` (TurkAnime DB bağlantısı), `914dfdd` (Anizm isimsiz fansub
  grupları) ve `4ecd7d7` (AppImage zsync/AppRun + CLI encoding/`ReadKey` guard) — sağlayıcı
  sayısı 14, extractor 38, breaking change yok.
- **Yeni sağlayıcılar için kod değişikliği gerekmez:** `DownloadSourceResolver` sağlayıcı
  listesini API'den dinamik okur; Deokwave otomatik katıldı.
- **Config uyumu:** Eski `config.json` dosyaları yeni alanlar (`DownloadDirectory`, `YtDlpPath`,
  `DownloadSubtitles`, `DownloadResume`, `DownloadOverwrite`) eklenmeden de güvenle yüklenir;
  eksik alanlar varsayılana düşer.
- **API surface:** Mevcut `/api/v1` uç noktaları değişmedi; `AnimeEndpoints`'e yalnızca kaynak
  metadata birleştirme yardımcıları eklendi. Breaking change yok.
- Hedef çerçeve `net10.0`; doğrulama .NET SDK **10.0.401**, xUnit v3 (`xunit.v3` 3.2.2,
  `xunit.runner.visualstudio` 4.0.0, `Microsoft.NET.Test.Sdk` 18.9.0) ile yapıldı.
- Upstream altyazı indirme davranışı (`MpvPlayerService` geçici dosyalar) ile bizim
  `SubtitleDownloader` arasında fonksiyonel benzerlik var; ortak yardımcıya çıkarılabilir (refactor fikri).

## Bilinen sınırlar

- **HLS bağımlılıkları:** `yt-dlp` zorunlu (config `YtDlpPath`), segment birleştirme için çoğu
  durumda `ffmpeg`; Migurdex bunları otomatik kurmaz. Progress ayrıştırması (`%` ve `MiB/x MiB`
  kalıpları) yt-dlp sürümüne bağlıdır — yt-dlp güncellemelerinden sonra `HlsDownloaderTests`
  koşulmalıdır.
- **HLS'te resume yok:** ağ kesilirse indirme geçici job diziniyle birlikte baştan alınır.
- **MP4 resume sunucu davranışına bağlı:** `200 OK` (Range yok sayıldı) durumunda parça sıfırlanıp
  dosya baştan iner. İmzalı/süreli kaynak URL'lerinde fingerprint değişir; eski `.part` kullanılmaz,
  indirme sıfırdan başlar (yanlış dosyaya append etmemek için bilinçli tercih). `416` yalnızca
  `Content-Range: bytes */<total>` + parça == total durumunda "tamamlandı" sayılır.
- **`Content-Length` yoksa MP4 indirilemez:** `200 OK` + chunked akışta indirme açık hata ile
  başarısız olur. Toplam boyut bilinmiyorsa ilerleme yalnızca bayt gösterir, yüzde hesaplanamaz.
- **Çoklu süreç kilidi:** Süreç çökerse `.migurdex.lock` diskte kalabilir; OS tanıtıcıyı yine
  serbest bırakır, sonraki indirme dosyayı sorunsuz yeniden açar. SMB/NFS üzerinde dosya kilidi
  davranışı platformdan platforma değişebilir; koruma yerel disk için tasarlandı.
- **Altyazı biçimleri:** Yalnız `.srt`/`.ass`/`.vtt` + zorunlu imza doğrulaması; `.sub`, PGS vb.
  desteklenmez. Altyazılar videoya gömülmez (mux yok) — oynatıcının sidecar dosyası bulması gerekir.
  Boyut limiti 10 MiB.
- **Deokwave upstream erişimi:** Cloudflare JS challenge (403) tüm uç noktalarda; sağlayıcı boş
  liste döndürüyor (bkz. Test ve doğrulama). Upstream erişim düzelirse kod değişikliği gerekmeden
  çalışır. **Yan bulgu:** sağlayıcı 403'ü sessizce boş listeye çeviriyor; sağlayıcı hatalarını
  görünür kılacak bir loglayıcı ileride eklenebilir.
- **Native Rust bağımlılığı:** Kaynak çözümü paketle gelen API'den geçer; API `migurdex_native.dll`
  olmadan açılmaz (exit 1). Dosyanın `migurdex.exe` yanındaki `api\` klasöründe bulunması gerekir.
  Risk tamamen kaynak çözümü tarafında; MP4/altyazı indirme kodu bu kütüphaneleri kullanmaz.
- **Sürüm damgası:** Yerel build'lerde `migurdex v0.0.0` beklenen değerdir (`Directory.Build.props`
  → `VersionPrefix` 0.0.0); etiketli sürüm numarası yayın CI'sinde `-p:Version` ile basılır, bu
  yüzden `v1.10.2` release paketinde `migurdex --version` doğru sürümü gösterir.

## Review notları

**Önerilen inceleme sırası (risk odaklı):**

1. `DownloadHttp.cs` — redirect zinciri ve üç header allowlist'i; güvenlik açısından en kritik dosya.
2. `Mp4Downloader.cs` + `Mp4ResumeMetadata.cs` — resume durum makinesi (`206`/`200`/`416`,
   `MaxResumeAttempts`), content-type reddi.
3. `YtDlpHlsDownloader.cs` + `ExternalProcessRunner.cs` — yt-dlp argüman listesi, batch-file
   URL geçişi, progress ayrıştırma.
4. `DownloadPathBuilder.cs` — sanitizasyon + byte bütçeleri (Windows rezerve adları, 240/1024 sınır).
5. `DownloadService.cs` + `DownloadTargetLock.cs` — orkestrasyon, kilit alınamazsa altyazı atlama,
   hata mesajı sanitizasyonu.
6. `DownloadCommand.cs` + `DownloadSourceResolver.cs` — CLI yüzeyi, çıkış kodları, JSON şeması.
7. TUI: `EpisodeSourcesView.cs`, `FuzzyPrompt.cs`, `TuiApplicationCancellation.cs`.

**Dikkat çekmek istediğim noktalar:**

- **`00b2ee7` (`--progress` fix):** yt-dlp'de `--print`'in `--quiet` ima etmesi tuzağı — `--print
  after_move:filepath` + `--progress` birlikteliği `HlsDownloaderTests`'te sabitlendi; ayrıştırma
  kalıplarına (`%`, `MiB/x MiB`) yt-dlp sürüm değişiminde dikkat.
- **TUI diff'i görece büyük** (`FuzzyPrompt` ~329, `EpisodeSourcesView` ~359 değişiklik satırı)
  ama iki bağımsız fix içerir: markup dengeleme (`6c366c9`, 23 yeni test) ve sonuç ekranında
  `Ara:` satırı gizleme (`b3dd5d5`, `searchable: false`, 18 yeni test).
- **Kod vs. docs oranı:** PR'daki 22 commit'in 4'ü kod (`be85d61` ana özellik, `6c366c9`, `b3dd5d5`,
  `00b2ee7`), geri kalanı dokümantasyon (`DOWNLOAD.md`/`TEST_RESULTS.md`/`DEVELOPMENT_LOG.md`/
  `PR_DESCRIPTION.md` çalışma kayıtları). Kod incelemesi bu 4 commit'e odaklanabilir; kesin
  dağılım için `git log --oneline v1.10.1..main` (commit sayısı:
  `git rev-list --count v1.10.1..main`).
- **Bilinçli tercihler, tartışmaya açık:** HLS'te resume yokluğu; altyazıda mux yerine sidecar;
  imzalı URL'de fingerprint değişince sıfırdan başlama; `.part`'ın iptalde korunması. Alternatif
  yaklaşım öneriniz varsa lütfen yorumda belirtin.
- **Refactor fikri:** `SubtitleDownloader` ile upstream `MpvPlayerService` altyazı indirme
  mantığı ortak yardımcıya çıkarılabilir; bu PR'da bilinçli olarak ertelendi (upstream çakışması
  riski).
- **İyileştirme fikri (sonraki PR):** Sağlayıcı hatalarını görünür kılan loglayıcı — Deokwave
  403'ün sessiz boş listeye düşmesi bu koşumda kullanıcıyı yanıltabiliyor.
- **Dokümantasyon güncelleme kuralı:** `DOWNLOAD.md` → `Güncelleme talimatı` bölümü, indirme
  koduna dokunan her değişiklikte hangi doküman bölümünün revize edileceğini eşler; bu PR'daki
  docs commit'leri bu kuralı izler.
- **Commit geçmişinde tek doğruluk kaynağı `git log`'dur.** Bu metindeki commit listeleri
  anlatıcı kayıttır; güncel liste `git log --oneline v1.10.1..main` ile alınır.

**Yerel doğrulama komutları:**

```bash
dotnet build Migurdex.slnx -c Release
dotnet test Migurdex.Tests/Migurdex.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName!~ExtractorSmokeTests"   # 275/275
```

**Diff / sürüm doğrulama komutları:**

```bash
git diff --shortstat v1.10.1..main   # 40 dosya, +11.559 / −249
git rev-list --count v1.10.1..main   # 23 commit (22 PR + merge)
git rev-list -n1 v1.10.2             # 44f4010 (release hedefi)
```

Ayrıntılı kayıtlar: [`DOWNLOAD.md`](DOWNLOAD.md), [`TEST_RESULTS.md`](TEST_RESULTS.md),
[`DEVELOPMENT_LOG.md`](DEVELOPMENT_LOG.md).
