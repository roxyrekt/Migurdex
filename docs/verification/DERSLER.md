# Dersler

46 ders. **Süreç hataları da teknik hatalar kadar kayıt altına alındı** — çünkü bu
listede en pahalı kayıtlar onlar.

Kaynak: görev sırasında tutulan işlem günlüğü. Her satır bir olayı, bir ölçümü ve
o olaydan çıkarılan kuralı içerir.

---

| No | Hata / karar | Nerede | Ders |
|---|---|---|---|
| 1 | `Environment.Exit`'in `finally`'i atladığını **çıkarımladım**, ölçmedim | Linux ajanı | Mekanizma iddiası ölçülmeden "doğrulandı" denmez |
| 2 | Linux SIGINT testi kontrol grubu olmadan yapıldı | Linux ajanı, kendi ilk ölçümünü | Bash, job control kapalıyken `cmd &`'e SIGINT'i `SIG_IGN` ile miras bırakır (`/proc/PID/status` → `SigIgn: …06`). Sinyal testi için `set -m` veya gerçek PTY + literal `\x03` zorunlu |
| 3 | `Commands/` → `Services/` taşındığı için negatif grep'i "düzeltilmiş" sandım | Kendim | Negatif grep "yok" demektir ama önce **yolun değişmediği** doğrulanmalı |
| 4 | Koşumsuz orphan temizliği mevcut sözleşmeyi kırdı | 3 test düştü | Mevcut testler **tasarım sözleşmesi** taşır; "temizlik" gerekçesi onları geçersiz kılmaz |
| 5 | Temizlik yalnız `*.part` eşleştirdi, `.part.segN` yok | Ajan raporu | Resume birimini **çalışma zamanında gözle**; dosya adı tahmin etme |
| 6 | Kara liste üçüncü sağlayıcıda yetersiz kaldı | Kendi canlı ölçümüm | Sağlayıcı başına kara liste ölçeklenmez; beyaz liste kullan |
| 7 | NUL baytı kaynak dosyaya girdi | `read` aracı "binary" dedi | Derleyici + testler sessizce geçebilir; kaynak dosyada bayt taraması yap |
| 8 | CRLF yazıldı, depo LF | `git diff` 899 satır | Büyük diff görünce önce satır sonlarını kontrol et |
| 9 | **BULGU 3'ü "v1.11.0'da düzeldi" diye kapattım — yayımlanmış paket ölçülmüş, kaynak koda bakılmamış** | PR-3'ü doğrularken `providers` 12 gördüm | "Paket 14" ile "kaynakta eksik" **farklı derleme yolları**. Bulguyu kapatmadan önce **kaynakta** doğrula |
| 10 | `ValidateHttpUri` senkron olduğu için DNS yapamıyor; async'e çevirirken 5 çağrı noktasını atlama riski vardı | kendi kontrolüm | Senkron/async ayrımı yaparken **tüm çağrı noktalarını** `grep`'le |
| 11 | SSRF korumasını kopyalamak yerine `Migurdex.Shared`'e taşıdım ama `using Migurdex.Shared;` eklemeyi unuttum | `CS0103` ×8 | Yeni dosya eklerken namespace'i hemen `using` ile bağla |
| 12 | Testte `Assert.Null(uri)` gibi saçma bir satır yazdım | 38/39 | Test yazarken **neyi kanıtlıyor** diye sor; cevap yoksa satırı sil |
| 13 | **TOCTOU düzeltmesi (`using` içinde silme) Windows'u kırdı** | 2 mevcut test düştü | Platformlar arası güvenlik düzeltmesi yaparken **her iki platformu** da test et; tek platformda "güvenli" görünen desen diğerinde sessizce hiçbir şey yapmayabilir |
| 14 | Sızıntı testi toplam sözlük boyutunu ölçtü; xUnit paralel çalıştığı için **kendi testim** kırıldı | 4 turluk doğrulamada 1 kırmızı | Global sayaç ölçümü testler arası gürültü toplar; **kendi ürettiğiniz anahtarları** ölçün |
| 15 | Kırılgan testi "yeşilleştirmek" yerine geçmişi değiştirdim; asıl sorun **üretimde keyfî aday kaybıydı** | tur analizi | Test kırılganlığı genellikle **üretim kusurunun belirtisidir**; testi ayarlamadan önce kökü sor |
| 16 | E9 düzeltmesi (sahte %100) iki mevcut testi kırdı; `Percent is not null` ölçütü satırları eler oldu | tam süit 6/6 kırmızı | Bilinçli davranış değişikliği mevcut testi kırabilir; testi **ayarlamak** yerine **niyetini** koruyacak şekilde yeniden yaz |
| 17 | Kesin toplam yolunun kapsamını sentetik bir satırla geri kazanmayı denedim; o satır **tekdüze (monoton) kuralına** takıldı (100 MiB, önceki 1,19 GiB'e yükseltildi) | test kırmızı | Yapay senaryoyla üretim kuralını ihlal eden test yazma; kapsamayı gerçek senaryoda ölç |
| 18 | **Kardeş testi unuttum.** `GetStaleResumePartPaths`'in iki testi aynı hatalı varsayımı taşıyordu; birini düzeltip diğerini atladım | Linux turu: **%95–100 kırılma** | Aynı kök nedenden etkilenen testleri **tek tek değil, kök nedene göre** ara. Düzeltilmiş görünen kardeş "halloldu" yanılgısı yaratır |
| 19 | **`LinuxOrphanGuard` ölü korumaydı.** İzleme ebeveyn sürecin içindeki thread ile yapılıyordu; `SIGKILL`'da thread de ölüyor | Linux turu: **4/4 sessiz alt süreç yetim kaldı**, guard thread'i `/proc/.../migurdex-linux-` olarak **var** | Ebeveyn ölümünü gözlemleyen mekanizma **ebeveynin dışında** yaşamalı. Yeşil test + doğru yorum, çalıştığının kanıtı değildir — **ölç** |
| 20 | Linux ajanı 4 adet ~5 saatlik takılı `git push` buldu (eski oturumlardan) | Linux turu | Push aslında **başarılıydı** (`nutalia/main` = beklenen SHA), süreçler kimlik isteminde takılıydı. WSL'de `git push` kullanmayın; `git ls-remote` ile doğrulayın |
| 21 | `Download_WhenSubtitleIsCancelledAfterVideoFinal` 2 sn bütçesi yalnız **izole** koşumda yetiyordu (465 ms); tam süt içinde 494 test paralel koşarken yetmiyor | Linux turu: 4 turun 3'ünde `TimeoutException` | Zaman aşımı bütçelerini **tam süt içinde** ölç, izole koşumda değil |
| 22 | **`LinuxOrphanGuard` ilk hali ölüydü**: ebeveyn sürecin içindeki thread `SIGKILL`'da ölüyor, guard hiç çalışmıyor | Linux turu: **4/4 sessiz alt süreç yetim kaldı** | Ebeveyn ölümünü gözlemleyen mekanizma **ebeveynin dışında** yaşamalı |
| 23 | **Zombie tuzağı**: `ReadStartTime` (`/proc` 22. alan) ve `kill(pid,0)` **ikisi de zombie'da "yaşıyor"** der | Linux turu: hedef **24 sn** yaşadı, ebeveyn `wait()` sonrası 1 sn | Ölüm kontrolü `state` alanına bakmalı (`Z`/`X` = ölü). "PID var" yaşamak kanıtı değildir |
| 24 | **Torun kapsamı**: `ffmpeg` daima `yt-dlp`'nin çocuğu, asla doğrudan çocuk değil | Linux turu: `sleep 900` torunu **18 sn** yaşadı | Yetim koruma doğrudan çocuğu değil **ağacın tamamını** kapsamalı |
| 25 | "Gözlenen temizlik kırılan stdout borusundan geliyor" — **yanlış atıf** | Kontrollü deney (FIFO vs dosya) | Python 8 KiB tamponladığı için `EPIPE` oluşmuyor. Boruya bağlı `yt-dlp` de 12+ sn yaşıyor |
| 26 | Ajanın ilk "0,10 sn" ölçümü **korumayı kanıtlamıyordu** — harness izleyici modunu işlemiyordu, hedef bordan ölüyordu | Ajanın **kendi** bulduğu hata | Doğrulama aracı **üretim kodunu** çağırmıyorsa ölçtüğün şey üretim yolu değildir. Ajan tüm ölçümleri yeniden yaptı |
| 27 | Ajan iki ölçüm hatasını **kendi** bildirdi: `ffmpeg -bsfs` kabiliyet sorgusu ≠ birleştirme; kendiliğinden çıkmış `ffprobe` ≠ "koruma öldürdü" | Ajan raporu §8 | `ps`/`kill -0` zombie'ı canlı sayar; sayım `/proc/<pid>/stat` **state** ile yapılmalı |
| 28 | `IsProcessAlive` `kill()` P/Invoke'u Windows'ta `libc` yok → `DllNotFoundException` | Windows test turu | `libc` çağrısı içeren `internal` metotlarda `IsSupported` kontrolü + try/catch |
| 29 | **PR gövdesi bozuk kodlamayla gitti** — `ç`→`Ã§`, `ü`→`Ã¼`. `Get-Content` PowerShell 5.1'de **ANSI (cp1252) okur**, ben de onu UTF-8 olarak yazdım | PR #5 ilk hali | Türkçe metin taşıyan dosyaları **`[System.IO.File]::ReadAllText(f, UTF8)`** ile oku; `Get-Content`/`Set-Content` zinciri kullanma. Göndermeden sonra **bayt bayt** doğrula |
| 30 | PR gövdesinin "Başlık" bölümünde **18** yazıyordu, PR adında 23 | Aynı turda fark edildi | PR metnini `.md`'den üretiyorsan, sayıları gövdede de güncelle — metin iki yerde ayrışıyor |
| 31 | **"CI yeşil" dedim, CI aslında hiç test çalıştırmıyordu.** `build-release.yml` içinde `dotnet test` **yok**; sadece build + publish | PR #5 CI'ını incelerken | Yeşil tik "derleniyor" demek, "testler geçiyor" demek değildir. **Bir CI'ın yeşil olduğunu söylerken neyi doğruladığını ayır** |
| 32 | CI'ın çalıştıracağı komutu yerelde koşturdum ve **3 test düştü**; tam süit aralıklı kırılıyor | 12 koşuda 5, 15 koşuda 3 hata | Ekleyeceğin CI adımını **push'tan önce** yerinde koştur. Push → kırmızı CI → "testler kırık" sanmak yerine önce ölç |
| 33 | Aralıklı kırılma için kapı koymayı reddettim ve bunu **upstream kusuru** ilan ettim | BULGU 29 | Yanlış teşhisi kayda geçirmek, doğru teşhisten daha pahalıdır. Kararı geri alıp ölçmeye devam et |
| 34 | `TestTempDirectory` regresyon mu sandım; **ayırıcı deneyde eski hâle de geri aldım, yine 3/12 kırıldı** | BULGU 29 | "Bu yüzden mi?" sorusunu yanıtlamak için **tüm** ilgili değişiklikleri geri al. Kısmi geri alma **kısmi kanıttır**; ben yalnız birini geri alıp "ikisi de geri alındı" diye okudum ve suçluyu yanlış adlandırdım |
| 35 | **Kendi eklediğim bir test** (`CleanupAll_Removes_Every_Tracked_Directory`) global temizliği test ortasında çağırıyor, paralel testlerin SQLite bağlantılarını bozuyordu — %20 kırılmanın **tek** nedeniydi | Düzeltme sonrası **40/40** temiz | Global/ortak duruma dokunan bir test, kendi dışındaki testleri bozabilir. "Birim testi" diye yazılmış olması onu yalnlaştırmaz; **neyi bozduğunu** düşünmek gerekir |
| 36 | Subagent "commitimi kendi commitine kattı, düzeltmem hâlâ commit edilmemiş" diye uyardı — **doğruydu**; `git add -A` ile commit'ten sonra diskte kalmış değişiklik vardı | Grup H raporu | Ajan raporundaki "benden bağımsız doğrula" uyarılarını ciddiye al. `git status` temizse **ve** ajan "değişiklik kaldı" diyorsa, o dosya yine de kontrol edilmeli |
| 37 | CI test adımını **kapı** yaptım (`release.needs`), sonra kırılganlığı çözünce `continue-on-error` geri alındı | BULGU 29 | Kök nedeni bulmadan verilen kararı, kanıt gelince **geri al**. Doğru hedefe (testler yeşil olmadan yayımlama) iki tur geç ulaştım |
| 38 | **Commit mesajı bozuk kodlamayla gitti**: `kırılganlığını` → `k?r?lganl???n?` (11 `?`). `$msg \| & git commit -F -` PowerShell pipe'ı non-ASCII'yi cp1252'ye düşürüyor | `ba3a641` | Commit mesajını **UTF-8 dosyadan** ver (`-F dosya`); PowerShell pipe'ı kullanma. Başlıkta ASCII'de kal — sorunu tamamen bitirir |
| 39 | Düzeltmek için `System.IO.File::WriteAllLines` kullandım; o **platform varsayılanı olarak CRLF** yazdı (246 satır) ve `git diff --check` kırıldı | Beyaz alan denetimi | .NET dosya yazma API'leri satır sonu için `Environment.NewLine` kullanır (Windows'ta CRLF). Depo LF ise `WriteAllText` kullan ya da `Join("`n")` ile birleştir |
| 40 | **Ayırıcı deneyim geçersizdi.** "Grup H öncesi de 3/12 kırılıyor" ölçümünde `rm` sonrası `git checkout` silinen dosyaları **geri getirmişti**; yani `TestTempDirectory` ağaçtaydı. Doğrusu: `git worktree add <commit>` ile ayırıcı kurmak | BULGU 30 | Geri alma deneyinde **hangi committe olduğunu doğrula**. Kısmi geri alma, kısmi kanıttır — ikinci kez aynı tuzağa düştüm |
| 41 | **"Sahibi ölmüş" demek "terk edilmiş" demek değil.** xUnit v3 test derlemesini **tek koşuda birden çok işlemde** çalıştırıyor (PID 645→673→703→707→719→745); her işlem kökü süpürüp ölen işlemin **hâlâ kullanılan** dizinini siliyordu | BULGU 30 · Linux 8–17 hata/koşu | Çapraz süreç temizlikte **zaman toleransı** koy. "Sahibi yok" ile "dokunulmuyor" aynı şey değil |
| 42 | **Unix'te `unlink` açık dosyada da başarılıdır.** Aynı hata Windows'ta `Directory.Delete` başarısız olup yutulduğu için **görünmüyordu** | BULGU 30 | "Bu sadece Windows'ta/şu platformda olur" sezgisi hatayı gizler. Unix'te sessizce **başarılı** olan işlemleri varsay |
| 43 | **`Path.GetFileName(entry)` ile tam yol (`RunRoot`) karşılaştırmak hiç eşleşmiyordu** — "kendi koşu dizinimi atla" güvencesi fiilen yoktu | BULGU 30 | "Kendi durumumu koru" koruması yazarken iki tarafın **aynı ölçekte** olduğunu doğrula; derleyici uyarmaz |
| 44 | **Dizin silme yardımcısı `GC.Collect()` + `WaitForPendingFinalizers()` çağırıyordu**; canlı dizin silinemeyince üç kez küresel sonlandırma zorluyordu | BULGU 30 | Bir yardımcının **süreç çapında** yan etkisi olmamalı. Paralel testlerde özellikle tehlikeli; etkisi sessiz ve teşhisi zor |
| 45 | Linux izleyici testi, **kardeş testlerin ürettiği global sayacı** ölçüyordu (`before=1`, sonra `0`) — 3/3 kararsız, üretim kodunda hata yoktu | `ChildProcessTrackerLinuxTests` | Ölçümü **teste özel bir kimliğe** bağla (hedef PID imzası gibi). "Şu an kaç tane var" türü sayaçlar komşu testlerden kirletilir |
| 46 | `DirectoryNotFoundException`, `IOException` türevidir; `catch (IOException)` altında **"başka bir indirme kullanıyor"** diye raporlanıyordu | `DownloadTargetLock` | Türetilmiş istisnaları genel kollarda yutmadan önce sırala. Kullanıcıya **yanlış sebep** söylemek, hata vermekten kötüdür |

---

## En pahalı beş ders

| # | Ders |
|---|---|
| **24** | **"CI yeşil" dedim, CI aslında hiç test çalıştırmıyordu.** Yeşil tik "derleniyor" demek, "testler geçiyor" demek değildir. Bir CI'ın yeşil olduğunu söylerken **neyi doğruladığını** ayır |
| **34 / 40** | **İki kez ayırıcı deneyi yanlış kurdum.** "Bu değişiklik yüzden mi?" sorusuna cevap vermek için **tüm** ilgili değişiklikleri geri al. Kısmi geri alma kısmi kanıttır — ve kısmi kanıtı tam gibi okumak en pahalı hatadır |
| **35** | **Kendi eklediğim bir test**, global/ortak duruma dokunarak kendi dışındaki testleri bozdu. "Birim testi" diye yazılmış olması onu yalnlaştırmaz; **neyi bozduğunu** düşünmek gerekir |
| **41** | **"Sahibi ölmüş" demek "terk edilmiş" demek değil.** Çapraz süreç temizlikte zaman toleransı koy |
| **42** | **Unix'te `unlink` açık dosyada da başarılıdır.** "Bu sadece şu platformda olur" sezgisi hatayı gizler |

## Metodoloji kuralları

| Kural | Nerede |
|---|---|
| Kontrol deneyi zorunlu — düzeltme olmasaydı ne olurdu? | [`README.md`](README.md) §1 |
| Ayırıcı deney commit'e dayalı kurulur (`git worktree`), dosya silmeyle değil | #34, #40 |
| Yanlış teşhis kayda geçer, sonra düzeltilir — **silinmez** | [`CI-TEST-ADIMI.md`](CI-TEST-ADIMI.md) §2 |
| CI'a eklenecek adım **push'tan önce** yerinde koşturulur | #32 |
| Commit mesajı UTF-8 **dosyadan** verilir; PowerShell pipe'ı non-ASCII bozar | #38 |
| .NET dosya yazma API'leri `Environment.NewLine` kullanır; LF depo için `WriteAllText` gerekir | #39 |
