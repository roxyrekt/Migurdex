# Döngü 6 — Taranmamış Yüzeyler: Api, Plugins, TUI, Update

**Tarih:** 30 Eylül 2026 · **Taban:** `main` = `d2987eb` · **Dal:** `fix/plugin-deadlock-and-serialization`

> Bu dal, 6. döngü ajanının **daha önce hiç taranmamış** alanlarda bulduğu 12 bulgudan
> dördünü düzeltir. Kalan sekiz kayda geçmiştir; gerekçeleri aşağıda.

---

## 0. Sonuç

| # | Konu | Şiddet | Karar |
|---|---|---|---|
| **1** | `OpenAnime` yeniden denemesi **kalıcı kilitlenme** | **kritik** | ✅ Düzeltildi |
| **2** | Kendini güncelleme yarı başarısızlıkta **kurulumu brick** ediyor | high | ✅ Düzeltildi |
| **3** | 3 DTO özelliği `System.Text.Json` tarafından **hiç doldurulmuyor** | medium-high | ✅ Düzeltildi |
| **12** | `matchedTitle: null` → NRE → **502** | low | ✅ Düzeltildi |
| 4–11 | URL kodlama, sağlama fail-open, sınırsız fan-out, DI yarışı, plugin `HttpResponseMessage` sızıntısı, iptal yutma, plugin hata propagasyonu, TUI görevi | medium/düşük | 📋 Kayda geçti |

---

## 1. BULGU 1 (KRİTİK) — 3 eşzamanlı 401 isteği kalıcı kilitlenme

### Hata

```csharp
private readonly SemaphoreSlim _requestSemaphore = new(5, 5);
...
await _requestSemaphore.WaitAsync(cancellationToken);              // 429 — 1 permit alır
try
{
    var response = await _httpClient.SendAsync(request, cancellationToken);
    if ((response.StatusCode == Unauthorized || response.StatusCode == Forbidden) && !isRetry)
    {
        _sessionId = null;
        _gatewayToken = null;

        return await SendRequestAsync<T>(url, method, body, true, cancellationToken);  // 458
    }                                                              // ↑ permit HÂLÂ tutulurken
}
finally { _requestSemaphore.Release(); }                           // 473 — asıl burada serbest kalır
```

`SemaphoreSlim` **yeniden girişe izin vermiyor**. 458'deki geri çağrı, 429'u tekrar
çalıştırır ve **ikinci bir permit** ister; oysa birincisi ancak `finally`'de serbest kalır.

### Neden bu kadar kolay tetikleniyor

`_requestSemaphore` **5** izinli. Yeniden deneme yapan her istek **2 permit** talep eder:

| Eşzamanlı 401 alan istek | Aynı anda istenen permit | Sonuç |
|---|---|---|
| 2 | 2 (ikinci dalga 2) | geçer |
| 4 | 4 (ikinci dalga 3) | geçer, kuyruk çözülür |
| **5** | **5 + 5 = 10** | **kilitlenme** |

**Düzeltilmiş teşhis.** Ajan raporu eşiği **3** olarak verdi; bu bir eksik sayımdı.
Kilitlenme için beş çerçevenin de **aynı anda** yeniden deneme yolunda olması gerekir.
3 eşzamanlı istekte ikinci dalga yalnızca 2 istek içindir, bir permit bulur ve kilitlenme
oluşmaz. Arz tükenmesi için talebin **5'i geçmesi** gerekir, yani eşik tam olarak semafor
kapasitesidir: **5**. Bu, regresyon testiyle de doğrulandı (aşağıya bakın).

Sağlayıcı `Task.WhenAll` ile eşzamanlı istek atıyor: sezonlar (180), episode+altyazı
(249), fansub (314). **3 eşzamanlı istek sıradan bir yol.**

### Neden felaket

Kilitlenme **semaforun içinde**, sokette değil. `HttpClient.Timeout` yardım etmiyor.
API'de `Task.WhenAll(searchTasks)` hiç dönmüyor, SSE `done` olayı yazılmıyor, CLI
Ctrl+C'ye kadar asılı kalıyor. Kullanıcının gateway token'ı süresi dolduğunda yani
**günlük bir olayda** oluşuyor.

### Düzeltme

```csharp
var needsRetry = false;

await _requestSemaphore.WaitAsync(cancellationToken);
try
{
    ...
    if ((Unauthorized || Forbidden) && !isRetry)
    {
        _sessionId = null;
        _gatewayToken = null;
        needsRetry    = true;
        return default;          // finally çalışsın
    }
    ...
}
finally { _requestSemaphore.Release(); }

// Permit ARTIK serbest: yeniden giriş kilitlenmesi imkânsız.
return await SendRequestAsync<T>(url, method, body, true, cancellationToken);
```

Kapsam korunuyor: yeniden deneme sayısı `isRetry` ile **1** ile sınırlıydı, o da aynı.

---

## 2. BULGU 2 (HIGH) — Yarı başarılı güncelleme kurulumu brick ediyor

```csharp
File.Move(dest, backup);        // eski exe artık YOK
File.Copy(newFile, dest, true); // hata verirse...
return true;
```

Çağırdaki iki `catch` filtresi:

```csharp
catch (Exception ex) when (... && TrySwapLockedExe(file, dest)) { }   // 416
catch (Exception ex) when (...)                                       // 425
{
    File.Copy(file, dest + ".new", true);
    deferred.Add(rel);
}
```

**C# `when` filtreleri aynı istisnada yeniden değerlendirilir.** Ölçülen zincir:

1. `File.Copy(exe)` → `IOException` (kilitli)
2. Filtre 1 → `TrySwapLockedExe`: `Move` **başarılı**, `Copy` yine kilitli → `false`
3. Filtre 2 → `dest.new` yazar
4. **Sonuç:** `migurdex` **yok**, `migurdex.old` ve `migurdex.new` var
5. `Program.CleanStaleBackup()` bir sonraki açılışta **`.old`'u da siler**

Kullanıcı elinde çalıştırılabilir dosya kalmıyor ve yedek de yok — kurulum kalıcı brick.

### Düzeltme

`TrySwapLockedExe` **transactional** hale getirildi: kopyalama hata verirse yedek geri
alınır ve istisna yeniden fırlatılır.

---

## 3. BULGU 3 (medium-high) — Üç DTO alanı hiç doldurulmuyordu

`System.Text.Json` yalnız **public setter**'ı olan (veya `[JsonInclude]`'lı) özellikleri
yazar. Salt getter'li bir otomatik özellik **tamamen atlanır** ve başlatıcı değerini korur:

```csharp
[JsonPropertyName("src")]
public string Src { get; } = string.Empty;        // ← hep ""
public string Name { get; } = string.Empty;        // ← hep ""
public string Provider { get; } = string.Empty;    // ← hep ""
```

Aynı DTO'daki kardeş alanlar (`Vid`, `Src`, `Typ`; `Data`) **setter** taşıyor ve
çalışıyor — bu yüzden gözden kaçıyor.

**Ölçülen etkiler:**

| Yer | Sonuç |
|---|---|
| Streamcash altyazıları | `Url = ""` → `HttpRequestMessage(Get, "")` → istisna → `catch { return null; }` → **tüm altyazılar sessizce kayboluyor** |
| `SearchAnimeAsync` | `failedProviders` boş stringlerden oluşuyor → `Arama başarısız (, , )` — **hangi sağlayıcının düştüğü belli olmuyor** |
| `GetMergedHostersAsync` | Hoster öncelik listesi boş girdi alıyor → sıralama yapılandırması çöp üzerine kuruluyor |

Üçüne de setter eklendi. Bu DTO'ları kapsayan **hiçbir test yoktu** — bu da kusurun
hayatta kalmasıyla tutarlı.

---

## 4. BULGU 12 (low) — `matchedTitle: null` → 502

`SaveTrackerMappingRequest`'te `MatchedTitle` null kontrolü **olmayan tek alan**
(`MyAnimeListId` var). `= string.Empty` başlatıcısı *atlanan* durumu korur ama anahtar
JSON `null` ile geldiğinde STJ setter'ı çağırır → `.Trim()` NRE → `502`.

CLI tarafı başlığı `""`'ye varsayılanladığı için üçüncü taraf istemciler için alan
fiilen opsiyonel. Düzeltme: `request.MatchedTitle?.Trim() ?? string.Empty`.

---

## 4b. Kontrol deneyi (kanıt)

BULGU 1 için, düzeltmenin gerçekten işe yaradığı **kanıtlanmadan** kabul edilmedi.
Her iki yönde de `--no-incremental` ile zorla yeniden derleme yapıldı:

| Yön | Derleme | 6/3 koşu sonucu |
|---|---|---|
| Düzeltme **var** | 0 hata | **0/6 başarısız** |
| Düzeltme **yok** (`git checkout` → HEAD) | 0 hata | **3/3 başarısız**, 30 sn'de, **süreç çıkıyor** |
| Geri yüklendi | 0 hata | 0/3 başarısız |

Test `OpenAnimeRequestSemaphoreTests`. Kilitlenme **kalıcı** olduğu için asılı bırakılmıyor:
30 saniyelik aşamada `Assert.Fail` veriyor, CI kilitlenmiyor. `SessionCount` /
`UnauthorizedCount` sayaçları hata mesajına gömülü, yani bir gün başka bir kilitlenme
kaynaklı kırmızı olursa hangi aşamada olduğu görünür.

### Ölçüm tuzakları (bu turda üç kez düştüm)

**1. `Copy-Item` zaman damgası taşıyor — sahte "düzeltme çalışmıyor".**
Dosyayı yedekleyip geri yüklediğimde `Copy-Item` yedeğin **eski** `LastWriteTime`'ını
taşıdı (18:03:32), DLL ise 18:13:54'teydi. Artımlı derleme kaynağı daha yeni sayıp
**atladı**, test eski (düzeltmesiz) DLL'e baktı ve "düzeltme işe yaramadı" hükmü doğdu.
`Get-Item $f).LastWriteTime = Get-Date` + `--no-incremental` ile çözüldü. Bu tuzak
ters yönde de sinsi: düzeltmeyi geri alıp atladığım derlemede test **yanlışlıkla yeşil**
kalır ve "kontrol deneyim tutmuyor" sanılır.

**2. PowerShell komut içindeki Türkçe karakterler bozuluyor.**
`'Başarılı!'` deseni komut satırına yazıldığında örüntü bozuldu, hiç eşleşmedi ve
**12/12 başarısız** raporladım — o koşuların hepsi yeşildi. Güvenilir ölçüt `$LASTEXITCODE`
ya da ASCII desen (`'Ba.Ar.s.z!'`). `$LASTEXITCODE` ile ölçtüğüm 12/12 ise gerçekti.

**3. Randevu (barrier) kapısı yarışlıydı.**
İlk test tasarımı, 5 isteğin *hepsinin* gelmesini bekliyordu. İş parçacığı havuzu
doyduğunda 5'inin hepsi aynı anda gelmediği için kapı hiç açılmıyor, istekler
`HttpClient.Timeout`'a (100 sn) kalıyor ve **düzeltmeli durumda** kırmızı üretiyordu.
Bu, testin kendi kusuruydu; üretim kodunda değil. Kapıya 2 saniyelik bir
`GateGrace` eklendi: ya N istek toplandı ya da süre doldu — artık **asılamıyor**.
Bu değişiklikten sonra izole 15/15 ve tam süit 6/6 yeşil.

**Ayrıca:** test ilk yazıldığında `CS0219` (`needsRetry` atanıyor ama okunmuyor) ve
`CS0162` (try'dan sonrası erişilemez) uyarıları üretti. `result`/`retry` ikilisiyle
yeniden yazıldı, ikisi de gitti. Kalan tek uyarı `DatabaseFilePermissionTests.cs`
CA1416'dır ve **bu dönüşte dokunulmadı** (önceki turlardan kalma).

---

## 4c. Doğrulama sonuçları

Dört düzeltmenin tamamı, iki platformda, **zorla yeniden derleme** ile ölçüldü.

| Ölçüm | Sonuç |
|---|---|
| Linux derleme | 0 hata / 0 uyarı |
| Windows derleme | 0 hata / 1 uyarı (`DatabaseFilePermissionTests.cs` CA1416 — **bu dönüşte dokunulmadı**) |
| Linux tam süit ×4 | **4/4 temiz**, 605 test |
| Windows tam süit ×10 | **9/10 temiz** — 1 koşuda `Xunit.Sdk.TestPipelineException` (aşağıya bak) |
| BULGU 1 regresyon testi (Linux) | **2/2**, **89 ms** (asılma değil) |
| BULGU 1 regresyon testi (Windows) | **2/2**, 202–238 ms |
| İzin testleri (`umask 022`) | 2/2 |
| Zombie süreç | yok |
| Çatışma izi / whitespace | temiz |

### Dürüst kayıt: bir koşuda `Xunit.Sdk.TestPipelineException`

Windows tam süit 10 koşudan birinde şu çıktı:

```text
[xUnit.net 00:00:16.18]     [FATAL ERROR] Xunit.Sdk.TestPipelineException
Başarılı!  - Başarısız: 0, Başarılı: 605, Atlanan: 0, Toplam: 605
```

Yani **özet 605/605 başarılı diyor**, ama ardından gelen hatadan süreç sıfırdan farklı
dönüyor. Bu, kayıtlı **BULGU 32**'nin (xUnit V3 → VSTest adaptöründe düşen sonuç
iletisi) yakın cousin'ı: sonuç doğru, taşıma katmanı hata veriyor.

**Ne yapabildim, ne yapamadım:** hatayı 6 ardışık ek koşuda **yeniden üretemedim**
(6/6 temiz, `exit=0`). İlk seferinde stderr'i PowerShell'in hata akışı bastığı için
istisnanın tam metnini kaybedemedim. Bu yüzden kök neni **tespit etmiş değilim** —
yalnızca varlığını ve davranışını kayda geçiriyorum. Yeni bulgu olarak değil,
BULGU 32'nin kapsamına giren gözlem olarak işaretlenmiştir.

Bu düzeltmelerin **bu koşuyu tetiklediğine** dair kanıtım da yok: 9 temiz koşu,
Linux'de 4/4 temiz ve iki platformda 0 derleme hatası. Yine de kesin olmayan bir
şeyi kesinmiş gibi yazmıyorum.

---

## 5. Kayda geçen sekiz bulgu

| # | Konu | Neden şimdi değil |
|---|---|---|
| 4 | Sağlayıcı ID'leri upstream URL'lerine **kodlanmadan** giriyor (`AniziumProvider:107/229/235`, `AnizmProvider:172`); uç doğrulaması yalnız uzunluk. `episodeId=123%7C1%26admin%3Dtrue%7C1` → upstream'e keyfi sorgu parametresi enjeksiyonu | Gerçek ve HTTP ile erişilebilir. Ama doğru sınır **plugin sınırında** mı API'de mi? Karar gerektiriyor; plugin'ler `|` ve `/` ile bölüyor. Ayrı ve izlenebilir iş |
| 5 | Sağlama (checksum) **üç ayrı yolda fail-open**: dosya yoksa / listede yoksa / indirilemezse `return true` | "Managed" kurulumun sağlama dosyası garanti edilebiliyorsa zorunlu olmalı. Bu **ürün kararı**; ayrıca sağlama dosyası da aynı GitHub release'inden geliyor — koruma bozulmaya karşı, ele geçirilmiş release'e karşı değil |
| 6 | `/sources?stream=true` **sınırsız fan-out** + `Channel.CreateUnbounded` — 400 kaynak 400 eşzamanlı çıkarım + 400 bellekte zarf, geri basınç yok | `Channel.CreateBounded(256, Wait)` + paylaşılan `SemaphoreSlim` doğru çözüm ama `ExtractorManager`'a eşzamanlılık kapısı eklemek ayrı bir mimari karar |
| 7 | `Program.cs:186` `Task.Run(...)` **hiç beklenmiyor**, `Dispose()` ile yarışıyor — kullanıcı TUI'den çıkınca `WatchSyncService`/`OAuthTokenStore` altta kullanılırken sökülüyor | Doğru düzeltme görev tanıtıcısını saklamak + `finally`'de `WaitAsync(5s)` + `FlushQueueAsync`'e `CancellationToken`. Ufuk teki ama bağımlılık sırası etkileniyor |
| 8 | `HttpResponseMessage` plugin'lerin çoğunda **dispose edilmiyor**; yeniden deneme yollarında ilk yanıt kalıcı olarak yetim kalıyor | Gerçek ve ölçülebilir, ama 8+ dosyada mekanik düzenleme. Ayrı tur |
| 9 | `UpdateService.CheckForUpdatesAsync` **iptali "çevrimdışı"** olarak yutuyor ve exit code 1 veriyor | `ApiClientService`'daki mevcut kalıba birebir uyarlanabilir; küçük. Ayrı iş kalemi olarak bir sonraki turda |
| 10 | Plugin'ler hata halinde `return []` → uç nokta bunu **başarılı** sayıyor → `succeeded: 3, failed: 0, total: 0`; kullanıcı "anime yok" sanıyor | Plugin sözleşmesi değişikliği gerektiriyor ("eşleşme yok" ile "başarısız" ayrımı). Tüm plugin'leri etkiler; ayrı ve izlenebilir iş |
| 11 | `FuzzyPrompt.ShowDynamic` arka plan görevini **hiç beklemiyor**, `CancellationTokenSource` dispose edilmiyor ve normal yolda iptal edilmiyor | Doğru düzeltme belli (`cts.Cancel()` + `await`), ama `AnsiConsole.Live` bloğu senkron; ayrıntılı test gerektirir |

---

## 6. Yöntem notu

Bu turda dört bulguyu da **kendi kodunu okuyarak** doğruladım ve kabul etmedim uyguladım:

- BULGU 1 için 458. satırın `finally`'deki `Release()`'in **fazla geç** çalıştığını ve
  `SemaphoreSlim`'in yeniden girişe izin vermediğini satır satır okudum.
- BULGU 2 için C# `when` filtrelerinin aynı istisnada yeniden değerlendirildiğini
  doğruladım — zincirin geri kalanı doğrudan koddan okundu.
- BULGU 3 için üç özelliğin de gerçekten setter'sız olduğunu ve kardeş alanların
  setter taşıdığını gördüm.

**Döngü 3'ten bu yana 12 bulgunun 3'ü ölçümle çürütüldü** (`DownloadTargetLock`,
`busy_timeout`, `EstimateRemaining`) — ajan raporlarının otomatik kabul edilmemesinin
somut kanıtı.
