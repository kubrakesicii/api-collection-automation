# ADR 0002 — Endpoint diff motoru (ChangeSet)

Durum: Kabul edildi (T18) · Tarih: 2026-11-05 · Bağlı: [ADR 0001](0001-endpoint-identity.md)

## Bağlam

Yeni analiz sonucu ile mevcut Postman collection arasındaki farkı bulmamız gerekiyor: hangi endpoint eklendi, değişti,
silindi, değişmedi ve değişenlerde *ne* değişti. Sonraki fazlar (silme koruması, dry-run, senkronizasyon) bu sonuca göre karar verecek.

## Karar: "mevcut collection" = baseline snapshot + yanında saklanan collection

Karşılaştırma, ADR 0001'deki **baseline snapshot'a** göre yapılır. Snapshot, collection'ın üretildiği andaki endpoint
sözleşmelerini tutar ve collection dosyasının SHA-256'sını içerir; `diff` bu hash'i yanındaki collection dosyasıyla
karşılaştırıp uyuşmazlık veya eksiklik olursa **uyarı** verir ("bu fark snapshot'ı anlatır, o dosyayı değil").

Rastgele bir Postman collection'ını ayrıştırıp karşılaştırmıyoruz: collection'daki `:id` gibi yer tutucular route
constraint'lerini (`{id:int}`) kaybettiği için kimlik güvenilir biçimde geri kurulamaz. Postman Cloud'daki collection'ı okumak
Postman Cloud fazının (Phase 06) işidir; orada eşleştirme için item kimliği gerekecek.

## Karar: sözleşme olguları üzerinden ad koyarak diff

`ContractDiff` iki olgu kümesini (`EndpointContract`) karşılaştırır. Olgu anahtarı `varlık.öznitelik` biçimindedir; varlıklar:
`endpoint`, `parameters`, `parameter[konum:ad]`, `body`, `body.property[yol]`, `body.propertyOrder[üst]`, `response[durum]`,
`auth`, `validation[özellik:kural]`.

- Yalnızca bir tarafta olan **varlık** tek bir `Added`/`Removed` değişikliğidir (öznitelikleri özetlenir).
- İki tarafta olan varlıkta her farklı **öznitelik** için bir `Changed`/`Added`/`Removed` değişikliği çıkar (`Before`/`After` ile).
- Sıra olguları (`parameters.order`, `body.propertyOrder[..]`) yalnızca iki tarafta da varsa ve **ortak öğelerin göreli sırası**
  farklıysa raporlanır; parametre eklemek aynı zamanda "yeniden sıralama" sayılmaz.
- Çıktı yol sırasındadır (deterministik).

| Kategori | Neyi kapsar |
|---|---|
| `Naming` | controller / action adı (kimlik aynı) |
| `Route` | route'un yazılışı (harf, parametre adı); kimlik aynı |
| `Parameter` | path/query/header parametreleri, tipleri, zorunluluk, varsayılan, sıra |
| `RequestBody` | body tipi, content type, zorunluluk, property'ler (iç içe dahil) |
| `Response` | response durumları, tipleri |
| `Authentication` | `[Authorize]`/`[AllowAnonymous]`: zorunluluk, scheme, rol, policy |
| `Validation` | FluentValidation kuralları ve argümanları |
| `Example` | örnek değerler, belgelenmiş örnekler, literal varsayılanlar, "örneği eksik alanlar" |

## Karar: ChangeSet modeli

`ChangeSet` (makine tarafından okunur; `ChangeSetSerializer` ile JSON): `schemaVersion`, `summary` (added/modified/removed/unchanged),
`hasChanges`, `warnings` ve kimliğe göre sıralı `endpoints` listesi. **Her endpoint tam bir kez** görünür (değişmeyenler dahil).
Her `EndpointDiff`: `id`, `status`, adlar, `changes` (yalnızca Modified) ve `possibleRename` (yalnızca öneri; ADR 0001).

Zaman damgası, mutlak yol ve secret yoktur; aynı girdi her seferinde aynı çıktıyı üretir.

`warnings` sonuca ne kadar güvenileceğini söyler; ne yapılacağına karar vermez:
- baseline collection değişmiş / yok / snapshot collection'a referans vermiyor,
- keşif uyarıları (`Discovery: …`). Eksik bir keşif yüzünden bir endpoint **Removed** görünebilir; silme koruması (sonraki task)
  bu uyarıyı kullanacaktır.

## Kullanım

```
dotnet run --project src/ApiCollection.Cli -- diff sample-api/SampleApi --baseline fixtures/postman/sample-api.snapshot.json --examples config/examples.sample-api.json --changes changes.json --report diff.txt --exit-code
```

- Rapor stdout'a yazılır; `--report` aynısını dosyaya, `--changes` ChangeSet JSON'unu yazar. Hiçbir çıktı analiz edilen API'nin
  içine veya baseline'ın üzerine yazılamaz; baseline hiçbir zaman değiştirilmez.
- `--exit-code`: değişiklik varsa çıkış kodu 2 (CI için); dosyalar yine yazılır. Hata 1, temiz 0.
- Baseline okunamıyor, elle düzenlenmiş (parmak izi uyuşmuyor) veya kimlik kuralı/şema sürümü farklıysa komut hata verir; yeni baseline gerekir.

## Sınırlamalar ve açık noktalar

- Karşılaştırma yalnızca bizim ürettiğimiz baseline ile yapılır; Postman'da elle yapılmış düzenlemeler görünmez.
- Added/Removed/Modified'a göre ne yapılacağı (silme koruması, "değişiklik yoksa yayınlama", dry-run, yedek/geri alma) bu ADR'nin kapsamı dışındadır.
- Olgular örnek metnini içerir; örnek JSON'ları büyük olursa snapshot dosyası da büyür. Gerekirse örnekler özetlenebilir (şema sürümü 3).
- Yalnızca attribute routing; çift kimlik üreten action'lar diff'te de hata verir (`AnalysisSnapshot.Create`).
