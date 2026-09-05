# Migurdex

<p align="center">
  <img src="assets/packaging/migurdex.svg" alt="Migurdex logo" width="160"/>
</p>

<p align="center">
  <a href="https://github.com/roxyrekt/Migurdex/actions"><img src="https://github.com/roxyrekt/Migurdex/actions/workflows/build-release.yml/badge.svg" alt="Build"/></a>
  <a href="https://github.com/roxyrekt/Migurdex/releases"><img src="https://img.shields.io/github/v/release/roxyrekt/Migurdex" alt="Release"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/roxyrekt/Migurdex" alt="License"/></a>
</p>

Terminalden anime arayıp izlemeyi sağlayan modüler araç: klavye odaklı TUI + sağlayıcı plugin'leriyle konuşan HTTP API + Rust ağ katmanı, oynatma MPV ile.

*Watch anime from your terminal: search across Turkish providers and play episodes via MPV.*

![Migurdex demo](assets/docs/demo.gif)

## Özellikler

- **Fuzzy arama** — harf atlamalı, skorlu sıralama (`opc` → One Piece).
- **12 Türkçe sağlayıcı** — Acheriya, AnimeciX, Animexe, AnimPow, Anizium, Anizm, AsyaAnimeleri, OpenAnime, SonAnime, TrAnimeIzle, TRAnimeci, TurkAnime.
- **Metadata** — AniList ve MAL üzerinden bilgi, poster ve sezon eşleştirme.
- **MPV ile izleme** — kaldığın yerden devam, ilerleme takibi, altyazı desteği.
- **Geçmiş ve favoriler** — tek tuşla devam etme, arama geçmişi yönetimi.
- **Otomatik kaynak seçimi** — sunucu / kalite / tür kuralları, uymazsa manuel listeye düşer.

## Hızlı Başlangıç

Hazır sürümleri [Releases](https://github.com/roxyrekt/Migurdex/releases) sayfasından indirin.

**Arşiv (Linux / Windows):** `tar.gz` / `zip` dosyasını açın, içindeki `migurdex` (veya `migurdex.exe`) dosyasını çalıştırın — API arka planda otomatik başlar.

**AppImage (Linux):**

```bash
chmod +x Migurdex-x86_64.AppImage
./Migurdex-x86_64.AppImage
```

## Gereksinimler

| Ne | Neden |
|---|---|
| [MPV Player](https://mpv.io/) (PATH'te) | Video oynatma |
| `libfuse2` (Linux) | AppImage'i çalıştırmak için |

Kaynaktan derlemek için ek olarak: .NET 10 SDK + Rust / cargo.

## Kullanım

**Arama -> Detay -> Kaynak -> Oynat:**

1. **Arama:** ana menüden aramaya girin, adı yazın (fuzzy daraltır).
2. **Detay:** sonuçtan seçince açıklama ve bölüm listesi gelir.
3. **Kaynak:** bölümü seçince fansub grupları ve çözülen kaynaklar (sunucu / kalite / tür) gelir.
4. **Oynat:** kaynağı seçince MPV açılır; kaldığınız yer kaydedilir.

`Esc` bir önceki ekrana döner.

## Kaynaktan Derleme

<details>
<summary>Komutlar (Linux / Windows)</summary>

```bash
# Debug dev-loop (Linux)
./build.sh
# Windows
.\build.ps1

# Release dev-loop
./build.sh --release
.\build.ps1 -Release

# Dağıtım paketi (dist/ altına arşiv)
./build.sh --publish
.\build.ps1 -Publish
```

Ardından iki ayrı terminalde:

```bash
# 1. API servisi
dotnet run --project Migurdex.Api

# 2. Terminal istemcisi
dotnet run --project Migurdex.Cli
```

</details>

## Yapılandırma

Tüm veriler `~/.config/migurdex/` altında tutulur (`config.json`, `history.json`, `search_history.json`, `favorites.json`). API logları `~/.config/migurdex/logs/api.log` dosyasına yazar.

## Mimari

| Proje | Rol |
|---|---|
| `Migurdex.Api` | HTTP API: arama, detay, kaynak çözümleme |
| `Migurdex.Cli` | Klavye odaklı terminal arayüzü |
| `Migurdex.Core` | Plugin yükleyici, Rust köprüsü, extractor'lar |
| `Migurdex.Shared` | Modeller + arayüzler (`IAnimeProvider`, `IExtractor`) |
| `Migurdex.Native` | Rust ağ katmanı: HTTP istemcisi, emülasyon |
| `Plugins/` | Sağlayıcı plugin'leri (`Migurdex.Plugins.*`) |

Akış: `TUI → API → plugin (+ Rust HTTP) → kaynak listesi → MPV`. Native kütüphane (`libmigurdex_native.so` / `migurdex_native.dll`) API ile birlikte gelir; eksikse API başlamaz.

## Yol Haritası

- [ ] **MyAnimeList & AniList izleme durumu eşitleme**
- [ ] **Otomatik yeni bölüm takibi**
- [ ] **Intro skip** (aniskip benzeri)

---

GPL-3.0 Lisansı. Detaylar için [LICENSE](LICENSE) dosyasına bakın.
