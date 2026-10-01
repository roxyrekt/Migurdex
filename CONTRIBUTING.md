# Katkı rehberi

Migurdex'e katkıda bulunurken uyulması gereken kurallar. Bu dosya bir **proje
kuralı** dosyasıdır: buradaki maddeler ihlal edildiğinde PR reddedilir.

Kuralların her biri gerçek bir hatadan çıkarıldı; "mantıklı görünüyor"
diye yazılmadı.

---

## 1. Düzeltmeyi düzelttiğini ölç

> **Kural:** Bir düzeltme yaptıktan sonra, **aynı oturumda** değişen dosyayı geri
> oku ve ölç. "Düzelttim" ile "doğruladım" farklı cümlelerdir.

Bir katkıda Türkçe metin bozulması (`ş` → `ş`) dört turda düzeltildi ve her turda
"temiz" denildi. Gerçek tablo:

| Tur | Yöntem | "Temiz" denildi mi | Gerçek sonuç |
|---|---|---|---|
| 1 | Parça-kelime eşlemesi | evet | 7 satır **kaçırıldı** |
| 2 | Genişletilmiş desen | evet | 3 **düzgün** satır bozuk sayıldı |
| 3 | Kelime listesi | evet | **yeni hata üretildi** (`Satır` → `Satşr`) |
| 4 | Tam satır eşlemesi | evet | 0 kalıntı — gerçekten temiz |

**Uyum:**

- Düzeltme aracı **çıktısını listelesin**, sadece sayı vermesin. Hangi satır
  değişti, eski hâli neydi, yeni hâli ne oldu — üçü de görünmeli.
- Düzeltme sonrası tarama, bulunan satır sayısının **azalmasını** bekler.
  Artarsa yeni bozulma ürettin demektir.
- Birleşik kelimeler parça parça düzeltilemez. `kullanıcı` + `nın` ayrı ayrı
  düzeltilirse `kullanşcşnşn` olarak birleşir. **Tam satır** değiştir.
- Aynı dosyada ikinci kez tarama yap ve sonucu ilk taramayla karşılaştır. Aynı
  desen iki kez aynı sayıyı veriyorsa test ayırt edici değildir; deseni değiştir.

## 2. Ölçüm aracı biçim varsaymamalı

> **Kural:** Tarama/düzeltme aracı, "şu desen bozuktur" diye yazılmaz.
> **"Şu ikili şu harfe dönüşür"** diye yazılır. Aracın ilk koşusuna
> güvenilmez.

Aynı hatada tarama aracı önce 7 bozuk satırı **kaçırdı**, sonra 3 düzgün
Türkçe satırı **bozuk saydı**. Sebep: `Ä`, `Å` gibi harfler tek başına
bozukluk kanıtı değildir — `Ğ`, `Ş`, `İ` gibi harflerin gerçek yazımı da onları
içerir.

**Uyum:**

- Eşleme tablosunu **çift kodlama yönüyle** kur: `ş` (U+015F) ← `Å` (U+00C5 U+00B8).
- ⭐ **Arama desenini elle yazma, koda ürettir.** Elle yazılan bozukluk
  desenleri iki kez yanlış çıktı: birinde küçük `œ` (U+0153) yazıldı,
  dosyada büyük `Œ` (U+0152) vardı. Doğrusu:

  ```python
  def bozuk(s):
      for enc in ("cp1252", "latin-1"):
          try:
              x = s.encode("utf-8").decode(enc)
          except Exception:
              continue
          if x != s:
              yield x
  ```

- ⭐ Doğrulamayı **derlenmiş çıktıda da** yap. Kaynak temiz olup
  derlenmiş dosya bozuk olabilir; tersi de. İkili içinde metin
  **UTF-16LE** olarak aranır (`s.encode("utf-16-le")`).
- ⭐ Üü tarayıcı **yanlış yazılmış kelimeyi yakalamaz.** `not
  yanlştır` yazıldı, doğrusu `not yanlıştır`; tarama 0 döndü,
  çünkü kodlama doğruydu. **Türkçe metni gözle de oku** — tarayıcı
  kodlama için, göz harf için.
- Bulguyu **kodepoint tablosuyla** doğrula, gözle değil:

  ```python
  import unicodedata
  for i, line in enumerate(open(path, encoding='utf-8').read().split('\n'), 1):
      for c in line:
          if ord(c) > 127:
              print(f'L{i}: U+{ord(c):04X} {unicodedata.name(c, "?")}')
  ```

- Türkçe metni içeren kaynak dosyayı **derleme sonrası ekran çıktısından
  doğrulama.** Konsol kodu sayfa kodlamasından geçirir; ekran görüntüsü
  düzgün görünürken diskteki dosya bozuk olabilir.

## 3. Depo dışı takip notu, projeyi tanımlamaz

> **Kural:** Özellik eklenince **o özelliğin kendi dokümanı** güncellenir.
> Kullanıcıya/sonraki ajana yazılan takip notu, proje dokümanının yerini
> tutmaz.

Bir özellik `README.md` güncellenmeden gönderildi. Projeyi okuyan kişi ne
geldiğini, neden öyle yapıldığını ve sınırların ne olduğunu **hiçbir yerde**
bulamadı.

**Uyum:**

- Yeni ekran/özellik için: ne yapar, nasıl kullanılır, sınırları ne, hangi
  dosyaları etkiler — `README.md` içine yazılır.
- Ölçüm günlüğü, hata geçmişi, ajan notu **PR'a girmez.** Bunlar depo dışında
  tutulur; PR diff'ini kirletirler.

## 4. PR gönderilmeden önce elle test zorunlu

> **Kural:** PR gönderilmeden önce akışı **elle çalıştır.** CI yeşil olması
> yetmez.

Bir PR'da CI 3/3 yeşildi, 363/363 test geçti — ve kullanıcının ilk çalıştırmasında
üç hata çıktı:

| Hata | Neden CI görmedi |
|---|---|
| Ekran açılışta çöküyordu | Konsol etkileşimi gerekiyor; test kapsamı dışı |
| Bayt/hız/ETA hiç görünmüyordu | `DownloadRequest.Progress` bağlanmamıştı; test bunu ölçmüyor |
| Hata nedeni hiç yazılmıyordu | `catch` istisnayı yutuyordu; test edilemez |

Üçünün de testi yeşildi.

**Uyum:**

- ⭐ **CI yeşil ≠ çalışıyor.** CI derlendiğini kanıtlar, ekranın açıldığını
  değil. Konsol etkileşimi gerektiren kod otomatik testle zorunlu kapsanamaz —
  o boşluk yalnız elle testle kapanır.
- Elle test edilen akış kayda geçer: **ne denendi, ne beklendi, ne oldu.**
- Bir davranış dokümante edilmişse, kodda gerçekten öyle olduğunu doğrula.
  "Bu ekranda `Esc` iptal eder" yazan bir doküman, tuş okuyan hiçbir satır
  içermiyorsa doküman **yanlıştır** — ya kodu yaz ya da dokümanı düzelt.

## 5. Ajan/otomasyon raporları otomatik kabul edilmez

> **Kural:** Bir raporu okumak, onu uygulamak **değildir.** Raporun her
> iddiasını bağımsız ölç.

12 bulgunun 1'i ölçümle çürütüldü; sayılar bile ayrıştı. Raporda geçen
dosya yollarının **bu depoda var olduğunu doğrula** — geçmişte başka bir
branch'in ağacına ait satır numaraları yanlış dosyaya bağlanarak kullanıldı.

**Uyum:**

- Rapordaki her satır numarasını ve dosya yolunu **bu depoda** doğrula.
- Rapordaki sayıları da doğrula, sadece teşhisi değil.

---

## 6. Doğrulama araçlarını çalıştırmadan önce denetle

**Ölçüldü (01.10.2026):** 60 satırlık bir indirme script'i yazıp doğrudan
arka planda başlattım. 40 saniye sonra `SyntaxError` geldi — tırnak
karışıklığı. Aynı oturumda **3 kez** tırnak hatası yapıldı.

**Uyum:**

- Uzun bir script'i çalıştırmadan **bır saniyelik** denetimden geçir:
  `python -c "import py_compile; py_compile.compile(PATH, doraise=True)"`
- Metinde **apostrof** varsa dış tırnağı **çift** yap:
  `"Script'i ..."`. Hatanın üçte biri de bu.
- ⭐ Dokümante edilmiş bir davranış kodla çelişmişse — ya kodu yaz
  ya dokümanı düzelt. "`Esc` iptal eder" yazan bir not, tuş okuyan
  hiçbir satır içermiyorsa **not yanlıştır**.

## Hızlı kontrol listesi

PR göndermeden önce:

- [ ] `dotnet build` temiz, `dotnet test` yeşil
- [ ] Değişen akış **elle çalıştırıldı**, sonucu yazıldı
- [ ] Özellik `README.md`'ye yazıldı
- [ ] Değişen dosyalar **geri okundu** (kodepoint taraması)
- [ ] Tarama desenleri **koda üretildi**, elle yazılmadı
- [ ] Değişen ekranın **derlenmiş çıktıda da** doğrulandı
- [ ] `.md` dosyalarındaki davranış **kodla eşleşiyor**
- [ ] Dokümante edilen davranış kodda **gerçekten var**
- [ ] PR diff'inde ölçüm günlüğü / ajan notu **yok**
