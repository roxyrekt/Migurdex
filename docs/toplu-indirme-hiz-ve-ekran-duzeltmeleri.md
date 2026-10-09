# Toplu indirme: hız çöküşü ve ekran taşması düzeltmeleri

Bu belge, 8 eşzamanlılıkla yapılan toplu indirmede gözlenen **üç arızayı** ve düzeltmelerini
tüm teknik ayrıntısıyla anlatır:

1. İndirme hızının özellikle **sona doğru KB/s seviyesine** çökmesi,
2. Toplu indirme listesinin **satır sarması** yüzünden bozulması,
3. Ayarlar ekranında üstte **"…" kalıntısı** kalması (başlığın yukarı kaçması).

İlgili belgeler: [toplu-indirme-ve-indirme-dizini.md](toplu-indirme-ve-indirme-dizini.md)
(toplu indirme akışının tamamı), [paralel-indirme.md](paralel-indirme.md)
(çok bölümlü paralellik + ayar yüzeyi).

> Bu değişiklik setindeki toplu indirme altyapısının (TUI seçim ekranı, `--episodes`, kuyruk
> servisi, indirme dizini ayarı) kendisi başka bir çalışmanın ürünüdür; bu belge yalnızca
> **hız, liste ve ekran taşması** düzeltmelerini anlatır ve altyapıya yapılan dokunuşları
> §"Bu değişiklik setinde dokunulan dosyalar" bölümünde kısaca listeler.

---

## 1. Şikâyetler ve ekran kanıtı

Kullanıcının bildirdiği üç durum ve ekran görüntülerinden okunan hâlleri:

| Şikâyet | Ekranda görülen |
|---|---|
| Hız sonlara doğru çöktü | `Toplu indirme • 8 eşzamanlı (paralel) • Esc: iptal`, `12 / 13 tamam`, etkin satır: `▶ S01E07 — ... Video indiriliyor • %83.2 • 346.76 KiB/s` |
| Liste bozuldu | Aynı ekranda her kayıt **iki fiziksel satıra** sarıyor (uzun bölüm adı + `→ E:\Miss Kobayashi's Dragon Maid\...` yolu) |
| Ayarlar ekranında "…" kalıyor | Listede ilk görünen satır `İndirme Bekleme [5.0 sn]`; üstünde `…` kalıntısı; `Ayarlar` başlığı ve listenin ilk satırları (Oynatma, Otomatik Oynat, Bekleme Süresi, Oynatıcı Logları, İndirme, Otomatik İndir) görünmüyor |

Üçüncü görüntüde ayarlar listesi **31 satır** + 5 satır çerçeve (başlık + boşluklar + ipucu)
= ~36 satır, pencere ise ~29 satır: yani liste pencereye sığmıyordu.

---

## 2. Kök nedenler

### 2.1 Parça (range) bağlantılarının sayısı: 8 dosya × 4 parça = 32

`Mp4Downloader` tek bir dosyayı, `ParallelThresholdBytes` (8 MiB) üstündeyse ve sunucu
`Range` destekliyorsa en fazla `ParallelMaxSegments` (4) parçaya bölüyordu:

```csharp
// ÖNCE
var count = (int)(total / ParallelBytesPerConnection);
return (int)Math.Clamp(count, ParallelMinSegments, ParallelMaxSegments);
```

Toplu indirmede 8 dosya paralel çalıştığında bu **8 × 4 = 32 eşzamanlı soket** demektir.
Sunucu ve istemci tarafındaki bağlantı başına kısıtlar yüzünden toplam hız düşer; dosyalar
teker teker bitmeye başladıkça sona kalanlar kısılmış hızla iner. Kullanıcının gördüğü
"en sonda KB'a kadar düşme"nin birinci bileşeni budur.

### 2.2 Sona doğru üst üste birleştirme (merge) ve disk doygunluğu

Her dosya bittiğinde parçalar `MergeSegmentsAsync` ile tek dosyada birleştirilir: bu iş
**tüm dosyayı okuyup yeniden yazar**. Dosyalar aynı anda başladığı için aynı anda da
bitiyorlar; 8 birleştirme üst üste binince hedef disk doyuyor ve hâlâ indiren dosyaların
yazımları aç kalıyor. `Finalizing` aşamasındaki bu doygunluk, ekranda `%83 • 346 KiB/s`
gibi düşük hızlar olarak görünür — "son dosya yavaş" sanılır, oysa darboğaz ağ değil
disktir.

```csharp
// ÖNCE (Mp4Downloader)
Report(progress, DownloadStage.Finalizing, total, total);
await MergeSegmentsAsync(partPath, segmentCount, cancellationToken);
```

### 2.3 Hız ölçer boşlukta kendini sıfıra çekiyordu

Eski `DownloadSpeedometer.Sample` paydaya "son örnekten bu yana geçen süreyi" koyuyordu:

```csharp
// ÖNCE
var instant = Math.Max(0, (bytes - _lastBytes) / elapsed);
_lastBytes  = bytes;
_bytesPerSecond = _hasEstimate ? _alpha * instant + (1 - _alpha) * _bytesPerSecond : instant;
```

Bayt akmayan bir aralıkta (ağ beklemesi, merge kuyruğu) `delta = 0` olduğu için `instant = 0`
oluyor ve üstel ortalama (EMA) **ölçülen hızı eritiyordu**. Yani indirme durakladığında
ekran kendiliğinden KB/s'ye iniyor, indirme bitmiş gibi görünüyordu. Hata mesajı değil ama
"yanlış ölçüm" sınıfına giren, kullanıcıyı yanıltan bir davranıştı.

### 2.4 Liste satırlarının sarması ve canlı bölgenin taşması

İlerleme satırı üç parçadan oluşuyordu: işaret (`✓` / `▶` / `·`), bölüm adı, ayraç (`•` veya
`→`), ayrıntı (aşama, yüzde, hız, hedef yol). Uzun bölüm adı + tam dosya yolu tek satıra
sığmayınca terminal **satırı sarıyor**; her kayıt iki fiziksel satır kaplıyor, canlı bölge
pencere yüksekliğini aşıyor ve terminal kayıyordu. Yani 2.5’teki hatanın aynısı, farklı ekranda.

### 2.5 Ayarlar ekranının çerçeve taşması (üstteki "…" kalıntısı)

`SettingsView` listeyi **tamamıyla** çiziyordu; `AnsiConsole.Live` karenin tamamını her
seferinde yeniden çizdiği için içerik pencereden uzun olduğunda terminal kayıyor, karenin
üstü ekrandan çıkıyordu. Sonuç: `Ayarlar` başlığı yukarı kaçıyor ve canlı bölgenin üstünde
önceki kareden kalıntılar (`…`) kalıyor. Taşma olan her ekranda aynı sınıf hata oluşur;
bu yüzden çözüm ekran bazlı yama değil, ortak ve test edilebilir bir pencereleme
matematiği oldu.

---

## 3. Düzeltmeler

### 3.1 `Migurdex.Cli/Tui/ListWindow.cs` (yeni — saf, test edilebilir pencereleme)

Pencereleme ve satır bütçesi tek yerde toplandı; üç ekran aynı matematiği kullanıyor.

```csharp
internal static class ListWindow
{
    /// -2: bir satır bölüm başlığını pencereye çekme payı, bir satır da son satıra yazılan
    /// yeni satırın terminali kaydırmaması için güvenlik payı.
    public static int Budget(int windowHeight, int chromeRows, int maxRows)
        => Math.Min(Math.Max(1, maxRows), Math.Max(1, windowHeight - chromeRows - 2));

    public static (int Start, int End) Compute(
        int count, int cursorIndex, int budget, Func<int, bool>? isSection = null)
    {
        if (count <= 0) return (0, 0);

        var span = Math.Clamp(budget, 1, count);
        cursorIndex = Math.Clamp(cursorIndex, 0, count - 1);

        var start = Math.Max(0, cursorIndex - (span / 2));
        var end   = Math.Min(count, start + span);
        if (end - start < span) start = Math.Max(0, end - span);

        if (isSection is not null && start > 0 && !isSection(start) && isSection(start - 1))
        {
            // Bölüm başlığı da görünsün. Pencere en fazla bir satır büyür; imleç yine içeride
            // kalır (büyüme payı Budget'ın güvenlik payından karşılanır).
            start--;
            end = Math.Min(count, Math.Max(start + span, cursorIndex + 1));
        }

        return (start, end);
    }
}
```

Sözleşme (testlerle sabitlenen):

- `end - start ≤ min(budget, count)` — normal durumda; bölüm başlığı çekildiğinde en fazla +1
  (pencere hiçbir zaman `budget + 1`i geçmez).
- İmleç **her zaman** `[start, end)` içinde (`Compute_PullsTheSectionHeaderIntoTheWindow`,
  `Compute_AlwaysKeepsTheCursorInsideTheWindow`).
- Pencere, üstünde bölüm başlığı olan bir satırda **başlamaz**: sahipsiz seçenekler görünmez
  (`Compute_NeverStartsOnAnOrphanedRowBelowASectionHeader`).
- İçerik, çerçeveyle birlikte pencerenin son satırlarını **doldurmaz**; 2 satır pay kalır
  (`SettingsScreen_ContentNeverFillsTheLastRowsOfTheWindow`,
  `BatchScreen_ContentNeverFillsTheLastRowsOfTheWindow`).

### 3.2 `SettingsView` — liste pencerelemeye geçti

Çerçeve sabiti gerçek değere indirildi ve test edilebilir yapıldı:

```csharp
/// Tablonun dışındaki gerçek çerçeve satırları: başlık + boşluk (2), tablo öncesi boşluk (1),
/// tablo sonrası boşluk (1) ve ipucu (1) = 5.
internal const int ChromeRows = 5;
```

Eski `Math.Max(5, WindowHeight - 8)` hesabı ve elle yazılmış pencere mantığı kaldırıldı:

```csharp
(int Start, int End) VisibleWindow()
{
    var budget = ListWindow.Budget(TuiConsole.WindowHeight, ChromeRows, items.Count);
    return ListWindow.Compute(items.Count, cursorIndex, budget, i => items[i].IsSection);
}
```

`BuildTable(config)` → `BuildTable(config, winStart, winEnd)`; yalnızca pencere içindeki
satırlar çizilir. Kırpıldığında ipucu satırı kullanıcıya nerede olduğunu söyler:

```
↑↓ gez • Enter değiştir • Esc geri • satır 4-27/31
```

### 3.3 `BatchDownloadView` + `BatchProgressLine` — tek fiziksel satır garantisi

Satır kırpma kararı saf fonksiyonda veriliyor; view yalnızca renk ekliyor:

```csharp
public static (string Label, string Detail) FitArrow(string label, string detail, int prefix, int width)

var budget = Math.Max(separator.Length + 4, width - Math.Max(0, prefix));
if (string.IsNullOrEmpty(detail)) return (Ellipsize(label, budget), string.Empty);
// Ayrıntı (aşama, %, hız) etiketten daha değerli: önce ona yer ayrılır, etiket kırpılır.
var minLabel     = Math.Min(24, Math.Max(4, budget / 3));         // satırın en çok 1/3'ü etikete
var detailBudget = Math.Max(0, budget - separator.Length - minLabel);
var detailText   = Ellipsize(detail, detailBudget);
var labelBudget  = Math.Max(0, budget - separator.Length - detailText.Length);
```

Satır genişliği `TuiConsole.WindowWidth - 1`; çizilen satırda işaret + boşluk = 2 kolon
(`RowPrefixWidth`) olduğu için toplam görünür genişlik pencereyi geçmez.

Satır sayısı artık terminalden türetiliyor:

```csharp
// ÖNCE: Math.Clamp(TuiConsole.WindowHeight - GridChromeRows, 3, MaxRows)  // kısa terminalde taşabiliyordu
var rowBudget = ListWindow.Budget(TuiConsole.WindowHeight, GridChromeRows, MaxRows);
```

`GridChromeRows = 9` (ekran başlığı + kuyruk başlığı + hız satırı + ipuçları),
`MaxRows = 12`.

### 3.4 `EpisodeMultiSelectPrompt` — sabit 15 satırlık sayfa pencereye sığdırıldı

Seçim listesi de aynı sınıf hataya açıktı (sabit `pageSize = 15`):

```csharp
private const int PromptChromeRows = 6;   // başlık, boşluk, aralık, boşluk, "Kontroller", ipucu

// Kısa terminalde sayfa taşarsa terminal kayar ve ekran kalıntısı kalır; sayfa boyu
// pencereye sığdırılır (uzun terminalde istenen sayfa boyu korunur).
pageSize = ListWindow.Budget(TuiConsole.WindowHeight, PromptChromeRows + headers.Count, pageSize);
```

### 3.5 `DownloadConnectionBudget` (yeni) — toplam parça bağlantısı bütçesi

```csharp
internal static class DownloadConnectionBudget
{
    public const int TotalBudget             = 8;   // toplu indirmede en fazla eşzamanlı parça bağlantısı
    public const int DefaultSegmentsPerFile  = 4;   // tek dosya üst sınırı (Mp4Downloader ile aynı)

    public static int SegmentsFor(int parallelFiles)
        => Math.Clamp(TotalBudget / Math.Max(1, parallelFiles), 1, DefaultSegmentsPerFile);

    public static void Apply(int parallelFiles) => _segmentsPerFile = SegmentsFor(parallelFiles);
    public static void Reset()                  => _segmentsPerFile = DefaultSegmentsPerFile;
}
```

Bütçe tablosu (`TotalBudget = 8`):

| Eşzamanlı dosya | Dosya başına parça | Toplam bağlantı |
|---|---|---|
| 1 (tek indirme) | 4 | 4 |
| 2 | 4 | 8 |
| 3 | 2 | 6 |
| 4 | 2 | 8 |
| 5–8 | 1 | 5–8 |

Kuyruk başında uygulanır, `finally` ile her durumda geri alınır
(`Migurdex.Cli/Services/Downloads/DownloadQueueService.cs`):

```csharp
DownloadConnectionBudget.Apply(options.EffectiveConcurrency);
try { ... }
finally { DownloadConnectionBudget.Reset(); }
```

Tüketici tarafı (`Mp4Downloader.ComputeSegmentCount`): bütçe 2'nin altına indirdiğinde
parça indirme hiç açılmaz, dosya tek akıştan iner:

```csharp
var maxSegments = Math.Min(ParallelMaxSegments, DownloadConnectionBudget.SegmentsPerFile);
if (maxSegments < ParallelMinSegments) return 0;                 // tek akış
var count = (int)(total / ParallelBytesPerConnection);
return (int)Math.Clamp(count, ParallelMinSegments, maxSegments);
```

Tek bölüm indirme akışı **etkilenmez**: kuyruk çalışmadığında `SegmentsPerFile` varsayılan 4'tür.

### 3.6 Merge kapısı — birleştirmeler sıraya girdi

```csharp
/// Parça birleştirme kapısı: birleştirme tam dosyayı okuyup yeniden yazar. Sekiz dosya aynı
/// anda birleştirilince hedef disk doyuyor ve hâlâ indiren dosyalar KB/s'ye iniyordu; aynı
/// anda tek birleştirme çalışır.
private static readonly SemaphoreSlim MergeGate = new(1, 1);
```

```csharp
Report(progress, DownloadStage.Finalizing, total, total);
await MergeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
try     { await MergeSegmentsAsync(partPath, segmentCount, cancellationToken).ConfigureAwait(false); }
finally { MergeGate.Release(); }
```

### 3.7 Hız ölçer — boşlukta düşmüyor, boşluktan sonra sıfırdan kuruluyor

```csharp
private const double IdleResetSeconds = 1.5;   // bu süreden uzun boşlukta tahmin sıfırdan başlar

if (bytes <= _lastBytes)
{
    // Bayt akmadı: geçen süre hız değildir. Ölçüm düşürülmez, yoksa birleştirme ya da
    // duraklama aralarında ekran KB/s gösterir ve indirme bitmiş gibi görünür.
    return;
}

var delta = bytes - _lastBytes;
_lastBytes    = bytes;
_lastFlowTime = now;                            // yeni: son akış zamanı
var instant   = delta / elapsed;
_bytesPerSecond = (!_hasEstimate || elapsed >= IdleResetSeconds)
                      ? instant                                  // eski tahmin taşınmaz
                      : _alpha * instant + (1 - _alpha) * _bytesPerSecond;
```

Yeni genel API: `IdleFor(now)` — son bayt hareketinden bu yana geçen süre. Ekran bunu
kullanarak uydurma hız yerine durum yazar (`BatchDownloadView.DescribeProgress`):

```csharp
var idle    = tracker.IdleFor(DateTimeOffset.UtcNow);
var stalled = idle is { TotalSeconds: >= 3 };
...
if (stalled)            detail += $" • duraklı {(int) idle!.Value.TotalSeconds} sn";
else if (speed > 0)     detail += $" • {FormatBytes((long) speed)}/s";
```

Ayrıca ekranın üstüne **tek bir toplam hız** satırı eklendi; böylece "sona kalan yavaş
dosya" bütün işi yavaş göstermiyor:

```csharp
var speedText = running == 0
                    ? "[grey]toplam hız: —[/]"
                    : totalSpeed > 0
                        ? $"[grey]{FormatBytes((long) totalSpeed)}/s toplam[/]  [grey]•[/]  [grey]{running} etkin[/]"
                        : $"[grey]{running} etkin[/]";
```

---

## 4. Testler

Yeni/dokunulan testler:

| Dosya | Kapsam |
|---|---|
| `Migurdex.Tests/ListWindowTests.cs` (yeni) | Bütçe payı, pencere taşmaz, imleç hep görünür, bölüm başlığı kuralı, sınır durumlar (boş liste, aralık dışı imleç, bütçe > liste) |
| `Migurdex.Tests/BatchProgressLineTests.cs` | Satır verilen genişliği geçmez, ayrıntı etiketten öncelikli, kısa girdi bozulmaz, ayraç/işaret genişlikleri |
| `Migurdex.Tests/DownloadConnectionBudgetTests.cs` | Bölme tablosu, tek dosya tavanı, `Reset`, toplam bağlantı ≤ 8 |
| `Migurdex.Tests/DownloadQueueServiceTests.cs` | Sıralı/paralel davranış, iptal, hata izolasyonu, **bütçenin koşu sırasında uygulanıp sonunda geri alınması** |
| `Migurdex.Tests/DownloadSpeedometerIdleTests.cs` | Boşluk hızı eritmez, boşluk sonrası ölçüm sıfırdan kurulur, `IdleFor` doğru |
| `Migurdex.Tests/BatchDownloadFlowTests.cs` | Gerçek indirme yığınıyla yazma, eşzamanlılık tavanı, kısmi hata, `DescribeConcurrency` metni |

`DownloadConnectionBudget` süreç genelinde **statik** bir durum tuttuğu için iki test sınıfı
aynı xUnit koleksiyonuna alındı; aksi hâlde sınıflar paralel koşarken biri diğerinin sayacını
bozabiliyordu:

```csharp
[Collection("DownloadConnectionBudget")]
public sealed class DownloadConnectionBudgetTests { ... }

[Collection("DownloadConnectionBudget")]
public sealed class DownloadQueueServiceTests { ... }
```

### Koşu sonucu

```
dotnet build Migurdex.Cli/Migurdex.Cli.csproj      → Oluşturma başarılı: 0 Uyarı, 0 Hata (EXIT=0)
dotnet test  Migurdex.Tests/Migurdex.Tests.csproj  → Başarılı! Başarısız: 0, Başarılı: 466, Toplam: 466
```

`ExtractorSmokeTests` koşudan çıkarıldı: gerçek sitelere ağ isteği atan duman testleri
(`SampleLinks`), bu ortamda bir kısmı ağ/URL durumuna bağlı olarak düşüyor; bu değişiklik
setiyle ilgisi yok (extractor'lara dokunulmadı). Filtresiz koşuda yalnız bu sınıf düşer.

---

## 5. Yerel doğrulama ve çalıştırma

```bash
# Taşınabilir SDK ile (makinede PATH'teki dotnet artık yalnız runtime içeriyor)
cd "C:\Users\naton\OneDrive\Desktop\Main Migu\real migu"
PATH="/c/Users/naton/.dotnet-sdk:$PATH" dotnet build Migurdex.Cli/Migurdex.Cli.csproj
PATH="/c/Users/naton/.dotnet-sdk:$PATH" dotnet test  Migurdex.Tests/Migurdex.Tests.csproj \
      --filter "FullyQualifiedName!~ExtractorSmokeTests"
```

Taze derleme ve çalıştırma:

```
Migurdex.Cli\bin\Debug\net10.0\migurdex.exe        (TUI, etkileşimli terminal)
Migurdex.Cli\bin\Debug\net10.0\migurdex.exe --version
```

> **Not (ortam):** Bu makinede `C:\Program Files\dotnet` artık SDK içermiyor (yalnız
> runtime). Derlemeler `C:\Users\naton\.dotnet-sdk` altındaki taşınabilir SDK 10.0.401 ile
> yapıldı; kendi komutlarınız için bu yolu `PATH`e ekleyin.

### Doğrulanan / doğrulanmayan

- **Doğrulandı:** Derleme 0 uyarı/0 hata; 466 test geçti; yeni testler pencerenin pencereye
  sığdığını, imlecin görünür kaldığını ve bütçenin koşu sonunda geri alındığını kanıtlıyor;
  taze `migurdex.exe` çalışıyor (`--version`, `--help`).
- **Doğrulandı (09.10.2026, son sürüm):** Aynı set `origin/main` (`95a65ff`) üzerine
  uygulandı → derleme 0 hata, 580/584 test geçti (4 kırmızı bizden değil, bkz. §7).
  Ayrıntı, yöntem ve temiz taban karşılaştırması: §7 "Upstream uyum ölçümü".
- **Doğrulanmadı (sınır):** TUI etkileşimli olduğu için kaydırma/`…` kalıntısı davranışı
  ekranda gözle teyit edilmedi; bu davranış pencere matematiği + testlerle sabitlendi.
  Gerçek terminalde son teyit kullanıcının çalıştırmasında yapılmalıdır (özellikle
  pencereyi çok küçültüp ayarlar/toplu indirme ekranlarına bakarak).

---

## 6. Bu değişiklik setinde dokunulan dosyalar

Hız/liste/ekran düzeltmelerinin kendisi:

| Dosya | Ne |
|---|---|
| `Migurdex.Cli/Tui/ListWindow.cs` | **Yeni.** Pencereleme + satır bütçesi (saf fonksiyon) |
| `Migurdex.Cli/Tui/BatchProgressLine.cs` | **Yeni.** İlerleme satırını tek fiziksel satıra sığdırır |
| `Migurdex.Cli/Tui/Views/SettingsView.cs` | Liste pencerelemeye geçti, `ChromeRows=5`, ipucuna `satır X-Y/N` |
| `Migurdex.Cli/Tui/Views/BatchDownloadView.cs` | Satır bütçesi `ListWindow.Budget`, toplam hız satırı, `duraklı N sn` |
| `Migurdex.Cli/Tui/EpisodeMultiSelectPrompt.cs` | Sabit 15 satırlık sayfa pencereye sığdırıldı |
| `Migurdex.Cli/Services/Downloads/DownloadConnectionBudget.cs` | **Yeni.** 8 bağlantılık toplam bütçe |
| `Migurdex.Cli/Services/Downloads/Mp4Downloader.cs` | Bütçeye göre parça sayısı, `MergeGate` |
| `Migurdex.Cli/Services/Downloads/DownloadSpeedometer.cs` | Boşlukta düşmeyen ölçüm, `IdleFor` |
| `Migurdex.Cli/Tui/TuiConsole.cs` | `WindowHeight` / `WindowWidth` (güvenli ölçüm + varsayılan) |
| `Migurdex.Tests/ListWindowTests.cs`, `BatchProgressLineTests.cs`, `DownloadConnectionBudgetTests.cs`, `DownloadQueueServiceTests.cs`, `DownloadSpeedometerIdleTests.cs` | Testler |

Bu PR'daki toplu indirme altyapısı (önceki çalışmadan, aynı dalda):

| Dosya | Ne |
|---|---|
| `Migurdex.Cli/Services/BatchDownloadPlan.cs` | Sıralama, seçim eşleme, özet satırları, eşzamanlılık metni |
| `Migurdex.Cli/Services/EpisodeSelectionSpec.cs` | `1,2,3` / `1-12` / `all` çözümleme ve sezon kuralı |
| `Migurdex.Cli/Services/Downloads/DownloadQueueService.cs` | Kuyruk: paralel/sıralı koşu, iptal, raporlama, bütçe uygulama |
| `Migurdex.Cli/Tui/Views/BatchDownloadView.cs` | Toplu indirme ekranı (seçim + ilerleme + özet) |
| `Migurdex.Cli/Tui/EpisodeMultiSelectPrompt.cs` | Çoktan seçmeli bölüm listesi |
| `Migurdex.Cli/Services/DownloadCommand.cs` | `--episodes` + `ExecuteBatchAsync` (CLI toplu indirme) |
| `Migurdex.Cli/Configuration/CliConfig.cs` | `DownloadParallelEnabled`, `DownloadConcurrency` (1-8), dizin normalizasyonu |
| `Migurdex.Cli/Program.cs`, `DownloadServiceCollectionExtensions.cs` | DI kayıtları |
| `Migurdex.Cli/Tui/Views/AnimeDetailsView.cs` | `⤓ Toplu indir...` girdisi |
| `README.md`, `docs/` | Kullanım + tasarım belgeleri |

> Not: `docs/`, `Migurdex.Cli/Services/BatchDownloadPlan.cs` vb. dosyalar bu dalda henüz
> commit edilmemiş (untracked); PR kapsamına alınacaksa `git add <yol>` ile **yol belirterek**
> eklenmeli.

---

## 7. Upstream uyum ölçümü (09.10.2026, Faz 1)

Bu değişiklik seti `b660265` tabanında yazıldı. PR açmadan önce **son sürüm** ile uyum
ölçüldü. Yöntem, ölçümler ve çıkan iki gerçek uyum işi aşağıda.

### 7.1 Ölçüm yöntemi (`origin/main` + 3 yollu birleştirme)

Kullanıcının çalışma ağacındaki değişiklikler korunur; ölçüm ayrı bir git worktree'de,
çalışma ağacına dokunulmadan yapılır:

```sh
R=".../real migu"
W="$TMP/migurdex-ust"
git -C "$R" fetch origin
git -C "$R" diff HEAD > ours.patch                       # tracked değişiklikler
git -C "$R" ls-files --others --exclude-standard > ours-untracked.txt
git -C "$R" worktree add --detach "$W" origin/main       # son sürüm, temiz ağaç
git -C "$W" apply -3 ours.patch                           # 3 yollu birleştirme
# untracked dosyalar yol korunarak kopyalanır
```

Temiz taban ölçümü için ikinci bir worktree (`origin/main`, hiç düzeltme yok) kullanıldı;
böylece kırmızı testlerin bizden mi upstream'den mi geldiği **ayırt edilebilir** oldu.

### 7.2 Ölçüm sonucu

| Ölçüm | Taban | Sonuç |
|---|---|---|
| Taban commit | `b660265` | `95a65ff` (`origin/main`) |
| Upstream farkı | — | **+8 commit**: `89e8c0c` blame · `178c2f4` nightly · `a89ab71` kaynak metadata · `1ff11c0` renk/hizalama · `75feef4` commit damgası · `bf5c5a0` README · `1d6f598`/`95a65ff` domain |
| Etiketler | — | `v1.12.0`, `v1.13.0`, `v1.13.1`, `nightly` |
| Yama uygulaması | — | 11 dosyanın **10'u temiz**; yalnız `README.md` çakıştı |
| Derleme (son sürüm + bizim değişiklikler) | — | **0 uyarı, 0 hata** |
| Test — pristine `origin/main` | 456 | 452 geçti, **4 kırmızı** |
| Test — son sürüm + bizim değişiklikler | 584 | 580 geçti, **aynı 4 kırmızı** |
| Yeni kırık test | — | **0** (+128 test bizden) |
| Çalışan ikili | — | `migurdex dev 95a65ff`; `--help`'te hem `blame` hem `--episodes` |

### 7.3 Çıkan iki gerçek uyum işi

1. **`README.md` çakışması (yalnız doküman).** Upstream `blame` satırlarını, biz `--episodes`
   satırlarını aynı kod bloğuna ekledik. Çözüm: **iki blok da korunur** — silinecek içerik yok.
2. **`BatchDownloadFlowTests.FakeApiClient` arayüz uyumu (gerçek derleme hatası, "rebase eklemesi").**
   Upstream, `blame` özelliğiyle `IApiClientService`'e
   `Task<ApiResult<BlameReport?>> GetBlameReportAsync(CancellationToken)` ekledi (`89e8c0c`).
   Bizim sahte sınıf bu üyeyi uygulamadığı için test projesi `CS0535` ile derlenmedi.

   ⛔ **Bu düzeltme tabana ÖNCEDEN uygulanamaz.** `BlameReport` tipi `b660265`'te **yok**;
   yalnız `origin/main` (`89e8c0c`+) ile gelir. Tabanda uygulanırsa derleme
   `CS0246: 'BlameReport' türü ... bulunamadı` ile düşer (ölçüldü). Bu yüzden ekleme bu
   değişiklik setine **dahil edilmedi**; rebase/merge **sonrasında** uygulanır:

   ```csharp
   // Migurdex.Tests/BatchDownloadFlowTests.cs → FakeApiClient, en son üye
   // (SaveTrackerMappingAsync'ten hemen sonra). Toplu indirme akışı bu üyeyi hiç çağırmaz;
   // gövde dosyanın kendi kuralına uyar.
   public Task<ApiResult<BlameReport?>> GetBlameReportAsync(
       CancellationToken cancellationToken = default) => throw new NotSupportedException();
   ```

   Kanıt: rebase sonrası ağaçta bu eklemeyle derleme **0 uyarı/0 hata** ve testler **580/584**
   (4 kırmızı bizden değil); ekleme olmadan `CS0535`. Tabanda ise eklemesiz durum yeşil:
   **0 hata · 466/466**.

### 7.4 Kesişen 5 dosyanın denetimi ("elden geçirme")

Hem bizim hem upstream'in dokunduğu dosyalar: `Migurdex.Cli/Program.cs`,
`Migurdex.Cli/Services/DownloadCommand.cs`, `Migurdex.Cli/Services/NonInteractiveCommand.cs`,
`Migurdex.Cli/Tui/Views/SettingsView.cs`, `README.md`.

Denetim ölçütü: birleşik dosyanın `origin/main`'e göre diff'inde **upstream'den silinen satır
olmamalı**. Sonuç:

- `Program.cs` — tek ekleme: `services.AddTransient<BatchDownloadView>();`. Upstream'in
  `blame` kaydı ve diğer DI satırları olduğu gibi duruyor.
- `NonInteractiveCommand.cs` — yalnız iki yardım satırı değişti; upstream'in
  `migurdex blame [--json]` satırı ve `GetBlameReportAsync` çağrısı (`439`) yerinde.
- `SettingsView.cs` — toplamsal: `ChromeRows`, `VisibleWindow()`, pencereli `BuildTable`,
  `satır X-Y/N` ipucu, `ParallelDownload`/`DownloadConcurrency` ayarları, indirme dizini
  doğrulaması. Upstream'in aynı dosyadaki değişiklikleri korunmuş.
- `DownloadCommand.cs` — toplamsal: `Episodes` alanı, `--episodes`/`--ep` ayrıştırma,
  `-e` ile çakışma hatası, `ExecuteBatchAsync`. Upstream'in kendi değişiklikleri duruyor.
- `README.md` — iki tarafın eklemeleri yan yana.

Upstream özelliklerinin birleşik ağaçta gerçekten durduğu ayrıca doğrulandı:
`Program.cs`'te `blame`, `UpdateService.cs`'te `nightly`, ayrıca `--help` çıktısı.

### 7.5 Değişiklik setinin son sürüme göre boyutu

`git diff origin/main --stat` → **31 dosya, 5.534 ekleme, 24 silme**: 17 üretim dosyası
(`Migurdex.Cli`), 11 test dosyası, 3 doküman, `README.md`.

### 7.6 Bağımsız bulgu: upstream'in kendi kırmızı testleri (bizimle ilgisi yok)

Aynı 4 test **pristine `origin/main`'de de kırmızı**; yani bu, bizim setimizin getirdiği bir
kırık değil:

```
BitrateTests.FormatBitrate_Formats(1500, "1.5 kbps")                                     [FAIL]
BitrateTests.FormatBitrate_Formats(6200000, "6.2 Mbps")                                  [FAIL]
BitrateTests.DisplayLabel_CombinesQualityAndBitrate(1080p, 6200000, "1080p • 6.2 Mbps")  [FAIL]
BitrateTests.DisplayLabel_AppendsCodec                                                   [FAIL]
```

**Kök neden (kanıtlı):** `Migurdex.Shared/Models/VideoSource.cs` içindeki `FormatBitrate`,
`$"{...:0.#} Mbps"` biçimini **`CultureInfo.InvariantCulture` olmadan** kullanıyor.
Makinenin kültürü `tr-TR`; ondalık ayırıcı `,` olduğu için çıktı `6,2 Mbps` geliyor,
test ise `6.2 Mbps` bekliyor.

- Kırmızı testlerin **dördü de ondalıklı** durumlar; `8 Mbps` / `800 kbps` / `500 bps`
  (ondalıksız) geçiyor — kültür kuramıyla tutarlı.
- **Kanıt:** kaynak koda dokunmadan `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` ile aynı
  filtre koşuldu → **77/77 geçti, 0 kırmızı**.
- Etki yalnız testle sınırlı değil: Türkçe yerelde **kullanıcıya görünen etiket** de
  `6,2 Mbps` yazar. Ayrı ve küçük bir PR adayıdır; bu setin kapsamına sokulmadı.

### 7.7 Faz 2 (PR) için öneri

1. Dalı `origin/main` üzerine taşı (`git rebase origin/main` ya da merge); çakışma
   beklentisi: **yalnız `README.md`** (iki bloğu da tut).
2. Untracked dosyalar bulunduğu için `git add -A` yerine **yol belirterek** `git add` kullan.
3. PR'ı tek tema tutmak için `BitrateTests`/`InvariantCulture` düzeltmesini **ayrı PR** yap.
4. PR gövdesi bu dokümandan türetilebilir; §7.2 tablosu "neden güvenli" sorusunu yanıtlar.

---

## 8. Bilinen sınırlar / sonraki adımlar

### AnimeciX toplu kaynak araması — karar ve güncel durum (10.10.2026)

Kullanıcının 10.10.2026 kararıyla toplu indirmede yalnızca AnimeciX kaynak çözümlemeleri
yaklaşık **5 saniye arayla** başlatılır. İlk çözümlemede doğrudan MP4/HLS kaynağı gelmezse
**15 saniye sonra bir kez** daha çözümleme yapılır. İstek aralığı ve yeniden deneme bekleyişi
iptal token'ını izler. Bu pacing kaynak çözümleme çağrılarındadır; medya indirmeleri kuyruktaki
mevcut paralel işçilerde çalışmaya devam eder.

Yeni yolun `AnimeciXBatchSourceResolverTests` ve
`BatchDownloadFlowTests.Plan_BuildItem_AnimeciXRetriesMissingDirectSourceAndExplainsTheResult`
ile birim/sahte API kapsamı vardır. Değişiklikten sonra **canlı AnimeciX sağlayıcısıyla koşu
yapılmadı**; dolayısıyla gerçek sağlayıcı temposu ve yeniden deneme sonucu henüz doğrulanmış
değildir. §7.2'deki 09.10.2026 ölçümleri önceki snapshot'ın tarihsel kanıtıdır; bu yeni AnimeciX
davranışının canlı doğrulaması olarak okunmamalıdır.


1. **Sunucuya uyarlanabilir bütçe yok.** Şu an sabit 8 bağlantı tavanı var; sunucunun
   hız tepkisine göre parça sayısını artırıp azaltan (AIMD) bir politika ve dosya bitince
   boşalan yuvayı beklemeden sonraki bölüme veren iş çalma (work-stealing) kuyruğu,
   sonraki adım olarak duruyor.
2. **Kısa terminal duman testi yok.** Pencere matematiği birim testleriyle korunuyor ama
   gerçek bir terminal (ConPTY) altında 10/20/40 satırlık pencerelerle açılıp ekran
   görüntüsünü doğrulayan bir duman testi eklenebilir.
3. **Görsel teyit kullanıcıda.** Kaydırma/kalıntı davranışının son onayı, düzeltilmiş
   derlemeyle gerçek terminalde yapılmalı.
4. **Ağ duman testleri ayrık.** `ExtractorSmokeTests` gerçek siteye bağlanır; CI'da koşacaksa
   ağ erişimi/URL ömrü hesaba katılmalı.
