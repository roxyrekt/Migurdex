# PR-4 — Kapsamlı Düzeltme Paketi (11 bulgu)

**Taban:** `nutalia/main` = `66ecbd2` · **Tarih:** 30 Eylül 2026
**Kapsam:** 23 dosya değişti, 15 dosya eklendi · **+1566 / −122 satır**
**Durum:** build **0 hata / 0 uyarı** · test **494/494** · 8 ardışık Windows turu yeşil

---

## 0. Özet

Bu PR üç bağımsız düzeltme grubunu bir araya getiriyor. Hepsinin ortak noktası
**aynı hatayı iki yerde iki farklı biçimde ele almak** — asıl sorun kod eksikliği
değil, kopya uygulamalardı.

| # | Bulgu | Gruba | Statü |
|---|---|---|---|
| C1 | CLI'da SSRF koruması yok | PR-3 | çözüldü |
| A1 | Bilinmeyen argüman → TUI'ye düşüp çöküyor | Grup A | çözüldü |
| A2 | `CopyPluginsToApi` listesinde 2 plugin eksik | Grup A | çözüldü |
| A3 | CLI API sürümünü doğrulamıyor | Grup A | çözüldü |
| C2 | HTTP istemcisinde zaman aşımı yok | Grup B | çözüldü |
| C1b | HLS ilerlemesinde toplam grotesk titriyor | Grup C | çözüldü |
| C1c | `.migurdex.lock` birikimi | Grup C | çözüldü |
| D1 | Yol bütçesi iki kez düşülüyor | Grup D | çözüldü |
| D2 | ffmpeg teşhisi çok genel | Grup D | çözüldü |
| D3 | Linux'ta yetim alt süreç | Grup D | çözüldü |
| E1 | `DownloadProgress.isAudioTrack` hiç atanmıyor | turda bulundu | çözüldü |
| **E2** | **`GetStaleResumePartPaths` kararsız sıralama** | **turda bulundu** | **çözüldü** |
| **E3** | **Kaynak tarama zaman aşımı** | **turda bulundu** | **genişletildi** (kanıt zayıf — bkz. E3) |
| E4 | `.migurdex-partial` kalıcı çöp | turda bulundu | çözüldü |
| E5 | `EXDEV` dışı hatalarda gereksiz tam boy kopya | turda bulundu | çözüldü |
| E6 | Yetim korumada PID geri dönüşümü | turda bulundu | çözüldü |
| E7 | Kilt TOCTOU'su (Linux) | turda bulundu | çözüldü |
| E8 | `SemaphoreSlim` sızıntısı | turda bulundu | çözüldü |
| **E9** | **Tahmini toplam kesin gibi gösteriliyor (sahte %100)** | **nihai turda bulundu** | **çözüldü** |
| **E10** | **Koruma ölümde değil `wait()` anında tetikleniyordu (zombie)** | **Linux turunda bulundu** | **çözüldü** |
| **E11** | **Guard yalnız doğrudan çocuğu öldürüyordu (`ffmpeg` yetim)** | **Linux turunda bulundu** | **çözüldü** |
| **E12** | **Yanlış atıf: "temizlik bordan geliyor"** | **Linux turunda çürütüldü** | **kayıt düzeltildi** |

---

## 1. C1 — CLI'da ağ geçidi (SSRF) koruması yok

Ayrıntılı kayıt: bu dosyanın §1 bölümü.

`DownloadHttp.ValidateHttpUri` yalnızca şema ve boş-olmayan host kontrol ediyordu.
API tarafında 47 satırlık tam bir koruma vardı. **Kopyalanmadı, `Migurdex.Shared`'e
taşındı** — asıl asimetri iki ayrı kopyadan doğuyordu.

| Metot | Kontrol |
|---|---|
| `ValidateHttpUri` (senkron) | şema + host + **IP literal** + `localhost` |
| `ValidateHttpUriAsync` | senkron olanlar + **DNS çözümlemesi** |

5 çağrı noktası async'e bağlandı: 3 indirme yolu + **yönlendirme hedefleri**.
39 yeni test. **384/384.**

---

## 2. Grup A — üst düzey argüman, plugin listesi, sürüm doğrulaması

### A1 — Bilinmeyen argüman TUI'ye düşüyordu

**Ölçüm (önce):**

| Argüman | non-TTY | gerçek konsol |
|---|---|---|
| `--bilinmeyen-bayrak` | exit −532462766, ham `InvalidOperationException` | sessizce TUI açılıyor |
| `bogus` | aynı | aynı |

**Kök neden:** `HelpCommand.IsTopLevelRequest` yalnız `--help`/`-h`/`help` tanıyordu;
`NonInteractiveCommand.IsCommand` yalnız `search|play|continue|download`. Gerisi TUI'ye
düşüyordu.

**Çözüm:** Yeni `TopLevelArguments.cs`. `Program.cs`'de TUI bloğundan hemen önce
doğrulama:

```
Hata: Bilinmeyen bayrak: --bilinmeyen
Kullanım: migurdex --help
```

→ **exit 2** (alt komutlarla aynı sözleşme: `download --bilinmeyen` zaten 2 veriyordu).

`HelpCommand.IsTopLevelRequest`'e **dokunulmadı** — `adadbf4` commit'inin düzelttiği yol
bu, bozulsaydı yardım çıktısı kırılırdı.

**Geçerli sayılan bayraklar** koddan çıkarıldı: `version`, yardım bayrakları, `update`,
`auth`, `search|play|continue|download`, ve TUI'yi açan tek bayrak `--no-update-check`.
Sonuncusu yardım metninde **belgelenmemişti** — boşluk kapatıldı.

**Test:** `TopLevelArgumentsTests` (22 vaka) + `TopLevelHelpTests` içinde
`PrintHelp_DocumentsEveryTuiFlagThatIsAccepted` — yardım metni kabul edilen bayrak
listesiyle **kayamaz**.

### A2 — `CopyPluginsToApi` listesinde 2 plugin eksik

**Ölçüm (bugün yeniden doğrulandı):**

| | Değer |
|---|---|
| `Migurdex.slnx` plugin projesi | **14** |
| `Migurdex.Api.csproj` → `CopyPluginsToApi` | **12** |
| Eksik | **`AniHub`, `Deokwave`** |
| Yerel build `/health` → `providers` | **12** |
| Yayımlanmış v1.11.0 paketi | 14 |

CI yayın yolu 14'ünü kopyalıyor (muhtemelen solution publish), `CopyPluginsToApi` listesi
yalnız lokal build'de kullanılıyor. Sonuç: **kaynaktan derleyen geliştirici `AniHub` ve
`Deokwave`'i hiç görmüyor.**

> **Bu bulgu daha önce "v1.11.0'da düzeldi" diye kaydedilmişti. Yanlıştı.** Yayımlanmış
> paket ölçülmüş, kaynak koda bakılmamıştı. İki farklı derleme yolu — "pakette 14"
> "kaynakta eksik" demek değil. Kayıt düzeltildi.

**Çözüm:** iki proje listeye eklendi. Ölçüm sonrası `Providers: 12 → 14` ✓
**Test:** `PluginProjectListTests` — csproj listesini `slnx` ile karşılaştırır, ileride
yeni plugin eklendiğinde liste unutulursa test kırılır.

### A3 — CLI API sürümünü doğrulamıyor

`IsApiOnlineAsync` yalnız `GET health` → `IsSuccessStatusCode` bakıyordu. `/health`
gövdesi `version` döndürüyor ama CLI **okumuyor**. Port 7045'te eski bir API çalışıyorsa
yeni CLI onu sessizce kullanıyor.

**Çözüm:** Yeni `ApiVersionCheck.cs`. `/health` gövdesinden `version` okunup CLI
sürümüyle karşılaştırılır.

**Tasarım kararı — uyarı, hata değil.** Gerekçe: geliştirme build'lerinde CLI `v0.0.0`,
API `0.0.0` döner; CI release'lerde ikisi de damgalıdır. Yani uyuşmazlık **yalnız gerçekten
karışık sürüm** durumunda oluşur. O durumda indirme yapılamayabilir ama API çalışıyor —
kesmek yerine **açıkça uyarıp devam etmek** daha doğru. Uyuşmayan `0.0.0` veya eksik
alan sessizce geçilir (yanlış pozitif üretmemek için).

**Test:** `ApiVersionCheckTests` — eşleşme, uyuşmazlık, `0.0.0`, eksik alan, geçersiz
JSON.

---

## 3. Grup B — C2: HTTP istemcisinde zaman aşımı yok

### Problem

```csharp
new HttpClient(handler, ...) { Timeout = Timeout.InfiniteTimeSpan }   // iki yerde
```

Sunucu başlık gönderip gövdeyi hiç göndermezse indirme **sonsuza kadar asılı kalır** ve
hiçbir hata fırlatmaz.

### Kritik tasarım kısıtı

`HttpClient.Timeout` **duvar-saati** limitidir. 272 MB dosya 1 Mbps'de ≈ 36 dakika sürer.
Naif `Timeout = 5dk` koymak mevcut sızıntı yerine **yeni hata** üretirdi.

### Çözüm: stall dedektörü

`DownloadStallGuard` bir akış okuma sarmalayıcısı. Her `ReadAsync` bir
`Task.Delay(remaining, callerToken)` gözcüsüyle `Task.WhenAny` yarışına girer.

| Tasarım kararı | Gerekçe |
|---|---|
| `stream.ReadAsync` (asenkron) | Senkron `Read` `CancellationToken` ile iptal edilemez; sonsuza dek bloklanırdı |
| `Stopwatch` ile "son hareket" | Eşik **son bayttan bu yana geçen süre**; toplam süreyle ilgisi yok |
| Önce `_callerToken.ThrowIfCancellationRequested()` | Ctrl+C düzgün `OperationCanceledException` verir, sonsuza dek döngü oluşmaz |
| Zaman aşımında `_readCts.Cancel()` + `ContinueWith(OnlyOnFaulted)` | Asılı kalan okuma iptal edilir, **unobserved exception bırakılmaz** |

| Eşik | Değer | Gerekçe |
|---|---|---|
| `ResponseHeaderTimeout` | 90 sn | Yavaş CDN el sıkışması |
| `FirstByteTimeout` | 30 sn | Başlık geldi ama gövde başlamadı |
| `IdleTimeout` | 20 sn | Segmentler arası normal bekleme bunu aşmaz |

Yapılandırılabilir (`DownloadStallOptions`, ctor enjeksiyonu). HLS zaten `yt-dlp` alt
süreç üzerinden çalışıyor ve `ExternalProcessRunner` kendi zaman aşımlarına sahip —
**ona dokunulmadı**. Bu düzeltme MP4 ve altyazı yolunu ilgilendirir.

**Test:** `DownloadStallTimeoutTests` — akış durur → hata · yavaş ama düzenli bayt → hata
yok · iptal → `OperationCanceledException` · hata tipi ve mesajı.

### Canlı doğrulama — eşikler gerçek CDN'de devreye girmedi

| Hedef | Exit | Süre | Boyut | Zaman aşımı |
|---|---|---|---|---|
| naruto / HLS | 0 | 22,0 sn | 260,47 MB | **yok** |
| naruto / MP4 | 0 | 187,8 sn | 259,75 MB | **yok** |
| one piece / HLS | 0 | 102,2 sn | 486,16 MB | **yok** |

Hız aralığı ~1,5 MiB/sn (yavaş) ile ~30 MiB/sn (hızlı). Üçünde de eşikler devreye girmedi.

---

## 4. Grup C — kilit birikimi ve HLS ilerleme titremesi

### C1c — `.migurdex.lock` birikimi

`taskkill /F` ile öldürülen HLS indirmesi indirme klasöründe 0 B'lık
`.migurdex.lock` bırakıyordu. `Dispose` `File.Delete` çağırıyor ama `TerminateProcess`
bu kodu çalıştırmıyor.

**Çözüm:** `TryRemoveStaleLockFile`, kilit **açılmadan hemen önce` çağrılıyor.** "Başka
bir indirme kullanıyor mu" ayrımı üç kademeli:

| Durum | Davranış |
|---|---|
| Dosya yok | dokunma |
| `FileShare.None` ile **açılamıyor** (`IOException`) | başka süreç tutuyor → **hiç dokunma**, akış normal `ConcurrentDownloadException` yoluna düşer |
| **Açılabiliyor** + 0 B | Migurdex kilidi asla yazmaz (`OpenOrCreate` yalnız açar) → kesin bayat kalıntı → sil |
| Açılabiliyor + **dolu** | elimizin değil → dokunma |

**Doğrulama:** `taskkill /F` sonrası kilit kalır (SIGKILL'de `finally` koşamaz, bu
beklenen) → **aynı klasöre tekrar indirme exit 0**, kalıntı sayısı **0**.

### C1b — HLS ilerlemesinde toplam (payda) grotesk titriyor

**Canlı ölçüm (aynı indirme içinde):**

```
1 KiB → 7.81 MiB → 177.03 MiB → 434.27 MiB → 521.3 MiB → 362.26 MiB → 280.65 MiB
```

Kaynak: yt-dlp'nin `of ~Y` tahmini. `ReportProcessProgress` yüzdeyi clamp ediyor ama
**toplamı clamp etmiyor.**

**Çözüm:** Faz içinde tahmin edilen toplam `Math.Max(önceki, yeni)`. Küçülen tahmin
yok sayılır.

**İki gerçek faz ayrımı:** HLS'te segment ham ~277 MiB → mux sonrası ~260 MiB; toplam
**küçülürse** bu gerçek bir faz geçişi olabilir. `frag` sayacı %100'e ulaşıp yeniden
başlıyorsa sıfırlamaya **izin ver**, aksi halde monoton tut. Yani yanlışlıkla
"ilerleme %95'ten %80'e geriledi" görüntüsü üretilmiyor.

**Test:** `HlsProgressMonotonicTests` — `HlsProgressHeartbeat` internal,
`InternalsVisibleTo` ile erişiliyor.

---

## 5. Grup D — yol bütçesi, ffmpeg teşhisi, yetim süreç

### D1 — Yol bütçesi

`EnsureFullPathBudget` çağrısında ayraç **1 kez** bütçeden düşülüyordu ama gerçekte
**2** kez gerekiyor (dosya adı + geçici son ek). Sınır durumu testleri eklendi: tam
bütçe, bütçe+1, çok uzun bölüm adı, çok uzun sağlayıcı adı.

**Test:** `DownloadPathBudgetTests`.

### D2 — ffmpeg teşhisi çok genel

`LooksLikeFfmpegFailure` yt-dlp stderr'inde genel `"failed"` geçiyorsa ffmpeg hatası
sanıyordu. Yanlış teşhis → kullanıcıya **"ffmpeg gerekiyor"** mesajı gider, gerçek neden
(ağ, 404, disk dolu) gizlenir.

**Çözüm:** ffmpeg'e özgü sinyaller aranır (`ffmpeg` + `error`/`not found`/
`No such file`/`Invalid data`). `DOWNLOAD.md` bu davranışı anlattığı için gerekçe
belgelendi.

**Test:** `FfmpegDiagnosisTests`.

### D3 — Linux'ta yetim alt süreç · **ilk uygulama ÖLÜYDÜ, yeniden yazıldı**

Windows'ta `ChildProcessTracker` Job Object kullanıyor. Linux'ta `PR_SET_PDEATHSIG`
olmadığı için ana süreç zorla öldürülürse `yt-dlp`/`ffmpeg` **yetim** kalıyordu.

İlk uygulama izlemeyi **ebeveyn sürecin içinde bir thread** ile yapıyordu:

```csharp
var thread = new Thread(() => Watch(parentPid, childPid, childStartTime));
thread.Start();
```

**Bu yapısal olarak işe yaramıyordu.** Ana süreç `SIGKILL` ile öldüğünde thread de
**anında** ölür; `KillIfAlive` hiç çalışamaz. Kod incelemesinde "mantık doğru" görünüyordu
ve testler yeşildi — **ölçülene kadar hiçbir şey işe yaramadığı belli değildi.**

**Linux ölçümü (çürütme kanıtları):**

| Kanıt | Sonuç |
|---|---|
| Guard thread gerçekten başlıyor mu | **EVET** — `/proc/16767/task/16779` → `comm = migurdex-linux-` (`TASK_COMM_LEN=16` ile kesilmiş). Yani thread yok değil, **yanlış yerde** |
| 4 sessiz alt süreç, `kill -9` sonrası | **4/4 YETİM KALDI** (10 sn sonra bile) |
| stdout'a yazan `bash` döngüsü | 6 sn yaşadı, yetim kaldı |
| **Ayırıcı deney:** doğrudan `yt-dlp` (boruya bağlı) | `kill -9` → 2 sn'de **öldü** |
| **Ayırıcı deney:** aynı `yt-dlp`, çıktıyı dosyaya | `kill -9` → **10 sn sonra hâlâ yaşıyor**, ppid değişmiş (yetim) |

Son satırlar belirleyici: gözlenen "temizlik" **kırılan stdout borusundan** geliyordu,
korumadan değil. Boru kullanmayan her alt süreç (sessiz `ffmpeg`, dosyaya yazan her şey)
`kill -9` sonrasında süresiz yetim olarak kalıyordu. Dahası, PID geri dönüşümü için
eklediğim `ReadStartTime` doğrulaması bile **hiçbir yola ulaşmıyordu** — o satırlar
`Watch`'in içindeydi, `Watch` hiç dönmüyordu.

> **Bu, tam olarak uyardığım riskin gerçekleşmesiydi.** Subagent'a "hangi yöntem daha
> düşük riskli, gerekçesini yaz, yanlış seçim gerçek indirmeyi bozar" demiştim; izlemeyi
> seçmişti ama **izleyicinin nerede yaşaması gerektiğini** sorgulamamıştım. Ölçüm yoksa
> bu kod sessizce hiçbir şey yapmayan bir düzeltme olarak kalacaktı — ve yorumu da yanlış
> güvence verdiği için ileride "zaten korunuyor" sanılacaktı.

**Çözüm — izleyici ayrı bir süreç:**

```csharp
// LinuxOrphanGuard.Attach
var startInfo = CreateWatcherStartInfo(parentPid, childPid, childStartTime);
var watcher = Process.Start(startInfo);   // ayrı, bağımsız Migurdex örneği
watcher.BeginOutputReadLine();            // sessiz kalsın
```

```
dotnet migurdex.dll --internal-watch-orphan <parentPid> <parentStart> <childPid> <childStart>
```

`Program.Main` bu argümanı görünce `LinuxOrphanGuard.RunWatcher(args)` çağırıp dönüyor —
TUI'ye hiç girmiyor.

| Tasarım kararı | Gerekçe |
|---|---|
| İzleyici **ayrı süreç** | Ebeveyn `SIGKILL` ile öldüğünde yaşamaya devam etmeli; ebeveyn içindeki hiçbir mekanizma bunu başaramaz |
| `sh`/`setsid`/`awk` **kullanılmadı** | PID ve başlangıç zamanı doğrulaması yine yönetilen kodda yapılır; harici araç bağımlılığı ve kabuk kaçış riski yok |
| `Environment.ProcessPath` + entry DLL | Framework-dependent çalıştırmada host `dotnet`'tir, DLL yolu ayrıca verilmelidir; apphost'ta gerekmez |
| **6 saatlik üst sınır** | Kalıcı sürgüç olmasın; iş dizini süpürmesiyle (6 saat) aynı mertebede |
| Çocuk bitince **hemen çık** | İndirme sırasında ek süreç yalnız indirme boyunca yaşar |
| Başlangıç zamanı doğrulaması korundu | Hedef kendiliğinden ölüp numarası geri dönerse ilgisiz sürece SIGKILL gitmesin |
| `kill(pid,0)` → `EPERM` = **yaşıyor** | Yanlışlıkla öldürme yerine güvenli taraf |

**Kabul edilen maliyet:** indirme boyunca ek bir .NET süreci yaşar (kısa ömürlü). Bu
bilinçli bir takastır: disk dolduran süresiz yetimlere karşı kısa ömürlü bir süreç.

**Test:** `LinuxOrphanWatcherTests` (20 test) — argüman sözleşmesi, yabancı argüman
reddi, hatalı sayı/PID reddi, çocuk yoksa anında çıkış, başlangıç zamanı kararlılığı,
Linux dışında izleyici modunun kapalı olması, `IsProcessAlive` zombie semantiği.

### E10 — Koruma ölümde değil **`wait()` anında** tetikleniyordu · **KRİTİK**

Bu, D3'ün düzeltmesi **ölçülünce** ortaya çıktı.

| | |
|---|---|
| Ölçüm | `t+14s … t+38s` **24 saniye** hedef yaşadı; ebeveyn `wait()` çağırıktan **1 sn** sonra öldü |

**Kök neden:** `ReadStartTime` `/proc/<pid>/stat` **22. alanı** (starttime) okuyor — bu
alan **zombie süreçte de geçerlidir**. `IsProcessGone` ise `kill(pid, 0)` kullanıyor — o da
zombie'a `0` döner. Yani büyükan reap etmedikçe izleyici **sonsuza kadar** yokluyor.

**Etki:** Migurdex'i **reap etmeyen** her ebeveyn için koruma sıfır — python
`subprocess` (`wait()`'siz), arka planda başlatıp devam eden kabuk, alışılmadık reaping
yapan supervisor. Sıradan etkileşimli bash ~1 sn içinde reap ettiği için maruziyet gerçek
ama **varsayılan değil** — yani "nadir köşe durumu" değil, sessizce güvenilmemesi gereken
bir mekanizma.

**Çözüm:** 3. alan (state) okunuyor; `Z` (zombie) ve `X` (ölü) **ölü** sayılıyor.
`IsProcessGone` **kaldırıldı** — tam olarak zombie tuzağını içeriyordu ve kullanılmıyordu.
`kill` bir `libc` çağrısı olduğu için `IsProcessAlive` içinde try/catch + `IsSupported`
kontrolü de eklendi (aksi halde Windows'ta `DllNotFoundException` fırlatıyordu).

**Doğrulama (kontrol deneyiyle):**

| Senaryo | Düzeltmeli | Kontrol (korumasız) |
|---|---|---|
| Sessiz `sleep 900` (kesin kanıt — yalnız SIGKILL ile ölebilir) | **0,10 sn** | **24 sn** yaşadı |
| Gerçek `yt-dlp` | **0,15 sn** | — |
| Reap eden ebeveyn (regresyon) | 0,10 sn | yanlış tetiklenme yok |

Migurdex'in `/proc/<pid>/stat` state geçmişi `['R','Z']` olarak ölçüldü: hedef t0+0,05
sn'de zombie iken, t0+0,10 sn'da öldü.

### E11 — Guard yalnız **doğrudan çocuğu** öldürüyordu

`ffmpeg` **hiçbir zaman** Migurdex'in doğrudan çocuğu değil, daima `yt-dlp`'nin çocuğu.
Ölçülen ilk belirti: korunan `bash` sarmalayıcı <1 sn'de öldü, **torunu** (`sleep 900`)
**18 sn** yaşadı. Sınıf dokümanının "`yt-dlp`/`ffmpeg` yetimlerini engeller" iddiası
gerçeği yansıtmıyordu.

**Çözüm — `KillProcessTree`:** `/proc` taranır → PID→ebeveyn haritası → kökten BFS →
her aday öldürülmeden hemen önce `/proc/<pid>/stat` yeniden okunur (PID geri dönüşümü
koruması) → **torunlar önce, kök sonra** (kök ölünce çocuklar yeniden bağlanmasın).
Harici araç (`pkill -P`, `pstree`) kullanılmadı; tümü yönetilen kodda.

**Doğrulama:**

| | Düzeltmeli | Karşı-olgusal (yalnız çocuğu öldür) |
|---|---|---|
| Çocuk (`bash`) | 0,10 sn | 0,05 sn |
| **Torun (`sleep 900`)** | **0,10 sn** | **20+ sn yaşadı** (2/5/10/18/20 sn) |
| **Gerçek `ffmpeg`** (MIG→yt-dlp→ffmpeg) | **0,45 sn** | — |

Gerçek `ffmpeg` senaryosu **canlı HLS** ile kuruldu; düz master playlist'lerde yt-dlp
`hlsnative` ile kendi içinde birleştirip `ffmpeg` çağırmıyor.

**İzleyici yanlışlıkla öldürülmüyor:** izleyici hedefin **torunu değil kardeşi** (biri de
Migurdex'in çocuğu). BFS kökten (`yt-dlp`) başladığı için ona ulaşamaz. Ölçüm:
`izleyici returncode = 0` → **normal çıkış**; `-9` olsaydı yanlışlıkla öldürülmüş olurdu.

**Yanlış öldürme:** `sleep 300` / bash döngüsü / `ffprobe` üçü de t+3/5/8/12/26 sn'de
**yaşadı**.

### E12 — Yanlış atfım düzeltmesi

Önceki kayıtta "gözlenen temizlik **kırılan stdout borusundan** geliyordu" yazıyordu.
**Bu yanlıştı.** Kontrollü deney:

| Varyant | `kill -9` sonrası |
|---|---|
| yt-dlp `stdout+stderr` → FIFO, tek okuyucu (ebeveyn) öldürüldü | **yt-dlp 12+ sn YAŞADI** |
| yt-dlp `stdout+stderr` → dosya, ebeveyn öldürüldü | **yt-dlp 12+ sn YAŞADI** |

Python **8 KiB blok tamponluyor**; tampon dolmadan `write` olmuyor, `EPIPE` oluşmuyor.
Gerçek akıştaki temizlik **guard'ın işidir**. Yan etkisi: "thread sürümü 4/4 yetim
bıraktı ama boru öldürdü" tespiti de yanlış atıftı; doğrusu "guard hiç çalışmadı **ve**
boru da çalışmıyor".

> **Kontrol deneyi olmadan bu düzeltmeler kanıtlanamazdı.** Ajanın ilk turunda harness
> `--internal-watch-orphan` modunu **işlemiyordu**, izleyici doğar doğmaz çıkıyordu ve
> hedef **kırılan bordan** ölüyordu — yani "0,10 sn" ölçümü korumayı kanıtlamıyordu.
> Ajan bunu **kendi** buldu, harness'i üretim koduna bağladı ve tüm ölçümleri yeniden
> yaptı. Aynı ajan ayrıca iki ölçüm hatasını kendi düzeltti: `ffmpeg -bsfs` kabiliyet
> sorgusunu "birleştirme" sayması ve kendiliğinden çıkmış `ffprobe`'u "koruma öldürdü"
> sayması. Ayrıntı: [`LINUX-DOGRULAMA-3.md`](linux/LINUX-DOGRULAMA-3.md) §5.

---

## 6. Turda bulunan düzeltmeler (E1–E8)

Windows doğrulama turu 9 bulgu getirdi. İki tanesi yüksekti.

### E1 — `DownloadProgress.isAudioTrack` hiç atanmıyor

`DownloadModels.cs:23` kurucu `isAudioTrack` parametresi alıyor, satır 45'teki
`IsAudioTrack` alanına **atamıyor**. Alan her zaman `false` kalıyordu, çok trackli HLS
ayrımı sessizce kayboluyordu.

```diff
  Track               = track;
+ IsAudioTrack        = isAudioTrack;
```

**Test:** `DownloadProgressModelTests` (3).

### E2 — `GetStaleResumePartPaths` kararsız sıralama · **YÜKSEK**

`Mp4Downloader.cs:649`:

```csharp
.OrderByDescending(entry => entry.ModifiedUtc)   // ← bağlayıcı (tiebreaker) yok
.Skip(MaxRetainedResumeParts)
```

`ModifiedUtc` bir **türev** alandır (gruptaki en yeni dosya). Zaman damgaları çakışınca
`Skip(2)` **hangi** 2 grubun korunacağını keyfî seçer.

**Bu sadece test hatası değildir.** Kodun kendi sözleşmesi (yorum satırı 552: *"aday
izolasyonu sözleşmesi korunur"*) bozuluyor: bir adayın `.part`'ı rastgele silinip diğeri
korunabilir.

**Ölçüm:** izole koşumda 12'de **3 FAIL (%25)**. 4 turluk Windows doğrulamasının
birinde kırmızıya düşmesinin tek sebebi bu testti.

**Çözüm:** `ResumeGroup.Key` alanı + kararlı ikincil ölçüt:

```csharp
.OrderByDescending(entry => entry.ModifiedUtc)
.ThenByDescending(entry => entry.Key, StringComparer.Ordinal)
.Skip(MaxRetainedResumeParts)
```

**Test:** Kırılgan test artık **40/40** geçiyor. Ayrıca
`GetStaleResumePartPaths_IsDeterministicWhenTimestampsTie` — beş grup bilinçli olarak
**aynı** zamanı alıyor, 25 tekrar boyunca sonuç birebir aynı olmalı.

> Testin kendisinde de hata vardı: yalnız `.part` dosyasının zamanını ayarlıyordu,
> `.seg0` yazım anında kalıyordu; dört grubun hepsi "şimdi" oluyordu. Artık **gruptaki
> her dosyanın** zamanı ayarlanıyor ve test **en yeni 2'nin korunduğunu** da doğruluyor.

### E3 — Kaynak tarama zaman aşımı · **YÜKSEK (ama kanıt zayıf — aşağıya bak)**

`CliConfig.DownloadAutoSelectTimeoutSeconds = 5`, ve bu değer `127.0.0.1`'deki **tek
bir API çağrısına** duvar-saati baskısı olarak uygulanıyordu.

**Birinci doğrulama turundaki ölçüm:**

| Dizi | Kaynak/bölüm | Ölçülen süre | Sonuç |
|---|---|---|---|
| naruto | — | < 5 sn | indirilebiliyor |
| **one piece** | 37 kaynak, 1166 bölüm | **46,98 sn** | **zaman aşımı, exit 1** |

> ### ⚠️ DÜRÜSTLÜK DÜZELTMESİ — bu hata sonradan ÜRETİLEMEDİ
>
> Nihai doğrulama turunda aynı komut ölçüldüğünde:
>
> | Adım | Süre |
> |---|---|
> | `GET /providers` | 0,07 sn |
> | `GET /anime/search` | 0,09 sn |
> | `GET /anime/TurkAnime/one-piece` (1166 bölüm) | 0,57 sn |
> | `GET /anime/TurkAnime/sources` (37 kaynak) | 1,05 sn |
> | **Toplam** | **≈1,78 sn** |
>
> Dahası: config **elle 5 saniyeye** zorlandığında indirme yine **`exit 0`** ile
> tamamlandı. 0,2 saniyeye zorlandığında da. Yani **5 saniyenin somut bir hata ürettiği
> kanıtlanmadı**; 46,98 saniye muhtemelen ağ yavaşlığına bağlı bir uç değerdi.
>
> **Bu, kaydın ilk halinde "9 katından düşük, popüler diziler indirilemiyordu" diye
> yazılmış iddianın düzeltilmesidir.** Ölçülen ilk değer doğru bir gözlemdi ama
> kalıcı bir kusun kanıtı değildi.

Hata mesajı ayrıca kullanılamazdı: config **anahtarını** söylüyordu ama **dosyanın nerede
olduğunu** söylemiyordu. Nihai doğrulama bunu da buldu: anahtar `config.json`'da **hiç
yazılı değil** (dosya yalnız yazılmış alanları serileştiriyor), yani "değerini büyütün"
talimatı uygulanamazdı.

**Çözüm:**

| Değişiklik | Önce | Sonra | Gerekçe |
|---|---|---|---|
| `CliConfig` varsayılanı | 5 sn | **60 sn** | Ölçülen uç değerin altında kalmayı önler — **genişletilmiş pay, düzeltilmiş hata değil** |
| TUI üst sınırı | 120 sn | **300 sn** | Kullanıcı gerekirse 60'ın üstüne çıkabilsin |
| Hata mesajı | anahtar adı | **tam yol + anahtarın eklenmesi + kolay yol** | Talimat uygulanabilir olsun |

> **Bu bir duvar-saati sınırıdır, stall dedektörü değil.** API yanıt veriyorsa süre
> işler. Bu yüzden MP4/HLS yolundaki `DownloadStallGuard`'dan (90/30/20 sn) ayrıdır ve
> daha uzun tutulmalıdır.

**Test:** `CliConfig_DownloadAutoSelectTimeout_DefaultHasHeadroom` — 60 sn'in altına
sessizce düşülmesini engelleyen regresyon koruması. Adı bilinçli olarak "varsayılan gerçek
işi kapsar" değil, "başlık payı bırakır" oldu.

### E9 — HLS ilerlemesinde **tahmini** toplam kesin gibi gösteriliyor

Nihai doğrulama turu bunu buldu. `one piece` 1. bölümde segment fazı **1,25 GiB** derken
gerçek dosya **486,16 MiB** oldu — **2,6× sapma** — ve yüzde tahminden türetildiği için
ilerleme **yanlış bir %100** gösteriyordu. Aynı satırdaki `ETA` de aynı yanlış toplamdan
türetiliyordu.

Bu, BULGU 1'in (sahte bayt göstergesi) kardeşidir: **gösterge gerçek değilse, ondan
türetilen hiçbir gösterge de gerçek değildir.** Roxy'nin ilk şikâyeti de tam olarak
"sahte 100 B" idi.

**Çözüm:** `ProgressTotalRegex` `of ~Y` içindeki `~` işaretini artık **gruba yakalıyor**
(daha önce `~?` regex'te emiliyor ve bilgi kayboluyordu). `DownloadProgress`'a
`IsEstimatedTotal` alanı eklendi:

| Durum | Gösterim |
|---|---|
| `of ~Y` (tahmin) | `486,16 MiB / ~1,25 GiB` · **yüzde yok** · **ETA yok** · parça sayacı var |
| `of Y` (kesin) | `486,16 MiB / 486,16 MiB` · yüzde var · ETA var |

Mux fazında yt-dlp gerçek boyutu bildirdiği için orada toplam kesinleşir.

**Test:** `HlsEstimatedTotalTests` (4 yeni).

> **Bu düzeltme iki mevcut testi kırdı ve ikisi de bilinçli güncellendi.**
> `HlsProgressMonotonicTests` ve `HlsDownloaderTests`, tahmin satırlarını
> `Percent is not null` filtresiyle seçiyordu; yüzde artık `null` olduğu için ölçüt
> satırların hepsini eliyordu. Testlerin **niyeti** korundu, ölçüt `IsEstimatedTotal`
> oldu. Kesin toplam yolunun (yüzde + ETA) kapsaması `HlsEstimatedTotalTests`'e taşındı;
> ilk denemede bu kapsamı sentetik bir satırla geri kazanmayı triedim ama o satır
> **tekdüze (monoton) kuralına** takıldı (100 MiB, önceki 1,19 GiB tahminine
> yükseltiliyor) — yapay senaryo olduğu için vazgeçildi.

### E4 — `.migurdex-partial` kalıcı çöp

`CopyThenDelete` istisnada geçici dosyayı siliyor ama SIGKILL/güç kesintisi
`File.Copy` ile `File.Move` arasında yakalanırsa dosya **kalıcı** kalıyor — ve sonraki
çalışmada da temizlenmiyordu (aynı dosya sisteminde taşıma başarılı olursa
`CopyThenDelete` hiç çağrılmıyor). `.migurdex.lock` için bu yol açıldı, burada da
**aynı kural** uygulandı.

### E5 — `EXDEV` dışı hatalarda gereksiz tam boy kopya

`IsCrossDevice` Unix'te **her zaman `true`** döndürüyordu. Bu yüzden disk dolu, izin yok
veya hedef kilitli gibi `EXDEV` dışı hatalarda da tam boy kopyalama deniyordu — hepsi
zaten başarısız olacak, kullanıcı boşuna bekliyordu ve bir `.migurdex-partial`
bırakıp siliniyordu.

**Çözüm:** Unix'te yalnız `EXDEV` (errno 18) kopyala-sili gerektirir. .NET errno'yu
`HResult`'ın düşük 16 bitine koyar; iç içe zincirde aranır. Windows'ta kök karşılaştırması
korunuyor (Windows `EXDEV` üretmez).

### E6 — Yetim korumada PID geri dönüşümü

`LinuxOrphanGuard` hedef PID'i bir kez yakalayıp **hiç yeniden doğrulamıyordu**. Hedef
süreç kendiliğinden ölürse numarayı başka bir süreç alabilir ve döngü ~500 ms'lik
pencerede **ilgisiz bir sürece SIGKILL** yollardı. `pid_max` 4M olduğu için olasılık çok
düşüktü ama **kod yorumu tersini iddia ediyordu** ("tasarımda mümkün değildir") — bu yanlış
güvence daha sonra başka birinin doğru davranacağı sanmasına yol açardı.

**Çözüm:** `/proc/<pid>/stat` 22. alanı (başlangıç zamanı) PID ile birlikte kimlik
oluşturuyor. `Watch` öldürmeden önce ikisini karşılaştırıyor; eşleşmiyorsa **hiçbir şey
öldürmüyor**. Okunamazsa 0 döner ve koruma eski davranışla devam eder (fail-safe).

> Tuzak: `comm` alanı parantez içindedir ve boşluk ya da `)` içerebilir — alan dizini
> **son** parantezden sonra başlar.

**Test:** `ReadStartTime` bilinmeyen PID'de 0 · Linux'ta kendi süreci > 0 · iki farklı
süreç farklı değer.

### E7 — Kilit TOCTOU'su (Linux)

`TryRemoveStaleLockFile` yoklamayı kapatıp **sonra** siliyordu. Windows'ta bu güvenli
(açık dosya silinemez). Linux'ta `unlink` **açık dosyada da başarılı** olduğu için iki
yabancı süreç arasında kalan mikrosaniyelik pencerede biri diğerinin kilidini
silebilirdi → karşılıklı dışlama sessizce bozulurdu.

**Çözüm:** Silme önce **yoklama akışı açıkken** denenir. `FileShare.None` Unix'te `flock`
aldığı için penceredeki rakip süreç zaten açamaz. Windows'ta bu deneme `IOException` ile
reddedilir; akış kapanır ve aynı silme bir kez daha denenir.

> **Bu ilk denemede Windows'u kırdı.** Silmeyi doğrudan `using` bloğuna taşıyan ilk
> sürüm iki mevcut testi düşürdü (`TryRemoveStaleLockFile_DeletesOrphanedZeroByteFile`,
> `Acquire_RemovesStaleZeroByteLockFile`) — çünkü Windows'ta açık dosya silinemiyor.
> İki platforma da çalışan desen yukarıdaki.

### E8 — `SemaphoreSlim` sızıntısı

`_processLocks` girdileri **hiç silinmiyordu**; uzun ömürlü TUI oturumunda her farklı
hedef için bir `SemaphoreSlim` kalıcı oluyordu.

**Çözüm:** `ProcessLock` sarmalayıcısı + `Acquisitions` sayacı. Yalnızca **kendi
eşzamanlılığını içermeyen** anahtar için silme yapılır; aynı hedef için eşzamanlı iki
indirme varsa kayıt güvenli tarafta kalır — sızıntı, işlev kaybı değildir.

Semafor bilerek **dispose edilmez**: silme ile yarışan bir alıcı onu tutuyor olabilir.
Asıl sızıntı sözlüktü.

**Test:** 25 ayrı hedef → **0 kayıt kaldı**. Toplam sözlük boyutu ölçülmüyor: xUnit tam
sütte testleri paralel çalıştırdığı için diğer testlerin alımları karışır
*(bu test ilk hâlinde tam sütte kırılmıştı — 4 turluk doğrulamada 1 kez)*. Bunun yerine
yalnız bu testin ürettiği anahtarlara bakılıyor.

---

## 7. Test ve doğrulama

### Test sayıları

| Tur | Test |
|---|---|
| Başlangıç (PR-1 merge sonrası) | 345 |
| + PR-2 (iş dizini) | 345 |
| + PR-3 (SSRF) | 384 |
| + Grup A, B, C, D | 480 |
| + E1 (`isAudioTrack`) | 483 |
| + E2–E8 regresyon testleri | 494 |
| + E9 (tahmini toplam) | 498 |
| + E10/E11 (zombie + torun) | **518** |

**17 yeni test dosyası**, 45 test dosyası toplam.

### Doğrulama turları

| Tur | Build | Test | Çıkış kodu |
|---|---|---|---|
| 1–8 | 0/0 | 494/494 | 0 |
| 9–16 | 0/0 | 498/498 | 0 |
| 17–22 | 0/0 | **518/518** | 0 |
| Linux 1–5 | 0/0 | 510/510 | 0 |
| Linux 6 | 0/0 | **518/518** | 0 |

E2 kırılgan testi ayrıca **40 ardışık kez** koşuldu: **0 başarısızlık** (öncesi %25).

### Linux doğrulaması — 3 yeni bulgu, 1'i kritik

WSL2 Ubuntu 24.04, .NET 10.0.401, rsync ile `/root/build`'e kopyalanmış kopya üzerinde
(şerif toplamıyla doğrulandı). **Git komutu çalıştırılmadı.**

Diskler arası taşıma (PR-2) **gerçekten çalışıyor**:

| Senaryo | Mount çifti | Sonuç |
|---|---|---|
| Farklı dosya sistemi | `/tmp` → `/mnt/c` | ✅ 20.344.071 B, kaynak silindi |
| Aynı dosya sistemi (atomik) | `/tmp` → `/root` | ✅ 0,00 sn |
| **Uçtan uca farklı** | iş `/tmp` → hedef `/mnt/c` | ✅ **307.134.562 B**, h264+aac 1920×1080, 300,025 sn, 160,9 sn, kalıntı 0 |
| **Uçtan uca aynı** | iş `/tmp` → hedef `/root` | ✅ 307.134.562 B, 300,025 sn |

`EXDEV` gerçekten oluşuyor (çıplak `rename(2)`: `/tmp` → `/mnt/c` → `errno=18`).

#### BULGU 1 (yüksek) — kardeş testi atlamışım

`GetStaleResumePartPaths_TrimsOldestGroupsBeyondRetentionLimit` Linux'ta **%95–100**
kırılıyordu. Aynı kök nedenden (`SetLastWriteTimeUtc` yalnız `.part`'a uygulanıyor) kardeş
testi `HandlesGlobMetacharactersInFileName`'i düzeltmiştim ama **bunu atlamıştım**.

Linux dosya sisteminin mtime çözünürlüğü yazma döngüsünden kaba:

```
dosya 0  mtime_ns=1790740012333165831
dosya 1  mtime_ns=1790740012333165831   fark=0
...
dosya 7  mtime_ns=1790740012333165831   fark=0
```

Beş grubun `ModifiedUtc`'si birebir eşit → seçim tamamen bağlayıcıya (fingerprint hash
sırası) düşüyor → testin beklediği indeks sırasıyla örtüşmüyor. **Üretim kodu doğruydu**
(`ThenByDescending(Key, Ordinal)` gerçekten kararlı, `IsDeterministicWhenTimestampsTie`
20/20 geçiyor). Düzeltme: döngüdeki **her** dosyayı damgalamak.

> **Ders:** aynı kök nedenden etkilenen testleri tek tek değil, **kök nedene göre** ara.
> Kardeş testi düzeltip diğerini unutmak, düzeltmemiş olmaktan kötüdür — bir tanesi
> yeşil göründüğü için "halloldu" sandım.

#### BULGU 2 (orta) — yük altında 2 sn yetersiz

`Download_WhenSubtitleIsCancelledAfterVideoFinal` 494 test paralel koşarken
`WaitAsync(2 sn)` zaman aşımına uğruyordu (4 turun 3'ünde, 8 koşunun 1'inde). İzole
koşumda 465 ms (4,4× marj) — **yük duyarlılığı, ürün hatası değil**. 15 sn'e çıkarıldı.

#### BULGU 3 (kritik) — `LinuxOrphanGuard` ölü koruma

Yukarıda ayrıntılı. Özet: ebeveyn içindeki thread `SIGKILL`'da öldüğü için koruma
**hiç çalışmıyordu**; gözlenen temizlik kırılan stdout borusundan geliyordu. Yeniden
yazıldı: izleyici artık **ayrı bir Migurdex süreci**.

#### Gözlem (hata değil) — `EXDEV` yolu .NET'te çoğu zaman ölü kod

Linux ölçümü: çıplak .NET `File.Move` **farklı dosya sisteminde de başarılı** — .NET
Unix'te `EXDEV`'i kendi içinde copy+delete ile yutuyor. Yani `ShouldFallBackToCopy` /
`IsCrossDeviceLink` çoğu durumda **çağrılmıyor**; taşımayı yapan .NET'in kendi yedeği.

Bu PR açısından sonuç değişmiyor (her iki yolda da dosya doğru yere taşındı), ama iki
gerçek sonuç var:

1. `CopyThenDelete`'nin sağladığı `<hedef>.migurdex-partial` **atomiklik** koruması bu
   yolda devreye girmiyor; kopyalama doğrudan nihai yola yazılıyor. Kısmi dosya temizliği
   (E4) yine de geçerli, çünkü dosya adı `.migurdex-partial`'dan farklı.
2. Yedek yol, .NET'in `EXDEV`'i yuttuğu ortamlarda **gelecekte** gerekebilir (farklı
   .NET sürümü, özel dosya sistemi, kopyalama seçenekleri). Bu yüzden **kaldırılmadı** —
   yalnızca "çoğu zaman tetiklenmez" notu eklendi.



### Nihai doğrulama — 4 gerçek indirme

| # | Hedef | Exit | Süre | Boyut | Zaman aşımı | Kalıntı |
|---|---|---|---|---|---|---|
| 1 | one piece e1 / HLS | 0 | 30,05 sn | 509.771.320 B | yok | 0 |
| 2 | naruto e1 / MP4 | 0 | 184,88 sn | 272.370.192 B | yok | 0 |
| 3 | naruto e1 / HLS | 0 | 17,15 sn | 273.127.301 B | yok | 0 |
| 4 | one piece e2 / HLS | 0 | 53,64 sn | 510.829.321 B | yok | 0 |

Dördü de tam bölüm süresinde (ffprobe 1375–1500 sn), `ftypisom`/`ftypmp42` geçerli MP4,
sahte bayt göstergesi 0 eşleşme. 4 tur build+test **birebir 494/494, exit 0**.

**SSRF uçtan uca kanıtlandı:** Gerçek API'yi yansıtan, 37 kaynak URL'sini
`http://127.0.0.1:9/evil-N.mp4` yapan bir proxy kuruldu; CLI 3/3 adayı
`Bu host'a istek gönderilemez.` ile reddetti ve 127.0.0.1'e **hiç bağlantı denenmedi**.

### Diğer ölçümler

| Ölçüm | Beklenen | Ölçülen |
|---|---|---|
| `/health` → `providers` | 14 | **14** ✓ (önce 12) |
| `Plugins\Migurdex.Plugins.*.dll` | 14 | **14** ✓ |
| `migurdex --bilinmeyen` | exit 2 | **2** ✓, stack trace yok |
| `migurdex bogus` | exit 2 | **2** ✓ |
| `--help`/`-h`/`help`/`--version`/`download --help` | exit 0 | 20/20 ✓ |
| SSRF — API sınırı | red | 7/7 red ✓ |
| SSRF — CLI yolu | red | 7 red, 2 izinli ✓ |

---

## 8. Düzeltilmeyenler ve gerekçeleri

| Bulgu | Gerekçe |
|---|---|
| BULGU 24 — API'de 20 sn extractor turu | upstream davranışı; hata değil, tasarım kararı gerektiriyor |
| BULGU 27 — `--version` yerel build'de `0.0.0` | CI release'de damgalı; lokal build sorunu, düşük değer |
| C3 — MP4'te `fsync` yok | Atomiklik zaten sağlanıyor; ek riski değerine göre yüksek |
| BULGU 4/12 — `ExtractorSmokeTests` non-deterministik | Ağ bağımlı canlı URL testleri; kod hatası değil |
| Stall koruması HLS/`yt-dlp` yolunu kapsamıyor | Kapsam genişletmesi ayrı iş; `yt-dlp` kendi yeniden denemelerini yapıyor |
| `Task.Delay` gözcüleri iptal edilmiyor | Hata değil; ~20 sn yaşar, ihmal edilebilir |

---

## 9. Commit/PR geçmişi ve notlar

- Tüm değişiklikler **tek PR**'da, iki mantıksal bloğa ayrılmış hâlde sunulabilir:
  *Blok 1* PR-2 + PR-3 (güvenlik + veri bütünlüğü), *Blok 2* Grup A–D + E1–E8.
- Upstream'e hiçbir şey gönderilmemiştir; tüm iş `Nutaliaxd/Migurdex` fork'unda.
- `origin` = `roxyrekt/Migurdex` (upstream), `nutalia` = `Nutaliaxd/Migurdex` (fork).
- Depo LF satır sonu kullanıyor; `core.autocrlf=false`.
- Git işlemleri Windows'tan yapıldı (WSL'de `root` kimliği üretiyor).
