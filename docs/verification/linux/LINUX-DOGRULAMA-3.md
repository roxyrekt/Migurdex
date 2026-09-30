# Linux Doğrulama — Zombie ve Torun Düzeltmeleri (Tur 3)

**Tarih:** 30 Eylül 2026 · **Ortam:** WSL2 Ubuntu-24.04, .NET SDK 10.0.401,
yt-dlp 2026.08.19, ffmpeg 6.1.1 · **Kod donduruldu** (sha256 doğrulandı)

> Önceki tur: [`LINUX-DOGRULAMA-2.md`](LINUX-DOGRULAMA-2.md)

---

## 0. Sonuç — ikisi de kanıtlandı, **kontrol deneyleriyle**

| Düzeltme | Ölçüm | Kontrol (düzeltme olmasaydı) |
|---|---|---|
| **A — zombie tuzağı** | hedef **0,10 sn**'de öldü | **24 sn** yaşadı |
| **B — torun öldürme** | torun **0,10 sn**'de öldü | **20+ sn** yaşadı |
| Gerçek `ffmpeg` torunu | **0,45 sn**'de öldü | — |

> **Kontrol deneyi olmadan bu ölçümler hiçbir şey kanıtlamazdı.** Ajanın ilk
> ölçümünde izleyici **hiç çalışmıyordu** (harness `--internal-watch-orphan` modunu
> işlemiyordu) ve hedef **kırılan stdout borusundan** ölüyordu — yani "0,10 sn" ölçümü
> korumayı kanıtlamıyordu. Harness üretim koduna (`LinuxOrphanGuard.RunWatcher`) bağlandı
> ve **tüm ölçümler yeniden yapıldı**. Bu, ajanın kendi hatasını kendi bildirmesidir.

---

## 1. DÜZELTME A — Zombie tuzağı

### Problem

`ReadStartTime` `/proc/<pid>/stat` **22. alanı** (starttime) okuyor, `kill(pid,0)`
kullanılıyordu. **İkisi de zombie süreçte "yaşıyor" der.** Büyükan reap etmedikçe
izleyici sonsuza kadar yokluyor. Ölçülen ilk belirti: hedef **24 saniye** yaşadı,
ebeveyn `wait()` çağrısından **1 saniye** sonra öldü.

### Ölçüm

| Hedef | Ölüm süresi | Zombie kanıtı |
|---|---|---|
| **Sessiz `sleep 900`** (kesin kanıt) | **0,10 sn** | Migurdex `state=Z` iken öldü |
| **Gerçek `yt-dlp`** | **0,15 sn** | Migurdex `state=Z` iken öldü |

Zombie state **aktif doğrulandı**: ölçüm sırasında Migurdex'in `/proc/<pid>/stat` state
geçmişi `['R','Z']`; t0+0,05 sn'de `Z`, hedef t0+0,10 sn'da ölü. Ebeveyn (python)
`wait()` **çağırmadı** ve ölümden sonra Migurdex `/proc`'ta **`Z` olarak kaldı**.

> **Neden "sessiz hedef" kesin kanıt:** `yt-dlp` stdout'a yazar; Migurdex ölünce boru
> kapanır ve `yt-dlp` **kendi kendine** ölebilir. Bunu dışlamak için hiç çıktı yazmayan
> `sleep 900` kullanıldı — o yalnızca `SIGKILL` ile ölebilir, yani öldüren tek şey guard'dır.

### Kontrol deneyi

Aynı düzen `Attach` **çağrılmadan** (koruma yok) çalıştırıldı → sessiz hedef
**24 saniye boyunca yaşadı** (`MIG.state=Z` boyunca `ALIVE`).

**Ölümün kaynağı korumadır; ölçüm geçerlidir.**

### Regresyon kontrolü

| Senaryo | Sonuç |
|---|---|
| Normal (reap eden) ebeveyn | hedef **0,10 sn**'de öldü — yanlış tetiklenme yok |
| Kontrol (korumasız) | 24 sn yaşadı |

---

## 2. DÜZELTME B — Torun (process tree) öldürme

### Problem

`ffmpeg` hiçbir zaman Migurdex'in doğrudan çocuğu değil, daima `yt-dlp`'nin çocuğu.
Önceki koruma yalnız doğrudan çocuğu öldürüyordu. Ölçülen ilk belirti: korunan `bash`
sarmalayıcı <1 sn'de öldü, **torunu** (`sleep 900`) **18 sn** yaşadı.

### Ölçüm

Zincir doğrulandı: `torun.ppid=3649 → çocuk.ppid=3641 → mig.ppid=3640`

| | Ölüm süresi |
|---|---|
| Çocuk (`bash`) | **0,10 sn** |
| **Torun (`sleep 900`)** | **0,10 sn** |

### Karşı-olgusal (eski davranış yeniden üretildi)

Yalnız doğrudan çocuğa `kill -9` uygulandığında: çocuk 0,05 sn'de öldü,
**torun 2 / 5 / 10 / 18 / 20 sn boyunca "HALA YASIYOR"** kaldı.

Eski davranış birebir yeniden üretildi → `KillProcessTree` düzeltmesinin etkisi
kanıtlandı.

### Gerçek `ffmpeg` senaryosu — ölçülebildi

Zorunlu gerçek zincir kuruldu: `MIG(2665) → yt-dlp(2679) → ffmpeg(2695)`,
`ffmpeg.ppid == yt-dlp.ppid` ile doğrulandı. Komut gerçek bir birleştirme çağrısıydı
(`ffmpeg -y … -c copy -f mpegts file:…`).

| | Ölüm süresi |
|---|---|
| `yt-dlp` | **0,45 sn** |
| **`ffmpeg` (torun)** | **0,45 sn** |

Kaynak: **canlı HLS** (`EXT-X-ENDLIST` yok). Migurdex'in **birebir kendi argümanlarıyla**
(`BuildStartInfo` ile aynı sıra, `-f` ve `--hls-prefer-ffmpeg` **yok**) çalıştırıldı.

> Düz master playlist'lerde (HLS-TS + fMP4) yt-dlp formatları `hlsnative` ile kendi
> içinde birleştirir, `ffmpeg` çağırmaz. Bu yüzden canlı HLS seçildi.

---

## 3. Yanlış öldürme (en kritik kontrol)

Migurdex **dışında**, bağımsız başlatılan üçüncü parti süreçler:

| Ölçüm | `sleep 300` | bash döngüsü | `ffprobe` | Çocuk | **Torun** |
|---|---|---|---|---|---|
| t+3 sn | YAŞADI | YAŞADI | YAŞADI | DEAD | DEAD |
| t+5 sn | YAŞADI | YAŞADI | YAŞADI | DEAD | DEAD |
| t+8 sn | YAŞADI | YAŞADI | YAŞADI | DEAD | DEAD |
| t+12 sn | YAŞADI | YAŞADI | YAŞADI | DEAD | DEAD |
| t+26 sn | YAŞADI | YAŞADI | YAŞADI | DEAD | DEAD |

**Yanlış öldürme yok.** Ağacın tamamı (hedef + torun) öldürülürken dışarıdaki süreçler
**26 saniye** yaşadı.

## 4. İzleyici yanlışlıkla öldürüldü mü? — **Hayır, kesin olarak kanıtlandı**

| | |
|---|---|
| İzleyicinin konumu | korunan hedefin **torunu değil, kardeşi** (biri de Migurdex'in çocuğu): `izleyici.ppid=2976 (python)`, `hedef.ppid=2977 (mig)` |
| Neden korunuyor | BFS **kökten** (`childPid` = `yt-dlp`) başladığı için izleyiciye ulaşamaz |
| Çıkış kodu | `izleyici returncode = 0` (0,06 sn sonra) |

`0` = **normal çıkış** (işini bitirip kendi isteğiyle çıktı). `-9` (SIGKILL) olsaydı
yanlışlıkla öldürülmüş olurdu.

---

## 5. Ajanın kendi düzelttiği 3 ölçüm hatası

Bunlar **raporlanıyor** çünkü ilk ölçümleri yanlış yönlendiriyordu:

| # | Hata | Etkisi | Düzeltme |
|---|---|---|---|
| **F1** (kritik) | `ffmpeg` yoklaması **birleştirme** sanıldı. yt-dlp ffmpeg'i kabiliyet sorgusu için kısa çağırır: `t=0.86s pid=2614 ffmpeg -bsfs`. İlk tespit bunu yakaladı → "ffmpeg 0.00 sn'de öldü" **anlamsızdı** (zaten bitmişti) | Yanlış kanıt | Yalnız `-c copy`/`file:` içeren **birleştirme** çağrısı sayılıyor + t0'da hâlâ yaşıyor olması şartı. Yeniden ölçüm: **0,45 sn** |
| **F2** (yanlış suçlama) | `ffprobe` "koruma tarafından öldürüldü" sanıldı. `state=Z` aslında **kendiliğinden çıkmış** demekti: ilk `ffprobe` çağrısı 0,06 sn'de `exit=1` ile bitiyordu | Yanlış suçlama | Yavaş HTTP HLS okuyan uzun ömürlü `ffprobe` kullanıldı → 5 ölçüm noktasında da `YAŞADI` |
| **F3** (ölçüm aracı) | İlk harness `--internal-watch-orphan` modunu **işlemiyordu**; izleyici doğar doğmaz çıkıyordu (`/proc`'da hiç görünmüyordu). Bu durumda hedef korumadan değil **kırılan stdout borusundan** ölüyordu | **"0,10 sn" ölçümü korumayı kanıtlamıyordu** | Harness `LinuxOrphanGuard.RunWatcher(args)`'a (üretim kodu) bağlandı; izleyici `state=S` doğrulandı, tüm ölçümler yeniden yapıldı |

> **F3 en önemlisi:** Kontrol deneyi olmadan, izleyici çalışmıyorken de "0,10 sn" görünür
> ve düzeltme **kanıtlanmış görünür**. Ajan kendi hatasını buldu ve tüm ölçümleri
> yeniden yaptı. Bu, önceki turun "boru öldürüyor" yanlış atfının da nedenini
> açıklıyor: **ölçüm aracı gerçeği değiştiriyor.**

> **F3 üretim kodunda değil, harness'teydi.** `Migurdex.Cli/Program.cs:45` doğru şekilde
> yönlendiriyor (`Program.Main` → `LinuxOrphanGuard.RunWatcher`).

### Tasarım gözlemi (hata değil)

`KillProcessTree` torunları **ters BFS sırasıyla** (en derinden) öldürüyor; yorumda
belirtildiği gibi "torunlar önce, kök sonra". Ölçümde çocuk ve torun aynı 0,10 sn'de
öldü — sıralama doğrulandı, gözlenen gecikme yok.

---

## 6. Metodoloji düzeltmesi (Tur 2'den gelen)

| Eski (hatalı) | Yeni |
|---|---|
| `ps` / `kill -0` ile sayım → **zombie'ı canlı sayıyor** | `/proc/<pid>/stat` **3. alanı (state)** okunuyor; `Z`/`X` = **ölü** |
| `ppid == 1` testi → WSL'de geçersiz (subreaper relay) | Ebeveyn **PID + `/proc` varlığı** ile takip; zombie "`/proc`'ta kaldı" olarak doğrulandı |

Ek olarak **kontrol deneyleri** eklendi; bu sayede iki ölçüm hatası bulundu (F1, F2).

---

## 7. Build ve testler

```
dotnet build Migurdex.slnx -c Release
  Build succeeded.  0 Warning(s)  0 Error(s)

dotnet test --filter "FullyQualifiedName!~ExtractorSmokeTests"
  Passed! - Failed: 0, Passed: 518, Skipped: 0, Total: 518   exit=0

dotnet test --filter "FullyQualifiedName~LinuxOrphan"
  Passed! - Failed: 0, Passed: 24, Total: 24
```

> **Açıklamalı sapma:** Görevde "`LinuxOrphanWatcher` filtresi = 24" bekleniyordu, **20**
> döndü. Nedeni yapısal, hata değil: `LinuxOrphanWatcherTests` = 17 `[Fact]` +
> 1 `[Theory]`×3 = **20**. **24** sayısı daha geniş `~LinuxOrphan` filtresinden geliyor
> (20 + 4 `LinuxOrphanGuardTests`). Ajan ayrıca koşup doğruladı. Test eksikliği değil,
> filtre adı farkı — benim verdiğim beklenen sayı yanlıştı.

---

## 8. Temizlik

| Öğe | Durut |
|---|---|
| Migurdex / izleyici / yt-dlp / ffmpeg / ffprobe / `sleep 900` / `sleep 300` | **0 kaldı** |
| `/tmp/migurdex-*` | 121 kalem silindi, **0 kaldı** |
| İndirme klasörleri (`/root/lab2`, `/root/h2`, `/root/Downloads/Migurdex`) | silindi |
| HLS sunucusu (`lab2_serve.py`, port 8099) | durduruldu |
| Ağ aliası `203.0.113.77/32` | **silindi** (`lo` yalnız `127.0.0.1/8` + `10.255.255.254/32`) |
| Kaynak bütünlüğü | `sha256(ExternalProcessRunner.cs) = a59b6fed…` **değişmedi** |

---

## 9. Özet karşılaştırma

| | Tur 1 (thread sürümü) | Tur 2 (ayrı süreç) | Tur 3 (zombie + torun) |
|---|---|---|---|
| Sessiz hedef, `kill -9` | **4/4 yetim** | 3/3 temiz | **0,10 sn** + kontrol 24 sn |
| `ffmpeg` torunu | — | — | **0,45 sn** |
| Yanlış öldürme | — | 26 sn yaşadı | **26 sn** yaşadı |
| İzleyici | ebeveyn içinde thread (ölü) | ayrı süreç | kardeş, `returncode=0` |
| Test | — | 510/510 × 5 | **518/518**, exit 0 |
