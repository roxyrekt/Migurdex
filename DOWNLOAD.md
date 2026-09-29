# İndirme Özelliği (Download)

API'nin çözdüğü doğrudan `MP4` ve `M3U8/HLS` kaynaklarını CLI'dan (`migurdex download`) veya
TUI'den (bölüm kaynak ekranında `İndir`) diske kaydeder. `Embed` ve `Unknown` kaynaklar indirilmez.
İndirme MPV açmaz, izleme geçmişine yazmaz ve tracker senkronu tetiklemez.

| Alan | Değer |
|---|---|
| Son güncelleme | 29 Eylül 2026 |
| Aktif dal | `main` — PR #1 merge edildi, merge commit `44f4010` (taban: `v1.10.1` @ `4ecd7d7`) |
| Özellik dalı | `feature/download` → `main` üzerine [PR #1](https://github.com/Nutaliaxd/Migurdex/pull/1) ile birleştirildi (29.09.2026, 10:05 UTC). Artık ayrı bir geliştirme dalı değil; kayıt `main` üzerinden yürütülür, eski dal adı yalnızca PR başlığı/hashesi bağlamında geçer |
| Son kod commit’i | `00b2ee7` (`fix(downloader): report live yt-dlp HLS progress`) — merge sonrasında da değişmedi |
| Sürüm | `v1.10.2` → `44f4010` (fork: `Nutaliaxd/Migurdex`) · [release](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2) · [CI run](https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972) (`Build and Release`, sonuç: **success**) — ayrıntı `Yapılan işlemler` → madde 15 |
| Upstream | `roxyrekt/Migurdex` `main` hâlâ `v1.10.1` (`4ecd7d7`); aynı PR metni upstream hedefi için kullanılabilir ([PR_DESCRIPTION.md](https://github.com/Nutaliaxd/Migurdex/blob/main/PR_DESCRIPTION.md)) |
| Paket | v1.10.0 tabanlı win-x64 paketi üretildi, hash düzeyinde doğrulandı ve kök kuruluma yüklendi (27.09.2026; 14 sağlayıcı / 38 extractor) — bkz. `Yapılan işlemler` → maddeler 12–13. Bu paket **yerel doğrulanmış kök kurulum** olarak durur; `v1.10.2` release'i CI tarafından ayrıca üretildi ve kök kurulum bundan yükseltilmedi |
| Temel commit | `be85d61` — feat: add anime download support (ilk yazılım `c3d307a`; v1.10.0 rebase karşılığı `321fe38`) |
| Testler | 81 test metodu / 110 çalışan case, 10 sınıf (bkz. `Test kapsamı`); offline doğrulama: 275/275 geçti (29.09.2026, v1.10.1 rebase sonrası) |

## Genel akış

```
DownloadCommand (CLI)  ─┐
EpisodeSourcesView (TUI) ─┴─> DownloadService
                               ├─ DownloadPathBuilder  → hedef yolu üretir
                               ├─ DownloadTargetLock   → aynı hedefe eşzamanlı indirmeyi engeller
                               ├─ Mp4Downloader        → MP4 (yerleşik HTTP, resume destekli)
                               ├─ YtDlpHlsDownloader  → HLS (harici yt-dlp süreci)
                               └─ SubtitleDownloader  → .srt/.ass/.vtt sidecar altyazılar
```

CLI akışı: Search -> Details -> Episode -> Group/Source -> İndir. Bayraklar: `-e/--episode`,
`-s/--season`, `-p/--provider`, `-g/--group`, `-o/--output`, `--format auto|mp4|hls`,
`--subs/--no-subs`, `--force`, `--no-resume`, `--debug`, `--json`, `--help`.
Çıkış kodları: `0` başarı, `1` çalışma/sağlayıcı hatası, `2` kullanım hatası, `130` video
tamamlandı ancak kullanıcı iptal etti (altyazı aşaması kesildi).
`--debug` URL, header veya token yazdırmaz; indirmeyi başlatmadan yalnızca güvenli kaynak özeti gösterir.
`--json` modunda stdout yalnız JSON içerir, ilerleme stderr'e gider.

## Hızlı başlangıç

### Terminalden ilk indirme

1. `migurdex` PATH'te kurulu değilse `.\dist\migurdex.exe` ile çalıştırın. İlk `download`
   çağrısında CLI gerekirse `api\` klasöründeki paket API'yi kendisi başlatır
   (`DownloadCommand.EnsureApiOnlineAsync` → `ApiClientService.TryStartApiDaemonAsync`).
2. MP4 kaynağı için ek kurulum gerekmez. HLS de deneyecekseniz `yt-dlp`'yi (ve segment
   birleştirme için çoğu durumda `ffmpeg`'i) PATH'e kurun; ayrıntı için
   [HLS indirme (yt-dlp)](#hls-indirme-yt-dlp) bölümüne bakın.
3. Temel komut (bölüm verilmezse deterministik olarak ilk bölüm iner):

   ```
   migurdex download "one piece"
   ```

4. İlerleme stderr'e yazılır (`İndiriliyor: 12.3 MiB / 45.6 MiB`), tamamlanınca stdout'a
   `İndirildi: <tam yol>` ve varsa `Altyazı: <tam yol>` satırları basılır.
5. Dosya varsayılan olarak `<profil>\Downloads\Migurdex\<anime>\S01E01 - <bölüm adı>.mp4`
   yoluna iner; tam düzen için `Kullanım örnekleri` bölümüne bakın.

### TUI ile ilk indirme

1. `migurdex` (argümansız) ile ana menüyü açın.
2. Arama ekranından animeyi bulun ve sonuç listesinden seçin.
3. Anime detay ekranında indirilecek bölümü seçin; kaynak ekranı açılır.
4. Kaynak listesinden bir MP4/HLS kaynağı seçin (`AutoSelectBestSource` açıksa kaynaklar
   otomatik taranır ve en iyi kaynak seçilir; `Esc` manuel listeye düşürür).
5. Eylem menüsünden **İndir**'i seçin (yalnız doğrudan indirilebilir kaynaklarda görünür).
6. İndirme sırasında durum satırındaki ilerlemeyi izleyin; `Esc` indirmeyi iptal eder.
7. Sonuç ekranında video ve altyazı dosyalarının tam yolları listelenir; bu ekran filtresizdir
   (`Ara:` satırı yok), `Enter` devam eder, `Esc` geri döner.

## Kullanım örnekleri

### Temel indirme (CLI)

```
migurdex download "one piece"                 # ilk bölüm, en iyi kaynak
migurdex download "one piece" -e 12           # 12. bölüm
migurdex download "one piece" -s 2 -e 3       # 2. sezonun 3. bölümü
migurdex download "one piece" -e 5.5          # kesirli bölüm numarası desteklenir
```

- Sorgu konumsal argümandır; araya bayrak girse bile parçalar tek sorguda birleştirilir
  (`DownloadCommand.TryParse`).
- Bölüm numarası `double` olarak çözümlenir; kesirli bölüm dosya adına `E5.5` olarak yansır.
- Yardım için: `migurdex download --help`.

### Sağlayıcı ve fansub

```
migurdex download "naruto" -p TurkAnime
migurdex download "naruto" -p turkanime -s 2 -g FansubAdı
```

- `-p/--provider`: ad büyük/küçük duyarsız eşleşir; benzersiz alt dize de kabul edilir.
  Bulunamazsa `'<ad>' sağlayıcısı bulunamadı (<sağlayıcı listesi>).`, birden çok eşleşirse
  `'<ad>' belirsiz (<eşleşenler>).` hatası verilir
  (`DownloadSourceResolver.TryResolveProvider`).
- `-g/--group`: fansub adı bölümün grup listesine göre doğrulanır; eşleşme yoksa hata mesajı
  geçerli grupları da listeler (`DownloadSourceResolver.TryResolveGroup`).

### Çıktı dizini

```
migurdex download "one piece" -o "D:\Anime"
migurdex download "one piece" --output-directory "D:\Anime"
```

`-o`, o indirme için `config.json` → `DownloadDirectory` değerini geçersiz kılar; boş geçilirse
config değerine düşer.

### Biçim seçimi

```
migurdex download "bleach" --format mp4
migurdex download "bleach" --format hls
migurdex download "bleach" --format=m3u8    # "m3u8", "hls"nin takma adıdır
```

- `auto` (varsayılan): `IsAutoEligible` filtresi + `SourceSelector` sıralamasıyla en iyi üç
  aday sırayla denenir (`DownloadSourceResolver.SelectCandidates`, `MaxCandidates = 3`).
- `mp4`/`hls`: adaylar ilgili türe sınırlanır; uygun kaynak yoksa
  `'<etiket>' biçiminde uygun kaynak bulunamadı.` hatası döner.

### Altyazılar

```
migurdex download "bleach" -e 1 --subs
migurdex download "bleach" -e 1 --no-subs
```

Config varsayılanı `DownloadSubtitles: true`'dur; `--no-subs` kapatır, `--subs` config kapalıysa
açar. İkisi birlikte verilemez (`--subs ve --no-subs birlikte kullanılamaz.`).

### Üzerine yazma ve devam etme

```
migurdex download "bleach" -e 1 --force
migurdex download "bleach" -e 1 --no-resume
```

- `--force`: hedef varsa üzerine yazar (config `DownloadOverwrite`, varsayılan `false`).
- `--no-resume`: MP4'te `.part` dosyasından devam etmeyi kapatır; indirme sıfırdan başlar.

### Debug (kuru çalıştırma)

```
migurdex download "bleach" -e 1 --debug
```

İndirme başlatılmaz (`exit 0`); stderr'e güvenli özet yazılır: sağlayıcı, anime, bölüm, grup,
hoster, kalite, tür ve aday sayısı (`DownloadCommand.WriteDebugSummary`). URL, header ve token
hiçbir koşulda yazdırılmaz. `--json` ile birlikte kullanılırsa stdout'a `dryRun: true` etiketli,
`candidateCount` içeren JSON çıkar.

### JSON çıktısı

```
migurdex download "one piece" -e 12 --json
```

stdout yalnızca tek bir JSON belgesi içerir; ilerleme satırları, uyarılar ve hatalar stderr'e
gider. Alan adları camelCase, enum'lar metin olarak serileştirilir (`JsonNamingPolicy.CamelCase`,
`JsonStringEnumConverter`). Başarı örneği (değerler örnektir):

```json
{
  "success": true,
  "dryRun": false,
  "provider": "TurkAnime",
  "animeId": "1234",
  "animeTitle": "One Piece",
  "episode": { "id": "5678", "title": "12. Bölüm", "season": 1, "number": 12 },
  "source": { "hoster": "GoogleDrive", "quality": "1080p", "format": "Mp4", "group": "FansubAdı" },
  "mediaPath": "C:\\Users\\ornek\\Downloads\\Migurdex\\One Piece\\S01E12 - 12. Bölüm.mp4",
  "subtitlePaths": [
    "C:\\Users\\ornek\\Downloads\\Migurdex\\One Piece\\S01E12 - 12. Bölüm.tr.srt"
  ],
  "warnings": []
}
```

Diğer şekiller: hata → `{ "success": false, "error": "..." }` (`exit 1`); video bittikten sonra
kullanıcı iptali → `success: true`, `cancelled: true` + `mediaPath`/`subtitlePaths`/`warnings`
(`exit 130`). Çıkış kodlarının tamamı için `Genel akış` bölümüne bakın.

### TUI akışı adım adım

1. `migurdex` (argümansız) → `MainMenuView`.
2. Arama yapın (`SearchView`) ve sonucu seçin (`SearchResultsView`).
3. Anime detay ekranında (`AnimeDetailsView`) bölümü seçin; seçim
   `EpisodeSourcesView.SetTarget` ile kaynak ekranına aktarılır.
4. Kaynak tarama (`EpisodeSourcesView.RenderAsync`):
   - `AutoSelectBestSource` açıksa: `Kaynaklar taranıyor...` durumu gösterilir; tam eşleşme
     (`SourceSelector.IsExactMatch` + `IsAutoEligible`) bulunursa tarama erken kesilir ve en
     iyi kaynak seçilir. Tarama ilk kaynaktan sonra en fazla `AutoSelectTimeoutSeconds`
     (varsayılan 5 sn), mutlak üst sınır 240 sn sürer; `Esc` manuel listeye düşürür. Otomatik
     seçim başarılıysa akış doğrudan 6. adıma geçer.
   - Kapalıysa (varsayılan): fuzzy kaynak listesi doğrudan açılır.
5. Manuel listede her satır `#sıra  fansub • hoster • kalite • tür` biçimindedir
   (`EpisodeSourcesView.FormatSources`; sıralama `SourceSelector.SortVideoSources`). Liste
   akarken yazarak filtreleyebilirsiniz; `Yeniden Tara` listeyi baştan tarar, `Geri` geri döner.
6. Kaynak seçilince eylem menüsü açılır (`HandleSelectedSourceAsync`): `Oynat`, `İndir` ve
   `Geri`. `İndir` yalnızca `DownloadSourceResolver.IsDirectDownloadable` kaynaklarında
   görünür; `Embed` kaynaklar zaten listeye girmez.
7. `İndir` → `DownloadSourceAsync`: `DownloadService.DownloadAsync` çağrılır; hedef ve bayraklar
   config'ten gelir (`DownloadDirectory`, `DownloadOverwrite`, `DownloadResume`,
   `DownloadSubtitles`). Durum satırında aşama ve bayt sayacı döner
   (`FormatDownloadProgress`); `Esc` indirmeyi iptal eder, `.part` dosyası korunur.
8. Sonuç ekranı (`ShowDownloadResultAsync`): video/altyazı yolları ve uyarılar listelenir. Tam
   başarılıda `Geri`/`Enter` kaynak ekranından da çıkarır (`navigator.Pop`). Video bittikten
   sonra altyazı fazında iptal edildiyse başlık `İndirme iptal edildi`, içerik `Video
   tamamlandı; altyazılar iptal edildi.` olur. Sonuç ekranı tek seçenekli bir bilgi ekranıdır:
   `FuzzyPrompt.Show(..., searchable: false)` ile açıldığından `Ara:` filtre satırı çizilmez,
   yazma/`Backspace`/`Delete` (ve kelime silme kısayolları) sorguyu değiştirmez; `↑↓` gezinme,
   `Enter` (devam) ve `Esc` (geri) davranışı aynen çalışır. Footer bu ekrana özgüdür:
   `Enter devam • Esc geri` (`↑↓ gez` ve `yazarak filtrele` ipuçları gösterilmez).

### Çıktı dosya/klasör düzeni

Varsayılan kök (`config.json` → `DownloadDirectory`): Windows'ta `<profil>\Downloads\Migurdex`,
Linux'ta `XDG_DOWNLOAD_DIR` tanımlıysa `<o dizin>\Migurdex`, aksi hâlde
`<profil>\Downloads\Migurdex` (`CliConfig.DefaultDownloadDirectory`). `-o` bu kökü tek indirme
için değiştirir.

```
<çıktı kökü>\
└─ <Anime adı>\                                       # sanitizasyonlu klasör (fallback: "Anime")
   ├─ S01E05 - <Bölüm adı>.mp4                        # bitmiş MP4
   ├─ S01E05 - <Bölüm adı>.tr.srt                     # sidecar altyazı (.ass/.vtt de olur)
   ├─ S01E05 - <Bölüm adı>.<fingerprint>.part         # MP4 indirme sırasında: parça dosyası
   ├─ S01E05 - <Bölüm adı>.<fingerprint>.part.meta    # MP4 indirme sırasında: resume metadata
   └─ S01E05 - <Bölüm adı>.migurdex.lock              # indirme sırasında: hedef kilidi
```

- Dosya adı: `S<sezon>E<bölüm>` + bölüm adı boş değilse ` - <bölüm adı>`
  (`DownloadPathBuilder.Build`). Sezon ve tam sayı bölüm iki haneli sıfırlıdır (`S02E05`),
  kesirli bölüm `E5.5` biçiminde kalır (`FormatEpisodeNumber`).
- MP4'te uzantı sabit `.mp4`; HLS'te gerçek uzantı yt-dlp çıktısından korunur (`.mp4`, `.mkv`,
  `.webm`, `.ts`, ...).
- Altyazı adı: `<video>.<dil|etiket>.<srt|ass|vtt>`; aynı etiketli ek altyazılara `.<sıra>`
  eklenir (`SubtitleDownloader.BuildSidecarPath`).
- `.part`, `.part.meta`, `.migurdex.lock` ve HLS'in geçici `.migurdex-job-<guid>` dizini indirme
  bittikten sonra ortadan kalkar. İptal edilen MP4 indirmesinde `.part` + `.meta` bilinçli
  olarak korunur; aynı kaynakla yeniden denendiğinde kaldığı yerden devam edilir.

## Sık sorular

**Kaliteye göre mi seçiyor?**
Evet. `--format auto` (varsayılan) adayları `SourceSelector.SortVideoSources` sıralar:
`config.json` → `SourceSortPriority` (varsayılan `Quality → Format → Hoster → Group`),
`PreferredQualityOrder` (varsayılan 2160p en üstte) ve `PreferredHosterOrder` listeleri
uygulanır. `Auto*Hosters/Qualities/Types` listeleri `IsAutoEligible` ile filtre uygular; en fazla
3 benzersiz aday (`MaxCandidates`) sırayla denenir, ilki başarısız olursa sıradakine geçilir.

**Fansub seçiyor mu?**
CLI'da yalnız `-g/--group` ile; ad API'den gelen grup listesine göre doğrulanır. Otomatik aday
seçiminde fansub yalnız eşitliği bozan son sıralama ölçütüdür (alfabetik). TUI'da kaynak
listesinde her satırda fansub adı görünür; istediğiniz fansubun kaynağını elle seçebilirsiniz.

**HLS için ne gerekiyor?**
`yt-dlp` zorunludur (`YtDlpPath`, varsayılan `yt-dlp`); segmentleri birleştirmek için çoğu durumda
`ffmpeg` de gerekir. Migurdex bunları otomatik kurmaz. yt-dlp eksikse `yt-dlp bulunamadı veya
çalıştırılamadı. yt-dlp kurun ve PATH'te bulunduğundan emin olun.`, ffmpeg eksikse `HLS için
ffmpeg gerekiyor ancak ffmpeg bulunamadı veya çalışmıyor.` hatası alınır.

**Dosya nereye iniyor?**
`<DownloadDirectory>\<anime>\SxxEyy - <bölüm>.<uzantı>` düzenine; varsayılan kök
`<profil>\Downloads\Migurdex`. Ayrıntı: `Kullanım örnekleri → Çıktı dosya/klasör düzeni`.

**Yarım indirmeye devam ediyor mu?**
MP4'te evet (varsayılan `DownloadResume: true`): `.part` + `.meta` dosyası mevcutsa, kaynak
fingerprint'i ve `ETag`/`Last-Modified` eşleşiyorsa `Range: bytes=<n>-` ile devam edilir. Sunucu
Range'i yok sayıp `200 OK` dönerse parça sıfırlanıp dosya baştan iner. HLS'te devam etme yoktur;
yt-dlp her denemeyi geçici iş dizininde baştan yapar.

**`--json` ne işe yarıyor?**
stdout'u tek makine okunur JSON belgesine indirir (betik/otomasyon için); ilerleme, uyarı ve hata
satırları stderr'e gider. Üç sonuç şekli vardır: başarı (`success: true` + yol listeleri), hata
(`{ "success": false, "error": ... }`), video sonrası iptal (`success: true`, `cancelled: true`,
`exit 130`). `--debug` ile birlikte kullanılırsa `dryRun: true` döner.

**Aynı anda iki indirme olursa ne oluyor?**
Aynı hedef dosyada çift katmanlı kilit devreye girer (`DownloadTargetLock`): süreç içi
`SemaphoreSlim` + `<hedef>.migurdex.lock` dosyası (`FileShare.None`). Aynı süreçte ikinci indirme
kilit boşalana dek bekler; ayrı süreçteyken ikinci indirme kilidi açamayıp `Video hedefi başka
bir indirme tarafından kullanılıyor.` hatası alır. Video bittikten sonraki altyazı fazında kilit
alınamazsa altyazılar atlanır, video sonucu korunur. Farklı hedef dosyalara yapılan indirmeler
birbirini etkilemez.

**Zaten var olan dosyanın üzerine yazar mı?**
Hayır. Hedef varsa hem MP4 hem HLS indirmesi `Video hedefi zaten var; overwrite kapalı.` hatası
verir; altyazılarda da aynı kural geçerlidir (`SubtitleDownloader.EnsureTargetAvailable`). Üzerine
yazmak için `--force` veya `config.json` → `DownloadOverwrite: true` gerekir.

## Dosya listesi

Üretim kodu (`Migurdex.Cli`):

| Dosya | Satır | Görev |
|---|---:|---|
| `Services/DownloadCommand.cs` | 740 | CLI komutu, argüman ayrıştırma, JSON çıktı |
| `Services/DownloadSourceResolver.cs` | 154 | Sağlayıcı/grup/bölüm çözümleme, en iyi üç aday denemesi |
| `Services/Downloads/DownloadHttp.cs` | 341 | HTTP istemcisi, redirect zinciri, header allowlist'leri, fingerprint |
| `Services/Downloads/DownloadInterfaces.cs` | 159 | `IDownloadService`, `IMp4Downloader`, `IHlsDownloader`, `ISubtitleDownloader`, `IExternalProcessRunner` |
| `Services/Downloads/DownloadModels.cs` | 229 | `DownloadRequest/Result/Progress`, `DownloadPath`, `HlsDownloadOptions` |
| `Services/Downloads/DownloadPathBuilder.cs` | 388 | Yol üretimi, sanitizasyon, UTF-8 byte bütçesi |
| `Services/Downloads/DownloadService.cs` | 351 | Orkestrasyon: video + altyazı, uyarı toplama |
| `Services/Downloads/DownloadServiceCollectionExtensions.cs` | 25 | DI kayıtları |
| `Services/Downloads/DownloadTargetLock.cs` | 133 | Süreç içi semaphore + `.migurdex.lock` dosya kilidi |
| `Services/Downloads/ExternalProcessRunner.cs` | 363 | Harici süreç çalıştırma (yt-dlp), çıktı ayrıştırma |
| `Services/Downloads/Mp4Downloader.cs` | 613 | MP4 indirme, HTTP Range resume |
| `Services/Downloads/Mp4ResumeMetadata.cs` | 130 | Resume meta dosyası (`.meta`), ETag/Last-Modified eşleşmesi |
| `Services/Downloads/SubtitleDownloader.cs` | 529 | Altyazı indirme, `data:` URI, biçim doğrulama |
| `Services/Downloads/YtDlpHlsDownloader.cs` | 605 | HLS indirme, yt-dlp sarmalayıcı |

Testler (`Migurdex.Tests`): `Mp4DownloaderTests`, `SubtitleDownloaderTests`, `HlsDownloaderTests`,
`DownloadServiceTests`, `DownloadCommandTests`, `DownloadPathBuilderTests`,
`ExternalProcessRunnerTests`, `ApiClientServiceTests`, `TuiMarkupSafetyTests`,
`FuzzyPromptSearchableTests`.

## MP4 indirme ve resume

- Hedef yol: `<çıktı kökü>/<anime>/SxxEyy - <bölüm>.mp4`. Bölüm numarası kesirli olabilir (`E5.5`).
- Kaynak yalnız HTTP/HTTPS kabul edilir (`DownloadHttp.ValidateHttpUri`).
- Resume parça dosyası: `<hedef>.<fingerprint>.part`; metadata: `<hedef>.<fingerprint>.part.meta`.
  `fingerprint`, URL + sıralı header'ların (Range hariç) SHA-256 özetinin ilk 24 hex karakteridir.
  Fingerprint'i eşleşmeyen `.part` dosyası kullanılmaz, sıfırdan başlanır.
- Metadata (`.meta`) `ETag`/`Last-Modified` değerlerinin SHA-256 özetlerini ve `TotalBytes`'ı taşır
  (`Mp4ResumeMetadata`, sürüm 1). Sunucu yanıtı ETag/Last-Modified açısından eşleşmiyorsa parça
  sıfırlanır; böylece değişmiş dosya bozuk şekilde devam ettirilmez.
- Resume akışı: mevcut parça boyutu kadar `Range: bytes=<n>-` header'ı gönderilir.
  - `206 Partial Content` → parçaya append; `Content-Range` tutarlılığı, `Content-Length` eşleşmesi
    ve metadata eşleşmesi doğrulanır.
  - `200 OK` → sunucu Range'i yok saydı; parça sıfırlanıp sıfırdan indirilir.
  - `416 Range Not Satisfiable` + `Content-Range: bytes */<total>` ve parça boyutu `== total` ise
    dosya zaten tamamlanmış sayılır, parça finale taşınır.
  - Uyuşmazlıkta en fazla 2 deneme (`MaxResumeAttempts`); sonra hata.
- Eski, fingerprint içermeyen `<hedef>.part` dosyaları bilinçli olarak resume edilmez, silinir.
- İptal (`Esc`/Ctrl+C) durumunda `.part` korunur; aynı indirme daha sonra devam edebilir.
- İndirme sırasında HTML / metin / JSON / XML / görsel içerik türü geldiğinde hata verilir; sunucu
  hata sayfası medya dosyasına yazılmaz.
- İlerleme raporu en fazla ~150 ms'de bir güncellenir (`ProgressIntervalMilliseconds`).

## HLS indirme (yt-dlp)

- HLS yalnız harici `yt-dlp` süreciyle indirilir; `YtDlpPath` config alanından çalıştırılabilir
  seçilir (varsayılan `yt-dlp`). yt-dlp bulunamazsa anlaşılır hata verilir.
- Segmentleri birleştirmek için yt-dlp'nin kullanabildiği `ffmpeg` gerekebilir; ffmpeg
  eksikliğinde yt-dlp çıktısından tespit edilip açık bir hata mesajı verilir.
- yt-dlp argümanları:

  ```
  --no-config --no-playlist --no-part --newline --progress
  --batch-file <job/.migurdex-input.txt>   # URL enjeksiyonuna kapalı geçiş
  --paths <job> --output media.%(ext)s
  --print after_move:filepath
  --add-header <ad>: <değer>                # yalnız allowlist header'lar
  ```

- **`--progress` neden gerekli:** yt-dlp'de herhangi bir `--print` kullanımı `--quiet` davranışını
  ima eder; `--print after_move:filepath` tek başına verildiğinde yt-dlp ilerleme satırı üretmez.
  Canlı TUI smoke testinde (27 Eylül 2026) bu gerçek hata bulundu: HLS indirmesi başarıyla
  tamamlanıyor ancak durum satırı `Bağlanıyor • 0 B • Esc: iptal` değerinde kalıyordu —
  yt-dlp'den progress satırı gelmediği için `ReportProcessProgress` hiç tetiklenmiyor ve aşama
  `Requesting`'ten `Downloading`'e geçemiyordu. Aynı argüman listesine `--progress` eklenmesi
  progress çıktısını geri açtı; `--print after_move:filepath` (çıktı yolunun stdout'dan
  öğrenilmesi) yerinde kalır, `--newline` sayesinde her güncelleme ayrı satır olarak akar.
  `HlsDownloaderTests` hem `--progress` + `--print after_move:filepath` birlikteliğini hem de
  progress satırlarının `Downloading` aşamasına çevrildiğini doğrular.

- Her indirme geçici `.migurdex-job-<guid>` dizininde çalışır, bitince silinir.
- Yeniden deneme: varsayılan 3 deneme (1-5 aralığına sabitlenir), denemeler arası 1 saniye
  (`HlsDownloadOptions.MaxAttempts`, `RetryDelay`).
- Çıktı dosyası yt-dlp'nin `--print after_move:filepath` satırından veya job dizininden bulunur;
  yalnız medya uzantıları kabul edilir (`.mp4`, `.mkv`, `.webm`, `.ts`, ...). Gerçek medya
  uzantısı yt-dlp çıktısına göre korunur; HTML/JSON/altyazı görünümlü dosyalar reddedilir.
- İlerleme, yt-dlp'nin stdout satırlarındaki yüzde (`%`) ve `MiB/x MiB` kalıplarından ayrıştırılır
  (`ReportProcessProgress` → `Downloading` aşaması); bu satırların üretilmesi yt-dlp'ye verilen
  `--progress` bayrağına bağlıdır (bkz. yukarıdaki not).

## Altyazı indirme

- Altyazılar medyanın yanına sidecar olarak yazılır: `<video>.<dil|etiket>.<srt|ass|vtt>`.
- Kaynak önceliği: altyazıya özel header'ı varsa o kullanılır; yoksa video kaynağı header'larından
  yalnız güvenli allowlist'tekiler fallback olarak taşınır: `Accept`, `Accept-Language`, `Origin`,
  `Referer`, `User-Agent`.
- `Referer`/`Origin` yalnız aynı origin'e taşınır: altyazı URL'si, header değerinde yazan origin
  ile eşleşmiyorsa düşürülür (`DownloadHttp.CopySubtitleFallbackHeaders`).
- `Authorization`, `Cookie`, API-key benzeri header'lar hiçbir koşulda altyazı isteğine veya
  yt-dlp'ye taşınmaz.
- Yalnızca HTTP/HTTPS; en fazla 5 yönlendirme. Cross-origin yönlendirmede yalnız güvenli
  header'lar (`Accept`, `Accept-Language`, `Origin`, `Range`, `Referer`, `User-Agent`) korunur,
  hassas header'lar düşürülür.
- Boyut limiti 10 MiB (`SubtitleDownloader.MaxSubtitleBytes`); bildirilen boyutla eşleşmeyen
  gövde reddedilir.
- `data:` URI altyazılar da desteklenir; gövde HTML/JSON görünümündeyse veya `.srt`/`.ass`/`.vtt`
  imzaları (`-->`, `[Script Info]`, `WEBVTT`) bulunamıyorsa dosya reddedilir.
- Altyazı hatası video sonucunu bozmaz; uyarı listesine eklenir.

## Güvenlik

- Yalnız HTTP/HTTPS kaynaklar; `Embed`/`Unknown` indirilmez.
- Cross-origin redirect'te `Authorization`/`Cookie` gibi hassas başlıklar düşürülür.
- yt-dlp'ye yalnız `Accept`, `Accept-Language`, `Origin`, `Referer`, `User-Agent` header'ları
  taşınır; URL komut satırı yerine batch dosyasıyla verilir.
- Aynı hedefe eşzamanlı indirme çift katmanlı kilit ile engellenir: süreç içi semaphore +
  `<hedef>.migurdex.lock` dosya kilidi (diğer süreçlere karşı). Kilit alınamazsa altyazılar
  atlanır, video sonucu korunur.
- Yol güvenliği: bileşen başına 200, geçici son ek için 96 UTF-8 byte bütçesi; tam yol bütçesi
  Windows 240, Linux 1024 byte. Sanitizasyon `<>:"|?*`, kontrol karakterleri ve Windows rezerve
  adlarını (`CON`, `NUL`, `COM1`...) temizler; anime klasörü çıktı kökünün dışına çıkamaz.
- Hata mesajları kaynak URL'sini veya header değerlerini sızdırıyorsa genel metne düşürülür
  (`DownloadService.SafeFailureMessage`).

## Yapılandırma (`config.json`)

| Alan | Varsayılan | Açıklama |
|---|---|---|
| `DownloadDirectory` | platform Downloads dizini altında `Migurdex` (Linux'ta `XDG_DOWNLOAD_DIR` köklü ise o dizin altında `Migurdex`) | İndirme çıktı kökü; `-o` ile geçersiz kılınır. Boş değer varsayılana düşer |
| `YtDlpPath` | `yt-dlp` | HLS için yt-dlp çalıştırılabilir adı/yolu |
| `DownloadSubtitles` | `true` | Varsayılan olarak altyazılar da iner (`--no-subs` ile kapatılır) |
| `DownloadResume` | `true` | Kısmi MP4 dosyasından devam etme (`--no-resume` ile kapatılır) |
| `DownloadOverwrite` | `false` | Var olan hedefin üzerine yazma (`--force` ile açılır) |

Eski config dosyaları yeni alanlar eklenmeden de güvenle yüklenir. Migurdex yt-dlp veya
ffmpeg'i otomatik indirmez/kurmaz.

## Test kapsamı

Gerçek test metrikleri (`dotnet test` ile birebir doğrulanmıştır). "Metod" sütunu
`[Fact] + [Theory]` sayısıdır; "Çalışan case" sütunu Theory'lerin `[InlineData]`
satırlarının da açıldığı toplam çalıştırılan test sayısıdır.

| Test sınıfı | `[Fact]` | `[Theory]` | `[InlineData]` | Metod | Çalışan case |
|---|---:|---:|---:|---:|---:|
| `Mp4DownloaderTests` | 20 | 0 | 0 | 20 | 20 |
| `SubtitleDownloaderTests` | 8 | 1 | 2 | 9 | 10 |
| `HlsDownloaderTests` | 8 | 0 | 0 | 8 | 8 |
| `DownloadServiceTests` | 7 | 0 | 0 | 7 | 7 |
| `DownloadCommandTests` | 11 | 1 | 4 | 12 | 15 |
| `DownloadPathBuilderTests` | 4 | 0 | 0 | 4 | 4 |
| `ExternalProcessRunnerTests` | 3 | 0 | 0 | 3 | 3 |
| `ApiClientServiceTests` | 2 | 0 | 0 | 2 | 2 |
| `TuiMarkupSafetyTests` | 5 | 3 | 18 | 8 | 23 |
| `FuzzyPromptSearchableTests` | 7 | 1 | 11 | 8 | 18 |

Not: `TuiMarkupSafetyTests` için "8 test" ve "23 test" ifadeleri aynı gerçeğin iki ölçümüdür:
8 test **metodu** vardır (5 `[Fact]` + 3 `[Theory]`); Theory'ler 18 `[InlineData]` ile
genişlediğinden xUnit toplam **23 test çalıştırır**.

Kolon toplamları: **81 metot / 110 çalışan case / 10 sınıf**. Bu kanonik değerdir; tüm doküman
ve PR metinlerinde geçen "109" (27.09.2026 öncesi), "102" (26.09.2026 öncesi) ve "103"
([TEST_RESULTS.md](https://github.com/Nutaliaxd/Migurdex/blob/main/TEST_RESULTS.md) bölüm 1'deki 8 sınıflık filtre koşusu) değerleri farklı tarih/kapsamın geçmiş
kayıtlarıdır — ayrım için `--progress` düzeltmesi bölümündeki açıklamaya bakın.

### Doğrulama sonuçları (offline, 26 Eylül 2026)

26 Eylül 2026'da `feature/download` dalında, temiz çalışma ağacı üzerinde (son kod commit:
`4cd2ac8`) kod değişikliği yapılmadan tamamen offline bir doğrulama koşusu çalıştırıldı. Ağ
erişimi gerektiren `ExtractorSmokeTests` filtreyle dışlandı; kalan tüm testler Release
derlemesi üzerinde koşuldu.

Ön kontroller ve derleme:

| Adım | Komut | Sonuç |
|---|---|---|
| Çalışma ağacı | `git status --short --branch` | Temiz (işlenmemiş değişiklik yok) |
| Boşluk denetimi | `git diff --check` | Temiz |
| Bağımlılık | `dotnet restore Migurdex.slnx` | Başarılı |
| Derleme | `dotnet build Migurdex.slnx -c Release --no-restore` | 18 proje, 0 uyarı, 0 hata (v1.9.2 tabanlı ağaç; Deokwave eklendiğinde çözüm 19 projeye çıkıyor) |

Tam takım koşusu (`ExtractorSmokeTests` hariç):

| Komut | Sonuç |
|---|---|
| `dotnet test Migurdex.Tests\Migurdex.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~ExtractorSmokeTests"` | **274/274 geçti** |

İndirme/TUI odaklı sınıflar ayrıca grup filtreleriyle tek tek de koşuldu:

| Test sınıfı | Geçen / toplam |
|---|---:|
| `Mp4DownloaderTests` | 20/20 |
| `SubtitleDownloaderTests` | 10/10 |
| `HlsDownloaderTests` | 7/7 |
| `DownloadServiceTests` | 7/7 |
| `DownloadCommandTests` | 15/15 |
| `ApiClientServiceTests` | 2/2 |
| `TuiMarkupSafetyTests` | 23/23 |
| `FuzzyPromptSearchableTests` | 18/18 |
| **Grup toplamı (8 sınıflık filtre)** | **102/102** |

Notlar:

- 274'lük genel toplam, `ExtractorSmokeTests` dışındaki tüm test sınıflarını kapsar: yukarıdaki
  8 indirme test sınıfının tamamı ile indirme kapsamı dışındaki sınıflar (OAuth, izleme senkronu,
  veritabanı, sezon/film, güncelleme vb.).
- **102'lik grup toplamı yalnızca ayrı filtrelerle koşulan 8 sınıfa aittir;** 10 sınıflık tam
  kapsam o gün 102 + 4 (`DownloadPathBuilderTests`) + 3 (`ExternalProcessRunnerTests`) = **109**
  olurdu. Bu koşumdan sonra `HlsDownloaderTests`'e 1 metot eklendi, bu yüzden güncel değerler
  **110** (10 sınıf) ve 8 sınıflık filtrede **103**'tür.
- Testler var olan Release derlemesi üzerinde `--no-build` ile koşulduğundan sonuçlar, 0 uyarı /
  0 hata ile tamamlanan derleme çıktısıyla birebir eşleşir.
- Bu koşumda kod değişmedi. `Test kapsamı` tablosundaki **güncel** metrikler
  **81 test metodu / 110 çalışan case**'dir; 109 → 110 farkı yalnızca 27 Eylül'deki
  `--progress` düzeltmesiyle eklenen `HlsDownloaderTests` metodundan gelir
  (`Download_ParsesYtDlpProgressLinesIntoDownloadingStage`), 102 → 103 farkı da aynı metoddandır
  (bkz. aşağıdaki `--progress` düzeltmesi bölümü).

### `--progress` düzeltmesi ve doğrulama (27 Eylül 2026)

Canlı TUI smoke testi gerçek bir hata buldu: HLS indirmesi başarıyla tamamlanmasına rağmen TUI
durum satırı `Bağlanıyor • 0 B • Esc: iptal` değerinde kalıyordu. Kök neden:
`YtDlpHlsDownloader.BuildStartInfo` yt-dlp'a `--print after_move:filepath` veriyor; yt-dlp'de bu
bayrak `--quiet` davranışını ima ettiğinden progress çıktısı türetilmiyor, `ReportProcessProgress`
hiç tetiklenmiyor ve aşama `Requesting` (`Bağlanıyor`) aşamasından ilerlemiyordu. Aynı argüman
listesine `--progress` eklenince progress satırları gelmeye başladı.

Düzeltme: yt-dlp argüman listesine `--progress` eklendi; `--print after_move:filepath` yerinde
kaldı. Böylece hem çıktı yolu stdout'dan öğrenilmeye devam ediliyor hem de ilerleme satırları
`--newline` ile satır satır akarak `Downloading` aşamasını besliyor.

Kapsam:

- `Migurdex.Cli\Services\Downloads\YtDlpHlsDownloader.cs` — `BuildStartInfo` içine `--progress` ve
  neden gerekli olduğunu açıklayan yorum eklendi.
- `Migurdex.Tests\HlsDownloaderTests.cs` — mevcut argüman testine `--progress` varlığı ve
  `--print` + `after_move:filepath` birlikteliği doğrulamaları eklendi; yüzde (`42.3%`) ve bayt
  (`10.50MiB / 24.00MiB`) kalıplı progress satırlarının `Downloading` aşamasına çevrildiğini
  doğrulayan yeni test `Download_ParsesYtDlpProgressLinesIntoDownloadingStage` yazıldı;
  `FakeProcessRunner` stdout satır callback'ini yakalayacak şekilde genişletildi.
- `DOWNLOAD.md`, `README.md` — HLS ilerlemesinin `--progress`'e bağlı olduğu ve smoke bulgusu
  belgelendi.

Doğrulama (portatif .NET SDK 10.0.401, `feature/download`, `ExtractorSmokeTests` hariç):

| Komut | Sonuç |
|---|---|
| `dotnet build Migurdex.slnx -c Release` | 18 proje, 0 uyarı, 0 hata (düzeltme v1.10.0 rebase'i öncesinde doğrulandı; Deokwave'li/aftaki ağaç 19 proje) |
| `dotnet test Migurdex.Tests\Migurdex.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~ExtractorSmokeTests"` | **275/275 geçti** |
| Aynı koşu + `FullyQualifiedName~HlsDownloaderTests` filtresi | **8/8 geçti** |
| İndirme/TUI odaklı sınıf filtreleri (Downloader/Service/Command/PathBuilder/ProcessRunner/ApiClient/TuiMarkup/FuzzyPrompt) | **110/110 geçti** (10 sınıf) |

Değişiklik bu doküman güncellemesiyle birlikte çalışma ağacında hazır bırakıldı; commit yapılmadı.

**103/103 ile 110/110 arasındaki 7 case farkı — neden:** iki sayı da doğrudur, fark yalnızca
filtre kapsamındadır.

- **110/110** = indirme/TUI kapsamındaki **10** sınıfın tamamı:
  `Mp4DownloaderTests` 20 + `SubtitleDownloaderTests` 10 + `HlsDownloaderTests` 8 +
  `DownloadServiceTests` 7 + `DownloadCommandTests` 15 + `DownloadPathBuilderTests` 4 +
  `ExternalProcessRunnerTests` 3 + `ApiClientServiceTests` 2 + `TuiMarkupSafetyTests` 23 +
  `FuzzyPromptSearchableTests` 18 = 110.
- **103/103** ([TEST_RESULTS.md](https://github.com/Nutaliaxd/Migurdex/blob/main/TEST_RESULTS.md) bölüm 1) = aynı 10 sınıftan yalnızca **8** tanesinin geçtiği
  koşu; `DownloadPathBuilderTests` (4) ve `ExternalProcessRunnerTests` (3) o koşunun tablosunda
  yer almıyor. 103 + 4 + 3 = 110.
- Güncel kanonik değer **110**'dur (`Test kapsamı` tablosu ve `Güncelleme talimatı`); 103 yalnızca
  8 sınıflık filtre koşusunun geçmiş bir kaydıdır.

### Canlı API smoke doğrulaması (26 Eylül 2026)

26 Eylül 2026 koşumudur ve **v1.10.0 rebase'i öncesindeki** ağacı kapsar: o sırada upstream
`v1.9.2` tabanındaydı ve `/health` **13 sağlayıcı** bildiriyordu. Rebase ile gelen `Deokwave`
(+1) sonrası beklenen sağlayıcı sayısı **14**'tür (bkz. `Yapılan işlemler` → madde 11 ve
[TEST_RESULTS.md](https://github.com/Nutaliaxd/Migurdex/blob/main/TEST_RESULTS.md) bölüm 7). Extractor sayısı 13/14 sağlayıcı için de 38'dir, değişmemiştir.

`feature/download` dalı, temiz bir kopya üzerinde rastgele bir loopback portunda
(`http://127.0.0.1:37972`) canlı API smoke testi koşuldu. API 2 saniyede hazır hâle geldi:
`providers=13`, `extractors=38`, `rust=true`.

Toplam **21 istek** gönderildi; **21/21 endpoint HTTP 200 ve geçerli JSON** döndü.

| Uç | Sonuç |
|---|---|
| `/health` | 200; 13 sağlayıcı, 38 extractor, Rust hazır |
| `/api/v1/providers` | 200; 13 sağlayıcı; isim/tür/base URL/capabilities dolu |
| `/api/v1/extractors` | 200; 38 extractor listesi |
| `/api/v1/anime/search?q=one piece` | 200; **13/13 sağlayıcı başarılı**, 0 hata, 153 sonuç |
| Anime detayları — 6 sağlayıcı | 200; AnimeciX 16 bölüm, AniHub 25, SonAnime 1171, TRAnimeci 1, AsyaAnimeleri 8, TrAnimeIzle 0 |
| Fansub grupları — 4 sağlayıcı | 200; her biri 1 grup döndü |
| Video kaynakları — 4 sağlayıcı | 200; AnimeciX 3, TRAnimeci 30, AniHub 1, SonAnime 3 kaynak |
| `/api/v1/metadata/search?q=one piece` | 200; 10 kayıt (AniList + Jikan) |
| `/api/v1/metadata/anilist/21` | 200; `ONE PIECE`, `source=AniList` |
| `/api/v1/tracker/lookup?anilistId=21` | 200; `anilistId=21`, `ONE PIECE` |

Yanıt süreleri:
- `/health`: 29 ms
- `/api/v1/providers`: 27 ms
- `/api/v1/extractors`: 6 ms
- anime arama: 1.681 ms
- anime detayları: 108–1.126 ms
- kaynak çözümleme: 487–980 ms
- metadata arama: 462 ms
- metadata detay: 186 ms
- tracker lookup: 354 ms

Log incelemesi:
- **0 exception**
- **0 stack trace**
- **0 NullReferenceException**
- **0 serialization hatası**
- **stderr boş**

İki upstream durumu not edildi; ikisi de uygulama hatası değil:

1. `TrAnimeIzle`: sağlayıcı captcha challenge’ı çözülemedi. Upstream isteği engelledi;
   sağlayıcı boş liste döndürdü ve endpoint yine 200 ile cevap verdi.
2. `VidmolyExtractor`: embed URL’si HTTP 302 ile `torroclk.com` reklam ağına yönlendi.
   Extractor bu kaynağı atladı ve diğer hoster’lardan kaynak dönmeye devam etti.

Test sonrası API süreci kapatıldı, port serbest bırakıldı ve geçici test dizini silindi.
Kaynak ağacında kod değişikliği yapılmadı.

## TUI markup güvenliği

- Dinamik her metin (anime adı, bölüm, fansub, sunucu, kalite, kullanıcı sorgusu) Spectre.Console
  `Markup.Escape` ile kaçırılır; köşeli parantezli veriler (`[SubsPlease]`, `[/]`, `[1080p]`)
  sahte renk etiketi olarak yorumlanamaz.
- Literal köşeli parantez gerektiren satırlarda (`[hoster / quality]` kalıbı) parantezler
  `[[ ]]` ile, içerik `Markup.Escape` ile kaçırılır.
- Bu kural `e9e01e9` commit'inde kaynağı düzeltilen dengesiz markup hatasının önünü kalıcı
  kapatır; `TuiMarkupSafetyTests` tüm prompt/header/choice satırlarının ayrıştırılabilir
  (dengeli) olduğunu doğrular.

## Bilinen sınırlar ve riskler

### HLS: yt-dlp/ffmpeg bağımlılığı

- HLS yalnız harici `yt-dlp` süreciyle indirilir; ikili `YtDlpPath` config'inden çözülür.
  Kurulu değilse indirme `yt-dlp bulunamadı veya çalıştırılamadı. yt-dlp kurun ve PATH'te
  bulunduğundan emin olun.` hatasıyla başarısız olur; segment birleştirme için `ffmpeg` gerekir.
- Harici araç sürümleri değiştikçe ilerleme satırı ayrıştırması (`%` ve `MiB/x MiB` kalıpları) ve
  `--print after_move:filepath` davranışı etkilenebilir; ilerleme çıktısı ayrıca yt-dlp'nin
  `--progress` bayrağını desteklemeye devam etmesine bağlıdır. yt-dlp güncellemelerinden sonra
  `HlsDownloaderTests` koşulmalıdır.
- HLS'te devam etme (resume) yoktur; ağ kesilirse indirme geçici iş diziniyle birlikte baştan
  alınır.

### MP4 resume ve sunucu Range davranışı

- Devam etme, sunucunun `Range` isteğini doğru karşılamasına bağlıdır. Sunucu `200 OK` dönerse
  (Range yok sayıldı) parça sıfırlanıp dosyanın tamamı yeniden iner.
- `416 Range Not Satisfiable` yalnızca `Content-Range: bytes */<total>` gönderilmişse ve parça
  boyu tam `total`'e eşitse "zaten tamamlandı" sayılır; aksi hâlde parça sıfırlanıp en fazla
  `MaxResumeAttempts` (2) kez yeniden denenir, sonra `Video aralık isteği tamamlanamadı.` hatası
  verilir.
- Kaynak URL'si oturumlar arasında değişiyorsa (imzalı/süreli bağlantılar) fingerprint değişir;
  eski `.part` kullanılmaz ve indirme sıfırdan başlar. Bu, yanlış dosyaya append etmemek için
  bilinçli bir tasarım tercihidir.

### Content-Length / Content-Range sınırlamaları

- `200 OK` yanıtında `Content-Length` yoksa (chunked akış) MP4 indirmesi `Video sunucusu boş bir
  gövde döndürdü.` hatasıyla başarısız olur; bu tür sunuculardan MP4 indirilemez.
- `206` yanıtında `Content-Range`'in ayrıştırılamaması, başlangıç ofsetinin uyuşmaması veya
  `Content-Length` tutarsızlığı parçayı sıfırlar (en fazla 2 deneme), ardından
  `Video sunucusu geçersiz bir aralık yanıtı verdi.` / `Video sunucusu aralık uzunluğu
  tutarsız.` hatası verilir.
- Toplam boyut bilinmiyorsa ilerleme yalnızca indirilen baytı gösterir; yüzde hesaplanamaz.

### Çoklu süreç kilidi

- Kilit dosyası `<hedef>.migurdex.lock` `FileShare.None` ile açılır. Süreç çökerse dosya diskte
  kalabilir; işletim sistemi tanıtıcıyı yine serbest bırakır ve sonraki indirme `OpenOrCreate`
  ile dosyayı sorunsuz yeniden açar.
- Ağ paylaşımlarında (SMB/NFS) dosya kilidi davranışı platformdan platforma değişebilir; eşzamanlı
  indirme koruması yerel diskte tasarlandığı gibi çalışır.

### Altyazı formatları

- Yalnız `.srt`, `.ass`, `.vtt` desteklenir ve imza doğrulaması zorunludur (`-->`,
  `[Script Info]`, `WEBVTT`). Gövde HTML/JSON görünümündeyse dosya reddedilir; diğer biçimler
  (ör. `.sub`, PGS) desteklenmez.
- Altyazılar videoya gömülmez (mux yok); sidecar olarak iner, oynatıcının bunları bulması gerekir.
- Boyut limiti 10 MiB (`SubtitleDownloader.MaxSubtitleBytes`). Altyazı hatası video indirmesini
  bozmaz, yalnızca uyarıya dönüşür.

### Native Rust bağımlılığı

- İndirme yığını saf C#'tır; ancak kaynak çözümü paketle gelen API'den geçer ve API
  `migurdex_native.dll` olmadan açılmaz (`RustBridge.Initialize` → `Kritik: native kütüphane
  yüklenemedi`, exit 1). CLI API'yi kendisi başlattığından (`TryStartApiDaemonAsync`) bu
  dosyanın `migurdex.exe`'nin yanındaki `api\` klasöründe bulunması gerekir; eksikse indirme
  kaynak çözümleme aşamasında takılır.
- `build.ps1` Rust derlemesini (cargo) ve `migurdex_native.dll` + `e_sqlite3.dll` kopyalamasını
  içerir; Rust toolchain olmayan makinede paket üretilemez. Risk tamamen kaynak çözümü ve paket
  üretimi tarafındadır; MP4/altyazı indirme kodu bu kütüphaneleri kullanmaz.

### Sağlayıcı upstream erişimi (Deokwave)

- `deokwave.com` tüm uç noktalarında Cloudflare `"Just a moment..."` JS challenge ile HTTP 403
  döndürüyor; Deokwave araması boş liste dönüyor ve indirme akışı kaynak çözümlemesine
  ulaşamadan arama adımında temiz `exit 1` JSON hatasıyla duruyor (27.09.2026 canlı smoke,
  bkz. [TEST_RESULTS.md](https://github.com/Nutaliaxd/Migurdex/blob/main/TEST_RESULTS.md) → bölüm 8).
- Bu sınırlama indirme özelliğinin geneline değil, yalnızca Deokwave sağlayıcısına özgüdür:
  kontrol grubu `TurkAnime` ile aynı akış sorunsuz tamamlanıyor. Uygulama hatası değildir;
  upstream erişimi düzelirse sağlayıcı kod değişikliği gerektirmeden çalışır.
- Yan bulgu: sağlayıcı 403'ü sessizce boş listeye çeviriyor; sağlayıcı tarafı hataları görünür
  kılacak bir loglayıcı ileride eklenebilir.

## Yapılan işlemler

Bu bölüm, indirme özelliğinin `feature/download` dalında nasıl hazırlandığının kronolojik
çalışma kaydıdır. Özellik 29.09.2026'da `main` üzerine birleştirildi ve `v1.10.2` olarak
yayınlandı (madde 15). Maddeler 1–14 `feature/download` üzerindeki çalışmanın tarihsel
kaydıdır; bu maddelerde geçen dal adı geçmiş bağlamıdır — güncel durum `main` + `v1.10.2`'dir.

1. **Kaynak klonlama ve dal**: Depo `https://github.com/roxyrekt/Migurdex.git` adresinden
   `..\migu\source` içine klonlandı; `main` (`444a49e`) üzerinden `feature/download` dalı açıldı.
2. **Çekirdek indirme yığını** (`c3d307a`): `Migurdex.Cli\Services\Downloads\` altına 12 üretim
   dosyası yazıldı — `DownloadHttp`, `DownloadInterfaces`, `DownloadModels`,
   `DownloadPathBuilder`, `DownloadService`, `DownloadServiceCollectionExtensions`,
   `DownloadTargetLock`, `ExternalProcessRunner`, `Mp4Downloader`, `Mp4ResumeMetadata`,
   `SubtitleDownloader`, `YtDlpHlsDownloader` — ve çözümleme/komut katmanı olarak
   `Services\DownloadCommand.cs` + `Services\DownloadSourceResolver.cs` eklendi.
3. **API tarafı** (`c3d307a`): `AnimeEndpoints`'e kaynak metadata birleştirme
   (`MergeSourceMetadata`, `MergeSourceHeaders`) eklendi; `M3U8PlaylistExtractor`, bağlantıya
   özgü header'ların medya isteğine taşınmasını engelleyen `_nonTransferableHeaders` seti ve
   kaynak header birleştirmesiyle güncellendi.
4. **CLI entegrasyonu** (`c3d307a`): `NonInteractiveCommand`'a `download` alt komutu bağlandı;
   `Program.ConfigureServices` → `AddDownloadServices` + `HlsDownloadOptions` (Executable =
   `YtDlpPath`, `MaxAttempts = 3`, `RetryDelay = 1 sn`); `CliConfig`'e `DownloadDirectory`,
   `YtDlpPath`, `DownloadSubtitles`, `DownloadResume`, `DownloadOverwrite` alanları;
   `ApiClientService`'e API daemon otomatik başlatma (`TryStartApiDaemonAsync`).
5. **TUI entegrasyonu** (`c3d307a`): `EpisodeSourcesView`'e `İndir` eylemi, ilerleme izleme
   (`DownloadProgressTracker`), iptal ve sonuç ekranı (`DownloadSourceAsync`,
   `ShowDownloadResultAsync`) eklendi; `TuiApplicationCancellation` **yeni eklendi** (v1.9.2
   tabanında yoktu, `be85d61` ile geldi ve 26 satırdır).
6. **Güvenlik düzeltmeleri** (`c3d307a` kapsamında): `DownloadHttp`'te elle yönetilen yönlendirme
   zinciri (en fazla `MaxRedirects = 5` adım) ve üç ayrı header allowlist'i (cross-origin, yt-dlp,
   altyazı fallback); hata mesajlarında URL/header sanitizasyonu
   (`DownloadCommand.SanitizeFailure`, `DownloadService.SafeFailureMessage`); yt-dlp'ye URL'in
   batch dosyasından geçişi; `--debug`'in yalnızca güvenli özet vermesi.
7. **TUI markup hatası düzeltmesi** (`e9e01e9`): kaynak seçim ekranındaki dengesiz Spectre
   markup etiketleri düzeltildi; `FuzzyPrompt`/`EpisodeSourcesView` kaçışlamaları gözden
   geçirildi; `TuiMarkupSafetyTests` eklendi (5 `[Fact]` + 3 `[Theory]` → 23 çalışan case).
   Ayrıntı: `TUI markup güvenliği` bölümü.
8. **Test**: 9 indirme test sınıfı yazıldı; `dotnet test` ile 72 metod / 91 çalışan case
   doğrulandı (metrikler statik `[Fact]`/`[Theory]`/`[InlineData]` sayımıyla da tutarlı; bkz.
   `Test kapsamı`). Bu, ilk test yazımının anlık metriğidir; **güncel değer 81 metod / 110
   çalışan case / 10 sınıftır** (`DownloadPathBuilderTests` ve `ExternalProcessRunnerTests`
   eklendi, `--progress` düzeltmesiyle `HlsDownloaderTests` +1 case kazandı).
9. **Derleme ve paket üretimi** *(v1.10.0 öncesi paket)*: `.\build.ps1 -Publish` ile Release
   paketi üretildi — `dist\migurdex.exe` (win-x64, self-contained, single-file, trimmed),
   `dist\api\` (self-contained API + 13 provider eklentisi + `migurdex_native.dll` +
   `e_sqlite3.dll`), `dist\migurdex-win-x64.zip` (26.09.2026 09:53–09:54). Bu paket v1.10.0
   rebase'i öncesine aittir ve 13 sağlayıcı taşır; güncel paket madde 12'dedir.
10. **install-candidate kopyası** *(v1.10.0 öncesi durumu)*: `dist` içeriği
    `..\migu\install-candidate\` klasörüne kopyalandı (`migurdex.exe`, `api\`,
    `migurdex-win-x64.zip`). Adaydaki API 26.09.2026 10:21'de açılıp 13 eklentinin tamamını
    yükledi (`api\logs\Migurdex-20260926.log`). Bu kopya, madde 12'deki v1.10.0 paketiyle
    yenilendi.
11. **v1.10.0 rebase**: Upstream `v1.10.0` (`4035f9a`) getirildi; `feature/download` bu sürüm
    üzerine rebase edildi. Rebase öncesi `backup/download-pre-v110` yedek dalı alındı.
    Upstream ile dosya çakışması çıkmadı. Rebase sonrası `Deokwave` plugin'i için
    `dotnet restore` çalıştırıldı, çözüm Release modunda 0 uyarı / 0 hata ile derlendi ve
    offline test takımı **275/275** geçti.
12. **v1.10.0 paket üretimi ve install-candidate güncellemesi** (27.09.2026): Rebase sonrası
    taban (temel `v1.10.0` @ `4035f9a`, son kod commit'i `5014025`) portatif .NET SDK
    **10.0.401** ile `.\build.ps1 -Publish` kullanılarak yeniden paketlendi. Rust derlemesi
    `cargo build --release` ile başarılı (süre 2 dk 53 sn); `api\migurdex_native.dll`
    27.09.2026 03:00:14 damgalı. Paket **14 sağlayıcı** (Deokwave dahil) ve **38 extractor**
    içeriyor.

    | Paket içeriği | Boyut (bayt) |
    |---|---:|
    | `migurdex.exe` | 23.466.537 |
    | `api\Migurdex.Api.exe` | 108.374.872 |
    | `api\migurdex_native.dll` | 8.661.504 |
    | `api\Plugins\*` — 14 sağlayıcı plugin DLL'i (Deokwave dahil) | — |
    | `migurdex-win-x64.zip` | 61.605.017 |

    | Dosya | SHA-256 |
    |---|---|
    | `migurdex.exe` | `DE9EA5634B16E07BC3B16B8C8C7EE07CB311FE489AF78E09AAD3E608C86E3E79` |
    | `api\migurdex_native.dll` | `91BA419E97AD5BC0F0CA1ADF9768CF60FDFAC72234AADE28AC02DB46C1B44627` |
    | `migurdex-win-x64.zip` | `BC17A9FFA03D0B9BE5AA9004C6E6E94DB35C469442BA1515B3090FB6C96DB5AA` |

    - `..\migu\install-candidate\` klasörü bu paketle güncellendi; `migurdex.exe`,
      `api\migurdex_native.dll` ve `migurdex-win-x64.zip` SHA-256 özetleri `dist` kopyalarıyla
      birebir aynı doğrulandı.
    - Smoke: `migurdex.exe --version` → `migurdex v0.0.0`, `exit 0`; `migurdex.exe download
      --help` → tam yardım metni, `exit 0`. (`v0.0.0` yerel build için beklenen değerdir:
      sürüm damgası `Directory.Build.props` → `VersionPrefix` 0.0.0'dan gelir; etiketli sürüm
      numarası yalnız upstream yayın CI'sinde `-p:Version` ile basılır.) API süreci
      başlatılmadı; doğrulama dosya listesi ve hash karşılaştırmasıyla yapıldı.
    - Paket kullanıma hazır:
      `C:\Users\naton\OneDrive\Desktop\migu\install-candidate\migurdex.exe`.
13. **Kök kurulum yükseltmesi** (27.09.2026): Madde 12'de üretilip doğrulanan v1.10.0 tabanlı
    paket, yerel kök kurulumuna (`C:\Users\naton\OneDrive\Desktop\migu`) yüklendi. Eski kök
    kurulum önce `..\migu\backup-v1.9.2\` klasörüne yedeklendi; ardından `install-candidate`
    içeriğinden `migurdex.exe`, `api\` ve `migurdex-win-x64.zip` köke kopyalandı. Kaynak
    ağacında (`source\`) bu işlem sırasında kod değişikliği yapılmadı.

    | Doğrulama | Sonuç |
    |---|---|
    | `migurdex.exe --version` | `migurdex v0.0.0` — çalışıyor (sürüm metadata build'de gömülmedi; bkz. madde 12 notu) |
    | `migurdex.exe download --help` | Çalışıyor; tam yardım metni |
    | `api\Plugins\` | 14 sağlayıcı plugin DLL'i (`Migurdex.Plugins.*`, Deokwave dahil) |
    | `api\migurdex_native.dll` | 8.661.504 bayt — madde 12 paketiyle aynı |

    | Dosya | SHA-256 |
    |---|---|
    | Eski `migurdex.exe` (v1.9.2 kök kurulumu; yedeği `backup-v1.9.2\` içinde) | `3879BE18B84EB23D069FB3BF51767571E58536EDAF4F1B283B3A67A19C26D712` |
    | Yeni `migurdex.exe` | `DE9EA5634B16E07BC3B16B8C8C7EE07CB311FE489AF78E09AAD3E608C86E3E79` |
    | Yeni `migurdex-win-x64.zip` | `BC17A9FFA03D0B9BE5AA9004C6E6E94DB35C469442BA1515B3090FB6C96DB5AA` |
    | Yeni `api\migurdex_native.dll` | `91BA419E97AD5BC0F0CA1ADF9768CF60FDFAC72234AADE28AC02DB46C1B44627` |

    - Yeni dosyaların özetleri madde 12'deki `dist`/`install-candidate` özetleriyle birebir
      aynı; köke inen ikilinin paketten saptırılmadığı hash düzeyinde doğrulandı.
    - Kök klasörün yeni durumu: yeni `migurdex.exe`, `api\`, `migurdex-win-x64.zip`; ayrıca
      `backup-v1.9.2\`, `install-candidate\` ve `source\` klasörleri.
    - Geri alma yolu: `backup-v1.9.2\` içindeki dosyaları köke geri kopyalamak.
    - Ayrıntılı kayıt: [TEST_RESULTS.md](https://github.com/Nutaliaxd/Migurdex/blob/main/TEST_RESULTS.md) → bölüm 9.
14. **v1.10.1 rebase** (29.09.2026): Upstream `v1.10.1` (`4ecd7d7`) getirildi;
    `feature/download` bu sürüm üzerine rebase edildi. Rebase öncesi
    `backup/download-pre-v1101` yedek dalı alındı. Upstream'in üç commit'i alındı:
    `e4a32b4` (TurkAnime DB bağlantı güncellemesi), `914dfdd` (Anizm isimsiz fansub grubu
    düzeltmesi), `4ecd7d7` (AppImage zsync/AppRun + CLI encoding guard kaldırma ve `ReadKey`
    guard'ı). 21 feature commit'i yeni tabana taşındı; tek içerik çakışması
    `Migurdex.Cli/Program.cs`'te çıktı (ana özellik commit'i `be85d61`):
    - Çözümde dal tarafının TUI iptal mimarisi korundu: `TuiApplicationCancellation`, `tuiToken`
      (`IsApiOnlineAsync`/`TryStartApiDaemonAsync` iptal akışı), `activeNavigator`,
      `catch (OperationCanceledException)` ve `finally` bloğu.
    - Upstream'in `Console.IsInputRedirected` guard'lı + try/catch `Console.ReadKey(true)`
      versiyonu `!isOnline` bloğuna alındı; dal tarafındaki sade `ReadKey` çağrısı bırakıldı.
    - Dosyanın tepesindeki encoding guard kaldırma değişikliğine dokunulmadı; otomatik merge
      ile geldi (`Console.OutputEncoding`/`InputEncoding` koşulsuz set edilir).
    - Çözümün doğruluğu `git diff backup/download-pre-v1101:Migurdex.Cli/Program.cs` ile
      teyit edildi: fark, birebir upstream'in v1.10.0→v1.10.1 `Program.cs` değişiklikleri.
    - `README.md` otomatik merge edildi; TurkAnime DB bağlantısının `roxyrekt/turkanime-db`
      olduğu ve indirme dokümantasyonu bölüm sırasının korunduğu gözle doğrulandı.
    - Rebase sonrası portatif .NET SDK 10.0.401 ile `dotnet restore` başarılı; Release
      derlemesi 19 proje / 0 uyarı / 0 hata; offline test takımı **275/275** geçti.
    - Rebase tüm feature commit hash'lerini yeniden yazdı; v1.10.0 tabanlı eski hash'ler
      (`321fe38`…`9711773`) `backup/download-pre-v1101` dalında korunuyor.
15. **PR #1 merge ve `v1.10.2` release** (29.09.2026): `feature/download` dalı fork
    (`Nutaliaxd/Migurdex`) `main` dalına PR ile birleştirildi ve aynı gün etiketli sürüm
    yayınlandı. Bu noktadan sonra aktif dal **`main`**'dir; `feature/download` yalnızca PR
    başlığı/hash bağlamında geçer.

    | Olay | Değer |
    |---|---|
    | PR | [#1 — feat: add anime download support](https://github.com/Nutaliaxd/Migurdex/pull/1) (`feature/download` → `main`) |
    | PR durumu | **merged** (29.09.2026 10:05 UTC) |
    | Merge commit | `44f4010` — `Merge pull request #1 from Nutaliaxd/feature/download` |
    | PR istatistikleri | 22 commit, 40 dosya, +11.559 / −249 |
    | Tag | `v1.10.2` → `44f4010` (upstream taban: `v1.10.1` @ `4ecd7d7`) |
    | Release | [v1.10.2](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2) (29.09.2026 11:41 UTC) |
    | CI run | [Build and Release #36562344972](https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972) — sonuç **success** (11:32 UTC, `workflow_dispatch` @ `v1.10.2`) |

    Merge commit'in iki ebeveyni vardır: `4ecd7d7` (upstream `v1.10.1`) ve `c4c06c8`
    (dokümantasyon kaydının son commit'i). Yani `main` üzerindeki indirme özelliği, upstream
    `v1.10.1` üzerine **ff-merge olmayan** (merge commit'li) birleşmiştir; `feature/download`
    dalları `backup/download-pre-v110` ve `backup/download-pre-v1101` korunuyor.

    **Release asset'ları** (üretim: CI, 29.09.2026):

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

    - Yerel `build.ps1 -Publish` paketi (madde 12) ile CI paketi **aynı değildir**: CI, `-p:Version`
      ile sürüm damgası basar ve üç platformda derler; `migurdex-win-x64.zip` boyutu 61.605.017
      (yerel) ↔ 60.031.590 (CI) olarak farklıdır.
    - CI paketinde `migurdex --version` gerçek sürümü (`migurdex v1.10.2`) basar; yerel pakette
      `v0.0.0` görünür (bkz. `Güncelleme talimatı` → sürüm damgası notu).
    - Upstream `roxyrekt/Migurdex` `main` dalı hâlâ `v1.10.1` (`4ecd7d7`); fork'taki `v1.10.2`
      yalnızca bu çalışma dalını içerir. Upstream hedefi için aynı PR metni kullanılabilir
      ([PR_DESCRIPTION.md](https://github.com/Nutaliaxd/Migurdex/blob/main/PR_DESCRIPTION.md)).

### Upstream v1.10.0 notları

- Upstream, altyazı oynatma davranışını düzeltti: `MpvPlayerService` artık altyazı URL’lerini
  mpv’ye doğrudan vermek yerine geçici dosyalara indiriyor.
- Bizim `SubtitleDownloader` ile upstream’in altyazı indirme mantığı arasında fonksiyonel
  benzerlik var; ileride ortak yardımcıya çıkarılabilir.
- Upstream yeni `Deokwave` sağlayıcısını ekledi; sağlayıcı sayısı 13’ten 14’e çıktı.
- Bu bölümdeki "13 sağlayıcı" geçen paket/smoke kayıtları **v1.10.0 öncesi** koşumlardır
  (bkz. `Yapılan işlemler` → maddeler 9–10 ve `Canlı API smoke doğrulaması`); güncel sayı 14'tür.
- `DownloadSourceResolver` sağlayıcı listesini dinamik okuduğu için Deokwave için ek kod
  değişikliği gerekmez.

### Upstream v1.10.1 notları

- TurkAnime sağlayıcısının HuggingFace veritabanı bağlantısı `mdexturkanime/turkanime-db`
  yerine `roxyrekt/turkanime-db` oldu (`e4a32b4`); README'deki DB bağlantısı upstream ile
  hizalandı.
- Anizm sağlayıcısı isimsiz fansub gruplarını artık düzgün ele alıyor (`914dfdd`); indirme
  akışındaki `-g/--group` doğrulaması için ek değişiklik gerekmez — grup listesi API'den
  dinamik gelir.
- AppImage güncelleme akışına zsync bilgisi ve AppRun locale sarmalayıcısı eklendi
  (`4ecd7d7`); Windows/CLI indirme akışını etkilemez.
- `Program.cs`'te encoding atama guard'ları kaldırıldı (output/input encoding koşulsuz set
  edilir) ve `!isOnline` bloğundaki `ReadKey` çağrısı `Console.IsInputRedirected` guard'ı +
  try/catch ile korundu; dal tarafındaki TUI iptal mimarimizle birleştirildi (bkz.
  `Yapılan işlemler` → madde 14).
- Sağlayıcı (14) ve extractor (38) sayıları ile `/api/v1` sözleşmesi değişmedi; indirme
  özelliği için breaking change yok.

### Kullanılan .NET SDK ve test komutları

- SDK: .NET SDK **10.0.401** (10.0.4xx bandı). Kanıt: `%USERPROFILE%\.dotnet` altındaki
  `10.0.401.*` ilk-kullanım sentinelleri ve `obj\project.assets.json` içindeki
  `SdkAnalysisLevel: 10.0.400`. Hedef çerçeve `net10.0` (`Migurdex.Cli.csproj`,
  `Migurdex.Tests.csproj`, `build.ps1`); makinede .NET 10.0.5 runtime kurulu.
- Test çerçevesi: xUnit v3 — `xunit.v3` 3.2.2, `xunit.runner.visualstudio` 4.0.0,
  `Microsoft.NET.Test.Sdk` 18.9.0.
- Komutlar:

  ```
  dotnet test Migurdex.Tests\Migurdex.Tests.csproj    # 110 çalışan case (indirme/TUI kapsamı)
  dotnet build Migurdex.slnx -c Release -m           # tam çözüm derlemesi (19 proje)
  .\build.ps1 -Publish                               # dist paketi (yerel, sürüm damgasız)
  ```

  Not: `dotnet test` filtresiz çalıştırıldığında tüm takımı kapsar (`ExtractorSmokeTests`
  dahil, 275'in üzerinde case). Yukarıdaki **110**, indirme/TUI kapsamındaki 10 sınıfın
  toplamıdır; `--filter "FullyQualifiedName!~ExtractorSmokeTests"` ile tüm takım **275/275**
  geçer. Karşılaştırmalı kayıt: [TEST_RESULTS.md](https://github.com/Nutaliaxd/Migurdex/blob/main/TEST_RESULTS.md) bölüm 1 (8 sınıflık filtre = 103) ve
  bölüm 3 (10 sınıflık filtre = 110).

### HEAD ve commit listesi

**Tek doğruluk kaynağı `git log`'dur.** Aşağıdaki tablo ve listeler o anki durumun okunabilir
bir anlatımıdır; yeni commit'lerde elle güncellenmesi gerekmez ve yanıltmamalıdır. Doğrulama
komutları:

```bash
git log --oneline v1.10.1..main              # upstream tabandan bu işe ait commit'ler
git rev-list --count v1.10.1..main            # toplam commit sayısı (dinamik)
git log --oneline --graph --all -30          # dal/merge grafiği
git describe --tags                          # HEAD'in işaretli sürümü (v1.10.2)
git rev-list --count v1.10.1..main -- Migurdex.Cli/Services/Downloads/   # yalnız indirme kodu
```

Aşağıdaki kayıt **29.09.2026, `v1.10.2` yayını anındaki** durumudur ve PR #1 kapsamındaki
22 commit'i içerir.

- Aktif dal: **`main`** — HEAD `44f4010` (`Merge pull request #1 from Nutaliaxd/feature/download`).
- Merge commit `44f4010`; iki ebeveyni: `4ecd7d7` (upstream `v1.10.1`) ve `c4c06c8`
  (`docs: record v1.10.1 rebase and compatibility`, dalın son commit'i).
- Etiketli sürüm: `v1.10.2` → `44f4010`.
- Son kod commit'i: `00b2ee7` — `fix(downloader): report live yt-dlp HLS progress`
  (2026-09-27; v1.10.1 üzerine rebase sonrası karşılığı). Merge sonrasında da değişmedi.
- `v1.10.1..main` — **23 commit** (22 PR commit + merge `44f4010`):
  `be85d61` → `6c366c9` → `320302e` → `b3dd5d5` → `6135f91` →
  `ed397f0` → `f19e2a4` → `fc6747f` → `ba2665f` → `00b2ee7` →
  `38972ce` → `07d8110` → `7792f47` → `e3345a9` → `8226b95` →
  `e9d8276` → `d6774a7` → `4d56774` → `9a52271` → `8c58e2f` →
  `7ca27de` → `c4c06c8`, ardından merge commit `44f4010`.
  (Bu kayıt, dokümanın kendi düzenleme commit'lerinden sonra `main` üzerine eklenmiştir;
  güncel ve eksiksiz liste için `git log --oneline v1.10.1..main` kullanılmalıdır.)
- Upstream v1.10.1, üç yeni commit içerir:
  `e4a32b4` (`fix: update database link for TurkAnime provider`),
  `914dfdd` (`fix(anizm): handle unnamed anizm fansub groups`) ve
  `4ecd7d7` (`feat(appimage): zsync update info + AppRun locale wrapper`).
- Rebase öncesi hash'ler (v1.10.0 tabanlı `321fe38`…`9711773`)
  `backup/download-pre-v1101` dalında korunuyor.

## Değişiklik geçmişi

| Commit | Tarih | Konu | Kapsam |
|---|---|---|---|
| `be85d61` | 2026-09-26 03:17 +0300 | `feat: add anime download support` | İndirme çekirdeği, CLI/TUI/API entegrasyonu, config alanları, testler — 32 dosya (+8041/−117) |
| `6c366c9` | 2026-09-26 09:51 +0300 | `fix(tui): balance source selection markup` | TUI markup dengeleme, `FuzzyPrompt` kaçışlama, `TuiMarkupSafetyTests` — 5 dosya (+188/−20) |
| `320302e` | 2026-09-26 12:33 +0300 | `docs: document download feature and operations` | `DOWNLOAD.md` eklendi ve geniş kapsamlı teknik dokümantasyon yazıldı — 1 dosya (+629) |
| `b3dd5d5` | 2026-09-26 13:23 +0300 | `fix(tui): hide search filter on download result` | İndirme sonuç ekranında tek seçenekli prompt'un gereksiz `Ara:` filtre satırı kaldırıldı; `searchable: false` desteği ve `FuzzyPromptSearchableTests` eklendi — 4 dosya (+457/−132) |
| `6135f91` | 2026-09-26 13:52 +0300 | `docs: update download history for result prompt fix` | `DOWNLOAD.md` tarihçesi güncellendi — 1 dosya (+10/−8) |
| `ed397f0` | 2026-09-26 13:54 +0300 | `docs: clarify latest code commit in download guide` | `DOWNLOAD.md` meta tablosundaki commit bilgisi netleştirildi — 1 dosya (+1/−1) |
| `f19e2a4` | 2026-09-26 20:12 +0300 | `docs: record offline validation results` | `DOWNLOAD.md` ve `README.md` içine offline test sonuçları işlendi — 2 dosya (+53/−1) |
| `fc6747f` | 2026-09-26 20:57 +0300 | `docs: record live API smoke results` | `DOWNLOAD.md` ve `README.md` içine canlı API smoke sonuçları işlendi — 2 dosya (+55) |
| `ba2665f` | 2026-09-27 01:05 +0300 | `docs: add consolidated test results report` | `TEST_RESULTS.md` eklendi ve tüm canlı/offline doğrulamalar tek dosyada toplandı — 1 dosya (+182) |
| `00b2ee7` | 2026-09-27 01:27 +0300 | `fix(downloader): report live yt-dlp HLS progress` | yt-dlp argüman listesine `--progress` eklendi, HLS progress parsing testleri genişletildi, dokümanlar güncellendi — 5 dosya (+165/−19) |
| `38972ce` | 2026-09-27 02:10 +0300 | `docs: record v1.10.0 rebase and latest verification` | v1.10.0 rebase kaydı işlendi; `DOWNLOAD.md` meta/HEAD/değişiklik geçmişi, `README.md` doğrulama özeti, `TEST_RESULTS.md` v1.10.0 bölümü — 3 dosya (+89/−14) |
| `07d8110` | 2026-09-27 02:14 +0300 | `docs: document upstream v1.10.0 integration notes` | `DOWNLOAD.md` içine `Upstream v1.10.0 notları` bölümü eklendi — 1 dosya (+10) |
| `7792f47` | 2026-09-27 03:37 +0300 | `docs: record v1.10.0 packaged build` | v1.10.0 paket üretimi + SHA-256 doğrulaması işlendi; `DOWNLOAD.md` madde 12/meta/HEAD + `TEST_RESULTS.md` bölüm 7 + `README.md` paket özeti — 3 dosya (+118/−14) |
| `e3345a9` | 2026-09-27 05:38 +0300 | `docs: record Deokwave smoke result` | Deokwave upstream 403 bulgusu işlendi; `DOWNLOAD.md` bilinen sınırlar/HEAD/değişiklik geçmişi + `TEST_RESULTS.md` bölüm 8 — 2 dosya (+64/−5) |
| `8226b95` | 2026-09-27 06:32 +0300 | `docs: record root installation upgrade` | Kök kurulum yükseltmesi işlendi; `DOWNLOAD.md` madde 13/meta/HEAD + `TEST_RESULTS.md` bölüm 9 + `README.md` yerel sürüm notu — 3 dosya (+88/−6) |
| `e9d8276` | 2026-09-29 02:21 +0300 | `docs: add development log and PR description` | `DEVELOPMENT_LOG.md` ve `PR_DESCRIPTION.md` eklendi — 2 dosya (+1281) |
| `d6774a7` | 2026-09-29 02:42 +0300 | `docs: record PR link in development log` | `DEVELOPMENT_LOG.md` içine PR bağlantısı işlendi — 1 dosya (+2/−1) |
| `4d56774` | 2026-09-29 02:53 +0300 | `docs: update PR description with final diff stats` | `PR_DESCRIPTION.md` diff istatistikleri güncellendi — 1 dosya (+2/−2) |
| `9a52271` | 2026-09-29 03:12 +0300 | `docs: finalize PR commit count` | `PR_DESCRIPTION.md` commit sayısı netleştirildi — 1 dosya (+1/−1) |
| `8c58e2f` | 2026-09-29 03:20 +0300 | `docs: make PR description commit count dynamic` | `PR_DESCRIPTION.md` commit sayısı sabit yerine dinamik ifadeye çekildi — 1 dosya (+3/−2) |
| `7ca27de` | 2026-09-29 03:32 +0300 | `docs: round PR diff stats` | `PR_DESCRIPTION.md` diff istatistikleri yuvarlandı — 1 dosya (+1/−1) |
| `c4c06c8` | 2026-09-29 04:03 +0300 | `docs: record v1.10.1 rebase and compatibility` | v1.10.1 rebase kaydı işlendi; `DOWNLOAD.md`, `DEVELOPMENT_LOG.md`, `PR_DESCRIPTION.md`, `TEST_RESULTS.md` — 4 dosya |
| `44f4010` | 2026-09-29 13:05 +0300 | `Merge pull request #1 from Nutaliaxd/feature/download` | PR #1 `main`'e merge edildi; ebeveynler `4ecd7d7` (upstream `v1.10.1`) + `c4c06c8`. 22 commit, 40 dosya, +11.559/−249. Ardından `v1.10.2` etiketi ve release yayınlandı |

> **Bu tablo git geçmişinin bir anlatımıdır, tek doğruluk kaynağı değildir.** Commit listesi,
> sırası, tarihleri ve istatistikleri için `git log --oneline v1.10.1..main` ile
> `git show --stat <hash>` çalıştırılmalıdır; tablo güncel değilse kayıt olarak değerini
> yitirmez ama karar dayanağı olarak kullanılmamalıdır.

Gelecekteki commit'ler için satır formatı:

```
| `<kısa hash>` | YYYY-AA-GG hh:mm +0300 | `<type>(<scope>): <özet>` | <etkilenen dosyalar/bölümler> |
```

- `type`: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `perf`, `style`.
- `scope` örnekleri: `cli`, `tui`, `downloader`, `mp4`, `hls`, `subs`, `security`, `config`,
  `build`, `api`.
- Yeni commit bu tabloya eklendiğinde baştaki `Son güncelleme` ve `HEAD` satırları da aynı
  commit'le güncellenmelidir.

## Güncelleme talimatı

Bu doküman kodla birlikte güncellenmelidir; indirme koduna dokunan her değişiklikte aşağıdaki
eşlemeye göre ilgili bölüm revize edilir.

| Değişen kod | Revize edilecek bölüm |
|---|---|
| `DownloadCommand.cs` (bayraklar, yardım metni, JSON şeması, çıkış kodları) | `Genel akış`, `Kullanım örnekleri`, `Sık sorular` |
| `DownloadSourceResolver.cs` (aday seçimi, doğrulamalar) | `Genel akış`, `Kullanım örnekleri`, `Sık sorular` |
| `Mp4Downloader.cs`, `Mp4ResumeMetadata.cs` | `MP4 indirme ve resume`, `Bilinen sınırlar ve riskler` |
| `YtDlpHlsDownloader.cs`, `ExternalProcessRunner.cs` | `HLS indirme (yt-dlp)`, `Bilinen sınırlar ve riskler` |
| `SubtitleDownloader.cs` | `Altyazı indirme`, `Bilinen sınırlar ve riskler` |
| `DownloadPathBuilder.cs` (yol üretimi, sanitizasyon, byte bütçeleri) | `Kullanım örnekleri → Çıktı dosya/klasör düzeni`, `Güvenlik` |
| `DownloadTargetLock.cs` | `Güvenlik`, `Sık sorular`, `Bilinen sınırlar ve riskler` |
| `DownloadHttp.cs` (yönlendirme, allowlist'ler) | `Güvenlik` |
| `DownloadService.cs` (orkestrasyon, hata sanitizasyonu) | `Genel akış`, `Güvenlik` |
| `EpisodeSourcesView.cs`, `FuzzyPrompt.cs`, `TuiApplicationCancellation.cs` | `Kullanım örnekleri → TUI akışı`, `TUI markup güvenliği` |
| `CliConfig.cs` / `config.json` alanları | `Yapılandırma (config.json)` |
| `Migurdex.Tests\*` (test ekleme/çıkarma) | `Test kapsamı` (yeniden sayım yapılır) |
| `build.ps1`, csproj, paket düzeni | `Yapılan işlemler`, `Bilinen sınırlar ve riskler` |
| Merge / etiket / release | `Son güncelleme` tablosu, `Yapılan işlemler` → madde 15, `HEAD ve commit listesi`, `Değişiklik geçmişi` |

Test sayılarının doğrulanması:

1. `dotnet test Migurdex.Tests\Migurdex.Tests.csproj` çalıştırın; indirme/TUI kapsamındaki
   sınıfların geçen test sayısı, `Test kapsamı` tablosundaki "Çalışan case" toplamıyla
   (şu an **110**, 10 sınıf) eşleşmelidir (takım konu dışı sınıfları da içerdiğinden genel
   toplam daha yüksektir). [TEST_RESULTS.md](https://github.com/Nutaliaxd/Migurdex/blob/main/TEST_RESULTS.md) bölüm 1'deki **103** değeri 8 sınıflık filtrenin
   sonucudur (110 − 4 `DownloadPathBuilderTests` − 3 `ExternalProcessRunnerTests`); ikisi de
   doğrudur, kapsamı farklıdır.
2. `dotnet` olmadan statik çapraz kontrol: her test sınıfında `[Fact]` + `[Theory]` (metod
   sayısı) ve `[Theory]` başına düşen `[InlineData]` satırları (case sayısı) sayılır; örn.
   `Select-String -Path "Migurdex.Tests\Mp4DownloaderTests.cs" -Pattern "\[Fact\]"`.
3. Sayım değiştiyse `Test kapsamı` tablosunu, tablonun altındaki notu, baştaki `Testler` satırını
   ve `Yapılan işlemler` bölümündeki metrikleri birlikte güncelleyin.

Commit ve tarih güncelleme kuralı:

- **Tek doğruluk kaynağı `git log`'dur.** `Değişiklik geçmişi` tablosu ve
  `HEAD ve commit listesi` bölümü anlatıcı kayıttır; doğrulama gerektiğinde
  `git log --oneline v1.10.1..main`, `git show --stat <hash>` ve
  `git describe --tags` çalıştırılmalıdır. Tablo güncellenmezse yanlışa düşmez, eskir.
- Yeni bir commit eklenirken: tabloya bir satır düşülür ve baştaki `Son güncelleme` (dokümanın
  gerçek düzenleme günü) ile `HEAD` alanları aynı değişiklikle tazelenir.
- Merge commit'leri de kayda girer; ancak birleştirilen dalın commit'leri zaten varsa tekrarlanmaz
  (bkz. `44f4010` satırı).
- Tarihler commit'in author tarihinden (+0300) alınır; commit mesajları yukarıdaki conventional
  formatın dışına çıkmaz.
- Bu doküman `docs:` commit'leriyle sürülür. İlk kaydın rebase öncesi karması `73535cb`, rebase
  sonrası eşleniği `81335bc`'tir; PR #1 merge edildikten sonra dal `main`'dir, yeni kayıtlar
  `main` üzerine düşer.
