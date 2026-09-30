# Linux Doğrulama Kaydı — 2. Tur (nihai)

**Tarih:** 30 Eylül 2026 · **Ortam:** WSL2 Ubuntu-24.04 (kernel 6.6.87.2), .NET SDK 10.0.401,
yt-dlp 2026.08.19, ffmpeg/ffprobe · **Yöntem:** `~/build`'e rsync + checksum doğrulaması

> **Git kuralı:** Bu turda **hiçbir git komutu çalıştırılmadı**. WSL'de `git push`
> kullanılması bu oturumda 4 adet ~5 saatlik takılı süreç bırakmıştı (hepsi aslında
> başarılıydı, kimlik isteminde asılıydı). Doğrulama her zaman `git ls-remote` ile yapıldı.

---

## 1. Özet — 3 bulgu, 1'i ölü koruma, hepsi düzeltildi

| | Bulgu | Etki | Durum |
|---|---|---|---|
| 🔴 | `LinuxOrphanGuard` **hiç işe yaramıyordu** (ebeveyn içindeki thread) | `kill -9` sonrası 4/4 yetim | yeniden yazıldı |
| 🔴 | Koruma **ölümde değil `wait()` anında** tetikleniyordu (zombie) | reap etmeyen ebeveynde koruma sıfır | düzeltildi |
| 🟠 | Guard yalnız **doğrudan çocuğu** öldürüyordu | `ffmpeg` yetim kalabiliyordu | düzeltildi |
| 🟠 | `GetStaleResumePartPaths_TrimsOldest…` **%95–100 kırık** | test hatası | düzeltildi |
| 🟠 | `Download_WhenSubtitle…` 2 sn yük altında yetmiyor | test hatası | düzeltildi |
| ⚪ | **Yanlış atfım**: "temizlik bordan geliyor" | kayıt hatası | düzeltildi |

---

## 2. Yeşil sonuçlar

### 2.1 Gerçek indirme + `kill -9`

| Senaryo | Koşu | Sonuç |
|---|---|---|
| Gerçek HLS + gerçek `yt-dlp`, `kill -9` @12–14 sn | **11/11** | **0 yetim**, hedef <1 sn'de öldü |
| Tekrarlı ölçüm | 5/5 | 0 yetim |
| **Sessiz** çocuk (stdout'a hiç yazmıyor) | **3/3** | **0 yetim**, <1 sn |

Test indirmeleri: `download "One Piece Fan Letter" -p AnimeciX --format hls -e 1` →
AnimeciX → GoogleDrive M3U8, **370,55 MiB**, 25–105 MiB/s.

### 2.2 Test turları

| Tur | Sonuç |
|---|---|
| 1–5 | `Failed: 0, Passed: 510, Total: 510` · **rc=0** (her biri) |
| `GetStaleResumePartPaths` × 20 | **20/20 rc=0**, 80 test, 0 kayıp |
| `Download_WhenSubtitleIsCancelled` × 20 | **20/20 rc=0**, 0 kayıp |

Build (iki kez): **0 hata / 0 uyarı**.

`ExtractorSmokeTests` ayrı koşuldu, **regresyon sayılmadı**: 9/10 ve 14/20 geçti;
başarısızlar MixDrop, VK, Abyss, HexUpload, Rumble, Cyberfile — hepsi "0 results",
yani üçüncü taraf siteler erişilemiyor. Toplam test sayısı bile koşular arası
değişiyor (10 → 20), veri-güdümlü.

### 2.3 İzleyici süreci

```
10010  9991  dotnet  /root/.dotnet/dotnet .../migurdex.dll --internal-watch-orphan 9991 1924501 10009 1924603
 9991  9983  dotnet  .../migurdex.dll download One Piece Fan Letter ...
10009  9991  yt-dlp  /usr/bin/python3 /usr/local/bin/yt-dlp --no-config --no-playlist --no-part --newline --progress ...
```

- `Environment.ProcessPath` = `/root/.dotnet/dotnet` → `dotnet` dalı → entry DLL
  (`migurdex.dll`) argüman olarak eklendi. **Yol doğru.**
- Argüman doğrulaması: `/proc/10009/stat` alan 22 = **1935084**, izleyicinin 4. argümanı
  = **1935084** ✓; ebeveyn için de ✓.
- Koruma zinciri yok: indirme başına **1** izleyici, 2 paralel indirmede **2**.

### 2.4 Sızıntı ve yanlış öldürme

| Kontrol | Sonuç |
|---|---|
| Başarılı indirme sonrası izleyici (bitiş +0…+12 sn) | **0** |
| Hatalı indirme sonrası izleyici (bitiş +0…+12 sn) | **0** |
| 2 paralel indirme | **ikisi de exit 0**, sonrası izleyici = 0, yt-dlp = 0 |
| `sleep 300` / `bash` döngüsü / `ffprobe` | **üçü de yaşadı** (t+1/3/5/8/12/26 sn) |
| PID geri dönüşümü: ölü ebeveyn + **doğru** `childStartTime` | çocuk **öldü** ✓ |
| PID geri dönüşümü: ölü ebeveyn + **yanlış** `childStartTime=1` | çocuk **yaşadı** ✓ |
| Hedef zaten yok | izleyici **hemen çıktı** ✓ |

### 2.5 Kaynak maliyeti

| Ölçüm | Değer |
|---|---|
| İzleyici RSS (10 ölçüm) | 32,4–34,4 MB |
| İzleyici CPU (tüm indirme boyunca) | **`00:00:00`** |
| İzleyici thread sayısı | 7 |
| Karşılaştırma | ana süreç 58–80 MB, `yt-dlp` 69–72 MB |

Kabul edilebilir: hedef bitince anında çıkıyor, 6 saatlik tavan var, CPU sıfır.

### 2.6 Diskler arası taşıma (PR-2) — doğrulandı

Mount'lar: `/`, `/root`, `/tmp` → ext4 (`st_dev=2096`) · `/mnt/c` → 9p/v9fs (`st_dev=69`)

| Senaryo | Mount çifti | Sonuç |
|---|---|---|
| Farklı dosya sistemi | `/tmp` → `/mnt/c` | ✅ 20.344.071 B, kaynak silindi, 0,13 sn |
| Aynı dosya sistemi (atomik) | `/tmp` → `/root` | ✅ 0,00 sn |
| **Uçtan uca farklı** | iş `/tmp` → hedef `/mnt/c` | ✅ **307.134.562 B**, h264+aac 1920×1080, **300,025 sn**, 160,9 sn, kalıntı 0 |
| **Uçtan uca aynı** | iş `/tmp` → hedef `/root` | ✅ 307.134.562 B, 300,025 sn |

Çıplak `rename(2)`: `/tmp` → `/mnt/c` → `errno=18 (EXDEV)` ✓

---

## 3. BULGU 1 (🔴 KRİTİK) — `LinuxOrphanGuard` ölü korumaydı

### İlk uygulama

```csharp
var thread = new Thread(() => Watch(parentPid, childPid, childStartTime));
thread.Start();
```

Ana süreç `SIGKILL` ile öldüğünde **thread de anında ölür**; `KillIfAlive` hiç
çalışamaz. Yapısal olarak imkânsız.

### Çürütme kanıtları

| Kanıt | Sonuç |
|---|---|
| Guard thread gerçekten başlıyor mu | **EVET** — `/proc/16767/task/16779` → `comm = migurdex-linux-` (`TASK_COMM_LEN=16` ile kesilmiş). Thread yok değil, **yanlış yerde** |
| 4 sessiz alt süreç, `kill -9` sonrası | **4/4 YETİM KALDI** (10 sn sonra bile) |
| stdout'a yazan `bash` döngüsü | 6 sn yaşadı, yetim kaldı |

PID geri dönüşümü için eklenen `ReadStartTime` doğrulaması bile **hiçbir yola
ulaşmıyordu** — o satırlar `Watch`'in içindeydi, `Watch` hiç dönmüyordu.

### Çözüm — izleyici ayrı süreç

```
dotnet migurdex.dll --internal-watch-orphan <ebeveynPid> <ebeveynBaşlangıç> <çocukPid> <çocukBaşlangıç>
```

`Program.Main` bu argümanı görünce TUI'ye girmeden `LinuxOrphanGuard.RunWatcher(args)`
çağırıp dönüyor.

| Karar | Gerekçe |
|---|---|
| İzleyici **ayrı süreç** | Ebeveyn `SIGKILL`'da öldüğünde yaşamaya devam etmeli; ebeveyn içindeki hiçbir mekanizma bunu başaramaz |
| `sh`/`setsid`/`awk` **yok** | PID ve başlangıç zamanı doğrulaması yönetilen kodda; harici araç bağımlılığı ve kabuk kaçış riski yok |
| `Environment.ProcessPath` + entry DLL | Framework-dependent çalıştırmada host `dotnet`; DLL ayrıca verilmeli |
| **6 saatlik tavan** | Kalıcı sürgüç olmasın; iş dizini süpürmesiyle aynı eşik |
| Çocuk bitince **hemen çık** | Ek süreç yalnız indirme boyunca yaşar |

---

## 4. BULGU 2 (🔴 KRİTİK) — koruma ölümde değil `wait()` anında tetikleniyordu

Bu, BULGU 1'in düzeltmesi **ölçülünce** ortaya çıktı.

### Ölçüm

```
[t+14.0s … t+38.0s]  MIG=state=Z   HEDEF=YAŞIYOR   IZLEYICI=YAŞIYOR(11872)   ← 24 saniye
>>> EBEVEYN SIMDI wait() ILE REAP EDIYOR   (wait() dondu rc=-9)
[reap+0s]  MIG=/proc KAYIP   HEDEF=YAŞIYOR   IZLEYICI:YAŞIYOR
[reap+1s]  MIG=/proc KAYIP   HEDEF=OLDU      IZLEYICI:CIKTI                  ← ≤1 saniyede öldürdü
```

### Kök neden

`ReadStartTime` `/proc/<pid>/stat` **22. alanı** (starttime) okuyor — bu alan
**zombie süreçte de geçerlidir**. `IsProcessGone` ise `kill(pid, 0)` kullanıyor — o da
zombie'a `0` döner. Yani büyükan reap etmedikçe izleyici **sonsuza kadar** yokluyor.

### Etki

Migurdex'i **reap etmeyen** her ebeveyn için koruma sıfır:
- python `subprocess` (`wait()` çağırmadan)
- arka planda başlatıp devam eden kabuk
- alışılmadık reaping yapan servis yöneticisi

Sıradan etkileşimli bash senaryosunda bash ~1 sn içinde reap ettiği için maruziyet
gerçek ama **varsayılan değil** — yani "nadir bir köşe durumu" değil, sessizce
güvenilmemesi gereken bir mekanizma.

### Çözüm

`/proc/<pid>/stat` **3. alanı (state)** okunuyor; `Z` (zombie) ve `X` (ölü) **ölü**
sayılıyor.

```csharp
internal static bool IsProcessAlive(int pid)
{
    if (!IsSupported || pid <= 0) return false;   // kill = libc, Windows'ta yok
    var state = ReadProcessState(pid);
    if (state != '\0') return state is not ('Z' or 'X');
    ...
}
```

`IsProcessGone` **kaldırıldı** — tam olarak zombie tuzağını içeriyordu ve kullanılmıyordu.

---

## 5. BULGU 3 (🟠) — guard yalnız doğrudan çocuğu öldürüyordu

### Ölçüm

2/2 koşuda: korunan `bash` sarmalayıcı <1 sn'de öldü, **torunu** (`sleep 900`)
**18 sn** yaşadı (`CANLI(ppid=9982)`, yeniden bağlanmış).

`ffmpeg` **hiçbir zaman** Migurdex'in doğrudan çocuğu değildir — daima `yt-dlp`'nin
çocuğudur. Sınıf dokümanının "`yt-dlp`/`ffmpeg` yetimlerini engeller" iddiası gerçeği
yansıtmıyordu.

Ölçülen HLS koşusunda bu ısırmadı: yt-dlp parçaları yerel indiriciyle çekmişti, kill
anında `ffmpeg` çocuğu yoktu (Migurdex'in tüm torunları = {yt-dlp, izleyici}). Ancak
yt-dlp'nin `ffmpeg` çağırdığı her akışta (ayrı video+ses birleştirme, `--exec`
post-process) açıktı.

### Çözüm — `KillProcessTree`

`/proc` taranır → PID→ebeveyn haritası → kökten BFS → her aday öldürülmeden hemen
önce `/proc/<pid>/stat` yeniden okunur (PID geri dönüşümü koruması) → **torunlar önce,
kök sonra** (kök ölünce çocuklar yeniden bağlanmasın).

Harici araç (`pkill -P`, `pstree`) kullanılmadı; tümü yönetilen kodda.

---

## 6. BULGU 4 (🟠) — kardeş testi unutulmuş

`GetStaleResumePartPaths_TrimsOldestGroupsBeyondRetentionLimit` Linux'ta **%95–100**
kırılıyordu (20 izole koşunun 20'sinde, 8 tam koşunun 7'sinde).

### Kök neden

Test 5 grup × 6 dosya yazıyor ama **yalnız `.part`** dosyasının zaman damgasını
ayarlıyor. `GetStaleResumePartPaths` grup zamanını **grup içindeki en yeni dosyadan**
alıyor (`if (modified > group.ModifiedUtc)`). Kalan 25 dosya yazım anını koruyor.

Bu dosya sisteminin zaman çözünürlüğü yazma döngüsünden kaba — ölçüldü (`/tmp`, 8
ardışık yazma):

```
dosya 0  mtime_ns=1790740012333165831
dosya 1  mtime_ns=1790740012333165831   fark=0
...
dosya 7  mtime_ns=1790740012333165831   fark=0
```

→ 5 grubun `ModifiedUtc`'si tam olarak eşit → `Skip(2)` seçimi bağlayıcıya
(fingerprint hash sırası) kalıyor → testin beklediği indeks sırasıyla örtüşmüyor.

**Üretim kodu doğruydu** — `ThenByDescending(Key, Ordinal)` gerçekten kararlı ve
`IsDeterministicWhenTimestampsTie` 20/20 geçiyor.

**Çürütücü kanıt:** yeşil oranı %12,5 (8 koşunun 1'i); kapanış yalnızca yazma döngüsü
bir zaman-tick sınırını aştığında oluyor.

Kardeş test `HandlesGlobMetacharactersInFileName` aynı düzeltmeyi **almıştı** — ben
onu düzeltip bunu **atlamıştım**.

---

## 7. BULGU 5 (🟠) — 2 sn bütçesi yalnız izole koşumda yetiyor

`Download_WhenSubtitleIsCancelledAfterVideoFinal` → `WaitAsync(TimeSpan.FromSeconds(2))`

- 4 zorunlu turun **3'ünde** `System.TimeoutException`
- 8 koşunun 1'inde
- İzole koşumda **20/20 geçiyor**, ortalama 465 ms (4,4× marj)

494 test paralel koşarken 2 sn yetersiz. **Yük duyarlılığı, ürün hatası değil** —
15 sn'e çıkarıldı.

---

## 8. DÜZELTME — yanlış atfım

Önceki kayıtta şu yazıyordu:

> "Gözlenen 'temizlik' **kırılan stdout borusundan** geliyordu, korumadan değil."

**Bu yanlıştı.** Kontrollü deney çürüttü:

| Varyant | Kurulum | `kill -9` sonrası |
|---|---|---|
| Boru | yt-dlp `stdout+stderr` → FIFO, tek okuyucu (ebeveyn) öldürüldü | **yt-dlp 12+ sn YAŞADI** |
| Dosya | yt-dlp `stdout+stderr` → dosya, ebeveyn öldürüldü | **yt-dlp 12+ sn YAŞADI** |

Python **8 KiB blok tamponluyor**; tampon dolmadan `write` olmuyor, dolayısıyla
`EPIPE` oluşmuyor. Yani gerçek akıştaki temizlik **guard'ın işidir**.

Bu düzeltmenin bir yan etkisi var: "thread sürümü 4/4 yetim bıraktı ama boru öldürdü"
tespiti de yanlış atıftı; doğru olan "guard hiç çalışmadı **ve** boru da çalışmıyor".

---

## 9. Metodoloji uyarısı (ajanın kendi düzeltmesi)

İlk turlarda iki ölçüm hatası yapıldı ve ajan bunu **kendi** bildirdi:

1. `ppid == 1` kriteri WSL2'de **geçersiz** — yetimler PID 1'e değil bir **subreaper
   relay** sürece yeniden bağlanıyor (`ppid=10078` gibi).
2. `ps` / `kill -0` **zombie'ı canlı sayıyor**.

Bu yüzden nihai sayımlar `/proc/<pid>/stat` **state** alanıyla yapıldı. Bir koşuda
(07:23) `yt-dlp`'nin 15 sn yaşadığı görüldü; `/proc` state ölçümüyle **9+1 koşuda yeniden
üretilemedi** (her biri ≤1 sn temiz). Ajan bu tek gözlemi kusur olarak bildirmedi ama
diğer ölçümlerle tutarsız olduğu için açıkça belirtti.

---

## 10. Ek gözlemler

| | |
|---|---|
| `mpv` korunmuyor | `MpvPlayerService.cs:175` doğrudan `Process.Start` kullanıyor; `ExternalProcessRunner` değil. Oynatma yolu — indirme kapsamı dışı |
| API daemon korunmuyor | `ApiClientService.cs:192-204` düz `Process.Start`. Migurdex öldürülünce API daemon yetim kaldı |
| Test paketi `/tmp` sızıntısı | Her koşu `/tmp/migurdex-{test,synctest,dbtest,listtest,mallisttest,tokentest}-<guid>` bırakıyor; 241 dizin / 19 MB birikmiş |

---

## 11. Temizlik

| Öğe | Durum |
|---|---|
| Migurdex / izleyici / yt-dlp / ffmpeg / ffprobe / API daemon | **0 kaldı** |
| `/tmp/migurdex-*` | **0 kaldı** (241 dizin + 19 MB silindi) |
| İndirme klasörleri (371 MB + diğerleri) | **0 kaldı** |
| `203.0.113.77/32` ağ aliası | kaldırıldı |
| `~/.config/migurdex/config.json` | geri yüklendi (`YtDlpPath = yt-dlp`) |
| Git | **hiç komut çalıştırılmadı** (yalnız istenen `git status --short`) |
| Repo | `diff -q` ile ajanın test ettiği sürümle **byte-byte aynı** |

---

## 12. Windows karşılaştırması

| | Windows | Linux |
|---|---|---|
| Alt süreç koruması | `ChildProcessTracker` + Job Object (`KILL_ON_JOB_CLOSE`) | ayrı izleyici süreci |
| `SIGKILL` sonrası | süreç ağacı zaten ölür | ebeveyn öldüğünde izleyici tetiklenir |
| Zombie kavramı | yok (Windows'ta handle kapanınca süreç biter) | `Z` durumu ayrıca ele alınmalı |
| Torun taraması | Job Object tüm ağacı kapsar | `/proc` taraması gerekir |
| Doğrulama | 4 gerçek indirme, 0 yetim | 11/11 + 3/3 sessiz, 0 yetim |
