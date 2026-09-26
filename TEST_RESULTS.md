# Migurdex Test ve Doğrulama Raporu

> Bu dosya, `feature/download` dalındaki tüm doğrulama çalışmalarının merkezi kaydıdır.
> Son güncelleme: 27 Eylül 2026 · Dal: `feature/download`

## 1. Offline doğrulama

- `git status`: temiz
- `git diff --check`: temiz
- `dotnet restore Migurdex.slnx`: başarılı
- `dotnet build Migurdex.slnx -c Release --no-restore`: 18 proje, 0 uyarı, 0 hata
- `dotnet test Migurdex.Tests\Migurdex.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~ExtractorSmokeTests"`: **274/274 geçti**

İndirme/TUI odaklı test grupları ayrıca tek tek koşuldu:

| Test sınıfı | Sonuç |
|---|---:|
| `Mp4DownloaderTests` | 20/20 |
| `SubtitleDownloaderTests` | 10/10 |
| `HlsDownloaderTests` | 7/7 |
| `DownloadServiceTests` | 7/7 |
| `DownloadCommandTests` | 15/15 |
| `ApiClientServiceTests` | 2/2 |
| `TuiMarkupSafetyTests` | 23/23 |
| `FuzzyPromptSearchableTests` | 18/18 |
| **Grup toplamı** | **102/102** |

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

### Bulunan hata

HLS indirmesi sırasında ilerleme satırı güncellenmedi ve şu şekilde kaldı:

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
- Sonuç olarak TUI progress satırı hiç güncellenmiyor.

**Düzeltme:** yt-dlp argüman listesine `--progress` eklenmesi gerekiyor. Bu düzeltme şu anda ayrı bir çalışma oturumunda uygulanıyor.

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
- İki gerçek test bulgusu not edildi:
  1. **HLS progress güncellenmiyor** — düzeltme devam ediyor.
  2. **Ortam bulgusu:** sistem dotnet’inde ASP.NET Core runtime yoktu; geçici SDK kurulunca aşıldı. Bu bir kod hatası değil.
- Uygulama tarafında kalıcı bir çökme veya veri bozulması bulunmadı.
