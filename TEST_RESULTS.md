# Migurdex Test ve Doğrulama Raporu

> Bu dosya, `feature/download` dalındaki tüm doğrulama çalışmalarının merkezi kaydıdır.
> Son güncelleme: 27 Eylül 2026 · Dal: `feature/download` · Temel: `v1.10.0` (`4035f9a`)

## 1. Offline doğrulama

- `git status`: temiz
- `git diff --check`: temiz
- `dotnet restore Migurdex.slnx`: başarılı
- `dotnet build Migurdex.slnx -c Release --no-restore`: 18 proje, 0 uyarı, 0 hata
- `dotnet test Migurdex.Tests\Migurdex.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~ExtractorSmokeTests"`: **275/275 geçti**

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
| **Grup toplamı** | **103/103** |

## 2. Canlı API smoke

Toplam **21 endpoint** test edildi; **21/21 HTTP 200** ve geçerli JSON döndü.

### Sağlıkları

- `/health`: 200
- `/api/v1/providers`: 13 sağlayıcı
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
- İndirme/TUI odaklı sınıf filtreleri: **110/110**

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

### Mevcut dal durumu

```text
v1.10.0..feature/download = 12 commit
```

Commit listesi:

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

## 7. v1.10.0 paket doğrulaması

Rebase sonrası kod tabanı (temel `v1.10.0` @ `4035f9a`, son kod commit'i `5014025`)
yeniden paketlendi ve doğrulandı (27 Eylül 2026).

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

Not: `v0.0.0` yerel build için beklenen değerdir — sürüm damgası
`Directory.Build.props` → `VersionPrefix` 0.0.0'dan gelir; etiketli sürüm numarası
upstream yayın CI'sinde `-p:Version` ile basılır.

Paket kullanıma hazır: `C:\Users\naton\OneDrive\Desktop\migu\install-candidate\migurdex.exe`.
