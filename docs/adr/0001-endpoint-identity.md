# ADR 0001 — Endpoint kimliği ve analiz snapshot'ı

Durum: Kabul edildi (T17) · Tarih: 2026-11-05

## Bağlam

Aynı API tekrar analiz edildiğinde endpoint'lerin **yeni öğe gibi görünmemesi** gerekiyor. Aksi halde bir sonraki fazlarda
(diff, senkronizasyon, silme koruması) her çalıştırma eski endpoint'i silip yenisini ekliyor gibi davranır.
Bunun için (1) endpoint'i çalıştırmalar arasında tanıyan bir kimlik, (2) önceki analizi hatırlayan bir snapshot gerekir.

Kimlik; dosya yolundan, satır numarasından, kaynak sırasından, biçimlendirmeden ve makineden bağımsız olmalı.

## Karar: kimlik = HTTP method + kanonik route

```
GET /api/products/{:int}        <-  [HttpGet("{id:int}")] on [Route("api/[controller]")] ProductsController
```

Controller ve action adı **kimliğe girmez**. Sebep: refactor'lar (sınıf/metot adı değiştirmek) URL'yi değiştirmez ve
tüketici için aynı endpoint'tir. Tüketicinin gördüğü şey method + URL'dir.

### Kanonik route kuralları (`EndpointIdentity`, sürüm 1)

| Kural | Örnek |
|---|---|
| Baştaki `~/` ve `/`, sondaki `/` atılır; tekrarlı `/` tek olur | `/api//Products/` → `api/products` |
| Sabit segmentler küçük harfe çevrilir (ASP.NET Core route eşleşmesi büyük/küçük harfe duyarsız) | `API/Products` → `api/products` |
| Parametre **adı atılır** (yeri kalır) | `{id}` ve `{productId}` → `{}` |
| Varsayılan değer atılır | `{id:int=5}` → `{:int}` |
| Constraint adları küçük harf, **argümanları olduğu gibi** (regex büyük/küçük harfe duyarlı) | `{c:Regex(^[A-Z]{3}$)}` → `{:regex(^[A-Z]{3}$)}` |
| `?` (opsiyonel) ve `*` / `**` (catch-all) korunur | `{id:int?}` → `{:int?}`, `{*rest}` → `{*}` |
| Kanonikleştirme **idempotent**: kimlik tekrar işlenirse değişmez | `Canonicalize(Create(...))` aynı |

Constraint'ler kimliğin parçasıdır: `{id:int}` ve `{name}` aynı method'da ayrı endpoint olabilir (constraint ile ayrışan overload).

### Değişikliklerin etkisi

| Değişiklik | Kimlik | Karşılaştırma sonucu |
|---|---|---|
| Biçimlendirme, yorum, satır sonu, dosya/namespace/metot sırası | aynı | Unchanged |
| Controller sınıfı yeniden adlandırıldı, route açık (`[Route("api/products")]`) | aynı | Modified |
| Action yeniden adlandırıldı | aynı | Modified |
| Route parametre adı veya route büyük/küçük harfi değişti | aynı | Modified |
| Parametre/body/auth/validation/örnek içeriği değişti | aynı | Modified |
| Yeni action eklendi / action silindi | yeni / kayıp | Added / Removed |
| Route değişti (`v2/` eklendi, constraint değişti) | **farklı** | Removed + Added (+ yeniden adlandırma adayı) |
| HTTP method değişti | **farklı** | Removed + Added (+ yeniden adlandırma adayı) |
| `[controller]` token'lı route'ta controller adı değişti | **farklı** (URL değişti) | Removed + Added, aday yok |

## Karar: içerik parmak izi (fingerprint)

`EndpointFingerprint` kimlik dışındaki, collection'a yansıyan her şeyin SHA-256 özetidir (controller/action adı, parametreler,
body ve property'ler, response'lar, auth, validation, örnek değerler). Aynı kimlikte parmak izi farklıysa **Modified**.
Örneğin **kaynağı** (dosya adı, satır numarası) parmak izine girmez: kodu taşımak veya araya satır eklemek endpoint'i
"değişmiş" göstermemelidir. Örneğin **değeri** girer.

## Karar: yeniden adlandırma yalnızca aday olarak raporlanır

Kayıp bir endpoint ile yeni bir endpoint **aynı controller + action** adını taşıyorsa ve bu çift tekil ise
(`SnapshotComparison.RenameCandidates`) bir *aday* sayılır. Adaylar Added/Removed listelerinde de kalır; hiçbir şey tahminle
birleştirilmez veya silinmez. Belirsiz eşleşme (aynı action'ın birden çok overload'ı) veya controller'ın da değiştiği durum aday üretmez.

## Karar: snapshot modeli ve saklama

`AnalysisSnapshot` (Core; JSON dönüşümü Infrastructure'da `AnalysisSnapshotStore`):

```json
{
  "schemaVersion": 1,
  "identityVersion": 1,
  "collection": { "fileName": "sample-api.postman_collection.json", "sha256": "…" },
  "endpoints": [
    { "id": "GET /api/products/{:int}", "fingerprint": "…", "httpMethod": "GET",
      "route": "api/Products/{id:int}", "controllerName": "Products", "actionName": "GetById" }
  ]
}
```

- **Önceki collection**: collection dosyasının kendisi snapshot'ın yanında ayrı dosya olarak durur; snapshot yalnızca
  dosya adını ve LF-normalize SHA-256 özetini tutar (saklanan collection snapshot ile uyuşuyor mu diye).
- **Önceki analiz metadata'sı**: endpoint başına kimlik, parmak izi ve son bilinen adlar. Tam `EndpointDefinition` saklanmaz;
  alan düzeyinde fark gerekirse sonraki faz saklanan collection'ı kullanır veya `schemaVersion` yükseltilir.
- **Deterministik**: endpoint'ler kimliğe göre sıralı, LF, zaman damgası yok, mutlak yol yok, secret yok. Değişmemiş bir API
  her seferinde birebir aynı dosyayı üretir (Git'te anlamlı diff).
- **Sürümleme**: `identityVersion` kanonikleştirme kuralları değişirse, `schemaVersion` dosya biçimi değişirse artar.
  `SnapshotComparer` sürümler farklıysa karşılaştırmayı **reddeder**; yeni baseline alınmalıdır.
- **Benzersizlik**: aynı kimliği üreten iki action (ASP.NET çalışma zamanında zaten `AmbiguousMatchException` verir)
  keşif sırasında uyarı olarak raporlanır; `AnalysisSnapshot.Create` ve CLI `--snapshot` bunu sessizce atlamak yerine **hata** verir.

### Kullanım

```
dotnet run --project src/ApiCollection.Cli -- generate sample-api/SampleApi --examples config/examples.sample-api.json --name "Sample API" --out fixtures/postman/sample-api.postman_collection.json --snapshot fixtures/postman/sample-api.snapshot.json
```

`--snapshot` yalnızca yazar ve mevcut dosyanın üzerine yazar. Baseline'ı korumak (yedek, dry-run, "değişiklik yoksa yayınlama")
sonraki fazın işidir. `fixtures/postman/sample-api.snapshot.json` ilk saklanan baseline'dır; `CommittedSnapshotTests` onu güncel
analizle ve committed collection ile karşılaştırır.

## Reddedilen alternatifler

- **controller.action kimliği**: route değişikliklerine dayanıklı ama refactor'da kırılır, overload'larda belirsiz, tüketicinin görmediği bir ad.
- **Parametre adlarını kimliğe katmak**: `{id}` → `{productId}` gibi zararsız değişiklikler "yeni endpoint" olurdu.
- **Constraint'leri atmak**: `{id:int}` / `{name}` overload'ları aynı kimliği paylaşıp birbirini ezerdi.
- **Kimlik olarak rastgele/üretilmiş UUID**: tekrar analizde aynı değeri üretemez; kaynakta saklanacak yer yok.

## Sınırlamalar ve açık noktalar

- Yalnızca attribute routing tanınır (conventional routing ve minimal API `MapGet` kapsam dışı).
- Postman Cloud collection/item UID'leri snapshot'ta yok; senkronizasyon fazında `schemaVersion` yükseltilerek eklenir.
- Parmak izi kaynak konumunu yok sayar, ama collection açıklamasındaki "kaynak" satırları konum içerir; yani satır kayınca
  collection **metni** değişebilir, endpoint "Modified" sayılmaz. Metin karşılaştırması yerine endpoint karşılaştırması esas alınmalı.
- Added/Modified/Removed'a göre ne yapılacağı (politika, silme koruması, dry-run, geri alma) bu ADR'nin kapsamı dışındadır.
