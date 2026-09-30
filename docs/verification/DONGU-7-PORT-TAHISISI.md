# Döngü 7 — OAuth loopback test portları: sabit kodlanmıştı (PR #10'ın CI'ını kırmızı yapan kusur)

**Tarih:** 1 Ekim 2026 · **Dal:** `fix/plugin-deadlock-and-serialization` (PR #10)

---

## 1. Olay

PR #10 gönderildi, CI'da tek bir job kırmızı döndü:

```text
run 36738132640  Build (linux-x64)     -> success
                 Build (linux-arm64)   -> success
                 Build (win-x64)       -> success
                 Test (windows-latest) -> success      (605/605)
                 Test (ubuntu-22.04)   -> failure      (604/605)
```

```text
Migurdex.Tests.AniListOAuthTests.Loopback_Receives_Code_From_Callback [FAIL]
System.Net.HttpListenerException : Address already in use
   at System.Net.HttpEndPointManager.GetEPListener(String host, Int32 port, HttpListener listener, Boolean secure)
   at System.Net.HttpEndPointManager.RemovePrefixInternal(String prefix, HttpListener listener)
   at System.Net.HttpEndPointManager.RemoveListener(HttpListener listener)
   at System.Net.HttpListener.Close(Boolean force)
   at System.Net.HttpListener.Dispose()
```

---

## 2. Mekanizma: `Start()` değil, `Dispose()`

Bu ayrım bütün teşhisin anahtarı. Patlama `listener.Start()` **değil**,
`HttpListener.Dispose()` zincirinde. Yani:

1. dinleyici sorunsuz açıldı,
2. istek alındı, kod döndürüldü,
3. testin **kendi assert'leri geçti**,
4. patlama **temizlik yolunda** oldu — `GetEPListener` prefix'i kaldırırken portu
   yeniden bağlamayı deniyor ve port o an dışarıdan tutulduğu için bağlanamıyor.

---

## 3. Kırılganlığın kaynağı

```csharp
const int port = 46499;   // sabit yazılmış
```

Üç test üç portu **sabit** yazıyordu: 46497 / 46498 / 46499. Depoda port dinleyen
başka test yok (`TcpListener` / `HttpListener` / `GetAvailablePort` sıfır), yani bu
tek çakışma noktasıydı — ama test, **paylaşımlı bir CI VM'de** o portu geçici olarak
tutan herhangi bir şeye karşı savunmasızdı.

`LoopbackCodeReceiver` ayrıca **aynı porta iki prefix** ekliyor (`127.0.0.1` +
`localhost`), ki yönetilen Linux `HttpListener` uygulamasında ek yüzey.

---

## 4. "Benim hatam mı?" — kontrol deneyi

| Ağaç | Ölçüm | OAuth loopback kırılması |
|---|---|---|
| `main` (603 test, PR'ın değişiklikleri **yok**) | tam süit ×14 | **1 kırılma** (`Loopback_Error_Param_Returns_Null`) |
| `fix/plugin-deadlock-and-serialization` (605 test) | tam süit ×6 | 0 |

Kırılma, **benim tek satırım olmayan kodda** da yerel olarak yakalandı. Yani bu PR'ın
getirdiği bir hata değil; PR yalnızca var olan bir kırılganlığı görünür kıldı.

---

## 5. Ölçümle ÇÜRÜTÜLEN hipotez

"Portta açık TCP bağlantısı varken `Dispose()` patlar" dedim (çünkü testin
`HttpClient`'ı `using`i henüz kapanmamış oluyordu). `LoopbackCodeReceiver`'ın yaptığını
birebir taklit eden küçük bir Linux programı yazdım:

```text
httpAcikBirakildi=True   Start+istek=OK kod='loop-code-1'  Dispose -> TEMIZ
httpAcikBirakildi=False  Start+istek=OK kod='loop-code-1'  Dispose -> TEMIZ
SONUC: baglanti ACIK -> temiz | baglanti KAPALI -> temiz      (3/3 tekrar)
```

**Hipotez ölü.** Mekanizmayı hafızadan tahmin etmek yerine yığın izini okumak gerekirdi.
Ders #51.

---

## 6. Tespit edilmeyen: portu ne tutuyordu?

Logda `ss` / `netstat` çıktısı yok. Yerel ölçümlerde portu kimse tutmuyor.

**Hipotez (kanıtsız):** çok süreçli xUnit modelinde (BULGU 30'da ölçüldü) bir süreç
portu tutarken diğeri aynı koleksiyonu yeniden çalıştırmış olabilir; ya da paylaşımlı
VM'de geçici bir dış süreç. **Logla desteklenmediği için tespit edilmiş gibi yazılmadı.**

---

## 7. Düzeltme ve KONTROL DENEYİ (belirleyici)

Kırılma **14 koşuda 1** kez olduğu için "düzeltince yeşil, geri alınca kırmızı"
biçiminde bir kontrol deneyi **statistiksel olarak anlamsızdır** — 50 koşu bile ayırt
edemez. Onun yerine kırılmayı kovalamak yerine **korumanın kendisini** belirleyici
biçimde sınayan beş test yazıldı (`FreeLoopbackPortTests`):

- `Next_ReturnsAPortThatCanActuallyBeBound`
- `Next_ReturnsADifferentPortOnEveryCall`
- `Next_NeverReturnsOneOfThePreviouslyHardcodedPorts`
- `EskiSabitPortlar_AreTheThreePortsThatWereHardcoded`
- `LoopbackReceiver_WorksOnADynamicallyAllocatedPort`

**Kontrol deneyi — `Next()` bilerek sabit porta döndürüldü, tek tek ölçüldü:**

| Test | `Next()` sabit 46499 | Geri alınınca |
|---|---|---|
| `Next_ReturnsAPortThatCanActuallyBeBound` | geçti | geçti |
| `Next_ReturnsADifferentPortOnEveryCall` | **KIRMIZI** | geçti |
| `Next_NeverReturnsOneOfThePreviouslyHardcodedPorts` | **KIRMIZI** | geçti |
| `EskiSabitPortlar_AreTheThreePortsThatWereHardcoded` | geçti | geçti |
| `LoopbackReceiver_WorksOnADynamicallyAllocatedPort` | geçti | geçti |

Tam 2 test döndü — **ölçümle**, tahminle değil. Diğer üçü yeşil kaldı çünkü onlar
sabit portla da doğru olan özellik testleri.

---

## 8. Doğrulama

| Ölçüm | Sonuç |
|---|---|
| Linux derleme | 0 hata |
| Windows derleme | 0 hata / 1 uyarı (`DatabaseFilePermissionTests.cs` CA1416, bu dalda dokunulmadı) |
| Koruma testleri (Linux) | **5/5** |
| Koruma testleri (Windows) | **5/5** |
| Kilitlenme regresyon (Linux / Windows) | 2/2, 93 ms / 2/2 |
| İzin testleri (`umask 022`) | 2/2 |
| **OAuth loopback ×12 (Linux)** | **0/12 başarısız** |
| Linux tam süit ×4 | **4/4 temiz**, 610 test |
| Windows tam süit ×4 | **4/4 temiz**, 610 test |
| Zombie süreç | yok |
| Sabit dinleyici portu kaldı mı | **0** (`grep -cE 'const int port = 4[0-9]{4}'` → 0) |

Test sayısı 605 → **610** (+5 koruma testi).

---

## 9. Bu turda iki kendi hatan

| # | Hata | Sonuç |
|---|---|---|
| 1 | Windows kontrol deneyi `FreeLoopbackPort.cs`'i sabit porta sabitlerken **Linux doğrulaması aynı dosyayı okuyordu** | Linux ölçümü geçersiz: koruma testleri "Failed 2/3 Passed" çıktı. Yeniden koşuldu → 5/5, 4/4 temiz |
| 2 | Linux betiğinde `Migurdex.**t**ests.csproj` yazım hatası | Tam süit 4/4 "başarısız" göründü; aslında proje bulunamadı |

**Ders:** bir kaynağı okuyan ölçüm ile o kaynağı değiştiren ölçüm **aynı anda
çalışmaz.** Kontrol deneyini doğrulama koşusundan ayrıklaştır.

---

## 10. Ortam kaybı (bu tur başında)

Temp temizliği iki doğrulama ortamını birden sildi:

```text
Temp\opencode\dotnet-sdk\dotnet.exe        -> SILINDI (sistemde SDK yok, sadece runtime)
Temp\opencode\wsl\distro\ext4.vhdx         -> SILINDI (Ubuntu-24.04'un diski)
```

İkisi de kalıcı yere taşındı: Windows `~\.dotnet-sdk`, WSL `/root/.dotnet`
(vhdx `AppData\Local\wsl\...`). İkisi de **10.0.401** — kayıtlardaki sürümle aynı,
yani ölçümler karşılaştırılabilir.

**Kural:** bir **araç zinciri** Temp'e konmaz. Temp betikler ve geçici çıktı içindir.
