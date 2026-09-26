# İndirme Özelliği (Download)

API'nin çözdüğü doğrudan `MP4` ve `M3U8/HLS` kaynaklarını CLI'dan (`migurdex download`) veya
TUI'den (bölüm kaynak ekranında `İndir`) diske kaydeder. `Embed` ve `Unknown` kaynaklar indirilmez.
İndirme MPV açmaz, izleme geçmişine yazmaz ve tracker senkronu tetiklemez.

| Alan | Değer |
|---|---|
| Son güncelleme | 26 Eylül 2026 |
| Dal | `feature/download` (temel: `main` @ `444a49e`) |
| Son kod commit’i | `4cd2ac8bbb012fe3884eba044d999f528dda1f18` (`4cd2ac8` — fix(tui): hide search filter on download result) |
| Temel commit | `c3d307a` — feat: add anime download support |
| Testler | 80 test metodu / 109 çalışan case, 10 sınıf (bkz. `Test kapsamı`) |

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
| `Services/Downloads/YtDlpHlsDownloader.cs` | 601 | HLS indirme, yt-dlp sarmalayıcı |

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
  --no-config --no-playlist --no-part --newline
  --batch-file <job/.migurdex-input.txt>   # URL enjeksiyonuna kapalı geçiş
  --paths <job> --output media.%(ext)s
  --print after_move:filepath
  --add-header <ad>: <değer>                # yalnız allowlist header'lar
  ```

- Her indirme geçici `.migurdex-job-<guid>` dizininde çalışır, bitince silinir.
- Yeniden deneme: varsayılan 3 deneme (1-5 aralığına sabitlenir), denemeler arası 1 saniye
  (`HlsDownloadOptions.MaxAttempts`, `RetryDelay`).
- Çıktı dosyası yt-dlp'nin `--print after_move:filepath` satırından veya job dizininden bulunur;
  yalnız medya uzantıları kabul edilir (`.mp4`, `.mkv`, `.webm`, `.ts`, ...). Gerçek medya
  uzantısı yt-dlp çıktısına göre korunur; HTML/JSON/altyazı görünümlü dosyalar reddedilir.
- İlerleme, yt-dlp'nin stdout satırlarındaki yüzde (`%`) ve `MiB/x MiB` kalıplarından ayrıştırılır.

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
| `HlsDownloaderTests` | 7 | 0 | 0 | 7 | 7 |
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
  `--print after_move:filepath` davranışı etkilenebilir; yt-dlp güncellemelerinden sonra
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

## Yapılan işlemler

Bu bölüm, indirme özelliğinin `feature/download` dalında nasıl hazırlandığının çalışma
kayıdıdır.

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
   `ShowDownloadResultAsync`) eklendi; `TuiApplicationCancellation` genişletildi.
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
   `Test kapsamı`).
9. **Derleme ve paket üretimi**: `.\build.ps1 -Publish` ile Release paketi üretildi —
   `dist\migurdex.exe` (win-x64, self-contained, single-file, trimmed), `dist\api\`
   (self-contained API + 13 provider eklentisi + `migurdex_native.dll` + `e_sqlite3.dll`),
   `dist\migurdex-win-x64.zip` (26.09.2026 09:53–09:54).
10. **install-candidate kopyası**: `dist` içeriği `..\migu\install-candidate\` klasörüne
    kopyalandı (`migurdex.exe`, `api\`, `migurdex-win-x64.zip`). Adaydaki API 26.09.2026
    10:21'de açılıp 13 eklentinin tamamını yükledi (`api\logs\Migurdex-20260926.log`).

### Kullanılan .NET SDK ve test komutları

- SDK: .NET SDK **10.0.401** (10.0.4xx bandı). Kanıt: `%USERPROFILE%\.dotnet` altındaki
  `10.0.401.*` ilk-kullanım sentinelleri ve `obj\project.assets.json` içindeki
  `SdkAnalysisLevel: 10.0.400`. Hedef çerçeve `net10.0` (`Migurdex.Cli.csproj`,
  `Migurdex.Tests.csproj`, `build.ps1`); makinede .NET 10.0.5 runtime kurulu.
- Test çerçevesi: xUnit v3 — `xunit.v3` 3.2.2, `xunit.runner.visualstudio` 4.0.0,
  `Microsoft.NET.Test.Sdk` 18.9.0.
- Komutlar:

  ```
  dotnet test Migurdex.Tests\Migurdex.Tests.csproj    # 91 çalışan case
  dotnet build Migurdex.slnx -c Release -m           # tam çözüm derlemesi
  .\build.ps1 -Publish                               # dist paketi
  ```

### HEAD ve commit listesi

- Dal: `feature/download` (temel: `main` @ `444a49e`).
- HEAD: `4cd2ac8bbb012fe3884eba044d999f528dda1f18` — `fix(tui): hide search filter on download result`
  (2026-09-26 13:23:27 +0300).
- `main..feature/download` (4 commit): `c3d307a` → `e9e01e9` → `73535cb` → `4cd2ac8`.
- Bu doküman (`DOWNLOAD.md`) `73535cb` ile dal üzerinde commit'lendi; `4cd2ac8` ile güncellendi.

## Değişiklik geçmişi

| Commit | Tarih | Konu | Kapsam |
|---|---|---|---|
| `c3d307a` | 2026-09-26 03:17 +0300 | `feat: add anime download support` | İndirme çekirdeği, CLI/TUI/API entegrasyonu, config alanları, testler — 32 dosya (+8038/−114) |
| `e9e01e9` | 2026-09-26 09:51 +0300 | `fix(tui): balance source selection markup` | TUI markup dengeleme, `FuzzyPrompt` kaçışlama, `TuiMarkupSafetyTests` — 5 dosya (+188/−20) |
| `4cd2ac8` | 2026-09-26 13:23 +0300 | `fix(tui): hide search filter on download result` | İndirme sonuç ekranında tek seçenekli prompt'un gereksiz `Ara:` filtre satırı kaldırıldı; `searchable: false` desteği ve `FuzzyPromptSearchableTests` eklendi — 4 dosya (+457/−132) |

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

Test sayılarının doğrulanması:

1. `dotnet test Migurdex.Tests\Migurdex.Tests.csproj` çalıştırın; indirme/TUI kapsamındaki
   sınıfların geçen test sayısı, `Test kapsamı` tablosundaki "Çalışan case" toplamıyla
   (şu an 109) eşleşmelidir (takım konu dışı sınıfları da içerdiğinden genel toplam daha
   yüksektir).
2. `dotnet` olmadan statik çapraz kontrol: her test sınıfında `[Fact]` + `[Theory]` (metod
   sayısı) ve `[Theory]` başına düşen `[InlineData]` satırları (case sayısı) sayılır; örn.
   `Select-String -Path "Migurdex.Tests\Mp4DownloaderTests.cs" -Pattern "\[Fact\]"`.
3. Sayım değiştiyse `Test kapsamı` tablosunu, tablonun altındaki notu, baştaki `Testler` satırını
   ve `Yapılan işlemler` bölümündeki metrikleri birlikte güncelleyin.

Commit ve tarih güncelleme kuralı:

- Her yeni commit `Değişiklik geçmişi` tablosuna bir satır ekler; baştaki `Son güncelleme`
  (dokümanın gerçek düzenleme günü) ve `HEAD` (tam hash + kısa hash + konu) alanları aynı
  değişiklikle güncellenir.
- Tarihler commit'in author tarihinden (+0300) alınır; commit mesajları yukarıdaki conventional
  formatın dışına çıkmaz.
- Bu doküman, feature dalının parçası olarak (`docs:` commit'i ile) sürülmelidir; dal üzerinde
  `73535cb` ile commit'lenmiştir.
