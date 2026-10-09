# Paralel indirme

Bu belge, toplu indirmede **birden fazla bölümün aynı anda** indirilmesini sağlayan mekanizmayı,
ayar anahtarlarını, geçerli sınırları ve iptal/kısmi hata davranışını anlatır. Toplu indirme akışının
tamamı için: [toplu-indirme-ve-indirme-dizini.md](toplu-indirme-ve-indirme-dizini.md).

> **Karıştırılmaması gereken:** Migurdex'te zaten var olan "paralel ranged MP4" davranışı **tek bir
> dosyanın** 8 MiB üstü olduğunda sunucu `Range` desteklerse 2-4 parçaya bölünmesidir
> (`Mp4Downloader`: `ParallelThresholdBytes`, `ParallelMaxSegments`). Bu belgedeki paralellik ise
> **birden fazla bölümün** eşzamanlı indirilmesidir. İki mekanizma bağımsızdır ve aynı anda
> çalışabilir: kuyruk 2 bölümü paralel indirirken her bölüm kendi içinde parçalara bölünebilir.

---

## 1. Ayar yüzeyi

`config.json` anahtarları ve TUI karşılıkları:

| `config.json` anahtarı | TUI girdisi | Tip | Varsayılan | Sınır |
|---|---|---|---|---|
| `DownloadParallelEnabled` | İndirme → **Paralel İndirme** | bool | `true` | — |
| `DownloadConcurrency` | İndirme → **Eşzamanlı İndirme** | int | `2` | `1`–`8` (yazma/okuma anında sıkıştırılır) |

Kod tarafındaki sabitler `CliConfig` içinde:

```csharp
public const int MinDownloadConcurrency     = 1;
public const int MaxDownloadConcurrency     = 8;
public const int DefaultDownloadConcurrency = 2;

public int DownloadConcurrency
{
    get => _downloadConcurrency;                        // varsayılan: 2
    set => _downloadConcurrency = ClampConcurrency(value);
}
```

`ClampConcurrency` hem setter'da hem de config dosyası okunurken System.Text.Json setter'ı çağırdığı
için **her yazma yolunda** uygulanır. Elle `"DownloadConcurrency": 99` yazan bir config dosyası
yüklenirken `8`'e, `0` veya negatif değer `1`'e çekilir; bu davranış testle doğrulanmıştır
(`DownloadSettingsTests.HandEditedConfigWithOutOfRangeConcurrency_IsRepairedWhileLoading`).

Üst sınır neden 8: her iş ayrı bir HTTP/yt-dlp akışıdır ve hepsi aynı diske yazar. 8'in üstü
tüketici hatlarda sağlayıcıların hız sınırına takılma, disk başlığı yarışı ve "hangi dosya indi"
takibinin zorlaşması riskini getirir; alt sınır 1 zaten "sıralı" anlamına geldiği için ayrı bir
değer olarak tutulmaz.

### Ayarların etkisi

```csharp
public sealed class DownloadQueueOptions
{
    public const int DefaultConcurrency = CliConfig.DefaultDownloadConcurrency;

    private int _maxConcurrency = DefaultConcurrency;

    public bool ParallelEnabled { get; init; } = true;

    // Sıkıştırma nesne KURULURKEN, tam burada yapılır: saklanan değer her zaman 1-8 arasındadır.
    public int MaxConcurrency
    {
        get  => _maxConcurrency;
        init => _maxConcurrency = CliConfig.ClampConcurrency(value);
    }

    // Paralel kapalıysa 1 (mevcut sıralı davranış), açıksa saklanan (zaten geçerli) değer.
    public int EffectiveConcurrency => ParallelEnabled ? _maxConcurrency : 1;

    public static DownloadQueueOptions FromConfig(CliConfig config) => new()
    {
        ParallelEnabled = config.DownloadParallelEnabled,
        MaxConcurrency  = config.DownloadConcurrency   // config kendi setter'ında sıkıştırır
    };
}
```

TUI ve CLI aynı `FromConfig` çağrısını kullanır; yani "kaç iş paralel" kararı tek yerde verilir ve
iki arayüz arasında farklılaşamaz.

**Sıkıştırmanın sahibi tek: değeri tutan nesne.** Her sahip kendi değerini yazarken sıkıştırır —
`CliConfig.DownloadConcurrency` setter'da (JSON okuma/yazma dâhil), `DownloadQueueOptions.MaxConcurrency`
ise `init` içinde. Bu yüzden `FromConfig` de, `EffectiveConcurrency` de **ikinci kez sıkıştırmaz**.
Kural şu: bir yerde sıkıştır, her yerde saklanan değere güven. Böylece ekranda gösterilen sayı,
config'de saklanan sayı ve kuyruğun kullandığı sayı asla ayrışmaz; gizli/geçersiz bir "aradaki değer"
kalmaz. (Bu, `EffectiveConcurrency`'nin üçüncü kez sıkıştırdığı eski hâlin denetimde işaret ettiği
belirsizliği kapatan düzeltmedir.)

---

## 2. Mekanizma

Kuyruk motoru: `Migurdex.Cli/Services/Downloads/DownloadQueueService.cs`.

### 2.1 İş modeli

```csharp
public sealed class DownloadQueueItem
{
    public string DisplayName { get; }
    public Func<CancellationToken, Task<DownloadResult>> Work { get; }
}
```

Kuyruk, indirilen şeyin ne olduğunu **bilmez**. Yalnızca "bir iş, iptal token'ı alır ve bir
`DownloadResult` döndürür" sözleşmesini bilir. Bu sayede:

* motor tek başına test edilebilir (testlerde işler yapay gecikmeli fonksiyonlardır),
* HLS (yt-dlp süreci), MP4 (HTTP) ve gelecekte eklenecek bir indirici aynı kuyruğu kullanabilir,
* kaynak çözümleme (API taraması) işin *içinde* kalır; kuyruk yalnızca zamanlama yapar.

İşin kurulumu (bölüm → görünen ad → çözülen adaylar → istek) `BatchDownloadPlan.BuildItem`
içindedir ve hem TUI'nin hem CLI'nin kullandığı **tek** yoldur; adayları sırayla denemek ise
`DownloadQueueWork.ForCandidateChain`'e aittir:

```csharp
BatchDownloadPlan.BuildItem(api, downloadService, provider, format, group,
                            episode, config, source => new DownloadRequest { ... })

DownloadQueueWork.ForCandidateChain(downloadService, candidates, requestFactory)  // sıradaki adayı dene
```

`ForCandidateChain`, tek indirmedeki davranışın aynısıdır: adaylar sırayla denenir; video tamamlanıp
yalnızca altyazı aşaması iptal edilmişse (`Success == true && IsCancelled == true` + `MediaPath` dolu)
zincir durur ve sonuç korunur. Aday sayısı `DownloadSourceResolver.MaxCandidates = 3` ile sınırlıdır.

### 2.2 Sıralı yol (paralel kapalı)

```csharp
if (options.EffectiveConcurrency <= 1)
{
    for (var index = 0; index < items.Count; index++)
    {
        results[index] = cancellationToken.IsCancellationRequested
            ? CancelledResult(items[index], index, "İndirme iptal edildi.")
            : await RunItemAsync(items[index], index, items.Count, progress, cancellationToken);
    }
}
```

* Girdi sırası korunur; işler hiçbir zaman üst üste binmez (gözlemlenen eşzamanlı iş sayısı = 1).
* İptal edilirse o anki iş `Cancelled` olur ve **kalanlar hiç başlatılmaz**.
* Bu yol, eşzamanlılık açısından tek indirme akışının birebir aynısıdır: tek seferde tek akış.

### 2.3 Paralel yol

```csharp
using var gate = new SemaphoreSlim(options.EffectiveConcurrency, options.EffectiveConcurrency);
var tasks = new Task[items.Count];
for (var index = 0; index < items.Count; index++)
{
    var capturedIndex = index;
    tasks[capturedIndex] = RunGatedAsync(capturedIndex);
}

await Task.WhenAll(tasks);

async Task RunGatedAsync(int index)
{
    var started = false;
    try
    {
        await gate.WaitAsync(cancellationToken);   // izin yoksa bekle
        started = true;
    }
    catch (OperationCanceledException)
    {
        results[index] = CancelledResult(items[index], index, "İndirme iptal edildi.");
        return;                                    // hiç başlamadı → iptal sayılır
    }

    try
    {
        results[index] = await RunItemAsync(items[index], index, items.Count, progress, cancellationToken);
    }
    finally
    {
        if (started)
        {
            gate.Release();                        // izin mutlaka geri verilir
        }
    }
}
```

Tasarım notları:

* **İzin (semaphore) tabanlı havuz, sabit işçi sayısı değil.** "N eşzamanlı" = "en fazla N iş aynı
  anda `Work` çalıştırıyor". İşler kısa/uzun karışık olsa da toplam paralellik asla N'i aşmaz.
* **Sonuçlar önceden ayrılmış dizide, indeksle yazılır.** `Task.WhenAll` sonunda rapor her zaman
  **girdi sırasındadır**; kullanıcı hangi bölümün hangi satırda olduğunu kaybetmez. Paralel bitiş
  sırası ekrana yansımaz, yalnızca durum ve dosya yolu eşleşir.
* **`gate.Release()` `finally` içinde ve yalnızca izin alındıysa.** İptal edilmiş bir bekleyiş
  sonrası yanlışlıkla izin bırakıp eşzamanlılığı şişirme ihtimali yoktur.
* **İptalde `Task.WhenAll` fırlatmaz.** `RunItemAsync` `OperationCanceledException`'ı yakalayıp
  işi `Cancelled` yaptığı için kuyruk her zaman bir özet döndürür; TUI/CLI bu özeti ekrana basabilir.
* **Ölü kilit yok:** ayrı bir eşzamanlılık sınırı yok; işler birbirini beklemez, yalnızca izni
  beklersiniz. Tek ortak kaynak hedef dosya kilididir (`DownloadTargetLock`) ve zaten hedef başına
  1'dir.

### 2.4 İş durumuna karar verme

```csharp
private static DownloadQueueItemState ResolveState(DownloadResult result)
{
    if (result.IsCancelled)
    {
        // Video tamamlanmışsa iptal yalnızca altyazı aşamasına aittir; iş tamamlanmış sayılır.
        return string.IsNullOrWhiteSpace(result.MediaPath)
            ? DownloadQueueItemState.Cancelled
            : DownloadQueueItemState.Completed;
    }

    return result.Success ? DownloadQueueItemState.Completed : DownloadQueueItemState.Failed;
}
```

`Cancelled` (diskte tam dosya yok) ile `Completed + IsCancelled` (video bitti, altyazı iptal)
ayrımı CLI'de çıkış kodunu belirler: ikincisi `3`, birincisi `1`.

### 2.5 Hata izolasyonu

```csharp
try
{
    result = await item.Work(cancellationToken);
    ...
}
catch (OperationCanceledException) { /* → Cancelled */ }
catch (Exception exception)        { /* → Failed, mesaj saklanır */ }
```

Bir işin patlaması diğerlerini etkilemez; kuyruk **iptal edilmedikçe** diğer işleri sürdürür. Bu,
"12 bölümden 11'i indi, 1'inin kaynağı ölmüş" durumunun normal karşılanması demektir.

---

## 3. İptal akışı

| Katman | Tetikleyici | Sonuç |
|---|---|---|
| TUI | Toplu indirme ekranında `Esc` | `CancellationTokenSource.Cancel()` → çalışan işler `Cancelled`, tamamlanan dosyalar diskte kalır; ekranda "İptal edildi; tamamlanan dosyalar diskte kaldı" bildirimi |
| TUI | `Ctrl+C` | `TuiApplicationCancellation.BeginModal` sayesinde ilk `Ctrl+C` yalnızca **toplu indirmeyi** iptal eder, uygulamayı kapatmaz |
| CLI | `Ctrl+C` | `Console.CancelKeyPress` token'ı iptal eder; kuyruk `Cancelled` özeti döner, tamamlanan dosyaların listesi yazılır, çıkış kodu `1` |

İptal, "şu ana kadar inen her şeyi sil" anlamına **gelmez**. Yarım kalan dosyalar `.part` +
`.meta` olarak bırakılır; `DownloadResume` açıkken sonraki çalıştırma kaldığı yerden devam eder.

---

## 4. Ölçümler ve kanıtlar

Aşağıdaki sonuçlar bu değişiklikle birlikte çalıştırılan testlerden alınmıştır.

| Ölçüm | Yöntem | Sonuç |
|---|---|---|
| Varsayılan politika | `DownloadQueueOptions.FromConfig(new CliConfig())` | `ParallelEnabled = true`, `EffectiveConcurrency = 2` |
| Sıkıştırma | `DownloadQueueServiceTests.Options_ClampOutOfRangeConcurrency` | `0 → 1`, `-5 → 1`, `99 → 8`, `(99, kapalı) → 1` |
| Paralel davranış | 4 iş, eşzamanlılık 2, iş başına 60 ms | Gözlenen tepe eşzamanlılık **2**; sonuç indeksleri `0,1,2,3` |
| Yükseltilmiş eşzamanlılık | 6 iş, eşzamanlılık 4 | Gözlenen tepe eşzamanlılık **4** |
| Sıralı davranış | 4 iş, `ParallelEnabled = false`, `MaxConcurrency = 4` | Gözlenen tepe eşzamanlılık **1**; bitiş sırası girdi sırası |
| Kısmi hata | 4 iş: 2 başarılı, 1 `Failed`, 1 istisna | `Succeeded = 2`, `Failed = 2`, hiçbiri diğerini durdurmadı |
| İptal | İlk iş token'ı iptal ediyor (sıralı mod) | İş 0 `Completed`, iş 1-2 `Cancelled` |
| Önceden iptal | Token işten önce iptal | 3/3 `Cancelled`, hiçbir iş başlamadı |
| Uçtan uca (gerçek indirici) | `BatchDownloadFlowTests`: 3 bölüm, eşzamanlılık 2 | 3 dosya diske yazıldı (96 KiB), HTTP katmanında tepe eşzamanlılık **2**; paralel kapalıyken aynı işlerle tepe **1** |
| Uçtan uca kısmi hata | 3 bölümden 2 numaralı bölüm 500 döndü | 2 dosya diskte, başarısız bölümün dosyası yok, özet `Failed = 1` |
| Regresyon | Tüm paket (446 test) | **436 başarılı**, 10 başarısız. Kalan 10 hata `ExtractorSmokeTests` içinde **ölü canlı örnek bağlantıları** (`0 results`); değişikliklerden değil. Düzeltme kaydı: [toplu-indirme-ve-indirme-dizini.md](toplu-indirme-ve-indirme-dizini.md) §8 |
| Uçtan uca (CLI, gerçek ikili + sahte API) | `migurdex download … --episodes 1-3`, paralel açık, eşzamanlılık 2 | 3 dosya diske yazıldı; medya sunucusunda ölçülen tepe eşzamanlılık **2**; çıkış kodu `0`; `--json` → `total=3, succeeded=3, success=true` |
| Uçtan uca: paralel kapalı | Aynı 3 bölüm, `DownloadParallelEnabled=false` | Tepe **1**; stderr `sıralı (paralel kapalı)` yazıyor; 3 dosya yine indi (mevcut davranış korundu) |
| Uçtan uca: kullanıcı yükseltti | Aynı 3 bölüm, `DownloadConcurrency=4` | Tepe **3** — değer 2'de takılı kalmıyor; stderr `4 eşzamanlı (paralel)` |
| Uçtan uca: elle bozulmuş değer | `"DownloadConcurrency": 99` olan config | Stderr `8 eşzamanlı (paralel)`; geçersiz sayı hiç kullanılmadı, indirme tamamlandı |
| Uçtan uca (TUI, gerçek ikili + ConPTY) | Ayarlar → **Eşzamanlı İndirme** `4` → Kaydet → toplu indir | Çoklu seçim ekranı `4 eşzamanlı (paralel)`; toplu indirmede tepe **3**, 3 dosya diskte |
| Uçtan uca (TUI): anahtar kapalı | Ayarlar → **Paralel İndirme** kapat → Kaydet → toplu indir | Ekran `sıralı (paralel kapalı)`; tepe **1**, 3 dosya diskte |

Not: Yukarıdaki "tepe eşzamanlılık" ölçümleri sayaçla yapılır (iş/prova ve test HTTP handler'ı
üzerinde), duvar saati süresiyle değil. Süreye dayalı eşikler yük altında kararsız olabildiği için
bilinçli olarak kullanılmamıştır.

Koşum takımının dosya listesi, çalıştırma komutları ve tuzakları: [toplu-indirme-ve-indirme-dizini.md](toplu-indirme-ve-indirme-dizini.md) §9.

"Uçtan uca" satırları, **diskte gerçekten yazan** koşumlardır: sahte bir HTTP API (arama, detay,
grup, kaynak uçları + statik medya) ayağa kaldırılır, uygulamanın `ApiBaseUrl`'i oraya çevrilir ve
**derlenmiş gerçek ikili** çalıştırılır. Bu koşum takımları sahte sunucuyu ve kullanıcının
`config.json`'unu geçici olarak değiştirdikleri için depo ağacının **dışında** tutulur; ölçümler
yalnızca ürettikleri kanıttır.

---

## 5. Bilinen sınırlar

* **Sağlayıcı yükü artar.** Eşzamanlılık 4-8'e çıkarıldığında aynı anda o kadar bölüm için kaynak
  taraması + indirme yapılır; sağlayıcı hız sınırı uyguluyorsa tek tek indirmek daha kararlı olabilir.
* **Tek dosya içi parçalama ayardan etkilenmez.** `Mp4Downloader` kendi 2-4 parçalı mantığını
  kullanır; toplu indirmede eşzamanlılık 8 ise ve dosyalar 8 MiB üstüyse teorik olarak çok sayıda
  bağlantı açılır. Bu durumda eşzamanlılığı düşürmek gerekir.
* **HLS'de yt-dlp süreç başına ayrı çalışır.** 8 eşzamanlı HLS indirmesi 8 `yt-dlp` (ve gerekirse
  8 `ffmpeg` aktarımı) demektir; bu araçlar kurulu değilse iş `Failed` olur ve diğerleri devam eder.
* **İlerleme gösterimi bölüm bazlıdır.** Aynı ekranda 12 ayrı ilerleme çubuğu yerine her satırda o
  işin anlık aşaması, yüzdesi ve hızı gösterilir; amaç TUI'yi okunur tutmaktır.
* **TUI ekranlarının otomatik regresyon testi depoda yok.** Etkileşimli ekranlar gerçek TTY
  ister; bu yüzden depodaki testler kuyruk motorunu, seçim/sıkıştırma kararlarını
  (`EpisodeMultiSelectState`, `EpisodeMultiSelectKeys`, `BatchDownloadPlan`) ve komut satırını
  kapsar. Ekranların uçtan uca davranışı, depo dışındaki bir ConPTY koşum takımıyla (gerçek ikili,
  gerçek tuşlar, gerçek dosyalar) §4'teki gibi doğrulanır; bu takım CI'a bağlı değildir.
