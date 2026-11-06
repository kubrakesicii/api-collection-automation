# ADR 0003 — Sync güvenlik kapısı: dry-run, silme koruması, idempotency

Durum: Kabul edildi (T19) · Tarih: 2026-11-06 · Bağlı: [ADR 0001](0001-endpoint-identity.md), [ADR 0002](0002-endpoint-diff.md)

## Bağlam

Eksik veya hatalı bir analiz, endpoint'leri collection'dan **silinmiş** gibi gösterebilir (ör. çözülemeyen bir route, sözdizimi
hatalı bir dosya). Böyle bir sonuç Postman'a yazılırsa kullanıcının endpoint'leri yanlışlıkla silinir. Yayınlamadan önce her
sonucun aynı kapıdan geçmesi gerekir.

Bu task'ta Postman'a yazan bir kod **yoktur** (Phase 06). Kapı, yayınlanacak şeyi yazan **yerel** `sync` komutunun önüne konur:
collection dosyası ve baseline snapshot. Phase 06'daki Postman yüklemesi aynı `SyncDecision`'dan geçecek; karar `Blocked` ise
hiçbir şey yazılmaz/yüklenmez.

## Karar: SyncSafetyValidator (Core)

`SyncSafetyValidator.Validate(SyncSafetyInput, SyncSafetyOptions)` saf bir fonksiyondur; dosya ve Postman bilmez. Girdi: ADR 0002'deki
`ChangeSet`, keşif sorunları ve baseline collection'ın durumu. Çıktı: `SyncDecision` = `Outcome` (`NoChanges` / `Allowed` / `Blocked`),
`Violations`, `Notes`, `Plan` (Create/Update/Delete listesi) ve kullanılan limitler. **Fail-closed**: belirsiz olan her şey engeller;
devam etmek için kişinin açıkça söylemesi gerekir. Tüm ihlaller birlikte raporlanır (yalnızca ilki değil).

| İhlal | Ne zaman | Geçilebilir mi |
|---|---|---|
| `DiscoveryFailed` | Baseline'da endpoint var ama **hiç endpoint keşfedilmedi** (neredeyse kesin hatalı analiz) | Hayır |
| `DiscoveryIncomplete` | Keşif **failure** verdi: endpoint gizleyebilecek sorun | `--allow-incomplete-discovery` |
| `BaselineCollectionChanged` | Saklanan collection, baseline'ın hash'iyle uyuşmuyor (sonradan düzenlenmiş) | Hayır |
| `BaselineCollectionMissing` | Saklanan collection yok / okunamıyor | Hayır |
| `BaselineUnverifiable` | Baseline collection'a referans vermiyor veya başka bir dosyaya referans veriyor | Hayır |
| `MassDeletion` | Tek seferde silinecek endpoint sayısı/oranı limiti aşıyor | `--max-removed`, `--max-removed-percent` |

- **Eşikler** (varsayılan): en fazla **10 endpoint** ve baseline'ın **%30'u**; ikisinden biri aşılırsa (`>`) engellenir. Küçük bir silme
  (örn. 15'ten 4) geçer, yarısını silmek geçmez. Limitler yalnızca bilerek yükseltilir.
- **Failure ve warning ayrımı**: `EndpointDiscoveryResult.Failures`, `Warnings`'in alt kümesidir. *Failure* = endpoint gizleyebilir:
  çözülemeyen route şablonu, `[Route]` olup HTTP attribute'u olmayan action, desteklenmeyen route token'ı, aynı kimliği üreten iki action ve
  **sözdizimi hatalı kaynak dosyası** (parser hatalı kodu atlayıp sessizce endpoint kaybettirebilir). *Warning* yalnızca ayrıntıyı eksiltir
  (DTO tipi bulunamadı, `[FromForm]`, global yetkilendirme notu); engellemez, rapora not düşülür.
- **İlk sync**: baseline yok; hiçbir şey silinemez, her şey `Create` olur. Hiç endpoint yoksa `NoChanges`.
- Değişiklik yoksa bile bir ihlal varsa sonuç `Blocked`'tır: belirsiz bir analiz sessizce "sorun yok" sayılmaz.
- **Kapsam dışı**: collection adı / baseUrl gibi üretim seçeneklerinin değişmesi endpoint farkı sayılmaz; yani yalnızca onlar değişirse
  `NoChanges` çıkar ve dosyalara yazılmaz.

## Karar: dry-run

`sync --dry-run` kararı ve planı yazar (`Sync decision: ALLOWED (dry run)`), **collection'a ve baseline'a hiçbir şey yazmaz**.
`--report` (metin), `--plan` (karar JSON'u) ve `--changes` (ChangeSet JSON'u) rapor dosyaları dry-run'da ve engellenen çalıştırmada da
yazılır; çünkü kararı anlatırlar, yayınlanacak şeyi değiştirmezler. `Blocked` ise (dry-run dahil) çıkış kodu **3**'tür.

## Karar: idempotency ve atomik yazma

- Karar `NoChanges` ise **hiçbir dosya yazılmaz**, dosyalara dokunulmaz (içerik ve değişiklik zamanı aynı kalır). Aynı kaynakla ikinci
  çalıştırma bu yüzden hiçbir değişiklik üretmez; biçimlendirme, kod taşıma ve sıra değişikliği de (ADR 0001) değişiklik sayılmaz.
- Uygulama sırasında `AtomicFileWriter` her dosyayı önce geçici (`*.tmp`) dosyaya yazar, hepsi yazıldıktan sonra yerine taşır; yazma
  sırasında hata olursa mevcut dosyalar değişmez ve geçici dosyalar silinir. **Baseline en son** taşınır: baseline collection'ı hash ile
  gösterdiği için iki taşıma arasında bir çökme, sessiz bir tutarsızlık değil, bir sonraki çalıştırmada `BaselineCollectionChanged` olarak
  yakalanan bir tutarsızlık bırakır.
- Başarılı bir sync'ten sonra baseline yeni durumu gösterir; bu yüzden hemen ardından `diff` de `sync` de değişiklik görmez.

## Kullanım

```
dotnet run --project src/ApiCollection.Cli -- sync sample-api/SampleApi --baseline store/api.snapshot.json --out store/api.postman_collection.json --examples config/examples.sample-api.json --dry-run
```

Çıkış kodları: `0` başarılı / değişiklik yok / dry-run, `1` hata (okunamayan baseline, hatalı argüman, yazma hatası), `3` kapı engelledi.
`generate`/`diff`'teki `2` (strict / changes) `sync` için kullanılmaz.

## Sınırlamalar ve açık noktalar

- Postman Cloud'a yazma henüz yok; kapı, Phase 06'daki yüklemenin geçeceği yerdir. Orada ayrıca "yükleme sonrası tekrar okuma/doğrulama" gerekecek.
- Yedek / geri alma yoktur: `sync` başarılı olunca yerel collection ve baseline'ın üzerine yazar (Git geçmişi dışında önceki hâl saklanmaz).
- Tek bir endpoint'in silinmesi limit altında serbesttir; "her silme için onay" gibi daha sıkı bir politika `MaxRemovedCount = 0` ile elde edilir.
- Keşif, yalnızca attribute routing ve statik kaynak analizidir; tanınmayan bir routing biçimi (conventional routing, minimal API) hiç
  uyarı vermeden endpoint'siz kalabilir. Böyle bir API için ilk keşifte endpoint sayısı beklenenden azsa bu kapı onu yakalayamaz.
