# Migurdex Test ve Doğrulama Raporu

> Bu dosya, `main` dalındaki tüm doğrulama çalışmalarının merkezi kaydıdır.
> Son güncelleme: 29 Eylül 2026 · Dal: **`main`** · Merge commit: `44f4010`
> (PR #1 merged) · Sürüm: **`v1.10.2`** → `44f4010` · Temel: `v1.10.1` (`4ecd7d7`)
> Release: https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2
> CI: https://github.com/Nutaliaxd/Migurdex/actions/runs/36562344972
> PR: https://github.com/Nutaliaxd/Migurdex/pull/1

## 1. Offline doğrulama

- `git status`: temiz
- `git diff --check`: temiz
- `dotnet restore Migurdex.slnx`: başarılı
- `dotnet build Migurdex.slnx -c Release --no-restore`: 19 proje, 0 uyarı, 0 hata
- `dotnet test Migurdex.Tests\Migurdex.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~ExtractorSmokeTests"`: **275/275 geçti**

Son koşum: 29 Eylül 2026, `v1.10.1` rebase sonrası (portatif .NET SDK 10.0.401).
Bu kod tabanı PR #1 ile `main`'e merge edildi (`44f4010`) ve `v1.10.2` olarak yayınlandı;
test kodu merge sonrasında değişmedi, dolayısıyla bu koşum `v1.10.2` ağacını da temsil eder.

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
  [PR #1](https://github.com/roxyrekt/Migurdex/pull/1) üzerinden açılmıştır. PR `OPEN` ve
  `MERGEABLE` durumdadır; fork PR CI'si maintainer onayı beklediği için
  [run](https://github.com/roxyrekt/Migurdex/actions/runs/36571814259) `action_required` sonucundadır.
- Release paketi, bölüm 7'deki yerel `build.ps1` paketinden farklıdır: CI sürüm damgası basar
  (`-p:Version`), üç platformu kapsar ve `sha256sums-*.txt` ile doğrulanabilir.
- Kök kurulum hâlâ yerel paketi çalıştırmaktadır; ayrıntı ve fark tablosu bkz. bölüm 9.
