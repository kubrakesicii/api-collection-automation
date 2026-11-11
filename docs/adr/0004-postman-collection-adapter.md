# ADR 0004: Postman koleksiyon adaptörü (create / read / update)

Durum: Kabul edildi (T22) · Tarih: 2026-11-06 · Bağlı: [ADR 0001](0001-endpoint-identity.md), [ADR 0003](0003-sync-safety.md)

Hedef okuyucu: Postman Cloud'a yazan kodu değiştirecek ya da gözden geçirecek geliştirici.

## Bağlam

Koleksiyonu Postman Cloud'da otomatik oluşturup güncellemek gerekiyor: ilk çalıştırmada oluşmalı, sonrakilerde **aynı** koleksiyon
güncellenmeli, gereksiz yeni koleksiyon oluşmamalı, request/folder kimlikleri korunmalı. Bu, ilk kez Postman'a **yazan** koddur; bu yüzden
her belirsizlik yazmadan durmayla çözülür (fail-closed).

## Doğrulanan Postman API bilgisi (resmi referans)

- `POST /collections?workspace=<id>`: gövde `{ "collection": { info, item, variable, ... } }`; 200 yanıt `{ "collection": { id, name, uid } }`.
- `PUT /collections/{collectionId}`: koleksiyonu **tamamen değiştirir**. Item `id`'leri yalnızca **gönderilirse** korunur; gönderilmezse item'lar
  silinip yeni id'lerle yeniden oluşturulur. `info._postman_id` isteğe bağlıdır; başka bir koleksiyondan kopyalanan `id`/`uid`/`_postman_id` hata verir.
  `Prefer: respond-async` ile 202 döner (biz göndermeyiz). Üst sınır 100 MB.
- `GET /collections?workspace=<id>&name=<tam ad>`: ad filtresi tam eşleşmedir ve `limit/offset` ile birleştirilemez. **Geçersiz workspace
  hata değil, boş liste döndürür.**
- `GET /collections/{collectionId}`: yanıt `{ "collection": { info, item, variable, ... } }`; bulunamayan koleksiyon **HTTP 400** ("Collection Not Found") olarak belgelenmiştir.
- Hata gövdeleri iki biçimdedir: `{ "error": { "name", "message" } }` ve düz `{ "type", "title", "detail", "status" }`.
- `info.schema` olarak `https://schema.postman.com/json/collection/v2.1.0/collection.json` istenir.

## Doğrulanamayanlar (varsayım; ilk gerçek çalıştırmada görülecek)

- `{collectionId}` yolunda `id` mı `uid` mi beklendiği belgede yazmıyor. Biz create yanıtındaki **`uid`**'yi saklar ve kullanırız.
- ~~Postman'ın bizim ürettiğimiz item `id`'lerini (UUIDv5) olduğu gibi kabul edip koruduğu.~~ **Gerçek çalıştırmada çürütüldü (2026-11-11):**
  `POST /collections` ile oluşturulan koleksiyonda her klasör ve request'e Postman kendi rastgele (v4) id'sini atadı; gönderdiğimiz
  UUIDv5 değerleri saklanmadı. İçerik (ad, metot, URL, gövde, header, auth, değişken) doğru kaydedildi. Bu yüzden doğrulama item id'sine
  değil yapıya göre yapılır (bkz. ADR 0005). `PUT` ile id'lerin korunup korunmadığı ayrıca doğrulanmadı; doğrulama buna bağlı değildir.
- Yazma yetkisi (API key'in bu workspace'e koleksiyon yazıp yazamadığı) T21'deki kontrolle sınanmaz.
- Hiçbir şey gerçek Postman'a karşı çalıştırılmadı; testler belgelenen davranışı taklit eden bellek içi bir Postman kullanır.

## Karar: üç katman

1. **`PostmanCloudClient`** (düşük seviye): `GetWorkspace`, `FindCollectionsByName`, `GetCollection`, `CreateCollection`, `UpdateCollection`. **Silme yok**, bu
   yöntem sınıfta bulunmaz. Her sonuç bir değerdir (`PostmanApiResult`), istisna değil. Anahtar yalnızca `X-API-Key` başlığında gider; https zorunlu,
   yönlendirme takip edilmez, yanıt boyutu sınırlı, Postman'dan gelen metin temizlenir ve anahtar `*****` ile kazınır. Kimlikler (`[A-Za-z0-9-]`) yola girmeden doğrulanır.
   Ortak HTTP mantığı (`PostmanApiTransport`) T21'in bağlantı denetimiyle paylaşılır.
2. **Yük hazırlama**: koleksiyon `{ "collection": ... }` içine sarılır, `info.schema` Postman'ın istediği adrese çekilir, koleksiyonun **kendi**
   `_postman_id`/`id`/`uid` alanları silinir; folder ve request id'leri **korunur**. `info.name` metin değilse ya da `item` yoksa, ya da yük 100 MB'ı aşıyorsa hiçbir şey gönderilmez.
3. **`PostmanCollectionRepository`** (karar mantığı): yayınlamanın tek yolu.

## Karar: kimlikler

Yayın moduna özel (`PostmanCollectionOptions.IdNamespace` dolu iken) folder ve request'lere deterministik **UUIDv5** `id` verilir (yerel dosyalar değişmez):

- request id = UUIDv5(`{namespace}|request|{endpoint kimliği}`): kimlik ADR 0001'dendir (method + kanonik route). Action ya da controller yeniden adlandırılsa id değişmez.
- folder id = UUIDv5(`{namespace}|folder|{controller adı}`).
- `namespace` hedefe özgüdür (workspace ID + koleksiyon adı). Aynı API'yi iki workspace'e yayınlamak aynı id'leri üretmez (Postman id'leri global tutar; çakışma hata verebilir).
- Uygulama bağımsız bir uygulamayla (Node) çapraz doğrulanmıştır ve RFC test vektörünü geçer.

## Karar: gereksiz koleksiyon oluşturmama (sahiplenme işareti)

Yayın modunda koleksiyona `automationManaged = api-collection-automation` değişkeni eklenir. Akış:

1. Girdi doğrulanır; `Blocked` karar (ADR 0003) **hiçbir istek göndermeden** durdurur. `NoChanges` ve uid biliniyorsa da istek gönderilmez.
2. Workspace okunarak doğrulanır. (Postman geçersiz workspace için boş liste döndürdüğünden, "koleksiyon yok" sonucuna ancak bundan sonra güvenilir.)
3. **uid biliniyorsa** doğrudan güncellenir. 404 gelirse (koleksiyon silinmiş) **yenisi oluşturulmaz**; `CollectionMissing` hatası verilir.
4. **uid bilinmiyorsa** aynı adlı koleksiyonlar aranır ve her biri okunup işaret aranır:
   - hiç yok: oluşturulur;
   - **tam bir** işaretli koleksiyon: sahiplenilir ve güncellenir (değişiklik yoksa yazılmaz, `Adopted`);
   - aynı adlı ama **işaretsiz** koleksiyon: **ezilmez, ikincisi de oluşturulmaz**: `NameConflict`;
   - birden çok işaretli: tahmin edilmez: `AmbiguousCollections`.

Create başarılı olup yanıtta uid okunamazsa, koleksiyon yine de oluşmuştur; sonraki çalıştırma onu işaretinden bulur, yani yinelenen oluşmaz.

## Karar: dry-run

`DryRun = true` yalnızca GET gönderir ve `WouldCreate` / `WouldUpdate` döner; hiçbir şey yazılmaz.

## Sınırlamalar ve açık noktalar

- **Uid'nin kalıcı saklanması yok:** sonuç `CollectionUid` döner ama snapshot'a yazılmaz (şema sürümü 3, sonraki iş). O zamana kadar her çalıştırma koleksiyonu adı ve işaretiyle bulur.
- **Uzak değişiklik algılama yok:** `PUT` tamamen değiştirir; Postman'da elle yapılan düzenlemeler sessizce ezilir. Güncellemeden sonra tekrar okuyup doğrulama da henüz yok.
- Güncelleme ve oluşturma arasında başka biri aynı adlı koleksiyon yaratırsa (yarış) bu fark edilmez.
- Hata sınıflaması var ama otomatik yeniden deneme yok; geçici hatalar (`RateLimited`, `ServerError`, `NetworkError`, `Timeout`) `IsTransient` ile işaretlenir.
- CLI'a bağlı değil: bu adaptör henüz bir komuttan çağrılmıyor.
