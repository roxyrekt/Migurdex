# Linux Doğrulama — Tur 4 (PR #5 CI kırmızısı)

**Tarih:** 30 Eylül 2026 · **Ortam:** WSL2 Ubuntu-24.04, .NET SDK 10.0.401, 16 çekirdek
**Tetikleyici:** PR #5 CI koşusu `36685447129`, iş `109790509869` → `Test (ubuntu-22.04)` **başarısız**

> Önceki turlar: [`LINUX-DOGRULAMA-2.md`](LINUX-DOGRULAMA-2.md) · [`LINUX-DOGRULAMA-3.md`](LINUX-DOGRULAMA-3.md)

---

## 0. Sonuç — dört hata, dört ayrı hata

| # | Hata | Nerede | Durum |
|---|---|---|---|
| 1 | `Views\X.cs` Windows ayracı | `TuiRedirectedStdinTests` (3 test) | **Düzeltildi** |
| 2 | `DirectoryNotFoundException` yanlış teşhis ediliyor | `DownloadTargetLock.AcquireAsync` | **Düzeltildi** |
| 3 | Linux izleyici testi kararsız | `ChildProcessTrackerLinuxTests` (3/3) | **Düzeltildi** |
| 4 | **Toplu SQLite çökmesi** (8–17 test/koşu) | `TestTempDirectory` süpürücüsü | **Düzeltildi** |

Bunlardan **1, 2 ve 3 CI'da görünmüştü**. 4 yalnız WSL'de tam koşuda göründü ama
kök nedeni aynıydı ve CI'daki 1 numaralı kilit hatasıyla da ilgiliydi.

---

## 1. Hata 1 — TUI testi Windows ayracı kullanıyordu

```
Beklenen TUI dosyası yok: Views\EpisodeSourcesView.cs
```

`[InlineData("Views\\EpisodeSourcesView.cs")]` → C# dizgesi `Views\EpisodeSourcesView.cs`.
Linux'ta `Path.Combine` bunu ayraç saymaz, tek parça dosya adı üretir.

**Düzeltme:** InlineData düz `/` kullanıyor, `Path.Combine` çağrısı ayracı işletim
sistemine çeviriyor. Windows da `/` kabul ettiği için tek yazım iki platformda geçerli.

---

## 2. Hata 2 — `DirectoryNotFoundException` "başka indirme kullanıyor" diye raporlanıyordu

CI logu:

```
ConcurrentDownloadException : Video hedefi başka bir indirme tarafından kullanılıyor.
---- System.IO.DirectoryNotFoundException : Could not find a part of the path
     '/tmp/.../ayni.mp4.migurdex.lock'
```

`DirectoryNotFoundException`, `IOException` türevidir. `AcquireAsync` yalnızca
`catch (IOException)` içerdiği için **hedef klasörü hiç olmayan** bir durum kullanıcıya
**"eşzamanlı indirme var"** diye bildiriliyordu.

**Düzeltme:** `catch (DirectoryNotFoundException)` ayrı bir kol olarak öne alındı;
klasörü adıyla söyleyen `DownloadException` fırlatıyor.

---

## 3. Hata 3 — Linux izleyici testi **kendisi** yarış içeriyordu

```
Assert.Equal() Failure: Expected: 1, Actual: 0
ChildProcessTrackerLinuxTests.RunWatcher_WithDeadChild_SpawnsNoFurtherWatcher
```

Test, `CountOurWatcherChildren()` ile **tüm** izleyici çocuklarını sayıyordu.
Aynı sınıftaki önceki testin izleyicisi hâlâ yaşıyordu (`before = 1`) ve 500 ms
sonra öldü (`after = 0`). Testin ölçtüğü şey (kendi çağrısının izleyici doğurmaması)
bu yüzden **3/3 kararsızdı** — üretim kodunda hata yoktu.

**Düzeltme:** ölçüm, hedef PID imzasına bağlandı. İzleyicinin komut satırında
`--internal-watch-orphan <ebeveyn> <başlangıç> <hedef> <hedefBaşlangıç>` bulunduğu
için, doğru doğan izleyici **yalnızca bu** hedef PID'sini taşır → ölçüm bu teste özeldir.

---

## 4. Hata 4 — asıl bulgu: süpürücü **canlı koşunun dizinini** siliyordu

### Belirti

Koşu başına **8–17** hata, hepsi 6 SQLite sınıfında + kilit testinde:

| Hata | Adet (6 koşu) |
|---|---|
| `SQLite Error 10: 'disk I/O error'` | 65 |
| `SQLite Error 14: 'unable to open database file'` | 10 |
| `DirectoryNotFoundException` | 6 |

### Ayırıcı ölçümler

| Deney | Sonuç | Ne kanıtladı |
|---|---|---|
| `34561a3` (Grup F/G/H **öncesi**) tam süit × 4 | **0 hata** | Sorun bu dalgada eklenen kodda |
| 6 SQLite sınıfı **tek başına** × 3 | 0 hata | SQLite'ın kendisi sorun değil |
| Sınıf **tek başına** × 3 | 0 hata | Kendi içinde yarış değil |
| `/tmp`'de 60 eşzamanlı SQLite yazıcı (saf dış yük) | **0 hata** | Ortam / disk / inode sorun değil |
| Süpürücü kapatılınca × 3 | **0 hata** | Süpürücü suçlu |
| Süpürücü açıkken × 3 | **47 hata** | — |

> **Durust not:** "Grup H öncesi de kırılıyor" ölçümünü ilk yaptığımda **geçersizdi** —
> `rm` sonrası `git checkout` dosyaları geri getirmişti. Doğru ayırıcı, commit'e dayalı
> `git worktree add 34561a3` ile alındı ve sonuç ters çıktı: **öncesi temizdi.**

### Kök neden — günlükle kanıtlandı

Süpürücüye "neyi sildin" günlüğü eklendi. Tek `dotnet test` çağrısının içinde:

```
RunRoot=/tmp/migurdex-tests/run-673-8df1ecd7610bde4
entry  =/tmp/migurdex-tests/run-645-8df1ecd75d52024
nameEqRunRoot=False  abandoned=True
   -> SILDI /tmp/migurdex-tests/run-645-8df1ecd75d52024
```

PID'ler: **645, 673, 703, 707, 719, 745** — hepsi aynı koşuda. xUnit v3 test
derlemesini **birden çok işlemde** çalıştırıyor; her biri modül başlatıcısını
çalıştırıp kökü süpürüyor. Bir işlem öldüğü anda **bir sonraki işlem onun hâlâ
kullanılan dizinini siliyor.**

İki ayrı kusur bunu mümkün kılıyordu:

1. **Ölü sahiplik = "terk edilmiş" demek, yeterli değil.** Ölmüş bir işlemin dosyaları
   hâlâ başka bileşenler tarafından kullanılabiliyor.
2. **`Path.GetFileName(entry)` ile `RunRoot` karşılaştırılıyordu** — dosya adı ile tam
   yol. **Hiç eşleşmiyordu**, yani "kendi koşu dizinimi atla" güvencesi fiilen yoktu.

### Neden yalnız Linux

Unix'te `unlink`/`rmdir` **açık dosya bulunsa da** başarılıdır; silme gerçekten olur.
Windows'ta açık kilit dosyası `Directory.Delete`'i başarısızdır ve hata yutulur —
yani aynı hata Windows'ta **görünmez**. CI'da 4 çekirdek varken yalnız 1 test
düştü; WSL'de 16 çekirdek ve daha çok örtüşen işlemle 8–17 test düşüyor.

### Düzeltme

| Değişiklik | Gerekçe |
|---|---|
| `SweepGracePeriod = 10 dakika` | Yeni bitmiş koşunun dizini **dokunulmamış sayılır**, toplanmaz. Yarışı tamamen kapatır |
| `entry` ↔ `RunRoot` **tam yol** karşılaştırması | Gerçekten çalışan bir "kendi dizinimi atla" güvencesi |
| `TryDelete`'den `GC.Collect()` + `WaitForPendingFinalizers()` **kaldırıldı** | Süpürücü canlı dizini silemeyince yardımcı üç kez küresel GC zorluyordu — bu da paralel testlerin nesnelerini sonlandırabilir |
| `IsAbandoned`: `pid == Environment.ProcessId` → asla terk edilmiş değil | Kendi canlı koşumuzu silmemek (daha önce eklenmişti, tek başına yetmiyordu) |

**Ödünleşim dürüstçe:** kalıntı artık "bir sonraki koşuda" değil, **"on dakika sonraki
koşuda"** toplanır. Birikim yine sınırsız büyümez. Doğruluk, daha agresif temizliğin
önüne geçti.

### Regresyon testi

`SweepAbandonedRuns_Keeps_Fresh_Run_Of_Dead_Process` — sahibi ölmüş görünen ama taze
koşu dizini **korunmalı**.

`SweepAbandonedRuns_Removes_Dead_Process_Run` ise artık dizini yaşlandırıyor: gerçek bir
çökme kalıntısı zaten eski olduğu için yeni eşikle de toplanıyor.

---

## 5. Doğrulama

| Ölçüm | Sonuç |
|---|---|
| Windows tam süit × 10 | **10/10 temiz** |
| **Linux tam süit × 8** | **8/8 temiz** (600 test) |
| Build (Windows + Linux) | 0 hata / 0 uyarı |
| CI'ın birebir komutu (Linux) | `dotnet test --filter "FullyQualifiedName!~ExtractorSmokeTests"` |

### Kontrol deneyi (zorunlu)

Düzeltme geri alınınca:

| Ölçüm | Sonuç |
|---|---|
| Yeni regresyon testi tek başına | **BAŞARISIZ** (kırmızı) |
| Tam süit × 3 | **40 hata** |

Yani test gerçekten bu hatayı yakalıyor; kırmızılık tesadüf değil.

---

## 6. Kapanmayan kalıntı

`/tmp`'de `bilinmeyen-<guid>` adlı, `run-` önekli olmayan dizinler var. Bunlar testlerin
kökün **altına doğrudan** açtığı dizinler. Yeni süpürücü 10 dakika yaşlanma kuralı
nedeniyle bunlara da dokunmuyor. Etkisi yok (eski kod da yalnız "1 saatten eski" olan
ad çözülemeyen dizinleri silirdi), ama kökün altına doğrudan yazan test varsa
temizlik kapsamı dışında kalır. Ayrı ve küçük bir iş kalemi.
