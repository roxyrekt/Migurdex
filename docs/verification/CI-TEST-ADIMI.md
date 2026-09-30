# CI Test Adımı — BULGU 29 ve BULGU 30

Bu kayıt, PR'ın CI'a eklediği `test` işinin **neden** eklendiğini, eklenirken
karşılaşılan iki hatanın **tam teşhisini** ve bu süreçte yapılan **iki yanlış
kararın** nasıl düzeltildiğini tutar.

---

## 1. Başlangıç: CI test çalıştırmıyordu

`build-release.yml` içinde `dotnet test` **yoktu**. İş yalnızca derliyor ve
paketliyordu.

> **Bu, "CI yeşil" ifadesinin ne anlama geldiğini ayırmak için önemli.**
> PR #5 ilk gönderimde 3/3 yeşildi. Yeşil tik "derleniyor" demekti,
> "testler geçiyor" demek değildi — testler kırık olsa bile PR yeşil kalırdı.

### Eklenen iş

```yaml
test:
    name: Test (${{ matrix.os }})
    runs-on: ${{ matrix.os }}
    strategy:
        fail-fast: false
        matrix:
            os: [ ubuntu-22.04, windows-latest ]

    steps:
        -   uses: actions/checkout@v7
        -   name: Setup .NET
            uses: actions/setup-dotnet@v6
            with:
                dotnet-version: ${{ env.DOTNET_VERSION }}
        -   name: Test
            run: >
                dotnet test Migurdex.Tests/Migurdex.Tests.csproj
                -c Release
                --filter "FullyQualifiedName!~ExtractorSmokeTests"

release:
    name: Create Release
    needs: [ build, test ]      # testler yeşil olmadan yayımlama yok
    if: startsWith(github.ref, 'refs/tags/v')
```

### İki tasarım kararı ve gerekçeleri

**1. Rust kurulumu yok.** Native kütüphaneyi yükleyen tek şey `ExtractorFixture`, o da
yalnız `ExtractorSmokeTests` tarafından kullanılıyor ve aşağıda filtreleniyor.
Doğrulandı: native DLL test çıktı klasöründe **yokken** 598 test geçiyor.

**2. `linux-arm64` yok.** Aynı testler `linux-x64`'te koşuyor; emülasyonlu arm
koşucusuna test eklemek yalnızca süre kazandırır, kapsam değil.

---

## 2. BULGU 29 — kendi yarattığım kırılganlık

### Belirti

Tam süit aralıklı kırılıyordu:

```
DatabaseMigrationAndConcurrencyTests.TrackerMappings_And_SyncQueue_Roundtrip
Microsoft.Data.Sqlite.SqliteException : SQLite Error 14: 'unable to open database file'
  at SqliteConnectionPool.GetConnection()
  at SqliteConnection.Open()
```

### İlk ölçümler

| Koşum | Sonuç |
|---|---|
| Sınıf **tek başına** × 12 | 0 başarısız |
| 6 SQLite sınıfı **birlikte** × 10 | 0 başarısız |
| **Tam süit** × 10 / × 12 / × 15 | 3 / 5 / 3 → ~%20 |
| `%TEMP%`'deki 12.864 dizin temizlendikten sonra × 15 | 3 (değişmedi) |

İlk üç satır "sınıflar-arası ortak kaynak çakışması" diyordu ve bu doğruydu.
Dördüncü "disk birikimi değil" diyordu, o da doğruydu.

### ❌ YANLIŞ TESPİT — kayda geçirildi, sonra düzeltildi

`NewTempDir` değişikliğini geri aldım (Grup H öncesi hâl) ve **3/12 kırılma** gördüm.
Buradan "demek ki bu kırılganlık **upstream'ten** geliyor, PR'ın getirdiği hata değil"
sonucuna çıktım ve CI test adımını **`continue-on-error: true`** ile ekledim.
Kararı gerekçelendirdim, kayda geçirdim.

**Teşhis yanlıştı.** Geri aldığım şey suçlu olan şey değilmiş: `TestTempDirectoryTests`
dosyası hâlâ ağaçtaydı ve hâlâ `CleanupAll()` çağırıyordu. İki katkıdan **birini**
geri alıp "ikisi de geri alındı" diye okumuşum.

### ✅ Gerçek kök neden

Grup H'nin eklediği `TestTempDirectoryTests` içindeki
`CleanupAll_Removes_Every_Tracked_Directory` testi **test ortasında** global temizliği
çağırıyordu:

```
CleanupAll() → SqliteConnection.ClearAllPools() + kayıtlı dizinlerin TAMAMININ silinmesi
```

xUnit test sınıflarını **paralel** çalıştırdığı için bu, eşzamanlı başka testlerin
kullandığı veritabanı bağlantılarını ve dizinlerini yok ediyordu.

### Düzeltme

Test **kaldırıldı** — global temizlik tanımı gereği süreç sonuna ait bir yoldur,
birim testinde çağrılamaz. Yerine yalnızca `ReleaseDatabaseHandles`'in istisna
atmadığını doğrulayan dar bir test kondu. Temizleme yolu (`ProcessExit` + SIGTERM/
SIGINT) ve güvenlik ağı aynen korundu.

| | |
|---|---|
| Kırık hâlde | 12 koşuda 5, 15 koşuda 3 |
| Düzeltilince | 20 + 20 koşu → **40/40 temiz** |

`continue-on-error` **kaldırıldı**, `release.needs: [ build, test ]` yapıldı. Yani
**testler yeşil olmadan yayımlama yapılmıyor** — eklemek istediğim asıl değer buydu;
iki ölçüm turu kaybedildi, sonra geri alındı.

---

## 3. BULGU 30 — kapı ilk CI koşusunda kırıldı

`Test (ubuntu-22.04)` işi kırmızı: koşu `36685447129`, iş `109790509869`.
Build işleri 3/3 yeşil, `Test (windows-latest)` yeşildi.

Dört ayrı hata çıktı. Ayrıntılı Linux kaydı:
[`linux/LINUX-DOGRULAMA-4.md`](linux/LINUX-DOGRULAMA-4.md).

### 3.1 TUI testi Windows ayracı kullanıyordu (3 test)

```csharp
[InlineData("Views\\EpisodeSourcesView.cs")]   // Linux'ta ayraç değil
```

`Path.Combine` Linux'ta `\`'i ayraç saymaz; `Views\EpisodeSourcesView.cs` adlı tek
parça bir dosya adı üretir ve tarama tutmaz. InlineData düz `/` kullanıyor, test ayracı
`Path.DirectorySeparatorChar`'a çeviriyor.

### 3.2 `DirectoryNotFoundException` yanlış teşhis ediliyordu

```csharp
catch (IOException ex)
{
    throw new ConcurrentDownloadException("Video hedefi başka bir indirme tarafından kullanılıyor.");
}
```

`DirectoryNotFoundException` bir `IOException` **türevidir**. Yani hedef klasör
**hiç yokken** kullanıcıya "eşzamanlı indirme var" deniyordu — tamamen yanlış sebep.
Artık ayrı bir `catch (DirectoryNotFoundException)` kolu var ve eksik klasörü adıyla
söylüyor.

### 3.3 Linux izleyici testi kendisi yarış içeriyordu (3/3 kararsız)

```csharp
var before = CountOurWatcherChildren();   // tüm izleyici çocukları
... 500 ms bekle ...
Assert.Equal(before, CountOurWatcherChildren());
```

Aynı sınıftaki önceki testin izleyicisi hâlâ yaşıyordu (`before = 1`) ve 500 ms
sonra öldü (`after = 0`). **Üretim kodunda hata yoktu**; ölçüm kardeş testlerden
kirleniyordu.

Düzeltme: ölçüm hedef PID imzasına bağlandı. İzleyicinin komut satırı
`--internal-watch-orphan <ebeveyn> <başlangıç> <hedef> <hedefBaşlangıç>` olduğu için,
doğru doğan izleyici **yalnızca bu** hedef PID'sini taşır.

### 3.4 Asıl bulgu: süpürücü canlı koşunun dizinini siliyordu

Koşu başına **8–17** hata:

| Hata | Adet (6 koşu) |
|---|---|
| `SQLite Error 10: 'disk I/O error'` | 65 |
| `SQLite Error 14: 'unable to open database file'` | 10 |
| `DirectoryNotFoundException` | 6 |

#### Ayırıcı ölçümler

| Deney | Sonuç | Ne kanıtladı |
|---|---|---|
| `34561a3` (Grup F/G/H **öncesi**) tam süit × 4 | **0 hata** | Sorun bu dalgada eklenen kodda |
| 6 SQLite sınıfı tek başına × 3 | 0 | SQLite'ın kendisi sorun değil |
| `/tmp`'de 60 eşzamanlı saf SQLite yazıcı | 0 | Disk / inode / ortam suçsuz |
| Süpürücü **kapalı** × 3 | **0** | Süpürücü suçlu |
| Süpürücü **açık** × 3 | **47** | — |

> **İkinci yanlış ölçüm.** Bu tablodaki ilk satırı ilk yaptığımda **geçersizdi**:
> `rm` sonrası `git checkout` silmek istediğim dosyayı geri getirmişti. Doğrusu
> `git worktree add 34561a3`. İki kez aynı tuzak, iki ayrı ders.

#### Kök neden — günlükle kanıtlandı

Süpürücüye "neyi sildin" günlüğü eklendi. Tek `dotnet test` çağrısının içinde:

```
RunRoot=/tmp/migurdex-tests/run-673-8df1ecd7610bde4
entry  =/tmp/migurdex-tests/run-645-8df1ecd75d52024
abandoned=True
   -> SILDI /tmp/migurdex-tests/run-645-8df1ecd75d52024
```

PID'ler: **645, 673, 703, 707, 719, 745** — hepsi aynı koşuda.

xUnit v3 test derlemesini **birden çok işlemde** çalıştırıyor; her biri modül
başlatıcısını çalıştırıp kökü süpürüyor. Bir işlem öldüğü anda **bir sonraki işlem
onun hâlâ kullanılan dizinini siliyor.** SQLite günlük dosyasını oluşturamadığı için
çöküyor.

İki ayrı kusur buna yol açtı:

1. **"Sahibi ölmüş" = "terk edilmiş" sayıldı.** Aynı şey değil: ölmüş bir işlemin
   dosyaları hâlâ başka bileşenler tarafından kullanılabiliyor.
2. **`Path.GetFileName(entry)` (dosya adı) ile `RunRoot` (tam yol) karşılaştırılıyordu.**
   **Hiç eşleşmiyordu** — yani "kendi koşu dizinimi atla" güvencesi fiilen hiç yoktu.
   Derleyici buna uyarı vermez.

#### Neden yalnız Linux

Unix'te `unlink`/`rmdir` **açık dosya bulunsa da** başarılıdır; silme gerçekten
gerçekleşir. Windows'ta açık kilit dosyası `Directory.Delete`'i başarısızdır ve hata
yutulur — aynı hata orada **hiç görünmez**. CI'da 4 çekirdek varken 1 test düştü,
WSL'de 16 çekirdekte 8–17 test düştü.

#### Düzeltme

| Değişiklik | Gerekçe |
|---|---|
| `SweepGracePeriod = 10 dakika` | Yeni bitmiş koşunun dizini dokunulmamış sayılır, toplanmaz. Yarışı tamamen kapatır |
| `entry` ↔ `RunRoot` **tam yol** karşılaştırması | Gerçekten çalışan "kendi dizinimi atla" güvencesi |
| `TryDelete`'den `GC.Collect()` + `WaitForPendingFinalizers()` **kaldırıldı** | Süpürücü canlı dizini silemeyince yardımcı üç kez küresel GC zorluyordu; bu da paralel testlerin nesnelerini sonlandırabiliyordu |
| `IsAbandoned`: `pid == Environment.ProcessId` → asla terk edilmiş değil | Kendi canlı koşumuzu silmemek |

**Dürüst ödünleşim:** kalıntı artık "bir sonraki koşuda" değil, **"on dakika sonraki
koşuda"** toplanır. Birikim yine sınırsız büyümez. Doğruluk, daha agresif temizliğin
önüne geçti.

#### Regresyon testi

`SweepAbandonedRuns_Keeps_Fresh_Run_Of_Dead_Process` — sahibi ölmüş görünen ama taze
koşu dizini korunmalı.

`SweepAbandonedRuns_Removes_Dead_Process_Run` artık dizini yaşlandırıyor; gerçek bir
çökme kalıntısı zaten eski olduğu için yeni eşikle de toplanıyor.

#### Kontrol deneyi

| Ölçüm | Sonuç |
|---|---|
| Yeni regresyon testi, düzeltme **açıkken** | yeşil |
| Yeni regresyon testi, düzeltme **kapalıyken** | **kırmızı** |
| Tam süit × 3, düzeltme **kapalıyken** | **40 hata** |

---

## 4. Sonuç

| Koşu | Sonuç |
|---|---|
| `36677801742` (commit 1) | ✓ 3/3 — ama `dotnet test` **çalışmıyordu** |
| `36685447129` (commit 2) | ✗ `Test (ubuntu-22.04)` kırmızı — 4 gerçek hata |
| `36692060302` (commit 4) | ✓ **5/5** — `Test ubuntu` ✓, `Test windows` ✓ (600/599), 3 build ✓ |

Kapı, eklendiği ilk gerçek koşuda kırıldı ve iki tur ölçümle düzeltildi. Bu, kapının
işe yaradığının kanıtı: test çalıştırmayan bir "yeşil" CI ile karşılaştırılabilir
bir fark var.

### Kapanmayan kalıntı (BULGU 31)

`/tmp/migurdex-tests` altında `run-` önekli **olmayan** `bilinmeyen-<guid>` dizinleri
var; testler kökün altına doğrudan yazıyor. Yeni süpürücü 10 dakikalık yaşlanma
kuralı nedeniyle bunlara dokunmuyor. Etkisi yok — eski kod da yalnız 1 saatten eski,
adı çözülemeyen dizinleri siliyordu — ama kapsam dışı kalıyor.

---

## 5. BULGU 33 — merge sonrası `main`'de kırılan test (kendi eklediğim test)

**Tetikleyici:** koşu `36696912854`, `main` / `0c6615e`. `Test (ubuntu-22.04)` **yeşil**,
`Test (windows-latest)` **kırmızı**. Tek hata:

```
DownloadStallTimeoutTests.Mp4_SlowButSteadyBody_CompletesWithoutError [FAIL]
DownloadException : Sunucu 1 saniye boyunca veri göndermedi; indirme takıldı.
```

Bu, tam da "kapı işe yaradı" kanıtının üçüncüsü: kapı, PR merge edildikten sonra ilk
gerçek koşuda bir testin **zaman duyarlılığını** yakaladı.

### Kök neden — **test**, üretim kodu değil

`Mp4_SlowButSteadyBody_CompletesWithoutError`, "yavaş ama düzenli akış takılmış sayılmamalı"
iddiasını sınar. Betiği 6 × 1 KiB blok, bloklar arası **40 ms** boşluk.

Bütçe `QuickOptions()`'tan geliyordu: `FirstByteTimeout = IdleTimeout = 200 ms`.

**40 ms'lik araya karşı yalnızca 5 kat başlık.** Yüklü bir koşucuda tek bir 1 KiB okuma
200 ms'yi aşınca koruma — **doğru şekilde** — devreye giriyor.

`QuickOptions()` bilerek küçük: *takılma* testlerinin (`Mp4_BodyStopsAfterFirstChunk`,
`Mp4_CancelWhileStalled`, `Mp4_ParallelSegmentsStall`, `Subtitle_BodyStopsMidStream`)
hızlı tetiklenmesi için. Sorun, "yavaş ama düzenli" testinin de aynı fabrikayı kullanması.

### Ölçüm — hatayı önce ürettim

| Koşum | Sonuç |
|---|---|
| Yüksüz, bu test sınıfı × 8 | **0** başarısız |
| 48 işlemci yakını (%100 yük, 16 çekirdek), aynı sınıf × 10 | **1** başarısız — **CI ile aynı test** |

Temiz makinede yakalanmıyordu; tahminle değil, **üretilerek** teşhis edildi.

### Düzeltme

`SlowSteadyOptions()` eklendi ve **yalnız bu test** ona bağlandı:

| | `QuickOptions()` (takılma testleri) | `SlowSteadyOptions()` (bu test) |
|---|---|---|
| `FirstByteTimeout` | 200 ms | **3 sn** |
| `IdleTimeout` | 200 ms | **3 sn** |
| 40 ms'lik araya başlık | 5× | **75×** |

Takılma testlerinin 200 ms bütçeleri **korundu** (dosyada 6 adet `FromMilliseconds(200)`
değerinin altısı da yerinde). 3 sn, `HangGuard`'ın (8 sn) altında kaldığı için gerçek bir
regresyon yine hızlı yakalanır. Normal akışta test ~240 ms sürer — zaman kaybı yok.

### Dokunulmayan: `ToWholeSeconds`

Hata mesajı 200 ms bütçe için *"1 saniye"* diyor. İnceledim: bu **kasıtlı** ve
dokümante — "çok küçük test eşikleri (ör. 200 ms) 0'a yuvarlanmasın diye en küçük değer
1'dir". Gerçek varsayılanlar 30 sn / 20 sn, yani üretimde mesaj doğru. **Dokunulmadı.**
