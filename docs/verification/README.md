# Doğrulama Kayıtları

Bu klasör, PR'nin iddialarının **denetlenebilir** olması için tutulan ölçüm kayıtlarıdır.
Her iddia için "ne ölçüldü, nasıl ölçüldü, ve düzeltme olmasaydı ne olurdu" bilgisi burada
bulunur.

> Kayıtlar **Türkçe** yazıldı; deponun kendi belgeleri (`DOWNLOAD.md`,
> `DEVELOPMENT_LOG.md`) de Türkçe.

---

## Dosyalar

| Dosya | İçerik |
|---|---|
| [`DUZELTMELER.md`](DUZELTMELER.md) | **Ana teknik kayıt.** C1 (SSRF), Grup A/B/C/D ve E1–E12 dahil her düzeltmenin problemi, kök nedeni, çözümü ve testi |
| [`CI-TEST-ADIMI.md`](CI-TEST-ADIMI.md) | Bu PR'ın CI'a eklediği test adımı ve **yayımlama kapısı**, **BULGU 29** (kendi yarattığım kırılganlık) ve **BULGU 30** (Linux toplu SQLite çökmesi) |
| [`BILINEN-SORUNLAR.md`](BILINEN-SORUNLAR.md) | E1–E12 ve BULGU 29/30/31/32/33 özet tablosu — ne düzeltildi, ne açık kaldı |
| [`DERSLER.md`](DERSLER.md) | 46 ders. Süreç hataları da teknik hatalar kadar kayıt altına alındı |
| [`DONGU-3-KALAN-HATALAR.md`](DONGU-3-KALAN-HATALAR.md) | 3. döngü: kalan hata taraması, Linux yetim korumanın **reap edilen ebeveynde** çalışmaması (kritik), ajan bulgularının doğrulanması |
| [`DONGU-4-TANI-KAYBI.md`](DONGU-4-TANI-KAYBI.md) | 4. döngü: altyazı hatalarının 29 sebebe indirgenmesi, plugin'lerin sessizce kaybolması, SQLite ölçüm adımı |
| [`linux/`](linux/) | WSL2 doğrulama turları: yetim/zombie öldürme, torun öldürme, toplu SQLite çökmesi, merge sonrası Windows CI kırılması (BULGU 33) |
## Ölçüm yöntemi

Bu kayıtlarda uyulan üç kural, okuyucuya sayıların neden güvenilir olduğunu gösterir.

### 1. Kontrol deneyi zorunlu

Bir düzeltmenin işe yaradığını söylemek yetmez — **düzeltme olmasaydı ne olduğunu**
göstermek gerekir. Her "ölçüldü" satırının bir karşılığı vardır:

| Düzeltme | Ölçüm | Kontrol (düzeltme olmasaydı) |
|---|---|---|
| Zombie tuzağı (`/proc` state) | hedef **0,10 sn**'de öldü | **24 sn** yaşadı |
| Torun öldürme (`KillProcessTree`) | torun **0,10 sn**'de öldü | **20+ sn** yaşadı |
| SSRF koruması | `127.0.0.1`'e **hiç** bağlantı denenmedi | koruma olmadan bağlanıyordu |
| Test paketi temizliği (BULGU 30) | süpürücü kapalı ×3 → **0** hata | süpürücü açık ×3 → **47** hata |
| BULGU 29 regresyon testi | düzeltilmiş hâlde yeşil | düzeltme geri alınınca **kırmızı**, 3 koşuda 40 hata |

### 2. Ayırıcı deneyde **tüm** değişiklikleri geri al

İki kez aynı tuzağa düşüldü ve ikisi de kayda geçti:

1. BULGU 29: "`TestTempDirectory` regresyon mu?" diye **yalnız `NewTempDir`**
   geri alındı; `TestTempDirectoryTests` dosyası hâlâ ağaçtaydı ve hâlâ suçlu
   `CleanupAll()` çağrısını yapıyordu. İki katkıdan birini geri alıp "ikisi de geri
   alındı" diye okudum.
2. BULGU 30: `rm` sonrası `git checkout` silinen dosyayı **geri getirmişti**.

Doğrusu commit'e dayalı ayırıcı: `git worktree add <commit>`.

### 3. Yanlış tespisi kayda geç, silme

BULGU 29'u "upstream'ten gelen kırılganlık" diye kaydetmiştim ve CI test adımını bu
yüzden `continue-on-error` yapmıştım. **İkisi de yanlıştı.** Doğru kayıt artık iki
şeyi birden tutuyor: ne ölçtüğümü **ve neden yanlış sonuç okuduğumu.** Yanlış bir
teşhisi düzeltmeden kayda geçirmek, o teşhisi taşımaktır.

---

## Nasıl yeniden üretilir

```bash
# CI'ın birebir komutu (bu filtre olmadan koşu non-deterministik)
dotnet test Migurdex.Tests/Migurdex.Tests.csproj -c Release \
  --filter "FullyQualifiedName!~ExtractorSmokeTests"
```

`ExtractorSmokeTests` neden filtreli: canlı URL'lere giden ağ bağımlı testler ve
**non-deterministik**. Ölçülen toplam test sayısı koşular arası bile değişiyor —
Windows 26–28/35, Linux 9–14/20 arasında salınıyor. Filtresiz çalıştırılsaydı CI
düzenli olarak kırılırdı; bu bir regresyon değil, ağ bağımlılığı.

### Platformlar

| | |
|---|---|
| Windows | .NET SDK 10.0.401 |
| Linux | WSL2 Ubuntu-24.04, .NET SDK 10.0.401, 16 çekirdek |
| CI | GitHub Actions, `ubuntu-22.04` + `windows-latest` + `linux-arm64` |

### Linux ölçüm tuzakları (yanlış ölçüme yol açan üç şey)

1. **`ps` ve `kill -0` zombie'ı canlı sayar.** `/proc/<pid>/stat` **3. alanı**
   (`Z`/`X` = ölü) okunmalı.
2. **WSL'de `ppid == 1` geçersizdir** — subreaper relay'i araya giriyor.
3. **Unix'te `unlink`/`rmdir` açık dosya varken de başarılıdır.** Windows'ta açık
   dosya `Directory.Delete`'i engeller ve hata yutulur — yani **aynı hata Windows'ta
   görünmez.** BULGU 30 tam olarak bu yüzden "sadece Linux" gibi görünüyordu.

---

## Test sayısı yolculuğu

```text
34561a3 (commit 1)   553 test
9a3a1c8 (commit 4)   600 test   (+ Grup F/G/H testleri, C16, BULGU 30 regresyon testi)
```

### Bilinen sınırlama — raporlanan test sayısı 599/600 arası salınıyor

Keşif (**`--list-tests`**) sabit **600**. Ama tam koşunun raporladığı toplam **599 veya
600** arasında değişiyor — 8 ardışık koşuda ölçüldü:

| Toplam | Kaç koşuda |
|---|---|
| 599 | 5 |
| 600 | 3 |

`Başarısız: 0` ve `Atlanan: 0` her koşuda. Yani salınım bir hata değil; bir testin bazı
koşularda **hiç çalışmaması** demek.

**Şüphelenilen neden — ölçülmedi:** xUnit v3'ün **çok işlemli** çalışma modu. BULGU 30'da
aynı mekanizma kanıtlandı: tek `dotnet test` çağrısı birden çok işlem açıyor
(PID 645 → 673 → 703 → …) ve sonuç toplama bu işlemler arasında yapılıyor. BULGU 30'da
çözülen kök neden (süpürücünün canlı koşu dizinlerini silmesi) aynı çok işlemli modelin
bir sonucuydu.

**Dürüstçe: hangi testin eksik kaldığını ayırt edemedim.** TRX rapor üretimi bu
kurulumda çalışmadı ve konsol çıktısından test adı listesi çıkarmak da mümkün olmadı.
Ayrı ve küçük bir iş kalemi olarak duruyor.

Bu bir **regresyon göstergesi değil** — hiçbir koşuda test düşmüyor. Ama "600 test koştu"
ifadesi bu ölçümle desteklenmiyor; desteklenen ifade **"keşif 600, koşu 599–600"**.
---

## Son durum

| | |
|---|---|
| Windows tam süit | **10/10** koşu temiz |
| Linux tam süit | **8/8** koşu temiz |
| Build (iki platform) | 0 hata / 0 uyarı |
| CI | 5/5 iş yeşil (`Test ubuntu` ✓, `Test windows` ✓, 3 build ✓) |
| Trim/AOT uyarıları | 10 adet, **tamamı** `Migurdex.Core/*` ve `IProvider.cs` içinde — bu PR'da değişen dosya değil; `main`'de de aynıları var |
