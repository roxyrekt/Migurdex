# Migurdex

<p align="center">
  <img src="assets/packaging/migurdex.svg" alt="Migurdex logo" width="160"/>
</p>

<p align="center">
  <a href="https://github.com/roxyrekt/Migurdex/actions"><img src="https://github.com/roxyrekt/Migurdex/actions/workflows/build-release.yml/badge.svg" alt="Build"/></a>
  <a href="https://github.com/roxyrekt/Migurdex/releases" title="Upstream release (indirme özelliği yok)"><img src="https://img.shields.io/github/v/release/roxyrekt/Migurdex" alt="Upstream release"/></a>
  <a href="https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2" title="Bu fork'un indirme özellikli release'i (kurulacak sürüm)"><img src="https://img.shields.io/badge/v1.10.2-fork-8250DF" alt="Fork release v1.10.2"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/roxyrekt/Migurdex" alt="License"/></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/Rust-native-dea584" alt="Rust"/>
</p>

Terminalden Türkçe anime aramak ve izlemek için araç. TUI + yerel HTTP API + Rust ağ katmanı, oynatma MPV ile.

*Watch anime from your terminal: search across Turkish providers and play episodes via MPV.*

![Migurdex demo](assets/docs/demo_3.gif)

## Dağıtım notu (Distribution)

> [!IMPORTANT]
> **Bu README hangi release'i anlatıyor? `Nutaliaxd/Migurdex v1.10.2`.**

Bu depo, [`Nutaliaxd/Migurdex`](https://github.com/Nutaliaxd/Migurdex) fork'udur ve **anime indirme
(download) özelliğini** içerir. Upstream `roxyrekt/Migurdex` `main` dalında bu özellik **henüz yok**;
bu özellik fork içinde [`PR #1`](https://github.com/Nutaliaxd/Migurdex/pull/1) ile merge edilip
`v1.10.2` release'ı olarak yayımlandı. Upstream entegrasyonu
[`roxyrekt/Migurdex PR #2`](https://github.com/roxyrekt/Migurdex/pull/2) üzerinden takip ediliyor
(temiz dal `upstream/download-clean`, 3 commit; durum `OPEN` ve `MERGEABLE`). Eski upstream
[`PR #1`](https://github.com/roxyrekt/Migurdex/pull/1) kapatıldı.

| | Upstream | Bu fork |
|---|---|---|
| Depo | `roxyrekt/Migurdex` | `Nutaliaxd/Migurdex` |
| `migurdex download` / TUI'de `İndir` | Bu fork'ta yok | Var |
| İndirmeyi içeren release | — | [`v1.10.2`](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2) |

**Bu repo üzerinden kurulum yapacaksan indirilecek release `Nutaliaxd/Migurdex v1.10.2` olmalıdır.**
Aşağıdaki tek satır kurulum komutları betiği `raw.githubusercontent.com/roxyrekt/Migurdex/main/...`
adresinden çeker; betiğin kendisi de paketi `roxyrekt/Migurdex` release'inden indirir. Bu yol bu repodaki
kodla aynı şeyi kurmaz ve indirme özelliğini getirmez. İndirme özelliğini istiyorsan
[Kurulum](#kurulum) → *Manuel kurulum* adımından `v1.10.2` paketini indir. Upstream'i izlemek istersen
`roxyrekt/Migurdex` release'lerinden kur; o yol bu fork'un eklediği özelliği içermez.

`migurdex update` ve açılıştaki otomatik sürüm kontrolü de güncellemeyi `roxyrekt/Migurdex` release
kanalından yapar, yani fork release'lerini otomatik kurmaz.

## Belgeler (start here)

*Not: Migurdex'in kendi yerel REST API'sinin tam referansı olan `API.md` şu anda
`docs/api-reference` dalındadır (fork PR #2, `OPEN`) ve `main`'e merge edildiğinde bu listeye
bağlantı olarak eklenecektir.*

- **[`DOWNLOAD.md`](DOWNLOAD.md)** — indirme özelliğinin tam dokümantasyonu. **İndirme özelliğini öğrenmek
  için buradan başla:** genel akış, CLI bayrakları, çıkış kodları, JSON çıktısı, `config.json` alanları,
  test kapsamı ve `Bilinen sınırlar ve riskler`.
- [`TEST_RESULTS.md`](TEST_RESULTS.md) — offline ve canlı API doğrulama kayıtları, upstream sürüm geçiş
  notları ve paket doğrulama sonuçları.
- [`DEVELOPMENT_LOG.md`](DEVELOPMENT_LOG.md) — indirme özelliğinin başlangıçtan PR'a kadar kronolojik
  geliştirme günlüğü.
- [`PR_DESCRIPTION.md`](PR_DESCRIPTION.md) — fork (merged) ve upstream hedefi için PR metni.

## Özellikler

- Fuzzy arama (`opc` -> One Piece gibi)
- 14 Türkçe sağlayıcı: Acheriya, AniHub, AnimeciX, Animexe, AnimPow, Anizium, Anizm, AsyaAnimeleri, Deokwave, OpenAnime, SonAnime,
  TrAnimeIzle, TRAnimeci, TurkAnime (Arşiv)
- AniList ve MAL ile bilgi/poster çekme ve izleme durumu eşitleme
- MPV ile kaldığın yerden devam etme
- API'den gelen doğrudan MP4/HLS kaynaklarını komut satırından veya TUI'den indirme
- Geçmiş, favoriler, arama geçmişi
- Discord RPC (ayarlanabilir)
- Otomatik kaynak seçimi (sunucu / kalite / tür kuralları, uymazsa manuel liste)
- Gizli mod (geçmiş ve senkronu duraklatır)
- Sağlayıcı açma/kapama ve sıralama öncelikleri

## Kurulum

> [!WARNING]
> Aşağıdaki tek satır komutları **upstream `roxyrekt/Migurdex`** betiğini çalıştırır ve paketi upstream
> release'inden indirir; indirme özelliğini içermez. İndirme özelliğini istiyorsan aşağıdaki
> **manuel kurulum** adımından [`Nutaliaxd/Migurdex v1.10.2`](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2)
> paketini indir. Ayrıntı: [Dağıtım notu](#dağıtım-notu-distribution).

**Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/roxyrekt/Migurdex/main/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/roxyrekt/Migurdex/main/install.ps1 | iex
```

Sonra `migurdex` yazıp çalıştır. API arka planda kendisi başlıyor.

Kaldırmak için:

```bash
# Linux, ayarları tutar
curl -fsSL https://raw.githubusercontent.com/roxyrekt/Migurdex/main/install.sh | bash -s -- --uninstall

# Linux, her şeyi siler
curl -fsSL https://raw.githubusercontent.com/roxyrekt/Migurdex/main/install.sh | bash -s -- --purge
```

```powershell
# Windows, ayarları tutar
irm https://raw.githubusercontent.com/roxyrekt/Migurdex/main/install.ps1 | % { & ([scriptblock]::Create($_)) -Uninstall }

# Windows, her şeyi siler
irm https://raw.githubusercontent.com/roxyrekt/Migurdex/main/install.ps1 | % { & ([scriptblock]::Create($_)) -Purge }
```

Manuel kurmak istersen [`Nutaliaxd/Migurdex v1.10.2`](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2)
release sayfasından paketi indirip çalıştırman yeterli. CI her tag'de platform başına şu dosyaları üretir:

| Platform | Dosyalar |
|---|---|
| Linux x86_64 | `migurdex-linux-x64.tar.gz`, `migurdex-x86_64.AppImage`, `migurdex-x86_64.AppImage.zsync` |
| Linux aarch64 | `migurdex-linux-arm64.tar.gz`, `migurdex-aarch64.AppImage`, `migurdex-aarch64.AppImage.zsync` |
| Windows x64 | `migurdex-win-x64.zip` |

Her platform için ayrıca `sha256sums-linux-x64.txt`, `sha256sums-linux-arm64.txt` ve
`sha256sums-win-x64.txt` özet dosyaları yayınlanır. Linux'ta AppImage'yi çalıştırmak için:

```bash
chmod +x migurdex-x86_64.AppImage        # aarch64'te: migurdex-aarch64.AppImage
./migurdex-x86_64.AppImage
```

AppImage için Linux'ta `libfuse2` gerekir (bkz. [Gereksinimler](#gereksinimler)).

### Sürüm notu (v1.10.2)

Bu release'in ana eklemesi anime **indirme** özelliği: API'nin çözdüğü doğrudan `MP4` ve `M3U8/HLS`
kaynakları komut satırından (`migurdex download`) ya da TUI'den (bölüm kaynak ekranında `İndir`) indirilebiliyor.
İndirme MPV açmaz, izleme geçmişine yazmaz ve tracker senkronunu tetiklemez. Ayrıntılı kullanım:
[`DOWNLOAD.md`](DOWNLOAD.md); doğrulama kayıtları: [`TEST_RESULTS.md`](TEST_RESULTS.md); geliştirme
günlüğü: [`DEVELOPMENT_LOG.md`](DEVELOPMENT_LOG.md).

## Gereksinimler

- [MPV](https://mpv.io/) kurulu ve PATH'te olmalı
- HLS (`.m3u8`) indirmek için [yt-dlp](https://github.com/yt-dlp/yt-dlp) kurulu olmalı; segmentleri birleştirmek/aktarmak için
  yt-dlp'nin kullanabildiği [ffmpeg](https://ffmpeg.org/) de gerekebilir. MP4 indirme bu araçlara ihtiyaç duymaz.
- HLS indirme ilerlemesi yt-dlp'nin ürettiği progress satırlarından okunur; Migurdex yt-dlp'ye daima `--progress` verir
  (yt-dlp'de `--print` bayrağı bu çıktıyı kapattığından).
- Migurdex yt-dlp veya ffmpeg'i otomatik indirmez/kurmaz.
- AppImage için Linux'ta `libfuse2`
- Kaynaktan derlemek için: .NET 10 SDK + Rust / cargo

## Kullanım

Akış basit: Arama -> Detay -> Bölüm -> Kaynak -> Oynat / İndir.

1. Ana menüden aramaya gir, adı yaz (liste fuzzy daralır).
2. Sonuçtan seçince açıklama ve bölüm listesi gelir.
3. Bölümü seçince fansub grupları ve kaynaklar (sunucu / kalite / tür) gelir.
4. Kaynağı seçince `Oynat`, `İndir` veya `Geri` seçilir. Otomatik kaynak seçimi de aynı menüyü açar.

`Esc` bir önceki ekrana döner. Yön tuşları + `Enter` ile kullanılıyor. İndirme sırasında `Esc` indirmeyi iptal eder;
indirme MPV'yi açmaz ve izleme geçmişi/tracker senkronunu tetiklemez.

### Komut satırı modu (non-interactive)

Menüye girmeden doğrudan arama/oynatma/indirme:

```bash
migurdex search "one piece"                  # sağlayıcı | başlık | id listeler
migurdex search "naruto" -p TurkAnime --json # JSON çıktı
migurdex play "one piece" -e 12              # 12. bölümü oynat
migurdex play "naruto" -s 2 -p TurkAnime -g FansubAdı
migurdex play "bleach" --debug               # mpv açmadan çözülen URL'yi yazdır
migurdex continue                            # kaldığın yerden devam et

migurdex download "one piece" -e 12
migurdex download "naruto" -s 2 -p TurkAnime -g FansubAdı -o ~/Videos/Anime
migurdex download "bleach" -e 1 --format mp4 --no-subs
migurdex download "bleach" -e 1 --format hls --force --no-resume
migurdex download "bleach" -e 1 --json       # stdout yalnız JSON, ilerleme stderr'de
```

`download` akışı Search -> Details -> Episode -> Group/Source sırasını izler. Yalnız API'nin çözdüğü doğrudan `MP4` ve
`M3U8/HLS` kaynakları kullanılır; `Embed` ve `Unknown` kaynaklar indirilmez. `-p`/`-g` verildiğinde seçimler doğrulanır.
Bölüm verilmezse izleme geçmişine bakılmaz; sezon filtreli deterministik ilk bölüm seçilir. `auto`, mevcut kalite/biçim
tercihleri ve `SourceSelector` kurallarına uyar, en iyi üç uygun adayı sırayla dener. `--force` var olan hedefi değiştirir,
`--no-resume` kısmi MP4 dosyasından devam etmez. Çıkış kodları:

| Kod | Anlam |
|---:|---|
| `0` | Başarı |
| `1` | Çalışma zamanı / upstream (sağlayıcı) hatası |
| `2` | Kullanım (usage) hatası |
| `130` | Video tamamlandı, altyazı aşaması kullanıcı tarafından iptal edildi |

`--debug` URL, header veya token yazdırmaz; indirmeyi başlatmadan yalnız güvenli kaynak özetini gösterir.
`--json` modunda stdout yalnız JSON, ilerleme ve uyarılar stderr'e gider.

Varsayılan çıktı kökü platformun Downloads klasöründe `Migurdex` altıdır (`-o` ile değiştirilebilir). Dosyalar
`<çıktı>/<anime>/SxxEyy - <bölüm>.<uzantı>` düzeninde yazılır. Altyazılar medyanın yanına ayrı `.srt`, `.ass` veya
`.vtt` sidecar dosyaları olarak kaydedilir. HLS'de gerçek medya uzantısı yt-dlp'nin ürettiği çıktıya göre korunur.

### Güncelleme

Açılışta yeni sürüm varsa sorulur (`Evet / Hayır / Bu sürümü atla`). Elle kontrol ve kurulum:

```bash
migurdex update                                    # yeni sürüm varsa onaylı kurar ve otomatik yeniden başlatır
migurdex update --no-restart                       # kurar ama yeniden başlatmaz
migurdex update --check                            # sadece kontrol eder
migurdex update --channel prerelease               # bu seferlik pre-release kanalından bakar
migurdex --version                                 # kurulu sürüm
```

Kanal ve otomatik kontrol Ayarlar menüsünden değiştirilir (Güncelleme Kontrolü / Güncelleme Kanalı). Kontrolü bir
seferlik atlamak için `migurdex --no-update-check` ile başlat. Güncelleme `~/.config/migurdex/` altındaki ayar ve
geçmişe dokunmaz.

## Ayarlar

Ayarlar menüsünden değiştirilebilenler: otomatik oynat, bekleme süresi, Discord RPC ve başlık modu, gizli mod, oynatıcı
logları, API adresi (varsayılan `http://127.0.0.1:7045`), AniList / MAL bağlantısı, sağlayıcı açma-kapama, sıralama
öncelikleri ve otomatik seçim kuralları (Otomatik / Asla / Sadece).

Kaydetmeden çıkarsan (`Esc` / İptal) değişiklikler uygulanmaz.

İndirme varsayılanları `config.json` içinden de değiştirilebilir: `DownloadDirectory`, `YtDlpPath`, `DownloadSubtitles`,
`DownloadResume` ve `DownloadOverwrite`. Sırasıyla platform Downloads/Migurdex dizini, `yt-dlp`, `true`, `true` ve `false`
varsayılanları kullanılır. Eski config dosyaları yeni alanlar eklenmeden de güvenle yüklenir. Migurdex bu harici araçları
otomatik indirmez.

## Güvenlik ve kaynak kullanımı

İndirme yalnız API'nin sağladığı doğrudan medya URL'lerini kullanır; gömülü oynatıcı sayfalarını veya `Unknown` kaynakları
otomatik olarak indirmez. Yalnız HTTP/HTTPS kaynakları kullanılır ve farklı origin'e yönlendirmede
`Authorization`/`Cookie` gibi hassas başlıklar düşürülür. Yalnız erişimine ve indirmesine izin verdiğiniz içerikleri
kaydedin; DRM/paywall korumasını aşmaya çalışan kaynakları indirmeyin. `--debug` çıktısı güvenlik için URL, header ve token
içermez.

## Bilinen sınırlar

- **HLS için harici araçlar gerekir.** `.m3u8` kaynaklarda [yt-dlp](https://github.com/yt-dlp/yt-dlp) zorunludur;
  segmentleri birleştirmek/aktarmak için çoğu durumda [ffmpeg](https://ffmpeg.org/) de gerekir. Migurdex bu
  araçları otomatik indirmez/kurmaz. `MP4` indirme bu araçlara ihtiyaç duymaz.
- **HLS'de resume yoktur.** MP4'te `.part` + `.meta` ile kaldığı yerden devam edilir; HLS'de yt-dlp her
  denemeyi geçici iş dizininde baştan yapar. `--no-resume` yalnız MP4'ü etkiler.
- **AppImage otomatik güncelleme kanalı upstream'tir.** `v1.10.2` workflow'u, AppImage'ın zsync
  güncelleme bilgisini `roxyrekt/Migurdex` olarak gömer. Bu nedenle AppImageUpdate veya benzeri bir
  araç fork release'i değil upstream `latest` sürümünü takip eder. Upstream'e merge edilirse bu
  kanal doğru repo'yu gösterecektir.
- **Altyazı mux edilmez.** Altyazılar videonun yanına ayrı `.srt` / `.ass` / `.vtt` **sidecar** dosyası olarak
  iner; videoya gömülmez. Altyazının oynatılması için oynatıcının sidecar'ı otomatik bulması gerekir.
- **Deokwave sağlayıcısı boş sonuç verebilir.** `deokwave.com` tüm uç noktalarında Cloudflare
  `"Just a moment..."` JS challenge'iyle HTTP 403 döndürdüğü için arama şu anda boş liste dönüyor ve bu
  sağlayıcı üzerinden indirme kaynak çözümlemesine ulaşamadan hata veriyor. Uygulama hatası değil, upstream
  erişim sorunu; diğer sağlayıcılar etkilenmiyor. Ayrıntı:
  [`DOWNLOAD.md`](DOWNLOAD.md) → `Sağlayıcı upstream erişimi (Deokwave)`.
- **PR / release akışı.** İndirme özelliği bu fork'ta
  [`v1.10.2`](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2) release'inde yayında. Upstream
  entegrasyonu ayrı bir akışta takip edilir; indirilecek paket şimdilik bu fork'un release'i olmalıdır,
  upstream raw/release adresleri değil — bkz. [Dağıtım notu](#dağıtım-notu-distribution).
- **İndirme, izleme kaydı üretmez.** İndirme MPV açmaz, izleme geçmişine yazmaz ve AniList/MAL tracker
  senkronunu tetiklemez.

Kapsamlı sınır listesi ve riskler için [`DOWNLOAD.md`](DOWNLOAD.md) → `Bilinen sınırlar ve riskler`
bölümüne bak.

## Dosyalar

Linux'ta `~/.config/migurdex/` altında tutulur:

`migurdex.db` (SQLite veritabanı: geçmiş, favoriler, arama, tokenlar, eşleştirmeler), `config.json`, loglar
`logs/api.log` içinde.

Windows'ta `%APPDATA%\migurdex\` altında aynı yapı var.

## Mimari

| Proje             | Ne yapar                                               |
|-------------------|--------------------------------------------------------|
| `Migurdex.Api`    | Arama, detay, kaynak çözümleme                         |
| `Migurdex.Cli`    | Terminal arayüzü                                       |
| `Migurdex.Core`   | Plugin yükleyici, Rust köprüsü, extractor'lar          |
| `Migurdex.Shared` | Modeller ve arayüzler (`IAnimeProvider`, `IExtractor`) |
| `Migurdex.Native` | Rust tarafı HTTP istemcisi                             |
| `Plugins/`        | Sağlayıcılar (`Migurdex.Plugins.*`)                    |

Akış: `TUI/CLI -> API -> plugin (+ Rust HTTP) -> kaynak listesi -> MPV veya indirici`. Native kütüphane
(`libmigurdex_native.so` /
`migurdex_native.dll`) API ile birlikte gelir, eksikse API başlamaz.

Built-in extractor'lar `Migurdex.Core/Extractors` altında. API tarafında `GET /api/v1/extractors` ve
`POST /api/v1/extractors/resolve` ile de çağrılabiliyor.

TurkAnime sağlayıcısı, kapanan sitenin arşivinin temizlenip doğrulanmış halini kullanır (ölü kayıtlar atıldı, başlıklar
onarıldı, AniList/MAL eşleştirmeleri eklendi; canlı Turso veritabanı üzerinden sorgulanır). Ham SQLite dosyası:
[roxyrekt/turkanime-db](https://huggingface.co/datasets/roxyrekt/turkanime-db/blob/main/turkanime-v1.db).

## Derleme

```bash
# Linux
./build.sh            # debug
./build.sh --release
./build.sh --publish  # dist/ altına paket

# Windows
.\build.ps1
.\build.ps1 -Release
.\build.ps1 -Publish
```

Geliştirirken iki terminalde:

```bash
dotnet run --project Migurdex.Api
dotnet run --project Migurdex.Cli
```

Testler:

```bash
dotnet test
```

Son offline doğrulama (29 Eylül 2026, `v1.10.2` yayını kapsamında, upstream `v1.10.1` üzerine rebase sonrası): çalışma
ağacı temiz; Release derlemesi 19 proje / 0 uyarı / 0 hata; ağ bağımlı `ExtractorSmokeTests` hariç **275/275** test geçti.
İndirme özelliğiyle ilgili test sınıfları ayrı grup koşularında **110/110** (10 sınıf) geçti. Ayrıntılı sonuçlar:
[`DOWNLOAD.md`](DOWNLOAD.md) → `Test kapsamı` ve [`TEST_RESULTS.md`](TEST_RESULTS.md).

Temiz dal `upstream/download-clean` üzerinde non-TTY üst düzey yardım düzeltmesi (`f3aaad7`) sonrası aynı
filtre ile **293/293** test geçti (275 taban + 18 yeni `TopLevelHelpTests`).

Canlı API smoke (26 Eylül 2026): 21 endpoint test edildi; 21/21 HTTP 200 ve geçerli JSON döndü. `/health`
13 sağlayıcı / 38 extractor / Rust hazır bildirdi; `q=one piece` araması 13/13 sağlayıcıda başarılı oldu ve
153 sonuç döndü. Anime detayları, gruplar, kaynaklar, metadata ve tracker lookup uçları da doğrulandı.
API loglarında uygulama hatası yok; yalnızca iki upstream durumu (TrAnimeIzle captcha, Vidmoly reklam
redirect’i) tespit edildi. Ayrıntılar: [`DOWNLOAD.md`](DOWNLOAD.md) → `Canlı API smoke doğrulaması`.

v1.10.0 geçişi: upstream sağlayıcı sayısı 14’e çıktı (Deokwave eklendi), altyazı oynatma davranışı düzeltildi ve
`feature/download` dalı `v1.10.0` üzerine temiz rebase edildi; çakışma çıkmadı.

v1.10.1 geçişi: TurkAnime veritabanı bağlantısı güncellendi (`roxyrekt/turkanime-db`), Anizm
isimsiz fansub grupları düzeltildi ve AppImage güncelleme akışına zsync/AppRun iyileştirmeleri
geldi. `feature/download` dalı `v1.10.1` üzerine rebase edildi; tek çakışma `Program.cs`
(TUI iptal yapısı korunarak upstream'in `ReadKey` guard'ı alındı) çözüldü. Ayrıntılar:
[`TEST_RESULTS.md`](TEST_RESULTS.md) → `v1.10.1 geçişi`.

Paket doğrulaması (27 Eylül 2026): üretilen `migurdex-win-x64.zip` (61.605.017 bayt) 14 sağlayıcı /
38 extractor içeriyor; paketin SHA-256 özeti dağıtılan dosyayla birebir aynı ve smoke kontrolleri
(`--version`, `download --help`) `EXIT=0` ile geçti. Ayrıntılar: [`TEST_RESULTS.md`](TEST_RESULTS.md) →
`v1.10.0 paket doğrulaması`. Release paketlerini [`Nutaliaxd/Migurdex v1.10.2`](https://github.com/Nutaliaxd/Migurdex/releases/tag/v1.10.2)
etiketiyle CI üretir; dosya adları [Kurulum → manuel kurulum](#kurulum) tablosundaki gibidir.

## Yol Haritası

- [x] MAL ve AniList senkronu
- [ ] Yeni bölüm atlama
- [ ] Intro skip

---

GPL-3.0. Detay için [LICENSE](LICENSE) dosyasına bak.
