# Döngü 3 — Kalan Hata Taraması ve Düzeltmeler

**Tarih:** 30 Eylül 2026 · **Taban:** `main` = `0604833` · **Dal:** `fix/orphan-guard-reaped-parent`

> Bu döngü, PR #5 ve #6 kapandıktan sonra **madde 1'e dön** ile başladı. İki ajan
> paralel çalıştı (madde 6): biri kalan hataları taradı, diğeri BULGU 32'yi
> araştırdı. **12 bulgu** geldi; hepsi kabul edilmedi.

---

## 0. Sonuç — 5 düzeltildi, 1 yanlış çıktı, 1 kendi hatam, 5 kayda geçti

| # | Bulgu | Şiddet | Karar |
|---|---|---|---|
| **34** | Reap edilen ebeveynde yetim koruma **hiç tetiklenmiyor** | **kritik** | ✅ **Düzeltildi** + regresyon testi |
| **35** | İzleyici `Process`'i dispose/reap edilmiyor → zombie birikimi | high | ✅ Düzeltildi |
| **36** | `ExtractorManager` `OperationCanceledException`'ı yutuyor | high | ✅ Düzeltildi |
| **37** | Kendi testim bloklanmış `cmd.exe` sızdırıyordu | orta | ✅ Düzeltildi |
| **38** | MP4 paralel segment ilerlemesi **monoton değil** | orta | ✅ Düzeltildi |
| **39** | `TryParseSize` alt-bayt değerlerde `true` dönüyor | orta | ✅ Düzeltildi |
| **40** | `DownloadTargetLock.ReleaseEntry` yarış iddiası | high | ❌ **Yanlış çıktı** — ölçüldü |
| 41–45 | `EstimateRemaining` taşması, altyazı `catch`, üst düzey `catch`, `busy_timeout`, WAL izinleri | orta/düşük | 📋 Kayda geçti |

---

## 1. BULGU 34 (KRİTİK) — Linux yetim koruma en yaygın senaryoda işlevsizdi

### Bulgu

`Watch` içinde ebeveyn kontrolü `ReadStartTime(parentPid) != 0` şartına bağlıydı.

```csharp
if (!IsProcessAlive(parentPid))
{
    var currentParentStartTime = ReadStartTime(parentPid);
    if (currentParentStartTime != 0                    // ← bu şart her şeyi belirliyor
        && (parentStartTime == 0 || currentParentStartTime == parentStartTime))
    {
        break;
    }
}
```

Ebeveyn `SIGKILL` edildikten sonra **reap edilirse** `/proc/<pid>` tamamen kaybolur →
`ReadStatField` gerçek dönüş değeri olarak `0` verir → `break` **hiç çalışmaz** →
izleyici `MaxWatchMilliseconds` (**6 saat**) boyunca yoklamayı sürdürür, hedefi
**öldürmez**.

### Kontrol deneyi — hata önce üretildi

WSL2, aynı harness, iki ebeveyn türü:

| Ebeveyn (`SIGKILL` sonrası) | Hedef öldü mü |
|---|---|
| A) reap **EDİLMEDİ** (zombie) | **0,16 sn** ✓ |
| B) reap **EDİLDİ** (normal) | **20 sn sonra hâlâ yaşıyor** ✗ |

Yanlış tetikleme kontrolü (ebeveyn yaşarken hedef yaşamalı) **iki durumda da geçti**.

### Önceki doğrulamam neden eksikti

`LINUX-DOGRULAMA-3.md` bunu açıkça yazıyordu: *"Ebeveyn (python) `wait()` **çağırmadı**
ve ölümden sonra Migurdex `/proc`'ta `Z` olarak kaldı."* Yani **yalnız A dalını** ölçmüşüm.
`kill -9 migurdex` yapan her ebeveyn — bash, systemd, her supervisor — **B dalına**
giriyor, yani koruma gerçek dünyada neredeyse hiç işe yaramıyordu.

### Düzeltme

```csharp
if (!IsProcessAlive(parentPid))
{
    break;
}
```

`IsProcessAlive` bu noktada zaten `false` döndürdüğü için ebeveyn ölmüştür (zombie,
gitti ya da hiç yok). **PID geri dönüşü bu dalı ilgilendirmez**: geri dönen bir süreç
yaşıyor olurdu ve `IsProcessAlive` `true` dönerdi, buraya giremezdi. Başlangıç zamanı
doğrulaması döngü **sonundaki** hedef kontrolünde zaten var.

### Ölçüm sonrası

| Ebeveyn | Önce | Sonra |
|---|---|---|
| A) zombie | 0,16 sn | **0,17 sn** ✓ |
| B) reap (normal) | **20 sn sonra hâlâ yaşıyor** ✗ | **0,15 sn** ✓ |

### Regresyon testi

`RunWatcher_WithAlreadyReapedParent_KillsChildWithoutWaiting` — ebeveyn PID'i
**hiç var olmamış** bir numara (`/proc/<pid>` yok = gerçek hayatta "reap edilmiş" ile
aynı durum), hedef ise canlı bir `sleep 120`. İzleyici 60 sn içinde dönmeli **ve**
hedefi öldürmeli.

Düzeltme geri alınırsa bu test **6 saat** sonra zaman aşımına düşer.
Ölçülen: **Linux'ta 190 ms.**

---

## 2. BULGU 35 (HIGH) — İzleyici süreci hiç reap/dispose edilmiyordu

`Attach` içinde `Process.Start` ile başlatılan izleyici hem `Dispose()` edilmiyor hem
de `WaitForExit` çağrılmıyordu. İki sonuç:

1. `SafeProcessHandle` sonlandırıcıya kalıyor → native tanıtıcı sızıntısı.
2. **Linux'ta .NET çocuğu yalnızca `WaitForExit*` çağrısında reap eder.** Çağrılmadığı
   için her izleyici kalıcı **zombie** oluyor; TUI oturumu boyunca `<defunct>`
   birikiyor. `ps` çıktısı da tam olarak yetim sorununu tanılamaz hale geliyor.

Düzeltme: bekleme arka planda yapılıyor, `Attach` indirmeyi bloklamıyor.

---

## 3. BULGU 36 (HIGH) — Kullanıcı iptali "kaynak bulunamadı" sanılıyordu

`ExtractorManager.ExtractAsync` her istisnayı yutuyordu:

```csharp
catch (Exception ex)   // OperationCanceledException filtresiz
```

Kullanıcı Ctrl+C yaptığında iptal **loglanıp yutuluyor**, döngü bir sonraki
extractor'a geçiyor ve o da hemen fırlatıyor. `ExtractAsync` normal dönüyor, çağıran
taraf "kullanıcı vazgeçti" ile "kaynak bulunamadı"yı **birbirine karıştırıyor**.
Üstelik iptal **gecikmeli** çalışıyor: her plugin sırayla çağrılıyor.

Düzeltme: `OperationCanceledException` filtresiyle yeniden fırlatılıyor.

---

## 4. BULGU 37 (orta) — **Kendi eklediğim test** süreç sızdırıyordu

`IsAbandoned_RejectsRecycledPidOfAnotherLiveProcess` (BULGU 29/30'da benim yazdığım
test) `cmd.exe /c pause` başlatıyordu. `Process.Dispose()` süreci **öldürmez** —
yalnız yönetilmeyen tanıtıcıyı serbest bırakır. Windows'ta stdin yönlendirilmediği
için `pause` **sonsuza kadar** bloklanıyordu: **her `dotnet test` koşusu bir `cmd.exe`
sızdırıyordu.**

Düzeltme: `finally` bloğunda `Kill()` + `WaitForExit`.

**Doğrulama:** düzeltmeden sonra 4 ardışık Windows koşusu, son 3 dakikada açılan
`cmd.exe` sayısı = **0**.

---

## 5. BULGU 38 (orta) — MP4 paralel segment ilerlemesi geriye gidiyordu

```csharp
var current = Interlocked.Read(ref sharedTotal[0]);   // kilit DIŞINDA
lock (progressLock) { shouldReport = ...; }           // kilit DIŞINDA
if (shouldReport) Report(..., current, total);         // kilit DIŞINDA
```

İki segment şu şekilde iç içe geçebiliyordu: T1 40 MB okur → T2 80 MB okur → T2
raporlar → T1 raporlar. **Arayüz bayt sayacını geriye götürür.**

HLS yolunda bu sınıf düzeltilmişti (E9/C1b) ama `ParallelMaxSegments` yolunda —
8 MB üzeri ve `Accept-Ranges` olan **her gerçek** MP4 indirmesinde — hiç monotonluk
koruması yoktu. Projenin kendi koyduğu değişmez bu yolda ihlal ediliyordu.

Düzeltme: okuma + monotonik taban + raporlama **hepsi kilit içinde**.

---

## 6. BULGU 39 (orta) — `TryParseSize` alt-bayt değerde `true` dönüyordu

Aralık kontrolü `double` üzerinde, atama ise **sıfıra doğru kirpar**. `(0, 1)`
aralığındaki her değer korumayı geçip `bytes = 0` ile `true` dönüyordu. Çağıranlar
"geçerli ve sıfırdan farklı" sandığı için `HlsProgressHeartbeat` monotonik tabanı
`0`'a sabitleniyor ve o dosyanın geri kalanında **tahminli toplam düzeltmesi (E9/C1b)
işe yaramıyordu.**

Düzeltme: karşılaştırma hedef alanda — `bytes >= 1`.

---

## 7. BULGU 40 — ajanın iddiası **YANLIŞ ÇIKTI** (ölçümle)

Ajan, `DownloadTargetLock.ReleaseEntry`'nin kuyrukta bekleyen varken kaydı sildiğini
iddia etti: *"`SemaphoreSlim.Release()` ... `CurrentCount == 1` olur."*

**Ölçtüm:**

| Adım | `CurrentCount` |
|---|---|
| T1 aldıktan sonra | 0 |
| T2 kuyruğa girdikten sonra | 0 |
| T1 `Release()` ettikten **hemen sonra** | **0** |
| T2 devam ettikten sonra | 0 |

`CurrentCount` **1 olmuyor**, `0` kalıyor. Yani `CurrentCount != 1` koruması **doğru
çalışıyor** ve kayıt **silinmiyor**. İddia yanlıştı — **düzeltme yapılmadı.**

Bu, ajan raporlarının da otomatik kabul edilmediğinin kanıtı: 12 bulgunun 1'i
(%8) ölçümle çürütüldü.

---

## 8. Kayda geçen, bu PR'da düzeltilmeyenler

| # | Konu | Neden şimdi değil |
|---|---|---|
| 41 | `EstimateRemaining` `OverflowException` riski | Ajan **ulaşılabilirliğini kanıtlayamadı**; sınırlayıcı `Math.Min` + `speed < 1` kontrolü önlem, ama önce ulaşılabilirlik ölçülmeli |
| 42 | Altyazı `catch` bloğu ayrıntılı sebebi yutuyor | Kullanıcıya dönük iyileştirme; ayrı iş kalemi |
| 43 | `DownloadService` üst düzey `catch` her şeyi "Video indirilemedi" yapıyor | `ILogger` enjekte etmek gerekir; kapsam dışı |
| 44 | `busy_timeout` `Open()` **sonrasında** uygulanıyor | Doğru çözüm `SqliteConnectionStringBuilder.DefaultTimeout`; **doğrulanmadan değiştirilmedi** (SQLite davranışı tahmin edilmemeli) |
| 45 | OAuth token'ları `migurdex.db-wal` içine yazılıyor, izinler yalnız `migurdex.db`'ye uygulanıyor | **Yüksek etki, tetikleyici belirsiz** — SQLite'in yan dosya modunu miras alıp almadığı doğrulanmadı. Önce ölçülmeli |
| 46 | `PluginLoader` yapılandırılamayan plugin türlerini **sessizce** düşürüyor | Tanısal eksiklik; `LogWarning` eklemek küçük ama ayrı iş kalemi |

---

## 9. BULGU 32 — kök neden çözüldü (davranış değişmedi)

`general` ajanı bu döngüde kök nedeni buldu:

> **xUnit V3 → VSTest adaptörü arasında düşen bir sonuç mesajı.** Test **koşuyor**,
> yalnızca sonucu kayboluyor. VSTest `--diag` kanıtı: 21 testlik bir sınıfta
> `RecordStart` × **21**, `RecordResult` × **20**, `RecordEnd` × **21**.

| Tespit | Sonuç |
|---|---|
| Test koşuyor mu | **Evet** — `RecordStart`/`RecordEnd` 21 |
| Sonucu ne zaman kayboluyor | `ITestCaseFinished` adaptörden geçerken |
| Hangi test | **Her koşuda değişiyor** (4 farklı test gözlendi) |
| Tek iş parçacığı ile | Deterministik: `TestTempDirectoryTests` → daima 20/21 |
| Yapılandırma ayarıyla çözülüyor mu | **Hayır** — `maxParallelThreads`, `.runsettings`, MTP modu denendi, hiçbiri düzeltmiyor |
| Self-hosted exe ile | **600 / 8 koşu** — sorun hiç oluşmuyor |

**Ajanın önerisi (sonraki döngü adayı):** `dotnet test` yerine self-hosted exe'yi
kullanmak ve `<test>` sayısını `--list-tests` ile karşılaştırıp uyuşmazlıkta
başarısız olmak — "sessiz eksik sayım"ı **yüksek sesli** hale getirmek.

**Bu PR'de uygulanmadı.** Gerekçe: koşucuyu değiştirmek, 46 kez doğruladığım CI
komutunu bu dalın ortasında değiştirmek anlamına gelir. Bu döngüde `general` ajanının
denemek için `Migurdex.Tests.csproj`'i değiştirdiğini (MTP'ye geçirdi, `Microsoft.NET.Test.Sdk`'ı
sildi) gördüm ve **hemen geri aldım** — kanıtlanmamış bir derleme değişikliği.

---

## 10. BULGU 31 — canlı kusur **değil** (ölçüm)

Grup H'den kalan `/tmp/migurdex-dbtest-*` dizinlerinin sayısı: **241 (19 MB)** — Grup H
raporundaki rakamın birebir kendisi, yani **tarihsel** kalıntı.

Yeni doğrulama:

| Koşu | `/tmp/migurdex*` sayısı |
|---|---|
| Önce | 241 |
| 1. koşu sonrası | 241 |
| 2. koşu sonrası | 241 |
| 3. koşu sonrası | 241 |

**Koşu başına büyüme 0.** Tek "yeni" kayıt `migurdex-jobs` — sabit adlı, büyümüyor.
`TestTempDirectory` işini yapıyor; kalan 240 dizin o düzeltmeden **önce** oluşmuş.

**Karar:** BULGU 31 açık kusur olarak kapatıldı. Eski kalıntılar tek seferlik
temizlenebilir (19 MB, zararsız).

---

## 11. Doğrulama

| | |
|---|---|
| Build (Windows) | **0 hata / 0 uyarı** |
| Build (Linux) | **0 hata / 0 uyarı** |
| **Windows tam süit × 4** | **4/4 temiz** |
| **Linux tam süit × 4** | **4/4 temiz** — 601 test |
| Yeni regresyon testi (Linux, izole) | **190 ms** — düzeltmesi 6 saat sürerdi |
| Sızan `cmd.exe` (4 koşu sonrası) | **0** |
| Zombie süreç | **yok** |

### Yöntem notu

Bu döngüde iki ayrı yük/harness hatası yaptım ve ikisini de ölçümle düzelttim:

1. İlk harness yanlış `dll` seçti **ve** bash her zaman reap ettiği için gerçek bir
   zombie senaryosu hiç oluşmadı.
2. Saat `SIGKILL`'dan **sonra** başlıyordu; hızlı ölüm `0.00 sn` görünüyordu, yani
   ölçüm penceresi yanlıştı. `t0`'yı `SIGKILL` anına alınca iki dal ayrıldı.
