# Migurdex Test ve Doğrulama Raporu

> Bu dosya, `main` dalındaki tüm doğrulama çalışmalarının merkezi kaydıdır.
> Son güncelleme: 29 Eylül 2026 · Dal: **`main`** · Merge commit: `44f4010`
> (PR #1 merged) · Sürüm: **`v1.10.2`** → `44f4010` · Temel: `v1.10.1` (`4ecd7d7`)
> Release: https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2
> CI: https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972
> PR: https://github.com/Nutaliaxd/Migurdex/pull/1
> Upstream PR: https://github.com/roxyrekt/Migurdex/pull/2
>
> **Test toplamları hangi ağaca ait:** bölüm 1'deki **275/275** ve **110/110** değerleri `main`
> dalını (`44f4010`) temsil eder. **293/293** sonucu ise `upstream/download-clean` dalına
> (`f3aaad7`) aittir ve bölüm 12'de kayıtlıdır.
>
> **Bölüm 13 (API dokümantasyon doğrulaması)** bir **canlı uç doğrulamasıdır, build/test
> koşumu değildir**; bu nedenle yukarıdaki test toplamlarının hiçbiri değişmemiştir.

## 1. Offline doğrulama

- `git status`: temiz
- `git diff --check`: temiz
- `dotnet restore Migurdex.slnx`: başarılı
- `dotnet build Migurdex.slnx -c Release --no-restore`: 19 proje, 0 uyarı, 0 hata
- `dotnet test Migurdex.Tests\Migurdex.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~ExtractorSmokeTests"`: **275/275 geçti**

Son koşum: 29 Eylül 2026, `v1.10.1` rebase sonrası (portatif .NET SDK 10.0.401).
Bu kod tabanı PR #1 ile `main`'e merge edildi (`44f4010`) ve `v1.10.2` olarak yayınlandı;
test kodu merge sonrasında değişmedi, dolayısıyla bu koşum `v1.10.2` ağacını da temsil eder.

> **Yeni doğrulama kaydı:** `upstream/download-clean` dalında (`f3aaad7`) non-TTY üst düzey
> yardım düzeltmesi sonrası aynı filtre ile **293/293** geçti (275 taban + 18 yeni
> `TopLevelHelpTests`). Ayrıntı ve Linux runtime kanıtı için bkz. **bölüm 12**.

> **Proje sayısı bağlamı:** 19 proje, `v1.10.0` ve sonrası (upstream `Deokwave` plugin
> projesi dahil) çözüm ağacıdır. `v1.9.2` tabanlı ağaçta 18 proje vardır; bu yüzden 26–27
> Eylül tarihli kayıtlarda "18 proje" yazar. 29 Eylül'den itibaren geçerli değer **19**'dur.

İndirme/TUI odaklı test grupları ayrıca tek tek koşuldu:

| Test sınıfı | Sonuç |
|---|---:|
| `Mp4DownloaderTests` | 20/20 |
| `SubtitleDownloaderTests` | 10/10 |
| `HlsDownloaderTests` | 8/8 |
| `DownloadServiceTests` | 7/7 |
| `DownloadCommandTests` | 15/15 |
| `ApiClientServiceTests` | 2/2 |
| `TuiMarkupSafetyTests` | 23/23 |
| `FuzzyPromptSearchableTests` | 18/18 |
| **Grup toplamı (8 sınıflık filtre)** | **103/103** |

### 103/103 ile 110/110 arasındaki fark

İki sayı da doğrudur; fark yalnızca **filtre kapsamındadır**.

| Değer | Filtre kapsamı | Toplam |
|---|---|---:|
| **103/103** (bölüm 1) | Yukarıdaki **8** sınıf (`DownloadPathBuilderTests` ve `ExternalProcessRunnerTests` bu koşuya dahil edilmedi) | 103 |
| **110/110** (bölüm 3, `DOWNLOAD.md`, `PR_DESCRIPTION.md`) | Aynı 8 sınıf + `DownloadPathBuilderTests` (4) + `ExternalProcessRunnerTests` (3) = **10** sınıf | 110 |

- 103 + 4 + 3 = 110. Kanonik/güncel değer **110**'dur (10 sınıf, 81 metot).
- Kısa bir filtreyle koşulduğunda 8 sınıf dışındaki iki sınıf ölçülmez; bu bir kayıp değil,
  kapsam farkıdır. Bkz. `DOWNLOAD.md` → `Test kapsamı` ve `--progress` düzeltmesi bölümü.

## 2. Canlı API smoke

Koşum tarihi: **26 Eylül 2026** (v1.10.0 rebase'i öncesi, `feature/download` dalı).
Toplam **21 endpoint** test edildi; **21/21 HTTP 200** ve geçerli JSON döndü.

> **Sağlayıcı sayısı bağlamı:** Bu koşum **v1.10.0 öncesi** olduğu için `/health` **13**
> sağlayıcı bildiriyordu. Upstream `v1.10.0` ile eklenen `Deokwave` sonrası beklenen sayı
> **14**'tür (bkz. bölüm 6 ve bölüm 7). Extractor sayısı her iki ağaçta da **38**'dir.
> Aşağıdaki "13/13 sağlayıcı" ifadeleri de bu 13 sağlayıcılık koşuma aittir.

### Endpoint sonuçları

- `/health`: 200
- `/api/v1/providers`: 13 sağlayıcı (v1.10.0 öncesi koşum; güncel: 14)
- `/api/v1/extractors`: 38 extractor
- `/api/v1/anime/search?q=one piece`: 13/13 sağlayıcı başarılı, 153 sonuç
- Anime detayları: 6 sağlayıcı
- Fansub grupları: 4 sağlayıcı
- Video kaynakları: 4 sağlayıcı
- Metadata arama: 10 kayıt
- AniList detayı: `ONE PIECE`
- Tracker lookup: `anilistId=21`

### Log durumu

- 0 exception
- 0 stack trace
- 0 serialization hatası

### Upstream notları

1. `TrAnimeIzle`: captcha challenge çözülemedi, sağlayıcı boş liste döndü.
2. `VidmolyExtractor`: embed URL’si `torroclk.com` reklam ağına yönlendi, extractor kaynağı atladı.

Bu iki durum da uygulama hatası değil.

## 3. TUI canlı smoke

Gerçek ConPTY terminalinde, gerçek `naruto` sorgusu ile uçtan uca test edildi.

### Kontrol listesi

| Kontrol | Sonuç |
|---|---|
| Uygulama çöküyor mu? | Hayır |
| Ana menü açılıyor mu? | Evet |
| Arama ekranı açılıyor mu? | Evet |
| Gerçek sorgu ile arama? | Evet |
| Anime/bölüm seçimi? | Evet |
| Kaynak ekranı hatasız açılıyor mu? | Evet |
| İndirme sonrası `Ara:` satırı kaldırılmış mı? | Evet |
| Klavye gezinmesi? | Çalışıyor |
| `Esc` davranışı? | Çalışıyor |

### Akış

1. Ana menü açıldı.
2. `naruto` araması yapıldı.
3. 50 sonuç döndü.
4. Anime seçildi.
5. Bölüm seçildi.
6. Kaynak ekranı açıldı.
7. İndirme başlatıldı.
8. **371.406.042 bayt** video indirildi.
9. Sonuç ekranı açıldı.
10. Uygulama `exit=0` ile kapatıldı.

### Bulunan hata ve düzeltme

HLS indirmesi sırasında ilerleme satırı güncellenmiyordu ve şu şekilde kalıyordu:

```text
Bağlanıyor • 0 B • Esc: iptal
```

Kök neden:

- `YtDlpHlsDownloader.BuildStartInfo` şu bayrağı kullanıyor:

```text
--print after_move:filepath
```

- Bu bayrak yt-dlp’de `--quiet` davranışını ima ediyor.
- `--quiet` açık olduğundan progress çıktısı türetilmiyor.
- Sonuç olarak TUI progress satırı hiç güncellenmiyordu.

**Düzeltme tamamlandı:**

- yt-dlp argüman listesine `--progress` eklendi.
- `--print after_move:filepath` yerinde kaldı.
- `HlsDownloaderTests` yeni testle genişletildi.
- Yüzde ve `MiB/x MiB` progress kalıplarının `Downloading` aşamasına dönüştüğü doğrulandı.
- Düzeltme sonrası TUI beklenen şekilde şunu gösteriyor:

```text
İndiriliyor • <indirilen> / <toplam> • Esc: iptal
```

İlgili test sonucu:

- Genel offline takım: **275/275**
- `HlsDownloaderTests`: **8/8**
- İndirme/TUI odaklı sınıf filtreleri: **110/110** (10 sınıf — bkz. bölüm 1'deki
  103/110 açıklaması)

## 4. CLI canlı smoke

### Ana komut

```powershell
migurdex download "one piece" -e 1 --debug --json
```

Sonuç:

- `EXIT=0`
- Süre: 42,6 sn
- Sağlayıcı: `AnimeciX`
- Anime: `One Piece`
- Bölüm: `S1E1`
- Kaynak: `Tau Video`
- Kalite: `1080p`
- Format: `Mp4`
- Fansub: `AoiSubs`
- Aday sayısı: `3`
- `success: true`
- `dryRun: true`

### Sağlayıcı bazlı sonuçlar

| Sağlayıcı | Kaynak sayısı | Seçilen kaynak | Hata | Süre |
|---|---:|---|---|---:|
| `TurkAnime` | 37 | `GoogleDrive`, 1080p, MP4 | Yok | 9.114 ms |
| `AnimeciX` | 9 | `Tau Video`, 1080p, MP4 | Yok | 3.034 ms |
| `OpenAnime` | 6 | `OpenAnime`, 1080p, MP4 | Yok | 8.063 ms |

### `--debug` sızıntı denetimi

Aşağıdaki desenler için 0 eşleşme bulundu:

- `https://`
- `Authorization`
- `Bearer`
- `Referer`
- `Cookie`
- `User-Agent`
- `token`
- `api_key`
- `secret`

### Gerçek MP4 indirmesi

```powershell
migurdex download "one piece" -e 1 -p AnimeciX --json
```

Sonuç:

- `EXIT=0`
- Süre: 10.112 sn
- Dosya boyutu: `437.668.399` bayt
- Dosya adı: `S01E01 - Ben Luffy! Korsanlar Kralı Olacak Adam!.mp4`
- Bütünlük: geçerli MP4
- ffprobe süresi: `1500,14` sn
- Bitrate: yaklaşık `2,33 Mbps`
- `subtitlePaths`: boş
- `warnings`: boş

## 5. Genel sonuç

- API, CLI ve TUI akışları canlı olarak doğrulandı.
- Offline test süiti tamamen geçti.
- Gerçek indirme başarıyla tamamlandı.
- Tek gerçek uygulama hatası bulundu ve düzeltildi:
  1. **HLS progress güncellenmiyordu** — `--progress` bayrağı eklenerek düzeltildi ve testlerle doğrulandı.
- Ortam bulgusu olarak not edildi:
  1. Sistem dotnet’inde ASP.NET Core runtime yoktu; geçici SDK kurulunca aşıldı. Bu bir kod hatası değil.
- Uygulama tarafında kalıcı bir çökme veya veri bozulması bulunmadı.
- v1.10.0 tabanlı paket üretildi, hash düzeyinde doğrulandı ve kullanıma hazır (bkz. bölüm 7).
- PR #1 `main` üzerine merge edildi ve çalışma `v1.10.2` olarak GitHub release'inde
  yayınlandı; CI release koşusu başarıyla tamamlandı (bkz. bölüm 11).

## 6. v1.10.0 geçişi

Upstream **v1.10.0** yayınlandıktan sonra `feature/download` dalı bu sürüm üzerine rebase edildi.

### Upstream değişiklikleri

- `8094425` — `fix(cli): altyazi ismi + link yerine indir`
- `4035f9a` — `feat(providers): add Deokwave`
- Sağlayıcı sayısı **13 → 14** oldu.
- `README.md` güncellendi ve Deokwave listeye eklendi.
- Breaking change yok.

### Rebase sonucu

- Yedek dal: `backup/download-pre-v110`
- Rebase hedefi: `v1.10.0` (`4035f9a`)
- Çakışma: **yok**
- Deokwave plugin’i ilk derlemede restore edilmemişti:
  - `dotnet restore Migurdex.slnx` çalıştırıldı
  - Build sonrası **0 uyarı / 0 hata**
- Test sonucu:

```text
dotnet test Migurdex.Tests/Migurdex.Tests.csproj -c Release --no-build `
  --filter "FullyQualifiedName!~ExtractorSmokeTests"

Sonuç: 275/275 geçti
```

### Dal durumu (kayıt anında)

```text
v1.10.0..feature/download = 12 commit
```

Bu alt bölüm 27 Eylül 2026 kaydıdır ve `feature/download` dalının o andaki durumunu anlatır.
Güncel durum için bkz. bölüm 10 (v1.10.1 rebase) ve bölüm 11 (merge + `v1.10.2` release).
**Tek doğruluk kaynağı `git log`'tur** — `git log --oneline v1.10.1..main` (veya
`git log --oneline backup/download-pre-v110..backup/download-pre-v1101` ile eski hash'ler).
Aşağıdaki liste o günün anlatımıdır; güncel ve eksiksiz commit listesi için git'e bakın.

O günkü commit listesi:

```text
321fe38 feat: add anime download support
bd59ca4 fix(tui): balance source selection markup
81335bc docs: document download feature and operations
3a3cfc5 fix(tui): hide search filter on download result
c8a7d75 docs: update download history for result prompt fix
8ef695e docs: clarify latest code commit in download guide
85f356a docs: record offline validation results
febe716 docs: record live API smoke results
1f82f2d docs: add consolidated test results report
5014025 fix(downloader): report live yt-dlp HLS progress
7763b2d docs: record v1.10.0 rebase and latest verification
21d687c docs: document upstream v1.10.0 integration notes
```

Son kod commit'i `5014025`; sonraki iki commit dokümantasyon kaydıdır. Bölüm 7'deki paket
doğrulaması da `docs: record v1.10.0 packaged build` commit'i ile işlenmiştir.

### Notlar

- Upstream’deki altyazı indirme davranışı ile bizim `SubtitleDownloader` arasında fonksiyonel benzerlik var.
- İleri sürümde ortak yardımcıya çıkarma refactor’u yapılabilir.
- Deokwave sağlayıcısı dinamik provider listesine otomatik eklenir; indirme akışında ek kod değişikliği gerekmez.

## 7. v1.10.0 paket doğrulaması (yerel `build.ps1` paketi)

Rebase sonrası kod tabanı (temel `v1.10.0` @ `4035f9a`, son kod commit'i `5014025`)
yeniden paketlendi ve doğrulandı (27 Eylül 2026).

> **Kapsam notu:** Bu bölüm **yerel `build.ps1 -Publish` paketinin** doğrulamasıdır. Bu paket
> daha sonra `v1.10.1` üzerine rebase edildi ve kök kuruluma yüklendi; **GitHub release
> paketi değildir**. `v1.10.2` için CI tarafından üretilen çok platformlu paket ve
> checksum'lar bölüm 11'dedir — iki paket aynı değildir (sürüm damgası ve platform farkı).

### Üretim

| Adım | Sonuç |
|---|---|
| Build | `.\build.ps1 -Publish`, portatif .NET SDK 10.0.401 |
| Rust derlemesi | `cargo build --release` başarılı — 2 dk 53 sn |
| `api\migurdex_native.dll` damgası | 27.09.2026 03:00:14 |
| Sağlayıcı / extractor | **14** sağlayıcı (Deokwave dahil) / **38** extractor |

### Paket içeriği ve boyutlar

| Dosya | Boyut (bayt) |
|---|---:|
| `migurdex.exe` | 23.466.537 |
| `api\Migurdex.Api.exe` | 108.374.872 |
| `api\migurdex_native.dll` | 8.661.504 |
| `api\Plugins\*` | 14 sağlayıcı plugin DLL'i (Deokwave dahil) |
| `migurdex-win-x64.zip` | 61.605.017 |

### SHA-256 özetleri

| Dosya | SHA-256 |
|---|---|
| `migurdex.exe` | `DE9EA5634B16E07BC3B16B8C8C7EE07CB311FE489AF78E09AAD3E608C86E3E79` |
| `api\migurdex_native.dll` | `91BA419E97AD5BC0F0CA1ADF9768CF60FDFAC72234AADE28AC02DB46C1B44627` |
| `migurdex-win-x64.zip` | `BC17A9FFA03D0B9BE5AA9004C6E6E94DB35C469442BA1515B3090FB6C96DB5AA` |

### install-candidate eşleşmesi

`..\migu\install-candidate\` klasörü yeni paketle güncellendi. `migurdex.exe`,
`api\migurdex_native.dll` ve `migurdex-win-x64.zip` SHA-256 özetleri `dist` kopyalarıyla
birebir aynı.

### Smoke sonuçları

| Kontrol | Sonuç |
|---|---|
| `migurdex.exe --version` | `migurdex v0.0.0`, `EXIT=0` |
| `migurdex.exe download --help` | Tam yardım metni, `EXIT=0` |
| API süreci | Başlatılmadı; doğrulama dosya listesi + hash karşılaştırmasıyla yapıldı |

Not: `v0.0.0` **yerel** build için beklenen değerdir — sürüm damgası
`Directory.Build.props` → `VersionPrefix` 0.0.0'dan gelir; etiketli sürüm numarası
yayın CI'sinde `-p:Version` ile basılır. Bölüm 11'deki `v1.10.2` release paketinde
`migurdex --version` gerçek sürümü (`migurdex v1.10.2`) basar; kök kurulumdaki yerel
paket ise `v0.0.0` göstermeye devam eder (bkz. bölüm 9).

Paket kullanıma hazır: `C:\Users\naton\OneDrive\Desktop\migu\install-candidate\migurdex.exe`.

## 8. Deokwave sağlayıcı smoke testi

27 Eylül 2026'da v1.10.0 paketi (bölüm 7'deki `install-candidate`) üzerinde, upstream'in
v1.10.0 ile eklediği `Deokwave` sağlayıcısına odaklı canlı smoke testi koşuldu. Amaç:
plugin'in paket içinde yüklü olduğunu, sağlayıcı çözümlemesinin ve indirme akışının hata
yollarının beklendiği gibi çalıştığını doğrulamak.

### Kontrol listesi

| Kontrol | Sonuç |
|---|---|
| `Deokwave` plugin pakette yüklü mü? | Evet — API her açılışta başarıyla yüklüyor |
| `-p Deokwave` sağlayıcı çözümlemesi | Çalışıyor |
| `one piece` araması | Boş liste |
| `naruto` araması | Boş liste |
| İndirme akışı | Arama adımında duruyor; `exit 1` + temiz JSON hatası |
| Gerçek indirme | Yapılamadı — akış kaynak çözümlemesine ulaşamıyor |
| Diskte yeni/kısmi dosya | Yok |
| Kullanıcı veritabanı | Değişmedi |
| `--debug` sızıntı denetimi | 0 eşleşme (URL, header, token) |

### Kök neden

`deokwave.com` **tüm uç noktalarında** Cloudflare `"Just a moment..."` JS challenge ile
**HTTP 403** döndürüyor. Sağlayıcı challenge'ı aşamadığından aramalar boş liste döndürüyor;
indirme akışı kaynak çözümlemesine hiç ulaşamıyor. **Bu bir uygulama hatası değildir** —
bulgu tamamen upstream tarafındaki erişim kısıtına aittir.

### Kontrol grubu

Aynı akış `TurkAnime` ile sorunsuz tamamlandı (bkz. bölüm 4: 37 kaynak, `GoogleDrive`,
1080p, MP4, hata yok). Böylece indirme özelliğinin kendisinin sağlam olduğu, bulgunun
yalnızca Deokwave upstream erişimine özgü olduğu doğrulandı.

### Yan bulgu

Deokwave, HTTP 403'ü sessizce boş listeye çeviriyor; sağlayıcı tarafındaki başarısızlık
kullanıcıya görünmüyor. İyileştirme fikri: sağlayıcı hatalarını görünür kılan bir loglayıcı
ileride eklenebilir. Bu koşumda kod değişikliği yapılmadı.

### Sonuç

- Paket, plugin yükleme, sağlayıcı çözümleme ve hata yolları beklendiği gibi çalışıyor.
- Sınırlama yalnızca Deokwave upstream'ine özgüdür; upstream challenge'ı kaldırırsa
  sağlayıcı kod değişikliği gerektirmeden çalışır hâle gelir.

## 9. Kök kurulum yükseltmesi

27 Eylül 2026'da yerel kök kurulum (`C:\Users\naton\OneDrive\Desktop\migu`) v1.9.2'den
v1.10.0 tabanlı pakete (indirme özelliğiyle; bölüm 7'de üretilen `install-candidate`
içeriği) yükseltildi. İşlem yalnızca dosya yedekleme/kopyalama ve doğrulamadan ibarettir;
kaynak ağacında kod değişikliği yapılmadı.

### Adımlar

1. Eski kök kurulum `..\migu\backup-v1.9.2\` klasörüne yedeklendi.
2. `install-candidate` içeriği köke kopyalandı: `migurdex.exe`, `api\`,
   `migurdex-win-x64.zip`.

### Yükseltme sonrası doğrulamalar

| Kontrol | Sonuç |
|---|---|
| `migurdex.exe --version` | `migurdex v0.0.0` — çalışıyor (sürüm metadata build'de gömülmedi; bkz. bölüm 7 notu) |
| `migurdex.exe download --help` | Çalışıyor; tam yardım metni |
| `api\Plugins\` | 14 sağlayıcı plugin DLL'i (`Migurdex.Plugins.*`, Deokwave dahil); klasördeki 18 dosyadan kalan 4'ü AngleSharp, DI/Logging abstractions ve `Migurdex.Shared` gibi altyapı DLL'leri |
| `api\migurdex_native.dll` | 8.661.504 bayt — bölüm 7 paketiyle aynı boyut |

### SHA-256 özetleri

| Dosya | SHA-256 |
|---|---|
| Eski `migurdex.exe` (v1.9.2 kök kurulumu; yedeği `backup-v1.9.2\` içinde) | `3879BE18B84EB23D069FB3BF51767571E58536EDAF4F1B283B3A67A19C26D712` |
| Yeni `migurdex.exe` | `DE9EA5634B16E07BC3B16B8C8C7EE07CB311FE489AF78E09AAD3E608C86E3E79` |
| Yeni `migurdex-win-x64.zip` | `BC17A9FFA03D0B9BE5AA9004C6E6E94DB35C469442BA1515B3090FB6C96DB5AA` |
| Yeni `api\migurdex_native.dll` | `91BA419E97AD5BC0F0CA1ADF9768CF60FDFAC72234AADE28AC02DB46C1B44627` |

Yeni dosyaların özetleri bölüm 7'deki `dist`/`install-candidate` özetleriyle birebir aynı;
köke inen ikilinin paketten saptırılmadığı hash düzeyinde doğrulandı.

### Kök klasörün yeni durumu

```text
C:\Users\naton\OneDrive\Desktop\migu\
├─ migurdex.exe            # yeni — v1.10.0 tabanlı paket
├─ api\                    # yeni — 14 sağlayıcı plugin'i + migurdex_native.dll
├─ migurdex-win-x64.zip    # yeni paket arşivi
├─ backup-v1.9.2\          # eski kök kurulumun yedeği
├─ install-candidate\      # köke kopyalanan paket adayı
└─ source\                 # bu depo (dal: main)
```

### Geri alma

Geri alma yolu: `backup-v1.9.2\` klasöründeki dosyaları köke geri kopyalamak.

### Kök kurulumun güncel durumu (29.09.2026)

Kök kurulum **hâlâ bölüm 7'deki yerel v1.10.0 tabanlı paketi** çalıştırır; 29 Eylül'de
`v1.10.2` release paketiyle yükseltilmedi. Bunun nedenleri ve farkları:

| Konu | Kök kurulum (yerel paket) | `v1.10.2` release (CI paketi) |
|---|---|---|
| Kaynak | `install-candidate\` (yerel `build.ps1 -Publish`) | GitHub release asset'ları |
| Taban | `v1.10.0` @ `4035f9a` | `v1.10.2` → `44f4010` (upstream taban `v1.10.1` @ `4ecd7d7`) |
| `--version` | `migurdex v0.0.0` | `migurdex v1.10.2` |
| `migurdex-win-x64.zip` | 61.605.017 bayt (SHA-256 `BC17A9FF…`) | 60.031.590 bayt |
| Platform | yalnız win-x64 | win-x64 + linux-x64/arm64 + AppImage (x86_64/aarch64) |
| Doğrulama | yerel smoke + hash karşılaştırması | CI build + `sha256sums-*.txt` |

Kök kurulumu release paketine geçirmek istenirse: `migurdex-win-x64.zip` indirilir,
`sha256sums-win-x64.txt` ile doğrulanır, mevcut kurulum `backup-v1.9.2\`'ye (veya yeni bir
`backup-*\` klasörüne) yedeklenir ve `migurdex.exe` + `api\` köke çıkarılır. Bu işlem bu
kayıtta **yapılmadı**; bölüm 11 yalnızca release varlığını doğrular.

## 10. v1.10.1 geçişi

Upstream **v1.10.1** yayınlandıktan sonra `feature/download` dalı bu sürüm üzerine
rebase edildi (29 Eylül 2026).

### Upstream değişiklikleri (v1.10.0 → v1.10.1)

- `e4a32b4` — `fix: update database link for TurkAnime provider`: README'deki HuggingFace
  DB bağlantısı `mdexturkanime/turkanime-db` → `roxyrekt/turkanime-db`.
- `914dfdd` — `fix(anizm): handle unnamed anizm fansub groups`: `AnizmProvider.cs`
  isimsiz fansub gruplarını ele alıyor.
- `4ecd7d7` — `feat(appimage): zsync update info + AppRun locale wrapper`:
  `build-release.yml`, `UpdateCommand.cs`, `assets/packaging/AppRun` ve `Program.cs`
  (encoding guard kaldırma + `ReadKey` guard'ı).
- Sağlayıcı (14) / extractor (38) sayısı ve `/api/v1` sözleşmesi değişmedi; breaking change yok.

### Rebase sonucu

- Yedek dal: `backup/download-pre-v1101`
- Rebase hedefi: `v1.10.1` (`4ecd7d7`)
- 21 feature commit'i yeni tabana taşındı.
- Tek çakışma: `Migurdex.Cli/Program.cs` (ana özellik commit'i, `be85d61`):
  - Dal tarafının TUI iptal mimarisi korundu: `TuiApplicationCancellation`, `tuiToken`,
    `activeNavigator`, `catch (OperationCanceledException)`, `finally` bloğu.
  - Upstream'in `Console.IsInputRedirected` guard'lı + try/catch `Console.ReadKey(true)`
    versiyonu alındı; dal tarafındaki sade `ReadKey` çağrısı bırakıldı.
  - Dosyanın tepesindeki encoding guard kaldırma otomatik merge ile geldi.
  - Çözüm `git diff backup/download-pre-v1101:Migurdex.Cli/Program.cs` ile doğrulandı:
    fark, birebir upstream'in v1.10.0→v1.10.1 `Program.cs` değişiklikleri.
- `README.md` otomatik merge edildi; TurkAnime DB bağlantısının `roxyrekt/turkanime-db`
  olduğu ve indirme dokümantasyonu bölüm sırasının korunduğu gözle doğrulandı.

### Doğrulama (rebase sonrası, 29 Eylül 2026)

| Komut | Sonuç |
|---|---|
| `dotnet restore Migurdex.slnx` | Başarılı (19 projeden 16'sı güncel) |
| `dotnet build Migurdex.slnx -c Release --no-restore` | **19 proje, 0 uyarı, 0 hata** |
| `dotnet test ... --filter "FullyQualifiedName!~ExtractorSmokeTests"` | **275/275 geçti** |

Bu koşum, PR #1'in merge edildiği ve `v1.10.2` etiketiyle yayınlandığı kod ağacının tamamıdır
(bkz. bölüm 11): merge sonrasında kaynak kodda değişiklik olmadı.

### Dal durumu (kayıt anında)

```text
v1.10.1..feature/download = 21 commit (bu kayıt commit'i hariç)
```

Güncel karşılık: `v1.10.1..main` = **22 commit** + merge commit `44f4010`
(bkz. bölüm 11). **Tek doğruluk kaynağı `git log`'tur** (`git log --oneline v1.10.1..main`).

Son kod commit'i: `00b2ee7` — `fix(downloader): report live yt-dlp HLS progress`.

Sık atıf yapılan hash eşlemeleri (v1.10.1 rebase tüm hash'leri yeniden yazdı; eski
hash'ler `backup/download-pre-v1101` dalında):

| Eski (v1.10.0 tabanlı) | Yeni (v1.10.1 tabanlı) |
|---|---|
| `321fe38` — feat: add anime download support | `be85d61` |
| `bd59ca4` — fix(tui): balance source selection markup | `6c366c9` |
| `3a3cfc5` — fix(tui): hide search filter on download result | `b3dd5d5` |
| `5014025` — fix(downloader): report live yt-dlp HLS progress | `00b2ee7` |

## 11. PR #1 merge ve `v1.10.2` release (29 Eylül 2026)

`feature/download` dalı fork (`Nutaliaxd/Migurdex`) `main` dalına PR ile birleştirildi ve
aynı gün etiketli sürüm yayınlandı. **Aktif dal `main`**'dir.

| Olay | Değer |
|---|---|
| PR | [#1 — feat: add anime download support](https://github.com/Nutaliaxd/Migurdex/pull/1) (`feature/download` → `main`) |
| PR durumu | **merged** — 29.09.2026 10:05 UTC |
| Merge commit | `44f4010` — `Merge pull request #1 from Nutaliaxd/feature/download` |
| Merge ebeveynleri | `4ecd7d7` (upstream `v1.10.1`) + `c4c06c8` (dalın son commit'i) |
| PR istatistikleri | 22 commit, 40 dosya, +11.559 / −249 |
| Tag | `v1.10.2` → `44f4010` |
| Release | [Nutaliaxd/Migurdex — v1.10.2](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2) — 29.09.2026 11:41 UTC |
| CI run | [Build and Release #36562344972](https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972) — sonuç **success**, tetikleyici `workflow_dispatch` @ `v1.10.2` |

### Release asset'ları

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

### Merge ve release doğrulaması

| Kontrol | Sonuç |
|---|---|
| `git log --oneline v1.10.1..main` | 22 commit + merge commit `44f4010` — PR istatistiğiyle birebir aynı |
| `git describe --tags` (HEAD) | `v1.10.2` |
| `git rev-list -n1 v1.10.2` | `44f4010…` — merge commit ile aynı |
| Release hedefi | `main`; PR #1 "New Contributors" notuyla `v1.10.2` release notlarına girdi |
| CI sonucu | `success` (3 build matrisi: linux-x64, linux-arm64, win-x64) |
| Kaynak kod değişikliği | Yok — merge yalnızca dokümantasyon commit'i (`c4c06c8`) ile sonlanan dalı birleştirdi; bölüm 1'deki 275/275 sonucu geçerlidir |

### Kapsam notu

- Bu release **fork'a** aittir. Upstream `roxyrekt/Migurdex` `main` dalı hâlâ `v1.10.1`
  (`4ecd7d7`) durumundadır; upstream entegrasyonu
  [PR #2](https://github.com/roxyrekt/Migurdex/pull/2) üzerinden açılmıştır. PR `OPEN` ve
  `MERGEABLE` durumdadır; temiz dal `upstream/download-clean`, 3 commit (`7f1e250` özellik,
  `f3aaad7` non-TTY help fix, `474a43d` dokümantasyon), 39 dosya, +10.173 / −266. Önceki upstream
  [PR #1](https://github.com/roxyrekt/Migurdex/pull/1) kapatıldı (duplicate).
- PR #2 kapsamı **kod + testler + `README.md` + `DOWNLOAD.md`** ile sınırlıdır; `DEVELOPMENT_LOG.md`,
  `TEST_RESULTS.md` ve `PR_DESCRIPTION.md` PR'ye dahil edilmemiştir, yalnızca fork `main` dalında
  durur ve PR #2 gövdesinde mutlak bağlantılarıyla işaret edilir. PR #2 ağacı (upstream `4ecd7d7`
  üzerine rebase edilmiş dal) ile `main`'in test edilen ağacı **aynı indirme kaynak kodunu** taşır;
  bölüm 1'deki 275/275 ve 110/110 sonuçları her ikisini de temsil eder. Aradaki tek fark
  `f3aaad7`'nin eklediği `HelpCommand`/`TopLevelHelpTests` ve bölüm 12'de kayıtlı **293/293**
  sonucudur.
- Release paketi, bölüm 7'deki yerel `build.ps1` paketinden farklıdır: CI sürüm damgası basar
  (`-p:Version`), üç platformu kapsar ve `sha256sums-*.txt` ile doğrulanabilir. Ancak Linux
  `sha256sums-*.txt` manifestleri AppImage'leri kapsamaz — bkz. bölüm 12 → (e).
- Kök kurulum hâlâ yerel paketi çalıştırmaktadır; ayrıntı ve fark tablosu bkz. bölüm 9.

## 12. Linux runtime doğrulaması ve non-TTY help fix (29 Eylül 2026)

Bu bölüm iki bağımsız kaydı içerir:

- **(a)** gerçek bir Linux çalıştırma ortamında (WSL2, Ubuntu 24.04.5) alınan uçtan uca
  doğrulama kanıtı,
- **(b)** bu koşunun bulduğu ve `f3aaad7` ile düzeltilen üst düzey yardım çökmesi.

İkisi de `upstream/download-clean` dalında yürütülmüştür. `main` dalındaki PR #1 kapsamı
(PR #1'in merge edildiği ağaç) bu iki kayıttan **etkilenmez**; bölüm 1'deki 275/275 hâlâ `main`
için geçerlidir.

### Ortam

| Öğe | Değer |
|---|---|
| Ortam | WSL2, Ubuntu 24.04.5 (x86_64) |
| .NET SDK | 10.0.401 (Windows koşumuyla aynı) |
| Doğrulanan ağaç | `upstream/download-clean` @ `f3aaad7` (tabandan `4ecd7d7` / v1.10.1) |
| Kullanılan paket | `v1.10.2` release binary'si (CI üretimi) |

### (a) Linux x64 runtime sonuçları

| Adım | Komut / ölçüm | Sonuç |
|---|---|---|
| Bağımlılık | `dotnet restore Migurdex.slnx` | başarılı |
| Derleme | `dotnet build Migurdex.slnx -c Release --no-restore` | 19 proje, **0 uyarı, 0 hata** |
| Offline takım | `dotnet test Migurdex.Tests/Migurdex.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~ExtractorSmokeTests"` | **275/275** (help fix'i öncesi ağaç) |
| CLI sürüm | `migurdex --version` | `migurdex v1.10.2` |
| CLI alt komut yardımı | `migurdex download --help` | release binary'siyle **byte-level aynı** |
| API | `/health` | **HTTP 200**; `providers=14`, `extractors=38`, `rust=true` |
| Yerel build ↔ release | GNU BuildID | ikisi de `c36ad71424f1fa2ffd952574ab64dd0d952b101a` |
| AppImage | `--appimage-extract` | extraction başarılı |
| AppImage | zsync `updateinformation` | gömülü ve doğru (`gh-releases-zsync\|roxyrekt\|Migurdex\|latest\|...`) |
| AppImage | çalıştırma | başarılı (x86_64) |

**BuildID eşleşmesinin anlamı.** Yerel Linux derlemesi ile yayınlanan release binary'si aynı GNU
BuildID'yi taşıyor (`c36ad71424f1fa2ffd952574ab64dd0d952b101a`). BuildID, kaynak kodu ve derleme
parametrelerini özetleyen bir linker çıktısı olduğu için iki çıktının aynı BuildID'yi taşıması
**paketleme adımının kaynak kodu değiştirmediğini** kanıtlar. İki binary arasındaki tek fark
sürüm damgası (`-p:Version` → `1.10.2` ↔ yerelde `0.0.0`) ve paket biçimidir. Bunun pratik
karşılığı: release binary'si doğrudan çalıştırılabilir bir referanstır, dolayısıyla Linux smoke
testlerinde yerel build yerine **release paketi** kullanılabilir ve `migurdex download --help`
çıktısı byte-level karşılaştırılabilir.

**`/health` sonucu.** `providers=14` (upstream `v1.10.0`'da eklenen `Deokwave` dahil),
`extractors=38`, `rust=true`. Bu, Rust native köprüsünün (`migurdex_native.so`) Linux'da
başarıyla yüklendiğini doğrular; `rust=false` olsaydı API kaynak çözümlemesini yapamazdı.
Windows tarafındaki bölüm 2 kaydı (13 sağlayıcı) v1.10.0 rebase'i öncesi bir koşuma aittir;
güncel sayı 14'tür ve iki kayıt çelişmez.

### Linux arm64

| Durum | Açıklama |
|---|---|
| Derleme | başarılı |
| ELF doğrulaması | mimari = AArch64 |
| AppImage içeriği | `unsquashfs` ile açıldı, dosya bütünlüğü doğrulandı |
| **Çalıştırma** | **yapılamadı** — doğrulama ortamı x64, QEMU emülasyonu kurulu değil |

Çalıştırılamayan kontroller: `migurdex --version`, `migurdex download --help`, `/health` ve canlı
indirme akışı. Bu bir ürün kusuru değil, **ortam sınırıdır**: arm64 paketi CI tarafından x64 ile
aynı kaynak koddan ve aynı iş akışıyla üretildiği için derleme düzeyinde bir sapma beklenmez.
Bununla birlikte arm64 runtime kanıtı **doğrulanmadan "arm64 destekleniyor" denmemelidir**;
bu kayıt bilinçli olarak eksik kalmayı tercih eder.

### (b) non-TTY `--help` çökmesi ve `f3aaad7` düzeltmesi

**Bulgu.** Linux koşumunda `migurdex --help` çalıştırıldığında süreç çöktü (exit 134 / SIGABRT).

**Kök neden — argüman yönlendirme boşluğu:**

1. `Program.Main` üst düzey `--help` / `-h` / `help` argümanlarını yakalamıyordu.
2. Argümanlar düşüyor ve TUI başlatma rotasına gidiyordu; TUI etkileşimli terminal bekliyordu.
3. Yönlendirilmiş stdin'de `Console.ReadKey` çalıştırıldığında süreç çöküyordu.

**Bu Linux'a özgü değildi.** `stdin` yönlendirilmiş olan her ortamda geçerliydi: CI job'ları,
Docker `CMD`/entrypoint, `nohup`, `migurdex --help > dosya`, `echo | migurdex --help`,
`$(migurdex --help)`. Yalnız Windows'ta görünmüyordu çünkü orada yönlendirme yapılmıyordu — bu,
hatayı "Linux hatası" sanma riski taşıyan bir bulgudur.

**Daraltılmış kapsam.** `--version` ve `migurdex download --help` **zaten doğru çalışıyordu**.
Sorun yalnızca üst düzey help rotasındaydı; alt komutların kendi yardım yolu hiç etkilenmedi.

**Düzeltme (`f3aaad7 fix(cli): handle top-level help without tty`):**

| Bileşen | Değişiklik |
|---|---|
| `Migurdex.Cli/Services/HelpCommand.cs` *(yeni, 71 satır)* | `IsHelpToken`, `IsTopLevelRequest`, `PrintHelp(TextWriter)`, `Run()` — TUI'siz, yalnız yazan yardım rotası |
| `Migurdex.Cli/Program.cs` (+10/−1) | `--version` rotasından sonra, TUI başlatılmadan önce top-level help kontrolü; `MaybePromptForUpdateAsync` içindeki `ReadKey` `Console.IsInputRedirected` ile korumaya alındı |
| `Migurdex.Cli/Services/NonInteractiveCommand.cs` (+25/−14) | `Help()` → yeniden kullanılabilir `internal static void PrintHelp(TextWriter?)`; metin `WriteCommandLines` + `WriteFlagLegend` olarak ayrıştırıldı — **tek kaynak**, iki yardım metni kopyalanmıyor |
| `Migurdex.Tests/TopLevelHelpTests.cs` *(yeni, 89 satır)* | 18 test |

`IsTopLevelRequest`, ilk argüman devredilen bir komut (`version`, `update`, `auth`, `search`,
`play`, `continue`, `download`) olduğunda `false` döndürür; böylece `migurdex download --help`
üst düzey rotaya düşmez ve kendi yardımını basmaya devam eder.

### (c) Test sonuçları: 275 → 293

| Koşu | Ağaç | Sonuç |
|---|---|---:|
| Offline takım, v1.10.1 rebase sonrası | `main` @ `44f4010` | **275/275** |
| Offline takım, non-TTY help fix sonrası | `upstream/download-clean` @ `f3aaad7` | **293/293** |

275 → 293 farkı **tam olarak 18 yeni `TopLevelHelpTests` case'idir**:

| Test | Case |
|---|---:|
| `IsTopLevelRequest_RecognizesEveryHelpAlias` | 5 |
| `IsTopLevelRequest_LeavesSubCommandHelpToSubCommand` | 7 |
| `IsTopLevelRequest_IsFalseWithoutAHelpRequest` | 3 |
| `PrintHelp_CoversTuiSubCommandsVersionAndHelp` | 1 |
| `PrintHelp_ReusesNonInteractiveCommandLines` | 1 |
| `Run_WritesHelpToTheGivenWriterAndSucceeds` | 1 |
| **Toplam** | **18** |

**Kapsam notu.** `TopLevelHelpTests` CLI yardım yönlendirmesini kapsar; indirme kodu kapsamında
değildir. Bu nedenle indirme/TUI grubu metrikleri **değişmedi**: hâlâ **81 metot / 110 çalışan
case / 10 sınıf** (bkz. bölüm 1 ve `DOWNLOAD.md` → `Test kapsamı`). 293, genel offline takımın
toplamıdır; 110 yalnız indirme/TUI kapsamındaki 10 sınıfın toplamıdır. İki sayı çelişmez.

### (d) Elle kontroller (redirected stdin)

| Komut | Sonuç |
|---|---|
| `migurdex --help` | `exit 0` |
| `migurdex -h` | `exit 0` |
| `migurdex help` | `exit 0` |
| `migurdex download --help` | alt komut yardımı korunuyor, `exit 0` |
| `migurdex --version` | değişmedi, `exit 0` |
| TUI açılıyor mu | hayır — çıktı stdout'a yazılıp süreç çıkıyor |
| Derleme | 19 proje, 0 uyarı, 0 hata |

### (e) Bilinen release süreç boşluğu: AppImage checksum manifesti

`v1.10.2` release'indeki `sha256sums-linux-x64.txt` ve `sha256sums-linux-arm64.txt` manifestleri
**yalnız `tar.gz` paketini** kapsıyor; iki AppImage bu manifestlerde **yer almıyor**.

Boyutlarla bağımsız doğrulama: bir `sha256sum` satırı `64 hex + 2 boşluk + ad + 1 satır sonu` =
67 + ad uzunluğu bayt tutar.

| Manifest | Kapsanan dosya | Beklenen boyut | Yayımlanan boyut | Yorum |
|---|---|---:|---:|---|
| `sha256sums-linux-x64.txt` | `migurdex-linux-x64.tar.gz` | 91 | 92 | tek dosya |
| `sha256sums-linux-arm64.txt` | `migurdex-linux-arm64.tar.gz` | 93 | 94 | tek dosya |
| `sha256sums-win-x64.txt` | `migurdex-win-x64.zip` | 86 | 87 | tek dosya |

(Beklenen/yayımlanan arasındaki sabit 1 bayt farkı tüm üç manifestte de aynıdır — muhtemelen
satır sonu sonrasında ek bir bayt; oran değil, **sayı** önemlidir: manifest başına tam olarak tek
dosya vardır. Eğer AppImage'ler de kapsansaydı boyutlar 91 + ~150 bayt daha büyük olurdu.)

**Sonuç:** AppImage bütünlüğü release sayfasında yayımlanan özetle **doğrulanamıyor**; yalnız
`.zsync` dosyaları (delta güncelleme için) mevcut. Bu bir indirme özelliği kusuru değil, upstream
release iş akışının (`build-release.yml`) eksik adımıdır — checksum üretimine AppImage'lerin de
eklenmesi gerekir. **Bu PR kapsamında düzeltilmemiştir**; iş akışı upstream'e aittir ve release
süreciyle ilgili ayrı bir konuşma konusudur.

### (f) Uyumluluk / risk

- `f3aaad7` yalnız yeni bir yönlendirme ve iki koruma guard'ı ekliyor; oynatma ve indirme akışı
  değişmedi.
- Etkilenen tek davranış: üst düzey yardım artık TUI'ye girmek yerine doğrudan basılıyor — bu,
  etkileşimli terminalde de istenen davranış.
- Windows ve Linux aynı şekilde fayda görüyor; platforma özgü kod veya `#if` yok.
- Yeni paket bağımlılığı, yeni `config.json` alanı, hedef çerçive veya `/api/v1` sözleşmesi
  değişikliği yok; breaking change yok.

## 13. API dokümantasyon doğrulaması (29 Eylül 2026)

Bu bölüm, `Migurdex.Api` için yazılan tam REST API referansının (`API.md`, dal
`docs/api-reference`, commit `5d59491`) dayandığı **canlı uç doğrulamasını** kaydeder.

> **Bu bir build/test koşumu değildir.** Yeni kod derlenmedi, yeni test yazılmadı, test takımı
> çalıştırılmadı. Bölüm 1'deki **275/275**, bölüm 3'teki **110/110** ve bölüm 12'deki
> **293/293** sonuçları **hiç değişmedi** ve bu bölümle hiçbir ilişkileri yoktur. Buradaki
> "doğrulama" sözcüğü, birim/integrasyon testi değil, **dokümanın yazdığı her uç ve her örneğin
> gerçek bir çalışan servisten alınan yanıtla karşılaştırılması** anlamındadır.

### Ortam

| Öğe | Değer |
|---|---|
| Tarih | 29 Eylül 2026 |
| İşletim sistemi | Windows 11 |
| Doğrulanan ağaç | `docs/api-reference` (tabandan `main` @ `83b9044`); commit `5d59491` |
| Çalıştırılan ikili | `C:\Users\naton\OneDrive\Desktop\migu\api\Migurdex.Api.exe` |
| Dinlenen adres | `http://127.0.0.1:7099` (CLI varsayılanı `7045`'tir; çakışma olmasın diye seçildi) |
| Sürüm damgası | `0.0.0` (yerel build) — release'de `1.10.2` |

Başlatma komutu:

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:7099"
.\Migurdex.Api.exe
```

Sağlık kontrolü:

```
GET /health → 200
{"status":"OK","version":"0.0.0","providers":14,"extractors":38,"rust":true}
```

`rust: true`, Rust native köprüsünün (`migurdex_native.dll`) başarıyla yüklendiğini doğrular;
`false` olsaydı kaynak çözümleme uçları çalışmazdı. `providers=14` ve `extractors=38`, bölüm 6/7
ve bölüm 12 kayıtlarıyla **aynı** değerlerdir.

### 28 uçluk doğrulama tablosu

| # | Uç | Sonuç | Gözlem |
|---:|---|---|---|
| 1 | `GET /health` | 200 | 14 provider, 38 extractor, `rust: true` |
| 2 | `GET /openapi/v1.json` | 200 | OpenAPI 3.1.1, 16 yol; **gövde şemaları boş** |
| 3 | `GET /api/v1/providers` | 200 | 14 kayıt (`type: 1`, `capabilities` 1 veya 3) |
| 4 | `GET /api/v1/extractors` | 200 | 38 kayıt, alfabetik |
| 5 | `GET /api/v1/anime/search` (`q` yok) | 400 | **boş gövde** — model bağlama seviyesi |
| 6 | `GET /api/v1/anime/search?q=test&provider=Yok` | 404 | `{"error":"Provider 'Yok' bulunamadı."}` |
| 7 | `GET /api/v1/anime/search?q=naruto&provider=Animexe` | 200 | 6513 bayt; sağlayıcı zarfı `[{provider,data}]` |
| 8 | aynı uç `&stream=true` | 200 | `searchResult` × n ardından `done` (`curl -N` ile) |
| 9 | `GET /api/v1/anime/Animexe/naruto` | 200 | 221 bölüm, 2 sezon eşlemesi |
| 10 | `GET /api/v1/anime/Animexe/groups?episodeId=naruto/1/1` | 200 | `["AniSekai","YuushaSubs"]` |
| 11 | `GET /api/v1/anime/Animexe/sources?episodeId=naruto/1/1` | 200 | 2 kaynak: `type: 1` (Mp4), `480p`, Tau Video |
| 12 | aynı uç `&stream=true` | 200 | `source` × 2 + `done {"succeeded":2,"failed":0,"errors":[],"totalItems":2}` |
| 13 | `GET /api/v1/metadata/search?q=naruto&source=anilist` | 200 | 11214 bayt, düz `MediaMetadata[]` |
| 14 | `GET /api/v1/metadata/anilist/21` | 200 | 2263 bayt, `source: 0` |
| 15 | `GET /api/v1/metadata/mal/20` | 200 | `source: 1` (Jikan) |
| 16 | `GET /api/v1/metadata/anilist/mal:20` | 200 | çapraz arama, `source: 0` (AniList) |
| 17 | `GET /api/v1/metadata/mal/anilist:21` | 200 | çapraz arama, `source: 1` (Jikan) |
| 18 | `GET /api/v1/metadata/bilinmeyen/1` | 404 | bilinmeyen kaynak adı |
| 19 | `GET /api/v1/tracker/seasons?anilistId=21` | 200 | 2 kalem, `seasonNumber` 0 ve 1 |
| 20 | `GET /api/v1/tracker/lookup?malId=20` | 200 | `MediaMetadata` döndü |
| 21 | `GET /api/v1/tracker/resolve?provider=Animexe&id=naruto&title=Naruto&year=2002&format=TV` | 200 | `fromCache: true` |
| 22 | `GET /api/v1/tracker/align?provider=Animexe&id=naruto` | 200 | `numberingMode: 1`, 4 kalemli zincir |
| 23 | `GET /api/v1/tracker/episode?provider=Animexe&id=naruto&season=1&episode=5` | 200 | `{"season":1,"episode":5,"totalEpisodes":220,"isOverflow":false}` |
| 24 | `POST /api/v1/tracker/mapping` | 200 | **boş gövde** (yazma onaylandı) |
| 25 | `POST /api/v1/extractors/resolve` (localhost hedefi) | 400 | `{"error":"Bu host'a istek gönderilemez."}` — SSRF koruması çalıştı |
| 26 | `POST /api/v1/extractors/resolve` (`Host` başlığı) | 400 | `{"error":"Header 'Host' gönderilemez."}` — başlık enjeksiyonu engeli |
| 27 | `POST /api/v1/extractors/resolve` (doğrudan `.mp4`) | 200 | `{"canExtract":false,"results":[]}` — normal davranış |
| 28 | `GET /api/v1/extractors/resolve` (yanlış yöntem) | 405 | **boş gövde** |

### Gözlemlenen hata biçimleri

Doğrulama, API'nin **üç ayrı hata gövdesi biçimi** kullandığını canlı olarak doğruladı:

| Biçim | Ne zaman | Gözlenen örnek |
|---|---|---|
| A — `{"error": "..."}` | Uç kodunun kendi validasyonu ve 404'ler | `{"error":"Provider 'Yok' bulunamadı."}`, `{"error":"Bu host'a istek gönderilemez."}` |
| B — RFC 7807 `application/problem+json` | `Results.Problem(...)` ile üretilen upstream hataları (502/504) | **canlı gözlemlenemedi** (bkz. bulgu 7) |
| C — çerçeve seviyesi | Zorunlu parametre eksikliği (400) ve yanlış yöntem (405) — **boş gövde** | satır 5 ve satır 28 |

Ayrıca "hata ama HTTP 200" davranışları doğrulandı: `/anime/search` non-stream modunda sağlayıcı
hatası `{"provider":"X","error":"…"}` olarak zarf içinde döner; `/tracker/resolve` eşleşme
bulamasa bile `200` + `entry: null` döner; `/anime/*/sources` extractor hatasında kaynağı atlar ve
`200` + eksik liste döner. **Bir istemci yalnız HTTP durum koduna bakarak hata tespiti yapamaz.**

### Bulunan 7 kusurun özeti

Düzeltilmedi, `API.md` içinde belgelendi. Gerekçe: her biri ayrı kod PR'ı ve ayrı test konusudur.

| # | Kusur | Canlı kanıt |
|---:|---|---|
| 1 | API'de CORS / auth / rate limiting yok (`Program.cs`) | Kod okuması; 24. satırda `POST /tracker/mapping` auth'suz 200 döndü |
| 2 | SSRF koruması yalnız `/api/v1/extractors/resolve` ucunda | 25. satır korumayı doğruladı; anime uçlarında koruma **yok** |
| 3 | SSE kaynak akışında hata sayımı tutarsız (`failed` her zaman 0) | 12. satır `failed: 0`; çözülen 2 kaynak `succeeded: 2` |
| 4 | Zorunlu parametre eksikliği **boş 400** döndürüyor | 5. satır boş gövde; `q=""` ise `{"error":"Arama sorgusu ('q') boş olamaz."}` |
| 5 | `/openapi/v1.json` gövde şemaları boş | 2. satır: `components/schemas` dolu değil |
| 6 | SSE `error` olayı hiç gönderilmiyor | `SseHelper.EventError` tanımlı, tek kullanım yeri tanımın kendisi |
| 7 | `502` (RFC 7807) canlı gözlemlenemedi | Geçersiz bölüm kimlikleri bile `200` + boş liste döndürdü; biçim yalnızca koddan doğrulandı |

Ayrıntılı gerekçe ve her kusurun hangi PR konusu olduğu: `DEVELOPMENT_LOG.md` → bölüm 17.5.

### Temizlik

| Öğe | Durum |
|---|---|
| API süreci | **durduruldu** (temiz kapatma; port `7099` boş) |
| Ortam değişkeni | `ASPNETCORE_URLS` yalnız o PowerShell oturumunda ayarlandı, kalıcı ayar yapılmadı |
| Çalışma ağacı | temiz — `git status` boş, `git diff --check` temiz |
| Kalıcı veri | 24. satır `POST /tracker/mapping` ile **bir** eşleme (`Animexe` / `naruto` → AniList 20) yazıldı; bu, tracker'ın kendi kalıcı SQLite tablosudur ve kalıcı bir eşlemedir (`main`'deki `TrackerMappingStore` verisi). Silinmedi; sonraki `resolve`/`align` çağrılarında `fromCache: true` beklidir |
| Değişen dosyalar | Yalnız `.md` — hiçbir `.cs`, `.csproj`, `.json` veya iş akışı dosyası değişmedi |

> **Not:** 24. satır kalıcı bir yazma işlemidir. Uygulamanın normal davranışıdır ve doğrulama için
> kasıtlı olarak yapılmıştır; `API.md` bölüm 10.3'te bu uç zaten belgelenmiştir.
