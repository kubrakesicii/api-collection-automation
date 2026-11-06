# T27: Push'tan Postman'a uçtan uca test

Durum: çalıştırma kılavuzu hazır; senaryolar kullanıcı tarafından çalıştırılır ve sonuçlar aşağıdaki rapor tablosuna yazılır.
Bu belge gerçek bir sonuç içermez. Her senaryo gerçek Postman workspace'ine yazar (S0 hariç, onay beklerken yazmaz), bu yüzden `postman` environment'ındaki onay (required reviewers) açık kalmalıdır.

## Önkoşullar
- [ ] `postman-check` başarılı (Workspace: OK). Postman API'si HTTP 500 veriyorsa başlama.
- [ ] GitHub'da `postman` environment'ı: secret `POSTMAN_API_KEY`, variables `POSTMAN_WORKSPACE_ID`, `POSTMAN_COLLECTION_NAME`; "Required reviewers" açık.
- [ ] Actions etkin; Settings > Actions > General > Workflow permissions bu workflow'un state branch'ine (`postman-sync-state`) yazmasına izin veriyor.
- [ ] Çalışma ağacı temiz: `changes.json` ve `plan.json` commit'e girmemeli (yerel denemelerden kalan dosyalar).

Her senaryo için kontrol: Actions > "Postman sync" run'ı (verify, publish, adımların durumu, özet sayfası, `postman-sync` artifact'ı) ve Postman'da koleksiyon. Koleksiyonu **Postman web'de** (web.postman.co) aç; Desktop kapalıyken de görünmesi kabul kriteridir. Bu sırada bilgisayarında CLI çalışmıyor olmalı.

## Not: S0, workflow'un kendi push'u
Workflow dosyasını içeren push zaten `.github/workflows/postman-sync.yml` yolunu tetikledi. Actions sekmesinde o run'ı gör: `verify` geçmeli, `publish` onay bekler. Postman çalışmıyorsa onaylama; reddet ya da iptal et.

## Senaryolar (sırayla; her birinden sonra bir sonrakine geç)
Değişiklikler `sample-api/SampleApi/Controllers/ProductsController.cs` üzerinde; her biri ayrı commit, `master`'a push.

| # | Değişiklik | Beklenen pipeline | Beklenen Postman |
|---|---|---|---|
| S1 İlk sync | Yok (S0 run'ı onaylandıysa bu o run'dır) | verify yeşil; publish onay sonrası `SYNCED`; `postman-sync-state` branch'i oluşur | "Sample API", 3 klasör, 15 request |
| S2 Added | `[HttpGet("count")] public IActionResult Count() => Ok(store.GetAll().Count());` ekle | `1 to create`, yedek artifact'ta, `SYNCED` | 16 request, `Products` altında yeni `GET /api/products/count` |
| S3 Modified | `CreateProductRequest`'e (ProductDtos.cs) yeni bir alan ekle | `1 to update`, `SYNCED` | Aynı koleksiyon (uid aynı), `POST /api/products` gövdesi güncel |
| S4 Removed | S2'de eklediğin `count` endpoint'ini sil | `1 to delete`, `SYNCED` | 15 request, `count` yok |
| S5 No-change | `README.md`'ye bir satır ekleyip push et (paths filtresi tetiklemez: run başlamamalı) sonra `workflow_dispatch` ile `dry_run=false` çalıştır | `NO CHANGES`, yazma yok, state değişmez | Hiçbir değişiklik |

## Hata senaryoları
| # | Nasıl tetiklenir | Beklenen | Temizlik |
|---|---|---|---|
| H1 Test başarısız | `master`'a kasıtlı kırık bir test commit'le (örn. bir `Assert.True(false)`) | `verify` kırmızı, `publish` **hiç çalışmaz** (skipped) | Commit'i revert et |
| H2 Silme sınırı | `OrdersController.cs`'i sil (5 / 15 endpoint, %33, sınır %30) | `verify` içinde "Safety validation" `BLOCKED` ile düşer, `publish` çalışmaz | Commit'i revert et |
| H3 Eksik konfigürasyon | Environment variable `POSTMAN_COLLECTION_NAME`'i geçici sil, `workflow_dispatch` (dry_run=false) çalıştır | `Sync with Postman` adımı "is not set" hatasıyla düşer, Postman'a istek gitmez | Variable'ı geri ekle |
| H4 Postman hatası | Doğal olarak Postman 5xx verirse ya da API key'i geçici olarak geçersiz yaparsan (secret'ı bozuk bir değerle değiştir) | `publish` `FAILED`, `Remote state: Untouched`, state branch değişmez, bir sonraki run aynı değişikliği yeniden dener | Secret'ı doğru key ile geri koy |
| H5 Paralel yazma | İki push'u birkaç saniye arayla gönder ve her ikisini onayla | İkinci `publish` birincinin bitmesini bekler; aynı anda iki yazma olmaz | Yok |

H1 ve H2'de `master`'a bozuk commit gitmesi, bozuk durumun Postman'a ulaşmadığını gösterir; ama kırmızı bir `master` bırakmamak için hemen revert et.

## Doğrulama kontrol listesi (her senaryo için)
- [ ] Run sonucu beklenen gibi (yeşil/kırmızı/skipped).
- [ ] Adım özeti (`verify` ve `publish` job özetleri) beklenen planı gösteriyor.
- [ ] Postman (web ve Desktop) beklenen durumda; koleksiyon uid'si değişmedi, kopya koleksiyon yok.
- [ ] Log'da API key, hiçbir yerde tam değeriyle görünmüyor (Run log'unu `PMAK-` için ara).
- [ ] `postman-sync-state` branch'inde snapshot ve koleksiyon dosyası son yayınla eşleşiyor.

## E2E rapor tablosu (sonuçlar buraya)
| Senaryo | Commit | Run bağlantısı | Pipeline sonucu | Postman'da görülen | Geçti mi |
|---|---|---|---|---|---|
| S1 | | | | | |
| S2 | | | | | |
| S3 | | | | | |
| S4 | | | | | |
| S5 | | | | | |
| H1 | | | | | |
| H2 | | | | | |
| H3 | | | | | |
| H4 | | | | | |
| H5 | | | | | |

Gözlemler / hatalar:

## Kabul kriteri
Push sonrası Postman'da (bilgisayardaki CLI çalıştırılmadan, Desktop kapalıyken web'den) doğru güncellenmiş koleksiyon görünür; S1-S5 geçer, H1-H4 beklenen gibi davranır.
