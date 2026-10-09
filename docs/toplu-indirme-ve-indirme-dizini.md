# Toplu indirme ve indirme dizini

Bu belge, Migurdex'e eklenen **toplu indirme** (birden fazla bölümü tek işlemde indirme) ve
**indirme dizini** davranışının ne olduğunu, neden bu şekilde yapıldığını ve teknik olarak nasıl
çalıştığını anlatır. Paralel yürütmenin ayrıntıları için: [paralel-indirme.md](paralel-indirme.md).

Kapsam dışı bırakılanlar: tek bölüm indirme akışı (`EpisodeSourcesView`, `migurdex download -e`),
oynatma, sağlayıcı/ayar sırası ve mevcut sıralı MP4 parçalama mantığı **değiştirilmedi**.

---

## 1. Ne eklendi

| Yetenek | Nerede | Nasıl |
|---|---|---|
| Birden fazla bölümü seçip tek işlemde indirme | TUI | Anime detay ekranındaki `⤓ Toplu indir...` girdisi |
| Birden fazla bölümü tek komutla indirme | CLI | `migurdex download "<sorgu>" --episodes 1-12` |
| İndirme hedef dizinini ayarlama, kalıcı saklama | TUI Ayarlar + `config.json` | `İndirme Dizini` ayarı, `DownloadDirectory` anahtarı |
| Hedef dizini doğrulama (oluşturma, boş değer reddi, yola göre geri düşme) | TUI Ayarlar | `SettingsView` içindeki `DownloadDirectory` seçimi |
| Yazılan dizinin genişletilmesi (`~`, `%DEĞİŞKEN%`, tırnak temizliği) | Config | `CliConfig.NormalizeDownloadDirectory` |

Toplu indirme **yeni bir indirici yazmaz**: kuyruk, mevcut `IDownloadService`'i ve mevcut
kaynak-seçme kurallarını kullanır. Değişen tek şey, işlerin kaç tanesinin aynı anda çalışacağı ve
sonuçların nasıl raporlandığıdır.

---

## 2. Neden bu yaklaşım

1. **Tek indirme yolu korunmalıydı.** `EpisodeSourcesView.DownloadSourceAsync` ve
   `DownloadCommand.ExecuteAsync` içindeki mevcut akış (aday sırası, `.part` devam, altyazı
   davranışı, iptal) test edilmiş ve çalışıyor. Bu yüzden toplu indirme, bu akışı *yeniden yazan*
   değil, onu *çağıran* ayrı bir katman olarak eklendi.
2. **Paralellik bir politika, indiricinin işi değil.** Hangi bölümün ne zaman başlayacağı bir
   zamanlama kararıdır; protokol/parça mantığı ise `Mp4Downloader` / `YtDlpHlsDownloader` içinde
   kalır. Bu ayrım sayesinde paralel mod kapatıldığında çağrılan kod yolu sıralı indirmenin
   aynısıdır.
3. **Kısmi başarı normaldir.** Bir bölümün kaynağı ölürse diğerleri durmamalı. Kuyruk bu yüzden
   "ya hep ya hiç" değil; her iş kendi `DownloadResult`'ını üretir ve hata yutulur, raporlanır.
4. **Ayar yüzeyi az tutuldu.** İki anahtar eklendi (`DownloadParallelEnabled`,
   `DownloadConcurrency`); sunucuya/indiriciye özel ek anahtar yok. Mevcut config dosyaları
   yeni alanlar olmadan da sorunsuz yüklenir (bkz. §6).

---

## 3. Dokunulan dosyalar

### Yeni dosyalar

| Dosya | Sorumluluk |
|---|---|
| `Migurdex.Cli/Services/Downloads/DownloadQueueService.cs` | Kuyruk motoru: `DownloadQueueItem`, `DownloadQueueItemState`, `DownloadQueueProgress`, `DownloadQueueItemResult`, `DownloadQueueSummary`, `DownloadQueueOptions`, `IDownloadQueueService`, `DownloadQueueService`, `DownloadQueueWork` |
| `Migurdex.Cli/Services/BatchDownloadPlan.cs` | Toplu indirmenin **karar mantığı**, Spectre'den bağımsız: `OrderEpisodes` (sezon kapsamı), `BuildLabels`, `FromSelection`, `DescribeEpisode`, `DescribeConcurrency`, `BuildItem` (aday zinciri + `DownloadQueueWork.ForCandidateChain`), `SummaryTitle`, `DescribeCounts`, `BuildSummaryRows`, `BatchSummaryRow`. Hem `BatchDownloadView` hem `DownloadCommand.ExecuteBatchAsync` bunu kullanır; iki yüzey aynı kararı verir |
| `Migurdex.Cli/Services/EpisodeSelectionSpec.cs` | CLI `--episodes` metnini ayrıştırma ve bölümlere çözme (`1,2,3`, `1-12`, `all`, `*`) |
| `Migurdex.Cli/Tui/EpisodeMultiSelectPrompt.cs` | Çoklu seçim ekranı. Durum ve tuş mantığı `EpisodeMultiSelectState` / `EpisodeMultiSelectKeys` / `MultiSelectOutcome` olarak ayrıldı; Spectre kısmı ince bir çizicidir. Tuşlar: `↑`/`↓`, `Space`, `A`, `N`, `Enter`, `Esc` |
| `Migurdex.Cli/Tui/Views/BatchDownloadView.cs` | Toplu indirme ekranı: seçim → onay → canlı ilerleme → sonuç listesi |
| `Migurdex.Tests/DownloadQueueServiceTests.cs` | Kuyruk motoru testleri (sıra, eşzamanlılık, kısmi hata, iptal, sıkıştırma) |
| `Migurdex.Tests/BatchDownloadFlowTests.cs` | Uçtan uca toplu indirme testi (gerçek `DownloadService` + gerçek MP4 indirici + disk) |
| `Migurdex.Tests/DownloadSettingsTests.cs` | Ayar varsayılanları, sıkıştırma, dizin normalizasyonu ve kalıcılık biçimi |
| `Migurdex.Tests/EpisodeSelectionSpecTests.cs` | `--episodes` söz dizimi ve bölüm çözümleme testleri |
| `Migurdex.Tests/EpisodeMultiSelectStateTests.cs` | Seçim durumu ve tuş eşlemesi testleri (10 test): imleç sarması, ön seçim, aralık dışı indeks reddi, boş liste, negatif sayı reddi; `Space` yalnız imleçteki satırı işaretler, `A`/`N` tümünü temizler/seçer, `Enter` seçim yoksa onaylamaz, `Esc` iptal eder, oklar listeden çıkmaz |

### Değiştirilen dosyalar

| Dosya | Değişiklik |
|---|---|
| `Migurdex.Cli/Configuration/CliConfig.cs` | `DownloadParallelEnabled`, `DownloadConcurrency` (+ `Min/Max/DefaultDownloadConcurrency`, `ClampConcurrency`), `NormalizeDownloadDirectory`; `DownloadDirectory` setter'ı artık normalizasyondan geçiyor |
| `Migurdex.Cli/Services/Downloads/DownloadServiceCollectionExtensions.cs` | `IDownloadQueueService` DI kaydı |
| `Migurdex.Cli/Tui/Views/SettingsView.cs` | `Paralel İndirme`, `Eşzamanlı İndirme` girdileri; `İndirme Dizini` doğrulaması ve dizin oluşturma |
| `Migurdex.Cli/Tui/Views/AnimeDetailsView.cs` | Çok bölümlü anlamelerde `⤓ Toplu indir...` girdisi; `BatchDownloadView.SetTarget`'e **seçili sezonu** geçirir (çok sezonda `activeSeason`, tek sezonda `null`) |
| `Migurdex.Cli/Program.cs` | `BatchDownloadView` DI kaydı |
| `Migurdex.Cli/Services/DownloadCommand.cs` | `--episodes` bayrağı ve ayrıştırma, `ExecuteBatchAsync`, `WriteBatchResults`, `ResolveBatchExitCode`, `StderrQueueReporter`, `ConsoleDownloadProgress(prefix)`, `UsageError`, yardım metni |
| `Migurdex.Cli/Services/NonInteractiveCommand.cs` | Yardım metinlerinde `--episodes` |

---

## 4. TUI akışı

```
Arama → Sonuç → Anime detayı
      ├── 01 Bölüm …                (mevcut: bölüm seçimi)
      ├── ⤓ Toplu indir...          (yeni)
      └── Geri
```

1. `AnimeDetailsView`, çok bölümlü anlamelerde `Toplu İndir...` girdisini listeye ekler.
   Seçilirse `BatchDownloadView.SetTarget(provider, animeId, title, details.Episodes, season)`
   çağrılır ve görünüm `navigator.Push(...)` ile açılır. `season`, çok sezonlu bir animede
   **o an seçili olan sezon**dur (`isMultiSeason ? activeSeason : null`); tek sezonlu animede
   `null` geçilir.
2. `BatchDownloadView` bu sezonu `BatchDownloadPlan.OrderEpisodes` ile uygular: çoklu seçim
   listesi **yalnızca seçilen sezonun** bölümlerini gösterir. (Düzeltilen hata: sezon seçildiği
   halde liste tüm sezonları gösteriyordu; başlıktaki sayaç artık örn. `0 / 2 seçili` der, tüm
   animenin toplamını değil.) Bölümler `S01E04 - Bölüm adı` biçiminde etiketlenir ve
   `EpisodeMultiSelectPrompt` ile sunulur. Ekranın başlığında hedef dizin, seçili sezon ve
   eşzamanlılık yazar; kullanıcı ne indireceğini **önceden** görür.
3. Seçim `_lastSelection` içinde tutulur: aynı anlamenin toplu indirme ekranına geri dönülürse
   işaretler korunur (görünüm kaybolunca sıfırlanır).
4. Onay ekranı, hedef dizini, altyazı/üzerine yaz/kaldığı yerden ayarlarını ve eşzamanlılığı
   özetler. `Hayır` denirse hiçbir şey indirilmez.
5. İndirme sırasında tek bir canlı tablo çizilir: `✓ tamam`, `▶ çalışıyor (%42, 3.2 MiB/s)`,
   `· sırada`, `✗ başarısız`, `… iptal`. `Esc`, kuyruğu iptal eder; **tamamlanmış dosyalar
   silinmez**.
6. Sonuç ekranı bölüm bölüm listelenir (`✓ S01E01 → C:\...\S01E01 - ....mp4`, `✗ S01E03 • hata`);
   `Enter`/`Esc` ile detay ekranına dönülür.

Toplu indirme, hedef yolu `CliConfig.DownloadDirectory`'den okur — CLI'daki `-o` gibi bir "bu seferlik
geçersiz kılma" davranışı TUI'de bilinçli olarak yoktur ki indirilen dosyalar her zaman kullanıcının
kalıcı olarak seçtiği dizine gitsin.

---

## 5. CLI akışı

```bash
# Tek bölüm (değişmedi)
migurdex download "one piece" -e 12

# Toplu: liste, aralık, karışık, hepsi
migurdex download "one piece" --episodes 1,2,3
migurdex download "one piece" --episodes 1-12
migurdex download "one piece" --episodes 1-3,7,9-10
migurdex download "one piece" --episodes 1-3 -s 2      # çok sezonlu: -s şart
migurdex download "one piece" --episodes all -s 2      # yalnız 2. sezon
migurdex download "one piece" --episodes all           # tüm sezonlar

# Toplu + çıktı dizini + JSON
migurdex download "one piece" --episodes 1-6 -o D:\Anime --json
```

* `--episodes` ile `-e` birlikte verilemez: kullanım hatası (çıkış kodu `2`).
* **Sezon kuralı** (bölüm numaraları sezona göre tekrar eder):
  * Tek sezonlu anime: `--episodes 1-12` doğrudan çalışır.
  * Çok sezonlu anime + numara listesi (`1-3`, `1,2`): `-s <sezon>` **zorunludur**. Verilmezse
    kullanım hatasıdır (çıkış kodu `2`) ve mesaj `-s <sezon>` ya da `--episodes all` ister.
  * Çok sezonlu anime + `all` (veya `*`, `tümü`, `hepsi`): `-s` verilirse o sezonun tamamı,
    verilmezse **tüm sezonlardaki** bütün bölümler.
* Seçim, sağlayıcıdan gelen bölüm listesine uygulanır. Sıra her zaman sezon → bölüm numarasıdır
  ve aynı numara iki kez dönerse tek kez indirilir.
* Her bölüm için kaynak çözümleme **işin içinde** yapılır (`DownloadCandidateResolver.ResolveAsync`),
  yani 12 bölümlük bir kuyruk başlamadan önce 12 kez tarama yapılmaz; tarama, o iş sıraya geldiğinde
  ve yer varsa yapılır.
* Aday zinciri tek indirmedeki ile aynıdır: `DownloadSourceResolver.MaxCandidates = 3`, sırayla
  denenir, video tamamlanıp altyazı iptal edilirse zincir durur ve sonuç korunur.
* `--debug`, indirmeden her bölüm için çözülen ilk adayı yazar (URL/header/token yazmaz).

### Çıktı ve çıkış kodları

| Kod | Anlam |
|---:|---|
| `0` | Tüm seçilen bölümler indirildi |
| `1` | En az bir bölüm başarısız **veya** iptal edildi (kısmi sonuçlar stdout/stderr'de listelenir) |
| `2` | Kullanım hatası (bozuk `--episodes`, `-e` çakışması) |
| `3` | Videolar tamamlandı, altyazı aşaması iptal edildi (tek indirmedeki kuralla aynı) |

`--json` ile stdout yalnız JSON'dur; canlı ilerleme ve uyarılar stderr'e gider:

```json
{
  "success": true,
  "cancelled": false,
  "batch": true,
  "total": 3,
  "succeeded": 3,
  "failed": 0,
  "cancelledCount": 0,
  "items": [
    {
      "index": 1,
      "episode": { "id": "…", "title": "Bölüm 1", "season": 1, "number": 1 },
      "state": "completed",
      "mediaPath": "C:\\Users\\…\\Migurdex\\One Piece\\S01E01 - Bölüm 1.mp4",
      "subtitlePaths": [],
      "warnings": [],
      "error": null
    }
  ]
}
```

---

## 6. İndirme dizini: ayar ve kalıcılık

### Ayar yüzeyi

| Katman | Anahtar / girdi | Varsayılan |
|---|---|---|
| `config.json` | `DownloadDirectory` | `<kullanıcı profili>/Downloads/Migurdex` (`CliConfig.DefaultDownloadDirectory`) |
| TUI Ayarlar → İndirme | `İndirme Dizini` | aynı |

Kalıcılık mevcut mekanizmayla yapılır, yeni bir saklama katmanı eklenmedi:

* `ConfigurationService.Save()` → `JsonSerializer.Serialize(CliConfig)` → `config.json.tmp` →
  `File.Move(tmp, config.json, true)`. Yani yazma atomiktir; yarım yazılmış bir config dosyası
  oluşmaz.
* Dosya konumu `~/.config/migurdex/config.json` (`%USERPROFILE%\.config\migurdex\config.json`).
  Yeni bir `migurdex` çalıştırması `ConfigurationService`'in kurucusunda bu dosyayı okur, dolayısıyla
  dizin seçimi sonraki oturumlarda korunur.
* Bozuk/eksik config: `JsonException` / `IOException` yakalanır, dosya
  `config.json.corrupt-<zaman>.bak` olarak kopyalanır ve varsayılanlar kullanılır. Eski config
  dosyalarında yeni alanlar olmadığı için varsayılanlar devreye girer.

### Girdi normalizasyonu

`CliConfig.DownloadDirectory` setter'ı `NormalizeDownloadDirectory` üzerinden geçer:

1. baş/son boşluk ve tırnaklar atılır (`"C:\Anime İndir"` → `C:\Anime İndir`),
2. `~/Anime` kullanıcı profilinin altına açılır,
3. `%DEĞİŞKEN%` biçimindeki ortam değişkenleri genişletilir,
4. boş sonuç `DefaultDownloadDirectory`'ye düşer (yani dizin asla boş kalmaz).

TUI'de dizin seçilirken ayrıca `Directory.CreateDirectory` çağrılır; yol geçersizse (izin, geçersiz
karakter, çok uzun yol) ayar **varsayılan dizine geri döner** ve kullanıcıya bildirilir — böylece
kullanılamaz bir hedef config'e yazılmaz.

Toplu indirme, `DownloadPathBuilder` üzerinden `<hedef>/<anime>/SxxEyy - <bölüm>.<uzantı>` düzenini
kullanır; yani toplu ve tek indirme aynı dosya adlandırmasını ve aynı yol bütçesi/güvenlik
kontrollerini (Windows'ta 240 bayt sınırı, `..` kaçışı reddi, sakıncalı karakter temizliği) paylaşır.

---

## 7. Hata ve kısmi başarı davranışı

| Durum | Davranış |
|---|---|
| Tek bölüm başarısız | O iş `Failed` olur, kuyruk devam eder; diğer bölümler etkilenmez |
| Beklenmeyen istisna | `DownloadQueueService.RunItemAsync` yakalar, işi `Failed` yapar, mesajı saklar; kuyruk çökmez |
| Kaynak tarama zaman aşımı | İş `Failed`; hata metni `DownloadAutoSelectTimeoutSeconds` değerini içerir |
| Kullanıcı iptali (TUI `Esc` / CLI `Ctrl+C`) | Çalışan işler iptal edilir, başlamayanlar `Cancelled` işaretlenir; **tamamlanmış dosyalar korunur** |
| Video bitti, altyazı iptal edildi | İş `Completed` sayılır, `AnySubtitleCancelled` true olur (CLI'de çıkış kodu `3`) |
| Aynı hedefe eşzamanlı yazma | `DownloadTargetLock` hedef başına dosya kilidi kullanır; çakışan ikinci iş `ConcurrentDownloadException` alır, altyazılar atlanır ya da iş başarısız olur — veri karışması olmaz |

Kısmi başarı bilinçli olarak "başarı" sayılmaz: `AllSucceeded` yalnızca tüm işler tamamlandıysa
true olur ve CLI bu durumda `1` döner.

---

## 8. Nasıl doğrulandı

Sayılar `dotnet test Migurdex.slnx` ile ölçüldü (`.NET SDK 10.0.401`, Python 3.14).

| Kontrol | Komut | Sonuç |
|---|---|---|
| Derleme | `dotnet build Migurdex.slnx` | 0 uyarı, 0 hata |
| Tüm testler | `dotnet test` | 446 test: **436 başarılı**, 10 başarısız |
| Extractor smoke testleri | `--filter FullyQualifiedName~ExtractorSmokeTests` (35 test) | **26 başarılı / 9 başarısız**; ayrı bir koşumda 25/10 (bir örnek canlı bağlantıya bağlı olduğu için oynak). Kalan hataların tamamı canlı hoster örnek bağlantılarının `0 results` dönmesi: Abyss, GoogleDrive, HexUpload, MixDrop, Rumble, Sibnet, Streamcash, Turkanime, Vidmoly |
| Extractor dışı tüm testler | `--filter "FullyQualifiedName!~ExtractorSmokeTests"` | 411 test, **0 başarısız** |
| Bu işin testleri | `--filter "DownloadQueueServiceTests\|DownloadSettingsTests\|EpisodeSelectionSpecTests\|BatchDownloadFlowTests\|EpisodeMultiSelectStateTests"` | **73/73 başarılı** |
| Gerçek yığın, eşzamanlılık | `BatchDownloadFlowTests.BatchDownload_WritesEveryEpisodeAndUsesTheConfiguredConcurrency` | Gerçek `DownloadService` + gerçek MP4 indirici ile 3 bölüm diske yazıldı; HTTP katmanında ölçülen eşzamanlı istek sayısı **2** (paralel) / **1** (paralel kapalı) |
| Kısmi hata | `BatchDownloadFlowTests.BatchDownload_OneBrokenEpisode_LeavesTheOthersComplete` | 3 bölümden 1'i 500 döndü: 2 dosya diskte, başarısız bölümün dosyası yok |
| Dizin kalıcılığı (uçtan uca) | `ConfigurationService` ile yazan→yeni örnek okuyan geçici konsol programı | `DownloadDirectory`, `DownloadParallelEnabled`, `DownloadConcurrency` yazıldı ve yeni okumada birebir geldi; elle `99` yazılan config `8` olarak okundu (test sonrası kullanıcının config dosyası geri yüklendi) |
| CLI yüzeyi | `migurdex download "x" --episodes 12-3`, `-e 1 --episodes 1-3`, `--help` | Sırasıyla `2`, `2`, `0` çıkış kodu ve beklenen mesajlar |

> **Düzeltme (06.10.2026).** Bu belgenin önceki sürümünde “446 test: **411 başarılı**, 35 başarısız” yazıyordu.
> Ölçüm ilerledi: o 35 hatanın 25'i **eksik native kütüphane** yüzündendi, kod hatası değildi.
> `migurdex_native.dll`, `Migurdex.Api/bin/Debug/net10.0/` altına konunca 25 test geçti; tam paket
> 436/10 oldu. Kalan 9-10 hata ölü **canlı** örnek bağlantılarıdır (`0 results`), yani dış dünyanın değişmesi.
> Yani “bu 35 hata temiz `HEAD` worktree'sinde de aynıydı” ıfadesi doğruydu ama **nedeni yanlış** yazılmıştı:
> sorun kodda değil, derleme çıktısında. Ayrıntı: §9 → “Yerel geliştirmede API'yi çalıştırma”.

### Yerel sahte API ile uçtan uca — CLI

Birim testleri parçaları ayrı ayrı kapsar; **kullanıcının dokunduğu yüzeyin** bütününü
(girdi → ağ → disk → çıkış kodu → `--json`) ayrıca gerçek derlenmiş ikili üzerinden ölçtük.
Sahte API, `ApiClientService`'in çağırdığı rotaları taklit eder: `/health`,
`/api/v1/providers`, `/api/v1/extractors`, `/api/v1/anime/search` (JSON + SSE),
`/api/v1/anime/{provider}/{animeId}` detay/grup/kaynak ve `/media/*.mp4`. Medya 128 KiB
parçalarla, aralarda gecikmeyle servis edilir; bu sayede eşzamanlı istek sayısı gerçekten
gözlemlenebilir. Sahte API **depo ağacının dışında** tutulur (`C:\Users\naton\migurdex-stub\`),
yani ürüne girmez.

| Kontrol | Girdi | Ölçülen sonuç |
|---|---|---|
| Tek sezon, aralık | `--episodes 1-3 --json` | **3 dosya** diskte (`S01E01 - Bölüm 1.mp4` …), çıkış kodu `0`, JSON `total=3, succeeded=3, success=true` |
| Çok sezonlu + `-s` | `-s 2 --episodes 1-2` | `S02E01`/`S02E02` yazıldı |
| Çok sezonlu + numara, `-s` yok | `--episodes 1-3` | çıkış kodu **2**, mesajda `-s` / `all` geçiyor, **0 dosya** |
| `all` | `--episodes all` | tüm sezonlardan **5 dosya** |
| Kısmi hata | bozuk bölüm içeren anime | çıkış kodu `1`, **2 dosya**, JSON `failed=1, succeeded=2, success=false` |
| Paralel kapalı | `DownloadParallelEnabled=false` | medya isteklerinde tepe eşzamanlılık **1**, stderr `sıralı (paralel kapalı)`, 3 dosya |
| Paralel, artırılmış | `DownloadConcurrency=4` | tepe eşzamanlılık **3**, stderr `4 eşzamanlı (paralel)` |
| Sıkıştırma | `DownloadConcurrency=99` | stderr `8 eşzamanlı (paralel)` (üst sınır), indirme yine tamamlandı |

Sonuç: **29/29 kontrol yeşil**. Bu satırlardan "çok sezonlu + numara, `-s` yok", düzeltilen
sezon/`all` uyumsuzluğunu; paralel/eşzamanlı satırları ise düzeltilen sıkıştırma sahipliğini
kanıtlar.

### Yerel sahte API ile uçtan uca — TUI (gerçek sözde terminal)

TUI de gerçek bir sözde terminal (ConPTY) üzerinden sürüldü: tuşlar konsol tuşu olarak
gönderilir ve ekran karakterleri okunur. (`winpty` piped stdin ile çalışmadığı için
`pywinpty.PtyProcess` kullanıldı.)

| Kontrol | Ölçülen sonuç |
|---|---|
| Çok sezonlu anime → sezon seç → toplu indir | **23/23**: sezon seçici `1.`/`2. Sezon` listeler; başlık `Toplu indirme • <başlık>`; **liste seçilen sezonla sınırlı** (`0 / 2 seçili`, `0 / 5` değil); `Space` işaretler, `A` tümünü seçer; onay ekranı; özet `tamam: N`; diskte doğru dosyalar (tam 128 KiB); tepe eşzamanlılık ≥ 2; çıkış `0` |
| Tek sezonlu anime → toplu indir | **21/21**: sezon seçici atlanır, liste doğrudan gelir; dosyalar ve sonuç aynı |
| Ayarlar → davranış | **36/36**: Ayarlar kalıcı dizini ve `2 bölüm` / `Paralel Açık` gösterir; değer `4`'e çıkarılıp kaydedilince `config.json` `DownloadConcurrency=4` olur ve toplu indirme ekranı `4 eşzamanlı (paralel)` yazar (**ölçülen tepe: 3**); `Paralel İndirme` kapatılıp kaydedilince ekran `sıralı (paralel kapalı)` yazar ve **ölçülen tepe: 1**; dizin korunur |

TUI koşum takımı da **depo dışında** yaşar (`C:\Users\naton\migurdex-stub\tui_*.py`), yani
depoya/CI'ya dahil değildir; kullanıcının `config.json`'u her koşumda yedeklenip geri yüklenir.

Bu iki koşum, önceki sürümde "TUI ekranlarının kendisi yalnızca kod incelemesiyle doğrulandı"
denen yüzeyi gerçek çalıştırmayla kapsar. Sezon/`all` düzeltmesinin TUI tarafı (girilen sezonun
listelenmesi) ve CLI tarafı (çok sezonlu numara listesinin `-s` istemesi) ayrı ayrı kanıtlanmıştır.

---

## 9. Koşum takımı, tuzaklar ve düzeltme günlüğü

Bu bölüm, §8'deki kanıtların **nasıl yeniden üretileceğini** ve bu çalışmada bulunan tuzakları yazar.

### Depo dışı koşum takımı

Konum: `C:\Users\naton\migurdex-stub\`. **Depo ağacına girmez ve CI'da çalışmaz** — çalıştırdığı için
gerçek ikiliyi (`Migurdex.Cli/bin/Debug/net10.0/migurdex.exe`) ve kullanıcının `config.json`'unu geçici olarak
değiştirir (her koşumda yedekler ve sonunda geri yükler).

| Dosya | İş | Çalıştırma |
|---|---|---|
| `e2e_batch.py` | Sahte API + CLI uçtan uca (29 kontrol). Sahte sunucu rastgele portta açılır, uygulamanın `ApiBaseUrl`'i oraya çevrilir | `py -3 e2e_batch.py post` (düzeltme sonrası) · `py -3 e2e_batch.py pre` (düzeltme öncesi durumu yeniden üretir: 16 kontrol, çok sezonlu çağrı 5 dosya yazar) |
| `tui_harness.py` | Ortak ConPTY sürücüsü: `Driver` (tuş gönder, ekran oku, `settle`, `move_to`, `selected_row`), `Checks` (PASS/FAIL raporu), tuş sabitleri | kütüphane |
| `tui_batch_e2e.py` | Toplu indirme akışının tamamı (arama → sezon → çoklu seçim → onay → özet → disk) | `py -3 tui_batch_e2e.py multi` (23/23) · `py -3 tui_batch_e2e.py single` (21/21) |
| `tui_settings_e2e.py` | Ayarlar → kalıcılık → davranış zinciri: değeri yükselt/kapat, kaydet, toplu indirmede ölç | `py -3 tui_settings_e2e.py` (36/36) |
| `probe_conpty.py` | Sonda: ConPTY tuşları gerçek konsol tuşu olarak iletiyor mu (evet) | `py -3 probe_conpty.py` |
| `probe_settings_rows.py` | Sonda: Ayarlar ekranındaki satır sırası (imleç taşıma için) | `py -3 probe_settings_rows.py` |
| `probe_pty.py` | Sonda: `winpty` ile piped stdin denemesi — **başarısız** (`stdin is not a tty`); bu yüzden `pywinpty.PtyProcess` (ConPTY) kullanıldı | — |

`tui_harness.Checks.report()` çıkış kodu döndürür: herhangi bir kontrol düşerse `1`. Yani `exit=0` "0 FAIL"
demektir; rapor satırını görmek şart değildir.

### Yerel geliştirmede API'yi çalıştırma (build'den açarken)

CLI/TUI tek başına çalışmaz: veriyi `ApiBaseUrl` (öntanımlı `http://127.0.0.1:7045`) üzerinden alır ve
`ApiClientService.TryStartApiDaemonAsync` **API'yi kendisi başlatır**. İki şart sağlanmazsa
“API bağlantısı kurulamadı” hatası alınır ve ekrana tam olarak şu yazılır:

1. **`migurdex_native.dll` API'nin yanında olmalı.** `Migurdex.Api/Program.cs` bu dosyayı kendi dizininde
   arar; yoksa `Kritik: native kütüphane yüklenemedi` yazıp **`return 1` ile çıkar** — yani API hiç dinlemez.
   Dosya `.NET` derlemesinin çıktısına **kopyalanmaz** (bunu yapan bir MSBuild hedefi yok); yayınlanmış paketten
   veya `Migurdex.Native` Rust derlemesinden alınıp `Migurdex.Api/bin/<yapılandırma>/net10.0/` altına konur.
   (Rust'tan derlemek CMake + MSVC ister: `btls-sys`/BoringSSL adımı `cmake` bulunamazsa düşer.)
2. **`dotnet`'in bulunduğu kökte `Microsoft.AspNetCore.App 10` kurulu olmalı.** CLI, `.dll` bulduğunda
   `FileName = "dotnet"` ile başlatır; muxer framework'leri **kendi köküne göre** çözer, yani
   `C:\Program Files\dotnet` içinden çözen bir `dotnet` AspNetCore bulamazsa API anında ölür ve
   `~/.config/migurdex/logs/api.log`'a “`No frameworks were found`” yazar.
   **Ölçülen iki tuzak (06.10.2026):** (a) `.dotnet10`'u **kullanıcı** PATH'ine eklemek yetmez —
   `C:\Program Files\dotnet` **makine** PATH'inde olduğu için etkin PATH'te her zaman önce gelir;
   (b) `DOTNET_ROOT` muxer'ı kurtarmaz (apphost'u kurtarır, ama CLI `.dll` yolunu kullanır).
   **Çalışan çözüm:** ASP.NET Core 10 runtime'ını **mevcut köke** eklemek (yönetici ister):
   `dotnet-install.ps1 -Channel 10.0 -Runtime dotnet -InstallDir "C:\Program Files\dotnet" -NoPath`
   ve aynısının `-Runtime aspnetcore` hâli (eşleşen `Microsoft.NETCore.App` sürümü için).

Ölçülen sıra (06.10.2026): (1) native DLL eksikken API `Kritik: native kütüphane yüklenemedi` ile çıkıyordu;
(2) DLL konunca SDK PATH'e alınarak `migurdex.exe search "naruto" --json` çıkış kodu `0` verdi;
(3) ASP.NET Core 10 runtime'ı `C:\Program Files\dotnet`'e kurulduktan sonra **sistem `dotnet`'i ile, PATH
hilesi olmadan** aynı komut yine çıkış kodu `0` ve gerçek sağlayıcı sonuçlarını döndü. Yani sorun artık kapandı.

### Koşum takımındaki tuzaklar

1. **Toast TUI iş parçacığını ~0,8 sn bloklar** (`Toast.Show` içinde `Thread.Sleep`). O sırada gönderilen ok tuşları
   kuyruğa yığılır ve **yanlış satıra** iner. Sürücü bu yüzden her bildirimden sonra `settle()` yapar
   (1,2 sn bekle + arabelleği temizle) ve `move_to` her tuştan **önce** arabelleği temizler (0,35 sn aralık);
   `Enter`'a basmadan önce seçili satır `›` işaretiyle doğrulanır.
2. **PTY kapanma yarışı.** Uygulama tam `isalive()` kontrolü ile `write()` arasında çıkarsa PTY kapanır ve
   `EOFError` atar. Sürücünün `write()` yardımcısı bunu yutar (`isalive()` + `try/except EOFError`); `send` ve `move_to`
   sessizce durur. Bu koruma olmadan temizlik adımı (6 × `Esc`), **bütün kontroller geçmiş olsa bile** koşumu
   `exit ≠ 0` ile bitiriyordu — yani kanıt "geçti" dese de boru hattı kırmızı oluyordu.
3. **Konsol kod sayfası cp1254.** Koşum takımı çıktıyı UTF-8'e çevirir (`use_utf8_stdout()`); yoksa Türkçe
   karakterlerde `UnicodeEncodeError` alır.
4. **`pre` fazı bilinçli başarısız**: düzeltme öncesi kodu yeniden üretmek için vardır; `post` ile karıştırmayın.

### Düzeltme günlüğü

| Düzeltme | Ne değişti | Kanıt |
|---|---|---|
| **Sezon kapsamı** (kullanıcının bildirdiği hata) | `BatchDownloadView.SetTarget(..., int? season)`; liste `BatchDownloadPlan.OrderEpisodes` ile yalnız seçilen sezon; `AnimeDetailsView` çok sezonda `activeSeason` geçer | TUI e2e: başlık `0 / 2 seçili` (eskiden `0 / 5`), `23/23` |
| **`all` ↔ çözümleyici uyumsuzluğu** | `EpisodeSelectionSpec.TryResolve`: çok sezon + numara listesi `-s` ister (yoksa çıkış `2`); `all` `-s` yoksa tüm sezonlar | CLI e2e: kullanım hatası + 0 dosya; `all` → 5 dosya |
| **Sıkıştırma sahipliği** | `DownloadQueueOptions.MaxConcurrency` kurulumda sıkıştırır; `EffectiveConcurrency` üçüncü kez sıkıştırmaz; `FromConfig` yeniden sıkıştırmaz. Gizli saklanan değer yok: kullanıcının config'i tek doğruluk kaynağı | CLI/TUI e2e: `4` → tepe 3, `99` → `8 eşzamanlı (paralel)` |
| **Ölü kod** | `DownloadQueueWork.ForRequest` kaldırıldı (hiç çağrılmıyordu); sınıf XML belgesi `BatchDownloadPlan.BuildItem` + `DownloadQueueService`'e yönlendirir | Derleme: 0 uyarı |
| **Yardım biçimi** | `DownloadCommand.PrintHelp` içinde tek satıra sıkışmış iki `Console.WriteLine` ayrıldı (davranış aynı) | `migurdex.exe download --help` → `exit 0`, `--episodes` iki yerde görünür |
| **Koşum takımı** | PTY kapanma yarışı için `write()` koruması (yukarıdaki 2. tuzak) | `single` senaryosu düzeltmeden sonra `exit 0` + `21/21` |
