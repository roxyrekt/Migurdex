# Bilinen Sorunlar ve Düzeltme Durumu

Bu PR'da ele alınan her kusurun **ne olduğu**, **nasıl doğrulandığı** ve
**ne yapılmadığı** tek tabloda. Ayrıntılı anlatım:
[`DUZELTMELER.md`](DUZELTMELER.md).

---

## E1 – E12 (commit 1, `34561a3`)

| ID | Problem | Nasıl doğrulandı |
|---|---|
| **E1** | `DownloadProgress.isAudioTrack` **hiç atanmıyordu** | `DownloadProgressModelTests` |
| **E2** | `GetStaleResumePartPaths` **bağlayıcısız sıralama → kararsız** | izole 12 koşuda %25 kırılıyordu → şimdi **40/40** |
| **E3** | `DownloadAutoSelectTimeoutSeconds` 5 sn → **60 sn** | ⚠️ **kanıt zayıf** — ilk turda 46,98 sn ölçüldü, sonraki turda hata **üretilemedi** (gerçek zincir ~1,78 sn). Değer **genişletilmiş pay** olarak yükseltildi, düzeltilmiş hata değil |
| **E4** | Bayat `.migurdex-partial` temizliği | `MoveOutput_RemovesStalePartialFileLeftByKilledProcess` |
| **E5** | `IsCrossDevice` Unix'te her zaman `true` → yalnız `EXDEV` | disk dolu/izin hatalarında boşuna tam boy kopya yok |
| **E6** | PID geri dönüşümü → ilgisiz sürece SIGKILL riski | `/proc/<pid>/stat` 22. alan doğrulaması |
| **E7** | Kilt TOCTOU'su (Linux `unlink` açık dosyada başarılı) | silme `flock` açıkken deneniyor |
| **E8** | `_processLocks` `SemaphoreSlim` sızıntısı | 25 hedef → 0 kayıt |
| **E9** | Tahmini toplam kesin gibi gösteriliyordu → **sahte %100 + sahte ETA** | `of ~Y` içindeki `~` korundu (`IsEstimatedTotal`); tahminli satırda yüzde/ETA yok, `~` ile işaretli |
| **E10** | Yetim koruma **ölümde değil `wait()` anında** tetikleniyordu (zombie) | `/proc/<pid>/stat` 3. alanı (`Z`/`X` = ölü). Ölçüm: **0,10 sn** (kontrol: 24 sn) |
| **E11** | Guard yalnız **doğrudan çocuğu** öldürüyordu; `ffmpeg` yetim kalabiliyordu | `KillProcessTree` — `/proc` taraması + BFS. Ölçüm: torun **0,10 sn**, gerçek `ffmpeg` **0,45 sn** (kontrol: 20+ sn) |
| **E12** | ~~"Gözlenen temizlik kırılan bordan geliyor"~~ | **Yanlış atıf.** Kontrollü deney: Python 8 KiB tamponladığı için `EPIPE` oluşmuyor; boruya bağlı `yt-dlp` de 12+ sn yaşıyor. Temizlik guard'ın işi |

---

## Grup F / G / H ve derleme (commit 2, `479ef16`)

| Konu | Problem | Nasıl doğrulandı |
|---|---|---|
| **Grup F** | `ChildProcessTracker.Track` Linux'ta **tamamen no-op** idi; bu yüzden `Track` çağıran **hiçbir** dış süreç korunmuyordu — `MpvPlayerService.cs:176` (`mpv`) ve `ApiClientService.cs:195` (API daemon) | `LinuxOrphanGuard.Attach` tek kavşaktan çağrılıyor; ajanın bulduğu **mükerrer** izleyici çağrısı kaldırıldı (`ExternalProcessRunner.cs:102`) |
| **Grup G** | TUI yönlendirilmiş stdin'de **çöküyordu** | Canlı ölçüm: `migurdex --no-update-check < boş-dosya` → `exit -532462766` + ham stack trace. Düzeltme sonrası **`exit 0`**, stack trace yok |
| Grup G — iki nokta | Tek çökme noktası değil **iki** taneydi: `FuzzyPrompt.cs:341` (`KeyAvailable` korumasız) **ve** `TuiConsole.cs:11` (`WaitForKey`'in `catch` bloğunun **içinde** patlıyordu) | Yalnız `FuzzyPrompt` düzeltilseydi ana döngü çökmeye devam ederdi. 11/11 `KeyAvailable`/`ReadKey` noktası korundu |
| **Grup H** | 6 test sınıfı kendi geçici dizinini açıyor, **hiçbir temizleme kodu** içermiyordu — ne `finally` ne `Directory.Delete` | Ölçülen birikim: **12.567 dizin / 1,41 GB**. `TestTempDirectory` ile koşu başına büyüme **0** |
| **C16** | `Mp4ResumeMetadata.TryRead` senkron okuma | **Ölçülüp dokunulmadı.** `.meta` dosyası **261 bayt**, `TryRead` 95,4 µs senkron; `ReadAllTextAsync` ile **149,8 µs** — async bu boyutta **1,57× daha yavaş**. Bloklama ölçekte değil (dosya başına en fazla `MaxResumeAttempts`+1). Gerekçeyle kayda geçti |
| **IL3000** | `Assembly.Location` single-file yayınlarda **boş** dönüyor → bozuk komut satırı üretilebilir | CI'ın yakaladığı tek gerçek derleme hatası: `main`'de bu dosyada **0**, PR'da **3** uyarı. `Assembly.Location` tamamen kaldırıldı → `AppContext.BaseDirectory` + `File.Exists`. CI'ın birebir komutuyla doğrulandı (IL3000 = 0) |

---

## BULGU 29 ve 30 — commit 2 ve 4

Tam anlatım: [`CI-TEST-ADIMI.md`](CI-TEST-ADIMI.md).

| ID | Problem | Durum |
|---|---|---|
| **BULGU 29** | Tam süit aralıklı kırılıyordu (~%20) → `SQLite Error 14` | ✅ **Çözüldü.** Kök neden **kendi eklediğim bir testin** (`CleanupAll_Removes_Every_Tracked_Directory`) global temizliği test ortasında çağırmasıydı. Düzeltme sonrası **40/40** koşu temiz |
| **BULGU 30** | Linux'da koşu başına 8–17 test çöküyordu → `SQLite Error 10` / `Error 14` | ✅ **Çözüldü.** Kök neden: xUnit v3 **tek koşuda birden çok işlem** açıyor; her biri kökü süpürüp ölen işlemin hâlâ kullanılan dizinini siliyordu. Linux × 8 → **8/8** |

> **İki yanlış teşhis de kayda geçirildi.** BULGU 29'u önce "upstream'ten gelen
> kırılganlık" diye yazdım ve CI test adımını bu yüzden `continue-on-error` yaptım;
> ikisi de yanlıştı. BULGU 30'un "Grup H öncesi de kırılıyor" ölçümü de geçersizdi
> (`git checkout` silinen dosyayı geri getirmişti). Doğru ayırıcı: commit'e dayalı
> `git worktree`. → [`DERSLER.md`](DERSLER.md) #40

---

## BULGU 31 — açık kaldı

| Konu | Etki | Durum |
|---|---|---|
| `/tmp/migurdex-tests` altında `run-` önekli olmayan `bilinmeyen-<guid>` dizinleri var; testler kökün altına **doğrudan** yazıyor. Yeni süpürücü 10 dakikalık yaşlanma kuralı nedeniyle bunlara dokunmuyor | **Etkisiz.** Eski kod da yalnız 1 saatten eski, adı çözülemeyen dizinleri siliyordu | ⬜ Açık — ayrı ve küçük iş |

---

## BULGU 32 — raporlanan test sayısı 599/600 arası salınıyor

| Konu | Etki | Durum |
|---|---|---|
| Keşif (`--list-tests`) sabit **600**, ama tam koşunun raporladığı toplam **599/600** arasında oynuyor — 8 ardışık koşuda 5 kez 599, 3 kez 600. `Başarısız: 0` ve `Atlanan: 0` her koşuda | **Regresyon değil** — hiçbir koşuda test düşmüyor. Ama bir test bazı koşularda hiç çalışmıyor, yani %0,17 kapsama açığı | ⬜ Açık — hangi test olduğu **ayırt edilemedi** |

**Şüphelenilen neden:** xUnit v3'ün **çok işlemli** çalışma modu — BULGU 30'da aynı
mekanizma kanıtlandı (tek `dotnet test` çağrısı birden çok işlem açıyor, PID 645 → 673 →
703 → …; sonuç toplama bu işlemler arasında yapılıyor).

**Neden ayırt edilemedi:** TRX rapor üretimi bu kurulumda çalışmadı; konsol çıktısından
test adı listesi çıkarmak da mümkün olmadı. Kayıt:

## Bilerek yapılmayanlar

Gerekçesiyle birlikte kayda geçirildi; bunlar "gözden kaçmış" değil, **karar**.

| Konu | Karar |
|---|---|
| C16 `Mp4ResumeMetadata` async'e çevrilmedi | 261 baytta async **1,57× yavaş**; çağrı sıklığı ölçekte değil |
| `ExtractorSmokeTests` CI'dan filtreli | Ağ bağımlı ve non-deterministik; filtresiz CI düzenli olarak kırılırdı |
| `linux-arm64` test işi yok | Aynı testler `linux-x64`'te koşuyor; emülasyon süresi kapsam değil |
| Kalıntı toplama "on dakika" yerine "bir sonraki koşu" değil | Ölçülen yarışı kapatmak için; birikim yine sınırsız büyümez |

---
