# Migurdex REST API

Migurdex'in yerel HTTP API'sinin tam referansı. CLI, TUI ve `Migurdex.Api` daemon'ı bu API üzerinden
çalışır. Dokümandaki her yanıt örneği, 29 Eylül 2026'da çalışan bir API sürecinden **gerçekten alınmış**
çıktıdır (bkz. [Doğrulama kaydı](#18-doğrulama-kaydı)).

> **Kapsam:** Bu belge yalnızca Migurdex'in **kendi** REST API'sini (`Migurdex.Api`) anlatır.
> Uygulamanın kullandığı harici servisler (AniList GraphQL, Jikan/MAL REST) bu API'nin arkasındaki
> upstream kaynaklarıdır; bu belgede yalnızca API'nin onlara nasıl bağlandığı anlatılır
> ([Metadata bölümü](#8-metadata-ani̇list--jikan)).

---

## İçindekiler

1. [Genel bakış](#1-genel-bakış)
2. [Hızlı başlangıç](#2-hızlı-başlangıç)
3. [Konvansiyonlar](#3-konvansiyonlar)
4. [Sağlık ve OpenAPI](#4-sağlık-ve-openapi)
5. [Sağlayıcı uçları](#5-sağlayıcı-uçları)
6. [Anime uçları](#6-anime-uçları)
7. [SSE akışı](#7-sse-akışı)
8. [Metadata (AniList / Jikan)](#8-metadata-anilist--jikan)
9. [Extractor uçları](#9-extractor-uçları)
10. [Tracker uçları](#10-tracker-uçları)
11. [Veri modelleri](#11-veri-modelleri)
12. [Enum sözlüğü](#12-enum-sözlüğü)
13. [Hata kodları](#13-hata-kodları)
14. [Güvenlik notları](#14-güvenlik-notları)
15. [Limitler ve validasyon](#15-limitler-ve-validasyon)
16. [Uçtan uca akışlar](#16-uçtan-uca-akışlar)
17. [CLI ↔ API eşlemesi](#17-cli--api-eşlemesi)
18. [Doğrulama kaydı](#18-doğrulama-kaydı)
19. [Bilinen sınırlar](#19-bilinen-sınırlar)

---

## 1. Genel bakış

`Migurdex.Api`, Migurdex çekirdeğini bir yerel HTTP servisine çeviren minimal ASP.NET Core uygulamasıdır.
Aynı süreç içinde şunları barındırır:

| Bileşen | Açıklama |
|---|---|
| **Sağlayıcı katmanı** | `Plugins/` klasöründen yüklenen 14 anime sağlayıcısı (Acheriya, Animexe, TurkAnime, …) |
| **Extractor katmanı** | 38 gömülü hoster extractor'ı (Tau Video, DoodStream, Voe, …) |
| **Metadata katmanı** | AniList (GraphQL) ve Jikan/MAL (REST) sağlayıcıları |
| **Tracker katmanı** | AniList ↔ MAL ↔ sağlayıcı kimlik/sezon hizalama ve kalıcı eşleme (SQLite) |
| **Rust köprüsü** | `migurdex_native.dll` / `libmigurdex_native.so` üzerinden HTTP istemcisi |

### Nasıl başlar

API'yi ayrı süreç olarak başlatmak zorunda değilsiniz; CLI gerekli olduğunda kendisi başlatır:

- `ApiClientService.TryStartApiDaemonAsync()` önce `GET /health` ile 500 ms'lik yoklamada servis
  hazır mı diye bakar.
- Değilse `Migurdex.Api` sürecini `ASPNETCORE_URLS` ortam değişkeni ile başlatır ve en fazla ~8 saniye
  bekler.
- Aday yol sırası: `<cli>/api/Migurdex.Api[.exe]` → `<cli>/api/Migurdex.Api.dll` →
  `<cli>/Migurdex.Api.dll` → geliştirme çıktı klasörleri.
- Başlangıçta yüklenen plugin ve extractor sayıları `/health` yanıtında raporlanır.

### Adres ve port

| Kaynak | Değer |
|---|---|
| Varsayılan taban adres | `http://127.0.0.1:7045` |
| Yapılandırma alanı | `CliConfig.ApiBaseUrl` → `config.json` içinde `apiBaseUrl` |
| CLI zaman aşımı (health yoklaması) | 500 ms |
| `dotnet run` profili | `http://localhost:7045` (`Properties/launchSettings.json`) |
| API'yi elle başlatma | `ASPNETCORE_URLS=http://127.0.0.1:7099 ./Migurdex.Api` |

`apiBaseUrl` boş bırakılırsa yine `http://127.0.0.1:7045` kullanılır.

### Kimlik doğrulama ve erişim kontrolü

**Yoktur.** Uygulamada CORS, authentication, authorization veya rate limiting yapılandırması yoktur
(`Program.cs` içinde `AddCors`/`UseAuthentication`/`UseRateLimiter` çağrısı bulunmaz). Servis
`127.0.0.1` üzerine bağlandığı için yerel makinede çalışır. Ağa açmak isterseniz API bir
proxy'nin arkasında kalmalıdır — ayrıntı için [Güvenlik notları](#14-güvenlik-notları).

---

## 2. Hızlı başlangıç

Servisin ayakta olduğunu doğrulayın:

```bash
curl http://127.0.0.1:7045/health
```

```json
{"status":"OK","version":"1.10.2","providers":14,"extractors":38,"rust":true,"time":"2026-09-29T14:37:15.4442346Z"}
```

Sağlayıcıları listeleyin:

```bash
curl http://127.0.0.1:7045/api/v1/providers
```

Anime arayın:

```bash
curl "http://127.0.0.1:7045/api/v1/anime/search?q=naruto&provider=Animexe"
```

Detay çekin:

```bash
curl http://127.0.0.1:7045/api/v1/anime/Animexe/naruto
```

Fan-sub gruplarını ve o bölümün kaynaklarını çekin:

```bash
curl "http://127.0.0.1:7045/api/v1/anime/Animexe/groups?episodeId=naruto%2F1%2F1"
curl "http://127.0.0.1:7045/api/v1/anime/Animexe/sources?episodeId=naruto%2F1%2F1"
```

---

## 3. Konvansiyonlar

### Yöntemler ve rotalar

Tüm uçlar `GET`'tir; yalnızca iki uç `POST`'tur ve gövde alır:

| Uç | Yöntem | Gövde |
|---|---|---|
| `/api/v1/extractors/resolve` | `POST` | JSON: `{"url": "...", "headers": {…}}` |
| `/api/v1/tracker/mapping` | `POST` | JSON: `{"provider": …, "providerId": …, "anilistId": …, …}` |

### Yanıt içerik tipi

Bütün JSON uçları `application/json; charset=utf-8` döner. İstisna: `Results.Problem(...)` ile üretilen
hatalar RFC 7807 `application/problem+json` formatındadır (bkz. [Hata kodları](#13-hata-kodları)).

### Enum serileştirmesi

Enum'lar **sayı** olarak serileştirilir (özel `JsonStringEnumConverter` yoktur). Yani
`"type": 1` görürsünüz, `"type": "Anime"` değil. Sayıların karşılıkları
[Enum sözlüğü](#12-enum-sözlüğü)'ndeki tablolardadır.

### Adres kodlama

`provider` ve `animeId` **yol** parametresidir; `/` içerebilirler. `episodeId` ve `group` ise
**sorgu** parametresidir ve mutlaka URL-encode edilmelidir:

```bash
# doğru
curl "http://127.0.0.1:7045/api/v1/anime/Animexe/sources?episodeId=naruto%2F1%2F1"

# yanlış — episodeId boş gelir, 400 döner
curl "http://127.0.0.1:7045/api/v1/anime/Animexe/sources?episodeId=naruto/1/1"
```

`/api/v1/anime/{provider}/{*animeId}` bir **catch-all** rotadır, yani `animeId` içinde `/` bulunabilir.
Bu rota `/api/v1/anime/search`, `/api/v1/anime/{provider}/groups` ve
`/api/v1/anime/{provider}/sources` ile çakışmaz; daha spesifik rotalar önce kaydedilir.

### Ad ve büyük/küçük harf duyarlılığı

Sağlayıcı adları (`Animexe`, `animexe`, `ANIMEXE`) büyük/küçük harf duyarsız eşleştirilir
(`StringComparison.OrdinalIgnoreCase`). Metadata sağlayıcı adı olarak `mal` kullanılabilir ve dahili
olarak `Jikan`'a çevrilir.

### Yanıt süresi

Yavaş upstream'ler nedeniyle bir istek saniyeler sürebilir. `stream=true` kullanımı ilk sonucu beklemeden
vermek için tasarlanmıştır; CLI arama ekranı bu modu, oynatma/indirme listesi ise toplu (non-stream)
modu kullanır.

---

## 4. Sağlık ve OpenAPI

### `GET /health`

Servisin canlı olup olmadığını, yüklü sağlayıcı/extractor sayılarını ve Rust köprüsünün durumunu döner.
Bu uç **access log'dan hariç tutulur** ve yoklama (health probe) için `IsApiOnlineAsync()` tarafından
500 ms zaman aşımıyla çağrılır.

| Alan | Tip | Açıklama |
|---|---|---|
| `status` | `string` | Her zaman `"OK"` |
| `version` | `string` | Uygulama sürümü. Yerel build'de `0.0.0`, release'de ör. `1.10.2` |
| `providers` | `int` | Yüklenen sağlayıcı plugin sayısı |
| `extractors` | `int` | Kayıtlı extractor sayısı |
| `rust` | `bool` | `libmigurdex_native` başarıyla yüklendiyse `true` |
| `time` | `string` | ISO 8601 UTC zaman damgası |

```json
{"status":"OK","version":"1.10.2","providers":14,"extractors":38,"rust":true,"time":"2026-09-29T14:37:15.4442346Z"}
```

`rust: false` dönerse HTTP süreci çalışıyor ama native kütüphane yüklenememiş demektir. Bu durumda
tüm HTTP istekleri başarısız olur; süreç başlangıçta native yükleme hatasında kritik olarak çıkar.

### `GET /openapi/v1.json`

`AddOpenApi()` + `MapOpenApi()` ile otomatik üretilen OpenAPI 3.1.1 belgesi. Şu anda
**yalnızca yol ve parametre şeması** üretilir; yanıt gövdesi şemaları (`components/schemas`)
modeller için tanımlanmadığı için `{}` döner. Kullanılabilir parametre listesi:

```
GET     /health
GET     /api/v1/providers
GET     /api/v1/anime/search                       [q:query*, provider:query, stream:query]
GET     /api/v1/anime/{provider}/groups            [provider:path*, episodeId:query*]
GET     /api/v1/anime/{provider}/sources           [provider:path*, episodeId:query*, group:query, stream:query]
GET     /api/v1/anime/{provider}/{animeId}         [provider:path*, animeId:path*]
GET     /api/v1/metadata/search                    [q:query*, source:query]
GET     /api/v1/metadata/{source}/{id}             [source:path*, id:path*]
GET     /api/v1/extractors
POST    /api/v1/extractors/resolve
GET     /api/v1/tracker/resolve                    [provider, id, title, title2, year, format, malId, anilistId]
GET     /api/v1/tracker/lookup                     [anilistId, malId]
POST    /api/v1/tracker/mapping
GET     /api/v1/tracker/seasons                    [anilistId:query*]
GET     /api/v1/tracker/align                      [provider, id]
GET     /api/v1/tracker/episode                    [provider, id, season, episode]
```

`*` = şema seviyesinde zorunlu. Bazı uçlarda zorunluluk yalnızca çalışma zamanında denetlenir
(ör. `tracker/resolve` için `provider`, `id`, `title`) — bu durumda OpenAPI işareti görünmez.

---

## 5. Sağlayıcı uçları

### `GET /api/v1/providers`

`IAnimeProvider` arayüzünü uygulayan tüm sağlayıcıları ada göre sıralı döner. Manga/MovieTv türündeki
sağlayıcılar listeye **dahil edilmez**.

| Alan | Tip | Açıklama |
|---|---|---|
| `name` | `string` | Rotalarda kullanılan sağlayıcı anahtarı |
| `type` | `int` | `ProviderType` bayrakları. Anime için `1` |
| `baseUrl` | `string` | Sağlayıcının site adresi. Embed çözümlemesinde `Referer` başlığı olarak kullanılır |
| `capabilities` | `int` | `ProviderCapabilities` bayrakları |

`capabilities` **otomatik** hesaplanır: sağlayıcı `SearchAsync`'i override ediyorsa `Search` (1),
`GetGroupsAsync`'i override ediyorsa `Fansubs` (2) eklenir. Yani `3` = arama + fan-sub desteği,
`1` = yalnızca arama.

```json
[
  {"name":"Acheriya","type":1,"baseUrl":"https://acheriya.com","capabilities":3},
  {"name":"Animexe","type":1,"baseUrl":"https://animexe.com","capabilities":3},
  {"name":"Anizium","type":1,"baseUrl":"https://anizium.co","capabilities":1}
]
```

Gerçek çıktıda 14 kayıt döner: Acheriya, AniHub, AnimPow, AnimeciX, Animexe, Anizium, Anizm,
AsyaAnimeleri, Deokwave, OpenAnime, SonAnime, TRAnimeci, TrAnimeIzle, TurkAnime.

---

## 6. Anime uçları

### 6.1 `GET /api/v1/anime/search`

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `q` | ✅ | Arama metni. Boş olamaz, en fazla 200 karakter. Çevresindeki boşluklar kırpılır |
| `provider` | — | Sağlayıcı filtresi. Verilirse yalnızca o sağlayıcı aranır |
| `stream` | — | `true` ise SSE akışı, aksi hâlde toplu JSON döner |

`provider` verilmiş ama eşleşen sağlayıcı yoksa `404`.

#### Non-stream modu (varsayılan)

Her sağlayıcı için ayrı bir nesne döner. **Sonuç listesi asla tek düz bir dizi değildir** — sağlayıcı
kimliğini taşıyan bir zarf dizisidir:

```json
[
  { "provider": "Animexe", "data": [ /* SearchResult[] */ ] },
  { "provider": "Deokwave", "error": "Upstream arama hatası." }
]
```

Bir sağlayıcı çökse bile HTTP durumu `200` kalır; hata yalnızca o sağlayıcının nesnesinde `error`
alanı olarak görünür. Tüm sağlayıcılar paralel (`Task.WhenAll`) sorgulanır.

`SearchResult` modeli için bkz. [SearchResult](#searchresult).

```json
[{"provider":"Animexe","data":[{"id":"naruto","title":"Naruto","providerName":"Animexe","type":1,"format":0,"englishTitle":null,"romajiTitle":null,"japaneseTitle":null,"alternativeTitles":[],"posterUrl":"https://image.tmdb.org/t/p/original/vauCEnR7CiyBDzRCeElKkCaXIYu.jpg","year":"2002","score":9.3,"categories":null,"url":"https://animexe.com/anime/naruto"}]}]
```

#### Stream modu

`stream=true` ile [SSE akışı](#7-sse-akışı) açılır. Sağlayıcı listesi boşsa (ör. `provider` filtresi
eşleşmediyse ve `stream` kullanıldıysa) yalnızca boş bir `done` özeti gönderilir.

### 6.2 `GET /api/v1/anime/{provider}/{*animeId}`

Bir anime için detay döner. Yanıta uygulanan `AnimeDetails.Normalize()` dönüşümü:

- `Tv` veya `Unknown` formatta, başlıklarda `Movie/Film/Gekijouban` kalıbı varsa veya tek bölüm
  varsa özet `film/filmi/...` kalıbını içiyorsa → `format` `Movie` olur.
- `Movie` formatında tek bölüm varsa `number`/`season` `1` yapılır; başlık `1. Bölüm`, `Bölüm 1`,
  `1`, `Movie`, `Film`, `Special` gibi jenerik değerlerse `Film` olur.
- `Movie` formatında bölüm listesi boşsa tek bir `Film` bölümü eklenir.
- `Movie` formatında sezon eşlemesi 1. sezonu içermiyorsa tek elemanlı bir eşleme yazılır.

```bash
curl http://127.0.0.1:7045/api/v1/anime/Animexe/naruto
```

```json
{
  "title": "Naruto",
  "englishTitle": null,
  "romajiTitle": null,
  "japaneseTitle": null,
  "posterUrl": "https://…",
  "alternativeTitles": [],
  "summary": "…",
  "format": 0,
  "episodes": [
    {"id":"naruto/1/1","title":"1. Bölüm","number":1,"season":1},
    {"id":"naruto/1/2","title":"2. Bölüm","number":2,"season":1}
  ],
  "seasonMappings": [
    {"seasonNumber":1,"aniListId":null,"myAnimeListId":"20","tmdbId":null},
    {"seasonNumber":2,"aniListId":null,"myAnimeListId":null,"tmdbId":null}
  ]
}
```

Sağlayıcı bulunamazsa `404 {"error":"Provider bulunamadı."}`. Upstream hata verirse `502`
(`Upstream detay hatası (<provider>).`).

### 6.3 `GET /api/v1/anime/{provider}/groups`

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `episodeId` | ✅ | Sorgu parametresi. En fazla 512 karakter |

Bölümün sunduğu fan-sub gruplarını düz bir `string[]` olarak döner.

```json
["AniSekai","YuushaSubs"]
```

Boş dönerse o bölüm için gruplama yoktur; `/sources` çağrısında `group` verilmeden tüm kaynaklar
listelenir. `capabilities` değerinde `Fansubs` (2) biti yoksa bu uç boş liste döner.

### 6.4 `GET /api/v1/anime/{provider}/sources`

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `episodeId` | ✅ | Sorgu parametresi, en fazla 512 karakter |
| `group` | — | Fan-sub grubu filtresi, en fazla 512 karakter |
| `stream` | — | `true` ise SSE akışı |

#### Kaynak çözümleme mantığı

1. Sağlayıcıdan ham kaynak listesi alınır.
2. `type == Embed (2)` **ve** `CanExtract(url)` doğruysa, kaynak `IExtractorManager` ile çözülür.
   Çözümleme sırasında `Referer: <provider.baseUrl>` başlığı eklenir.
3. Extractor sonucu ile ham kaynak metadata'sı birleştirilir (`MergeSourceMetadata`):
   - `url`, `quality`, `type` → **her zaman** extractor sonucu kazanır.
   - `hoster`, `group`, `language` → extractor boş bıraktıysa ham kaynaktan gelir.
   - `headers` → anahtar anahtar birleştirilir, **çözümlenen medya URL'sinin başlıkları önceliklidir**.
   - `subtitles` → extractor sonucu boşsa ham kaynağınki kullanılır.
4. Sonuçlar URL'e göre büyük/küçük harf duyarsız gruplanıp ilk kayıt tutulur (dedupe).
5. Bir kaynağın çözümlemesi çökerse o kaynak atlanır ve loglanır; diğerleri döner. `stream=true`
   modunda bu hata ayrıca `providerError` (`scope: "extract"`) olarak akışa yansır ve `done.failed`
   sayacına yazılır; non-stream yanıtta gövde düz bir dizi olduğu için hata yalnızca loglanır.

```bash
curl "http://127.0.0.1:7045/api/v1/anime/Animexe/sources?episodeId=naruto%2F1%2F1"
```

```json
[
  {"url":"https://renjiabari.asia/file/tau-video/8f80f1f3-8643-46c1-b55f-b8af7277c7e1.mp4","quality":"480p","type":1,"hoster":"Tau Video","group":"AniSekai","language":"Japonca","headers":null,"subtitles":null},
  {"url":"https://yhwach.asia/file/tau-video/3ed8bb89-c954-47c7-a46f-01d03b14a1f4.mp4","quality":"480p","type":1,"hoster":"Tau Video","group":"YuushaSubs","language":"Japonca","headers":null,"subtitles":null}
]
```

`type` alanı `VideoType` değeridir: `0 = M3U8`, `1 = Mp4`, `2 = Embed`, `3 = Unknown`. Doğrudan
`Mp4`/`M3U8` bağlantıları extractor'a uğramaz.

---

## 7. SSE akışı

`stream=true` ile açılan uçlar [Server-Sent Events](https://developer.mozilla.org/docs/Web/API/Server-sent_events)
formatında yanıt verir. İlk sonuç beklenmeden, upstream'den geldiği anda gönderilir.

### Yanıt başlıkları

```
HTTP/1.1 200 OK
Content-Type: text/event-stream; charset=utf-8
Cache-Control: no-cache
Connection: keep-alive
X-Accel-Buffering: no
Transfer-Encoding: chunked
```

`X-Accel-Buffering: no` nginx gibi reverse proxy'lerde tamponlamayı kapatır.

### Tel kurgusu

Her olay iki satırdır ve `\n\n` ile ayrılır:

```
event: <olay-adı>
data: <tek satırlık JSON>
```

### Olay türleri

| Olay | Gönderen uç | `data` şekli |
|---|---|---|
| `searchResult` | `/anime/search` | `{"provider":"<ad>","status":"success","data":{…SearchResult…}}` |
| `source` | `/anime/*/sources` | `{…VideoSource…}` |
| `providerError` | `/anime/search`, `/anime/*/sources` | `{"provider":"<ad>","scope":"search"\|"sources"\|"extract","error":"<mesaj>"}` |
| `done` | hepsi | `{"succeeded":<int>,"failed":<int>,"errors":[{…}],"totalItems":<int>}` |
| `error` | — | **Yok.** Sunucu hiçbir uçta bu olayı göndermez; `SseHelper.EventError` sabiti de kaldırılmıştır |

`providerError` içindeki `scope`, hatanın hangi işlemde olduğunu belirtir:

| `scope` | Anlamı |
|---|---|
| `"search"` | Sağlayıcının arama isteği başarısız oldu (tüm sağlayıcı bazında) |
| `"sources"` | Sağlayıcının kaynak listesi isteği başarısız oldu (tüm sağlayıcı bazında) |
| `"extract"` | Tek bir kaynağın embed → medya çözümlemesi başarısız oldu (kaynak bazında) |

`done` özetindeki `errors` dizisi aynı hataların yapılandırılmış hâlidir
(`{"provider","scope","error"}`).

### Akış sonu semantiği

| Uç | `succeeded` | `failed` | `totalItems` |
|---|---|---|---|
| `/anime/search` | Başarılı provider sayısı | Hata veren provider sayısı | Gönderilen toplam sonuç sayısı |
| `/anime/*/sources` | Gönderilen (benzersiz) kaynak sayısı | Çözümlenemeyen kaynak sayısı | `succeeded + failed` |

> **Kaynak akışında dikkat:** Extractor çözümlemesi başarısız olan her kaynak bir `providerError`
> (`scope: "extract"`) üretir ve `failed` sayacını artırır. "upstream'dan 3 kaynak geldi, hiçbiri
> çözülemedi" durumunda özet `{"succeeded":0,"failed":3,"errors":[…3 hata…],"totalItems":3}` olur.
> Bu, stream'li kaynak akışı içindeki davranıştır; `stream=true` **kullanılmayan** non-stream
> yanıtın gövdesi düz bir kaynak dizisi olduğu için aynı hata orada yalnızca sunucu logunda görünür
> ve gövde şeklini değiştirmeden bildirilemez.

### Örnek: arama akışı

```bash
curl -N "http://127.0.0.1:7045/api/v1/anime/search?q=naruto&provider=Animexe&stream=true"
```

```
event: searchResult
data: {"provider":"Animexe","status":"success","data":{"id":"naruto","title":"Naruto","providerName":"Animexe","type":1,"format":0,…,"year":"2002","score":9.3,…}}

event: searchResult
data: {"provider":"Animexe","status":"success","data":{"id":"naruto-shippuden-1739","title":"Naruto Shippuden",…}}

event: searchResult
data: {"provider":"Animexe","status":"success","data":{"id":"boruto-naruto-next-generations-10986","title":"Boruto: Naruto Next Generations",…}}

event: done
data: {"succeeded":1,"failed":0,"errors":[],"totalItems":<n>}
```

### Örnek: kaynak akışı

```bash
curl -N "http://127.0.0.1:7045/api/v1/anime/Animexe/sources?episodeId=naruto%2F1%2F1&stream=true"
```

```
event: source
data: {"url":"https://yhwach.asia/file/tau-video/3ed8bb89-c954-47c7-a46f-01d03b14a1f4.mp4","quality":"480p","type":1,"hoster":"Tau Video","group":"YuushaSubs","language":"Japonca","headers":null,"subtitles":null}

event: source
data: {"url":"https://renjiabari.asia/file/tau-video/8f80f1f3-8643-46c1-b55f-b8af7277c7e1.mp4","quality":"480p","type":1,"hoster":"Tau Video","group":"AniSekai","language":"Japonca","headers":null,"subtitles":null}

event: done
data: {"succeeded":2,"failed":0,"errors":[],"totalItems":2}
```

### İstemci tarafı davranış

- Akış, `done` olayı gelene kadar veya bağlantı kapanana kadar açık kalır; bir zaman aşımı
  uygulanmaz.
- İstemci bağlantıyı kapattığında upstream istekleri `CancellationToken` ile iptal edilir
  (`OperationCanceledException` yakalanıp yutulur).
- SSE verisi `System.Text.Json` + `camelCase` ile serileştirilir.

---

## 8. Metadata (AniList / Jikan)

Bu uçlar anime sağlayıcılarından **bağımsızdır**; AniList ve Jikan/MAL'den doğrudan veri çeker ve
`MediaMetadata` modelini döner. `source` olarak şunlar kullanılabilir:

| `source` | Karşılık |
|---|---|
| `anilist` | AniList (GraphQL) |
| `jikan` | Jikan/MAL (REST) |
| `mal` | `Jikan` için takma ad |

Dönen `MediaMetadata.source` alanı ise şu sayısal değerleri kullanır: `0 = AniList`, `1 = Jikan`,
`2 = Tmdb`.

### 8.1 `GET /api/v1/metadata/search`

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `q` | ✅ | Arama metni, en fazla 200 karakter |
| `source` | — | Verilirse yalnızca o sağlayıcı aranır |

`source` verilirse yalnızca o sağlayıcının **düz `MediaMetadata[]`** dizisi döner. Verilmezse iki
sağlayıcı paralel sorgulanır, hata verenler boş liste olarak katkıda bulunur ve sonuçlar **tek bir
düz dizide birleştirilir** (sağlayıcı zarfı yoktur).

```bash
curl "http://127.0.0.1:7045/api/v1/metadata/search?q=naruto&source=anilist"
```

```json
[{"externalId":"20","source":0,"title":"NARUTO","englishTitle":"Naruto","romajiTitle":"NARUTO","japaneseTitle":"NARUTO -???-","summary":"…","posterUrl":"https://s4.anilist.co/…","bannerUrl":null,"status":"FINISHED","year":2002,"score":null,"totalEpisodes":220,"format":0,"genres":["Action","Adventure"],"synonyms":["Naruto Shonen","NARUTO -???-"],"aniListId":"20","myAnimeListId":null}]
```

`source` verilmiş ama tanınmıyorsa `404 {"error":"Metadata sağlayıcısı '<x>' bulunamadı."}`.

### 8.2 `GET /api/v1/metadata/{source}/{id}`

Tek kaydın detayını döner. **Kimlik alanında önek desteği** vardır ve çapraz arama sağlar:

| `source` | `id` biçimi | Davranış |
|---|---|---|
| `anilist` | `21` | AniList 21 |
| `anilist` | `mal:20` | MAL 20 → AniList eşleşmesi bulunur, AniList kaydı döner |
| `jikan` / `mal` | `20` | Jikan 20 |
| `jikan` / `mal` | `anilist:21` | AniList 21 → MAL kimliği çözülür → Jikan kaydı döner |
| `mal` | `anilist:21` | `mal` alias'ı olduğu için aynı davranış |

Önek çözülemezse (boş kalırsa) `400`, kayıt bulunamazsa `404`, upstream hatasında `502`.

```bash
curl http://127.0.0.1:7045/api/v1/metadata/anilist/mal:20
curl http://127.0.0.1:7099/api/v1/metadata/mal/anilist:21
```

```json
{"externalId":"20","source":0,"title":"NARUTO","englishTitle":"Naruto","…":"…"}
{"externalId":"21","source":1,"title":"One Piece","englishTitle":"One Piece","…":"…"}
```

---

## 9. Extractor uçları

### `9.1 GET /api/v1/extractors`

Kayıtlı tüm extractor'ların adını alfabetik döner. 29 Eylül 2026 itibarıyla 38 kayıt:

```
Abyss, AitrVip, AnizmPlayer, Byse, Cyberfile, Dailymotion, DoodStream, Firestream, Flyfile,
Gofile, GoogleDrive, HdVid, HexUpload, M3U8Playlist, MailRu, MixDrop, Mp4Upload, OkRu, Puffy,
Rumble, Sendvid, Sibnet, Sistenn, StreamWish, Streamain, Streamcash, Streamtape, Tau Video,
Turkanime, Uqload, VK, Videa, Vidmoly, VidsSt, Vidsonic, Voe, YandexDisk, YourUpload
```

Bu liste, `/api/v1/providers` çıktısındaki `CliConfig.PreferredHosterOrder` ile birlikte otomatik
kaynak seçiminde kullanılır.

### 9.2 `POST /api/v1/extractors/resolve`

Bir URL'yi doğrudan extractor katmanından çözer. Anime akışından bağımsız çalışır ve `CanExtract`
kararını da döner.

#### Gövde

| Alan | Tip | Zorunlu | Kısıt |
|---|---|---|---|
| `url` | `string` | ✅ | Mutlak `http`/`https` URL'si, en fazla 2048 karakter |
| `headers` | `object<string,string>` | — | En fazla 20 başlık; anahtar ve değer başına en fazla 4096 karakter |

```json
{ "url": "https://ornek-sahne/video/1", "headers": { "Referer": "https://ornek-site.com" } }
```

#### Yanıt

```json
{
  "url": "https://…/file.mp4",
  "canExtract": false,
  "results": []
}
```

`canExtract` false ise `results` her zaman boştur — extractor'lar yalnızca destekledikleri desenlere
uygundur. Doğrudan `.mp4` bağlantılarında `canExtract: false` ve boş sonuç almak normaldir.

#### Doğrulama sırası

1. `url` boş → `400 {"error":"URL boş olamaz."}`
2. Uzunluk > 2048 → `400 {"error":"URL en fazla 2048 karakter olabilir."}`
3. Mutlak `http`/`https` değilse → `400 {"error":"URL yalnızca http/https olabilir."}`
4. `headers.Count > 20` → `400 {"error":"En fazla 20 header gönderilebilir."}`
5. Anahtar/değer uzunluğu > 4096 → `400 {"error":"Header anahtar/değer çok uzun."}`
6. Engelli başlık → `400 {"error":"Header '<ad>' gönderilemez."}`
7. **SSRF kontrolü** → `400 {"error":"Bu host'a istek gönderilemez."}`
8. Çözümleme 15 saniye içinde bitmezse → `504` (`Extractor zaman aşımı.`)
9. Diğer upstream hataları → `502` (`Upstream extractor hatası.`)

Engelli başlıklar: `host`, `authorization`, `proxy-authorization`, `proxy-authenticate`.
Karşılaştırma `OrdinalIgnoreCase` yapılır ve anahtar çevresindeki boşluklar temizlenir.

---

## 10. Tracker uçları

Tracker katmanı, sağlayıcı kimliklerini AniList/MAL kimliklerine bağlar, sezon zincirini kurar ve
hizalamayı kanonik sezon/bölüm numaralarına çevirir. Sağlayıcı kimliği → eşleme tablosu
`TrackerMappingStore` içinde SQLite'da kalıcıdır ve **küçük harfe normalize** edilerek saklanır
(`<provider>:<id>`).

### 10.1 `GET /api/v1/tracker/resolve`

Bir sağlayıcı kaydını AniList/MAL kimliğine çözer. Eşleme önce kalıcı tabloda aranır; bulunamazsa
başlık, yıl, format ve sezon eşlemelerine göre fuzzy eşleştirme yapılır.

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `provider` | ✅ | Sağlayıcı adı |
| `id` | ✅ | Sağlayıcı içindeki anime kimliği |
| `title` | ✅ | En az bir başlık |
| `title2` | — | İkinci başlık (alternatif ad). Varsa eşleştirilmede iki başlık kullanılır |
| `year` | — | Çıkış yılı, `int` |
| `format` | — | `ContentFormat` adı/enum değeri. Ayrıştırılamazsa yok sayılır (hata vermez) |
| `malId` | — | Bilinen MAL kimliği. Varsa 1. sezon için sezon eşlemesi önceden bildirilmiş olur |
| `anilistId` | — | Bilinen AniList kimliği. `malId` ile birlikte de verilebilir |

```bash
curl "http://127.0.0.1:7045/api/v1/tracker/resolve?provider=Animexe&id=naruto&title=Naruto&year=2002&format=TV"
```

```json
{
  "entry": {
    "providerName": "animexe",
    "providerId": "naruto",
    "aniListId": "20",
    "myAnimeListId": "20",
    "matchedTitle": "Naruto",
    "score": 1,
    "updatedAt": "2026-09-29T14:38:09.4212496Z"
  },
  "ambiguous": false,
  "fromCache": true,
  "candidates": []
}
```

- `fromCache: true` → eşleme kalıcı tabloda bulundu, fuzzy arama yapılmadı.
- `ambiguous: true` → birden fazla aday eşleşti; `candidates` doludur, `entry` `null` olabilir.
- `candidates` → `{metadata, score}` listesi. `metadata`, tam bir `MediaMetadata` nesnesidir.
- `entry` alanı `null` olabilir (eşleşme bulunamadıysa) — HTTP durumu yine `200`'dür.

### 10.2 `GET /api/v1/tracker/lookup`

Tracker kimliğinden `MediaMetadata` çözer. `anilistId` veya `malId`'den en az biri gerekir.

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `anilistId` | —* | AniList kimliği |
| `malId` | —* | MAL kimliği |

`*` En az biri zorunludur; ikisi de yoksa `400 {"error":"anilistId veya malId gerekli."}`.

```bash
curl "http://127.0.0.1:7045/api/v1/tracker/lookup?malId=20"
```

```json
{"externalId":"20","source":0,"title":"NARUTO","englishTitle":"Naruto","…":"…"}
```

`anilistId` verilirse önce AniList, yoksa MAL kimliği kullanılır. Bulunamazsa `404`.

### 10.3 `POST /api/v1/tracker/mapping`

Eşlemeyi kalıcı tabloya **elle** yazar. Daha sonraki `resolve`/`align`/`episode` çağrılarında
`fromCache: true` ile döner.

| Alan | Tip | Zorunlu | Açıklama |
|---|---|---|---|
| `provider` | `string` | ✅ | Sağlayıcı adı |
| `providerId` | `string` | ✅ | Sağlayıcı içindeki anime kimliği |
| `anilistId` | `string` | ✅ | AniList kimliği |
| `myAnimeListId` | `string` | — | MAL kimliği |
| `matchedTitle` | `string` | — | Eşleşen başlık. Boş olabilir |

```bash
curl -X POST http://127.0.0.1:7045/api/v1/tracker/mapping \
  -H 'Content-Type: application/json' \
  -d '{"provider":"Animexe","providerId":"naruto","anilistId":"20","myAnimeListId":"20","matchedTitle":"Naruto"}'
```

Yanıt: `200 OK` ve **boş gövde**.

`provider`, `providerId`, `anilistId` alanlarından biri eksikse
`400 {"error":"provider, providerId ve anilistId boş olamaz."}`. Yazma hatasında
`502` (`Eşleşme kaydedilemedi.`). `score` her zaman `1.0` olarak yazılır, `updatedAt` sunucu saatidir.

### 10.4 `GET /api/v1/tracker/seasons`

Bir AniList kimliğinden sezon zinciri kurar ve tüm ilişkili sezonları/kalemleri döner.

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `anilistId` | ✅ | AniList kimliği |

```bash
curl "http://127.0.0.1:7099/api/v1/tracker/seasons?anilistId=21"
```

```json
{
  "rootAniListId": "21",
  "entries": [
    {"seasonNumber":0,"aniListId":"167404","myAnimeListId":"56055","title":"MONSTERS: Ippaku Sanjou Hiryuu Jigoku","totalEpisodes":1,"year":2024,"format":5},
    {"seasonNumber":1,"aniListId":"21","myAnimeListId":"21","title":"ONE PIECE","totalEpisodes":null,"year":1999,"format":0}
  ],
  "truncated": false
}
```

- `seasonNumber: 0` → filmler/OVAlar gibi sezon dışı kalemler.
- `format: 5` → `Unknown`; `0` → `Tv`; `1` → `Movie`; `3` → `Special`.
- `truncated: true` → zincir bir sınır nedeniyle kırpıldı.
- Zincir kurulamazsa `404 {"error":"Sezon zinciri kurulamadı."}`. Boş listede 200 dönen de olabilir.

### 10.5 `GET /api/v1/tracker/align`

Bir sağlayıcı kaydını alır, AniList kimliğini çözer, sezon zincirini kurar ve **hizalama** üretir.
Bu, "sağlayıcının 5. bölümü" ile "kanonik sezon 2, bölüm 5" arasındaki farkı çözen uçtur.

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `provider` | ✅ | Sağlayıcı adı |
| `id` | ✅ | Sağlayıcı içindeki anime kimliği |

```bash
curl "http://127.0.0.1:7099/api/v1/tracker/align?provider=Animexe&id=naruto"
```

```json
{
  "details": {
    "title": "Naruto",
    "format": 0,
    "episodeCount": 221,
    "mappings": [
      {"seasonNumber":1,"aniListId":null,"myAnimeListId":"20","tmdbId":null},
      {"seasonNumber":2,"aniListId":null,"myAnimeListId":null,"tmdbId":null}
    ]
  },
  "anilistId": "20",
  "chain": {
    "rootAniListId": "20",
    "entries": [
      {"seasonNumber":1,"aniListId":"20","myAnimeListId":"20","title":"NARUTO","totalEpisodes":220,"year":2002,"format":0},
      {"seasonNumber":2,"aniListId":"1735","myAnimeListId":"1735","title":"NARUTO: Shippuuden","totalEpisodes":500,"year":2007,"format":0}
    ],
    "truncated": false
  },
  "alignment": {
    "numberingMode": 1,
    "seasons": [
      {"providerSeasonNumber":1,"canonicalSeasonNumber":1,"aniListId":"20","myAnimeListId":"20","providerEpisodeCount":220,"canonicalEpisodeCount":220,"startOffset":1}
    ],
    "warnings": []
  }
}
```

AniList kimliği **çözülemezse** hata verilmez; bunun yerine kısmi yanıt döner:

```json
{"details":{"title":"…","format":0},"resolved":null,"ambiguous":false,"candidates":[],"chain":null,"alignment":null}
```

Bu dalda `resolved` alanı `TrackerResolveResult`'tır ve belirsiz eşleşmede `candidates` doludur.
Zincir kurulabilir ama hizalama üretilemezse yalnızca `chain` alanı dolu, `alignment` `null`'dır.

`numberingMode` (`EntryNumberingMode`): `0 = Unknown`, `1 = PerSeason`, `2 = Absolute`.
`startOffset` her sezonun kanonik numaralamada başladığı bölüm numarasıdır.

### 10.6 `GET /api/v1/tracker/episode`

Tek bir bölümü kanonik koordinata çevirir. `align` uçundan farklı olarak yalnızca sonucu döner.

| Parametre | Zorunlu | Açıklama |
|---|---|---|
| `provider` | ✅ | Sağlayıcı adı |
| `id` | ✅ | Sağlayıcı içindeki anime kimliği |
| `season` | — | Sağlayıcı tarafındaki sezon numarası. Verilmezse `1` kabul edilir |
| `episode` | ✅ | Bölüm numarası, `double` (ondalıklı bölümler için) |

```bash
curl "http://127.0.0.1:7099/api/v1/tracker/episode?provider=Animexe&id=naruto&season=1&episode=5"
```

```json
{"aniListId":"20","myAnimeListId":"20","season":1,"episode":5,"totalEpisodes":220,"isOverflow":false}
```

`isOverflow: true`, bölümün kendi sezonunun `totalEpisodes` sınırını aştığını gösterir (ör. özel/OVA
bölümleri). Hata ayrımı: sağlayıcı yoksa `404`, tracker ID çözülemezse
`404 "Tracker ID çözülemedi."`, zincir kurulamazsa `404 "Sezon zinciri kurulamadı."`,
bölüm eşlenemezse `404 "Bölüm eşlenemedi."`.

---

## 11. Veri modelleri

Tüm modeller `Migurdex.Shared/Models` altındadır ve CLI ile API arasında paylaşılır.

### `AnimeDetails`

| Alan | Tip | Zorunlu | Açıklama |
|---|---|---|---|
| `title` | `string` | ✅ | Ana başlık. Boş olabilir |
| `englishTitle` | `string?` | — | İngilizce başlık |
| `romajiTitle` | `string?` | — | Romaji başlık |
| `japaneseTitle` | `string?` | — | Japonca başlık |
| `posterUrl` | `string?` | — | Afiş görseli |
| `alternativeTitles` | `string[]` | — | Ek başlıklar (varsayılan `[]`) |
| `summary` | `string` | ✅ | Özet. Boş olabilir |
| `format` | `int` | ✅ | `ContentFormat` |
| `episodes` | `Episode[]` | — | Bölüm listesi (varsayılan `[]`) |
| `seasonMappings` | `SeasonMapping[]` | — | Sezon → tracker kimliği eşlemeleri (varsayılan `[]`) |

### `Episode`

| Alan | Tip | Açıklama |
|---|---|---|
| `id` | `string` | Sağlayıcıya özel bölüm kimliği. `groups`/`sources` uçlarına bu değer verilir |
| `title` | `string` | Bölüm başlığı |
| `number` | `double` | Bölüm numarası (ondalık desteklenir) |
| `season` | `int?` | Sezon numarası. Boş olabilir |

### `SeasonMapping`

| Alan | Tip | Açıklama |
|---|---|---|
| `seasonNumber` | `int` | Sezon numarası |
| `aniListId` | `string?` | AniList kimliği |
| `myAnimeListId` | `string?` | MAL kimliği |
| `tmdbId` | `string?` | TMDB kimliği (doldurulmuyor) |

### `SearchResult`

| Alan | Tip | Açıklama |
|---|---|---|
| `id` | `string` | Sağlayıcı içindeki anime kimliği (detay uçlarına bu verilir) |
| `title` | `string` | Ana başlık |
| `providerName` | `string` | Sonucu üreten sağlayıcı |
| `type` | `int` | `ProviderType` bayrakları |
| `format` | `int` | `ContentFormat` |
| `englishTitle` / `romajiTitle` / `japaneseTitle` | `string?` | Alternatif başlıklar |
| `alternativeTitles` | `string[]` | Ek başlıklar |
| `posterUrl` | `string?` | Afiş görseli |
| `year` | `string?` | **Metin** — `"2002"` gibi. Sayı değil |
| `score` | `double?` | Puan |
| `categories` | `string[]?` | Kategori etiketleri. Sağlayıcıya göre `null` olabilir |
| `url` | `string?` | Sağlayıcı sayfa adresi |

### `VideoSource`

| Alan | Tip | Açıklama |
|---|---|---|
| `url` | `string` | Doğrudan medya adresi |
| `quality` | `string` | Kalite etiketi, ör. `"480p"`, `"1080p"`, `"Auto"` |
| `type` | `int` | `VideoType`: `0 = M3U8`, `1 = Mp4`, `2 = Embed`, `3 = Unknown` |
| `hoster` | `string?` | Barındıran servis |
| `group` | `string?` | Fan-sub grubu |
| `language` | `string?` | Ses dili, ör. `"Japonca"` |
| `headers` | `object<string,string>?` | Medya isteği için gereken ek başlıklar (Referer, Cookie, …) |
| `subtitles` | `Subtitle[]?` | Altyazı adayları |

> `headers` alanı indirme/oynatma istemcisine **aktarılmalıdır**. Bu alan olmadan bazı hoster'ler
> `403` döner. `Referer` otomatik olarak sağlayıcının `baseUrl` değeriyle doldurulur.

### `Subtitle`

| Alan | Tip | Açıklama |
|---|---|---|
| `url` | `string` | Altyazı dosya adresi |
| `language` | `string` | Dil kodu/etiketi |
| `label` | `string?` | Görünen etiket |
| `format` | `string?` | Biçim, ör. `"srt"`, `"ass"` |
| `headers` | `object<string,string>?` | Altyazı isteği için ek başlıklar |

### `MediaMetadata`

| Alan | Tip | Açıklama |
|---|---|---|
| `externalId` | `string` | Kaynağın kendi kimliği |
| `source` | `int` | `MetadataSource`: `0 = AniList`, `1 = Jikan`, `2 = Tmdb` |
| `title` | `string` | Ana başlık |
| `englishTitle` / `romajiTitle` / `japaneseTitle` / `originalTitle` | `string?` | Başlık varyantları |
| `summary` | `string?` | Özet |
| `posterUrl` / `bannerUrl` | `string?` | Görseller |
| `status` | `string?` | Yayın durumu, ör. `"FINISHED"` |
| `year` | `int?` | Yıl |
| `score` | `double?` | Puan |
| `totalEpisodes` | `int?` | Toplam bölüm sayısı |
| `format` | `int` | `ContentFormat` |
| `genres` | `string[]` | Türler (varsayılan `[]`) |
| `synonyms` | `string[]` | Eş anlamlı adlar (varsayılan `[]`) |
| `aniListId` | `string?` | AniList kimliği. Yalnızca Jikan cevabında doludur |
| `myAnimeListId` | `string?` | MAL kimliği. Yalnızca AniList cevabında doludur |

### `SeasonChain` / `SeasonChainEntry`

| Alan | Tip | Açıklama |
|---|---|---|
| `rootAniListId` | `string` | Zincirin kök AniList kimliği |
| `entries` | `SeasonChainEntry[]` | Kalemler |
| `truncated` | `bool` | Zincir kırpıldı mı |

`SeasonChainEntry`: `seasonNumber` (`0` = sezon dışı film/OVA), `aniListId`, `myAnimeListId`, `title`,
`totalEpisodes`, `year`, `format`.

### `EntryAlignment` / `AlignedSeason`

| Alan | Tip | Açıklama |
|---|---|---|
| `numberingMode` | `int` | `EntryNumberingMode`: `0 = Unknown`, `1 = PerSeason`, `2 = Absolute` |
| `seasons` | `AlignedSeason[]` | Sezon hizalamaları |
| `warnings` | `string[]` | Hizalama uyarıları |

`AlignedSeason`: `providerSeasonNumber`, `canonicalSeasonNumber`, `aniListId`, `myAnimeListId`,
`providerEpisodeCount`, `canonicalEpisodeCount`, `startOffset` (varsayılan `1`).

### `TrackerResolveResult` / `TrackerCandidate` / `TrackerMappingEntry`

| Alan | Tip | Açıklama |
|---|---|---|
| `entry` | `TrackerMappingEntry?` | Kesin eşleşme. Yoksa `null` |
| `ambiguous` | `bool` | Birden fazla aday çakıştı mı |
| `fromCache` | `bool` | Kalıcı tablodan mı geldi |
| `candidates` | `TrackerCandidate[]` | Aday listesi (`{metadata, score}`) |

`TrackerMappingEntry`: `providerName`, `providerId`, `aniListId`, `myAnimeListId`, `matchedTitle`,
`score`, `updatedAt` (UTC).

### `TrackerEpisodeMapping`

| Alan | Tip | Açıklama |
|---|---|---|
| `aniListId` | `string` | Kanonik AniList kimliği |
| `myAnimeListId` | `string?` | Kanonik MAL kimliği |
| `season` | `int` | Kanonik sezon |
| `episode` | `double` | Kanonik bölüm |
| `totalEpisodes` | `int?` | Kanonik sezonun toplam bölüm sayısı |
| `isOverflow` | `bool` | Bölüm sezon sınırını aşıyor mu |

---

## 12. Enum sözlüğü

Enum'lar JSON'da **sayı** olarak görünür.

### `ContentFormat`

| Değer | Ad |
|---:|---|
| 0 | `Tv` |
| 1 | `Movie` |
| 2 | `Ova` |
| 3 | `Special` |
| 4 | `Manga` |
| 5 | `Unknown` |

### `VideoType`

| Değer | Ad |
|---:|---|
| 0 | `M3U8` |
| 1 | `Mp4` |
| 2 | `Embed` |
| 3 | `Unknown` |

### `ProviderType` (bayrak)

| Değer | Ad |
|---:|---|
| 1 | `Anime` |
| 2 | `Manga` |
| 4 | `MovieTv` |
| 8 | `Other` |

### `ProviderCapabilities` (bayrak)

| Değer | Ad |
|---:|---|
| 0 | `None` |
| 1 | `Search` |
| 2 | `Fansubs` |
| 3 | `Search \| Fansubs` |

### `MetadataSource`

| Değer | Ad |
|---:|---|
| 0 | `AniList` |
| 1 | `Jikan` |
| 2 | `Tmdb` |

### `EntryNumberingMode`

| Değer | Ad |
|---:|---|
| 0 | `Unknown` |
| 1 | `PerSeason` |
| 2 | `Absolute` |

---

## 13. Hata kodları

API üç farklı hata gövdesi biçimi kullanır. İstemci yazarken üçünü de ele alın.

### Biçim A — `{ "error": "..." }`

Validasyon ve bulunamadı hataları. `ApiErrors.BadRequest` / `ApiErrors.NotFound` ve bazı elle yazılan
dallar bunu üretir. `Content-Type: application/json; charset=utf-8`.

| Durum | Örnek gövde |
|---|---|
| 400 | `{"error":"Arama sorgusu ('q') boş olamaz."}` |
| 400 | `{"error":"Arama sorgusu en fazla 200 karakter olabilir."}` |
| 400 | `{"error":"URL yalnızca http/https olabilir."}` |
| 400 | `{"error":"Bu host'a istek gönderilemez."}` |
| 400 | `{"error":"anilistId veya malId gerekli."}` |
| 404 | `{"error":"Provider 'Animexe' bulunamadı."}` |
| 404 | `{"error":"Metadata bulunamadı."}` |
| 404 | `{"error":"Bölüm eşlenemedi."}` |

### Biçim B — RFC 7807 `application/problem+json`

`Results.Problem(...)` ile üretilen upstream hataları. Gövde `type`, `title`, `status`, `detail`
alanlarını içerir.

| Durum | `detail` |
|---|---|
| 502 | `Upstream detay hatası (Animexe).` |
| 502 | `Upstream grup hatası (Animexe).` |
| 502 | `Upstream kaynak hatası (Animexe).` |
| 502 | `Upstream metadata hatası (anilist).` |
| 502 | `Upstream extractor hatası.` |
| 502 | `Tracker çözümleme hatası.` / `Sezon zinciri hatası.` / `Hizalama hatası.` / `Bölüm eşleme hatası.` |
| 502 | `Eşleşme kaydedilemedi.` |
| 504 | `Extractor zaman aşımı.` (15 saniye) |

### Biçim C — çerçeve seviyesi hatalar

| Durum | Gövde | Neden |
|---|---|---|
| 400 | **boş** | Zorunlu sorgu parametresi hiç gönderilmemiş (ör. `/anime/search` için `q`) |
| 405 | **boş** | Yanlış HTTP yöntemi (ör. `GET /api/v1/extractors/resolve`) |
| 500 | `{"error":"Beklenmeyen sunucu hatası."}` | Yakalanmamış istisna; `UseExceptionHandler` üretir |

> **Tutarsızlık notu:** Zorunlu parametre eksikliği JSON hata gövdesi yerine **boş 400** döner, çünkü
> bu doğrulama ASP.NET Core model bağlama katmanında (`{provider}`, `q` gibi) yapılır ve uç koduna
> ulaşmaz. `q` gönderilip boş string verildiğinde ise Biçim A'ya uygun
> `{"error":"Arama sorgusu ('q') boş olamaz."}` döner. İstemciler boş gövdeyi de ele almalıdır.

### HTTP 200 dönen hatalar

Bazı hatalar durum kodu taşımaz:

- `/anime/search` (non-stream): sağlayıcı hatası → `{"provider":"X","error":"Upstream arama hatası."}`
- `/metadata/search` (tüm sağlayıcılar): sağlayıcı hatası → boş liste, diğer sağlayıcıların sonuçlarıyla birleşir
- `/tracker/resolve` ve `/tracker/align`: eşleşme bulunamazsa veya belirsizse `200` + `entry: null` / `ambiguous: true`
- `/anime/*/sources` (stream'li, `stream=true`): extractor hatası → `200` + akış içinde
  `event: providerError` (`scope: "extract"`) ve `done.failed` sayacında artış
- `/anime/*/sources` (non-stream): extractor hatası → o kaynak atlanır, `200` + eksik liste;
  gövde düz bir dizi olduğu için hata **gövdede bildirilemez**, yalnızca sunucu logunda görünür

---

## 14. Güvenlik notları

### Servis yereldir ve kimlik doğrulaması yoktur

API `127.0.0.1` üzerine bağlanır ve **hiçbir uç yetkilendirme gerektirmez**. `0.0.0.0` üzerine
bağlanıp ağa açarsanız ağınızdaki herkes sağlayıcı araması yapabilir, extractor çalıştırabilir ve
`POST /api/v1/tracker/mapping` ile kalıcı eşleme tablosunu değiştirebilir. Ağa açacaksanız
ters proxy + kimlik doğrulama katmanı ekleyin.

### Extractor SSRF koruması

`POST /api/v1/extractors/resolve` yalnızca `http`/`https` kabul eder ve host'u **çözümlemeden önce**
kontrol eder:

- `localhost` adı doğrudan reddedilir.
- IP literal ise doğrudan test edilir.
- Alan adı ise `Dns.GetHostAddressesAsync` ile çözülür; **herhangi bir** adres engelli aralıktaysa
  istek reddedilir.
- DNS çözümlemesi başarısız olursa istek **reddedilmez** (`false` döner) — çözümlenemeyen host için
  istek zaten yapılamaz.

Engelli adresler:

| Aile | Aralıklar |
|---|---|
| IPv4 | `0.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16`, `100.64.0.0/10` |
| IPv6 | tüm loopback, `::` (IPv6Any), link-local (`fe80::/10`), site-local |

> **Kapsam notu:** Bu koruma yalnızca `/api/v1/extractors/resolve` ucundadır. Anime sağlayıcıları
> kullanıcı tarafından verilmiş birer alan adına istek atar ve bu uçtan SSRF kontrolü **yoktur**;
> koruma, alan adının plugin tarafında sabit kodlanmış olmasına dayanır. SSRF kontrolü bir zamanlar
> bu uca da eklenmeli.

### Başlık enjeksiyonu engeli

`/api/v1/extractors/resolve` isteğinde `Host`, `Authorization`, `Proxy-Authorization` ve
`Proxy-Authenticate` başlıkları gönderilemez. Bu, extractor'ın isteği hedef sunucudan farklı bir
yere kimlik bilgileriyle gitmesini veya `Host` değiştirerek beklenmedik bir vhost'a ulaşmasını engeller.

### URL sızıntısı

`/anime/*/sources` yanıtındaki `url` alanları imzalı ve süreli geçerlidir. Loglara yazılacaksa
sorgu parametreleri maskelenmelidir. `CliConfig` ve loglama tarafında URL maskeleme yardımcıları
kullanılır; indirme sırasında gösterilen hata mesajları da token içermemelidir.

### Kalıcı veri

`POST /api/v1/tracker/mapping` kalıcı SQLite tablosuna yazar. Anahtar `<provider>:<id>`
şeklinde **küçük harfe** normalize edilir; sağlayıcı ve kimlik büyük/küçük harf duyarsızdır.
Aynı anahtar tekrar yazılırsa üzerine yazılır.

---

## 15. Limitler ve validasyon

| Uç | Alan | Limit | Aşım davranışı |
|---|---|---|---|
| `/anime/search` | `q` | 200 karakter | `400` |
| `/anime/search` | `provider` | — | eşleşmezse `404` |
| `/anime/{provider}/{animeId}` | `animeId` | 512 karakter | `400` |
| `/anime/{provider}/groups` | `episodeId` | 512 karakter | `400` |
| `/anime/{provider}/sources` | `episodeId` | 512 karakter | `400` |
| `/anime/{provider}/sources` | `group` | 512 karakter | `400` |
| `/metadata/search` | `q` | 200 karakter | `400` |
| `/extractors/resolve` | `url` | 2048 karakter | `400` |
| `/extractors/resolve` | `headers` sayısı | 20 | `400` |
| `/extractors/resolve` | header anahtar/değer | 4096 karakter | `400` |
| `/extractors/resolve` | süre | 15 saniye | `504` |
| `/tracker/episode` | `episode` | `double` | geçersizse `400` (model bağlama) |
| tüm uçlar | gövde boyutu | Kestrel varsayılanı (~30 MB) | ASP.NET çerçeve hatası |

Boş ve yalnızca boşluk içeren değerler tüm uçlarda "boş" kabul edilir; ayrıca giriş değerleri
`Trim()` edilir.

---

## 16. Uçtan uca akışlar

### Arama → oynatma/indirme (CLI'nin izlediği yol)

```
1. GET /api/v1/providers                     → sağlayıcı listesi (TUI/CLI doğrulaması)
2. GET /api/v1/anime/search?q=…[&stream=true] → sonuçlar (arama ekranı akışta beslenir)
3. GET /api/v1/anime/{provider}/{animeId}     → bölüm listesi
4. GET /api/v1/anime/{provider}/groups?episodeId=…   → fan-sub listesi
5. GET /api/v1/anime/{provider}/sources?episodeId=…[&group=…]
                                             → VideoSource[] (embed'ler çözülmüş)
6. Kaynak seçimi (istemci tarafı)             → kalite/tür/hoster kuralları, auto-select
7. VideoSource.url + headers                 → mpv'ye ya da indiriciye verilir
```

Adım 5–6 arasında `VideoSource.headers` **atlanmamalıdır**; barındıran sunucular çoğunlukla
`Referer` veya `Cookie` ister.

### Kaynak çözümleme

```
GET /api/v1/providers
  → capabilities & 2 (Fansubs) dolu mu?
GET /api/v1/anime/{provider}/groups?episodeId=…
  → grup listesi (boşsa Fansubs desteği yoktur)
GET /api/v1/anime/{provider}/sources?episodeId=…&group=…
  → yalnızca bu gruptaki kaynaklar
```

Tek bir URL'yi bağımsız çözmek için `POST /api/v1/extractors/resolve` kullanılır.

### İzleme senkronu (tracker)

```
1. GET /api/v1/tracker/resolve?provider=…&id=…&title=…
     → entry null değilse ve ambiguous değilse: senkron kimliği hazır
     → belirsizse: kullanıcı seçimi, ardından
2. POST /api/v1/tracker/mapping             → seçimi kalıcılaştır
3. GET /api/v1/anime/{provider}/{animeId}   → sağlayıcının bölüm listesi
4. GET /api/v1/tracker/align?provider=…&id=…
     → providerSeason → canonicalSeason eşlemesi + warnings
5. GET /api/v1/tracker/episode?provider=…&id=…&season=…&episode=…
     → tek bölümün kanonik koordinatı
6. AniList/MAL'ye gönderim (CLI, bu API'nin dışında)
```

---

## 17. CLI ↔ API eşlemesi

`Migurdex.Cli/Services/ApiClientService.cs` şu eşlemeyi kullanır:

| CLI işlemi | API uçu |
|---|---|
| Sağlayıcı listesi | `GET /api/v1/providers` |
| Arama (TUI akışı) | `GET /api/v1/anime/search?q=…&stream=true` |
| Arama (tek seferlik) | `GET /api/v1/anime/search?q=…[&provider=…]` |
| Anime detayı | `GET /api/v1/anime/{provider}/{animeId}` |
| Fan-sub listesi | `GET /api/v1/anime/{provider}/groups?episodeId=…` |
| Kaynak listesi | `GET /api/v1/anime/{provider}/sources?episodeId=…` |
| Kaynak listesi (akışlı) | `GET /api/v1/anime/{provider}/sources?episodeId=…&stream=true` |
| Extractor listesi | `GET /api/v1/extractors` |
| Tracker çözümleme | `GET /api/v1/tracker/resolve?provider=…&id=…&title=…&…` |
| Tracker sorgusu | `GET /api/v1/tracker/lookup?anilistId=…&malId=…` |
| Eşleme kaydetme | `POST /api/v1/tracker/mapping` |
| Bölüm eşleme | `GET /api/v1/tracker/episode?provider=…&id=…&season=…&episode=…` |
| Servis yoklaması | `GET /health` (500 ms zaman aşımı) |

CLI tarafında **doğrudan çağrılmayan** uçlar: `GET /openapi/v1.json`, `GET /api/v1/metadata/search`,
`GET /api/v1/metadata/{source}/{id}`, `POST /api/v1/extractors/resolve`, `GET /api/v1/tracker/seasons`,
`GET /api/v1/tracker/align`. Bunlar ya yalnızca TUI'nin alt bileşenlerinde ya da harici istemciler
içindir; `align` örneğin TUI'nin "izleme durumunu düzelt" akışında dolaylı olarak kullanılır.

---

## 18. Doğrulama kaydı

Bu belgedeki tüm JSON örnekleri ve durum kodları, 29 Eylül 2026'da Windows 11 üzerinde
`C:\Users\naton\OneDrive\Desktop\migu\api\Migurdex.Api.exe` sürecinin `127.0.0.1:7099` üzerinde
başlatılarak alınan gerçek yanıtlardır. Kullanılan zincir:

| Uç | Sonuç |
|---|---|
| `GET /health` | `200` — 14 provider, 38 extractor, `rust: true` |
| `GET /openapi/v1.json` | `200` — OpenAPI 3.1.1, 16 yol |
| `GET /api/v1/providers` | `200` — 14 kayıt |
| `GET /api/v1/extractors` | `200` — 38 kayıt |
| `GET /api/v1/anime/search` (q yok) | `400` — boş gövde (çerçeve seviyesi) |
| `GET /api/v1/anime/search?q=test&provider=Yok` | `404` — `{"error":"Provider 'Yok' bulunamadı."}` |
| `GET /api/v1/anime/search?q=naruto&provider=Animexe` | `200` — 6513 bayt sonuç |
| `GET …&stream=true` | `200` — `searchResult` × n, ardından `done` |
| `GET /api/v1/anime/Animexe/naruto` | `200` — 221 bölüm, 2 sezon eşlemesi |
| `GET /api/v1/anime/Animexe/groups?episodeId=naruto/1/1` | `200` — `["AniSekai","YuushaSubs"]` |
| `GET /api/v1/anime/Animexe/sources?episodeId=naruto/1/1` | `200` — 2 kaynak (`type: 1`, `480p`, Tau Video) |
| `GET …&stream=true` | `200` — `source` × 2, `done {succeeded:2}` |
| `GET /api/v1/metadata/search?q=naruto&source=anilist` | `200` — 11214 bayt |
| `GET /api/v1/metadata/anilist/21` | `200` — 2263 bayt |
| `GET /api/v1/metadata/mal/20` | `200` — `source: 1` (Jikan) |
| `GET /api/v1/metadata/anilist/mal:20` | `200` — çapraz arama, `source: 0` (AniList) |
| `GET /api/v1/metadata/mal/anilist:21` | `200` — çapraz arama, `source: 1` (Jikan) |
| `GET /api/v1/metadata/bilinmeyen/1` | `404` — sağlayıcı bulunamadı |
| `GET /api/v1/tracker/seasons?anilistId=21` | `200` — 2 kalem (`seasonNumber` 0 ve 1) |
| `GET /api/v1/tracker/lookup?malId=20` | `200` — AniList `MediaMetadata` |
| `GET /api/v1/tracker/resolve?provider=Animexe&id=naruto&title=Naruto&year=2002&format=TV` | `200` — `fromCache: true` |
| `GET /api/v1/tracker/align?provider=Animexe&id=naruto` | `200` — `numberingMode: 1`, 4 kalemli zincir |
| `GET /api/v1/tracker/episode?provider=Animexe&id=naruto&season=1&episode=5` | `200` — `{season:1, episode:5, totalEpisodes:220}` |
| `POST /api/v1/tracker/mapping` | `200` — boş gövde |
| `POST /api/v1/extractors/resolve` (localhost) | `400` — SSRF koruması |
| `POST /api/v1/extractors/resolve` (`Host` başlığı) | `400` — engelli başlık |
| `POST /api/v1/extractors/resolve` (doğrudan `.mp4`) | `200` — `canExtract: false`, `results: []` |
| `GET /api/v1/extractors/resolve` (yanlış yöntem) | `405` — boş gövde |

Doğrulama sırasında API'yi **elle** başlatmak şu komutla yapıldı:

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:7099"
.\Migurdex.Api.exe
```

---

## 19. Bilinen sınırlar

- **`502` gözlemlenmedi.** Yukarıdaki testlerde tüm sağlayıcı istekleri başarılı oldu; geçersiz
  bölüm kimlikleri bile `200` + boş liste döndürdü. Biçim B (`application/problem+json`) yalnızca
  koddan doğrulandı, canlı yanıtla teyit edilmedi.
- **Deokwave Cloudflare koruması** nedeniyle arama/kaynak isteklerinde upstream `403` dönebiliyor.
  Bu durum sağlayıcı hatası olarak `{"provider":"Deokwave","error":"Upstream arama hatası."}` ya da
  SSE `providerError` olarak yansır.
- **`/openapi/v1.json` gövde şemaları boş.** Modeller için `System.Text.Json` şema üretimi
  tanımlanmadığı için `components/schemas` dolu değil. Bu belge bu boşluğu kapatır.
- **Yalnızca iki uç `POST`.** Toplu işlem (batch), abonelik, favoriler ve izleme geçmişi HTTP
  üzerinden **açık değildir**; bunlar CLI/TUI içinde yerel veritabanı üzerinden yürütülür. `Migurdex.Shared`
  içinde `BatchRequest`/`BatchResponse` modelleri bulunur ancak API'ye bağlı değildir.
- **SSE'de `error` olayı yok.** Tüm hatalar `providerError` ile ifade edilir. Ölü olan
  `SseHelper.EventError` sabiti kaldırılmıştır; istemcilerin bu olayı dinlemesi yalnızca geriye
  dönük uyumluluk açısından zararsızdır, sunucu hiçbir zaman göndermez.
- **Kaynak akışında hata sayımı artık tutarlı (düzeltildi).** `/anime/*/sources` stream modunda
  extractor hatası artık `providerError` (`scope: "extract"`) üretir ve `done.failed` gerçek sayıyı
  taşır. Kalan sınır: `stream=true` **kullanılmayan** non-stream yanıtın gövdesi düz bir kaynak
  dizisi olduğu için per-source hata orada bildirilemez (yalnızca loglanır).
- **Yazma uçları yalnızca tracker eşlemesi.** API üzerinden veri silme/güncelleme ucu yoktur.

---

## İlgili belgeler

- [`DOWNLOAD.md`](DOWNLOAD.md) — indirme özelliğinin tam dokümantasyonu (bu API'nin `/sources`
  ucundan beslenen tarafı)
- [`README.md`](README.md) — kurulum, kullanım ve ayarlar
- [`TEST_RESULTS.md`](TEST_RESULTS.md) — offline ve canlı doğrulama kayıtları
- [`DEVELOPMENT_LOG.md`](DEVELOPMENT_LOG.md) — geliştirme günlüğü
