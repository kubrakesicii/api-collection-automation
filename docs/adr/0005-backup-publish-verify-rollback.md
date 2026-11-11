# ADR 0005: Yedekle, yayınla, doğrula, geri al

Durum: Kabul edildi (T23) · Tarih: 2026-11-06 · Bağlı: [ADR 0003](0003-sync-safety.md), [ADR 0004](0004-postman-collection-adapter.md)

Hedef okuyucu: Postman'a yazan akışı değiştirecek ya da işletecek geliştirici.

## Bağlam

`PUT /collections/{id}` koleksiyonu tamamen değiştirir. Yanlış ya da yarım bir yazma, koleksiyonun eski halini geri getirilemez biçimde
kaybettirebilir. Bu yüzden her güncelleme yazmadan önce yedeklenir, yazıldıktan sonra yeniden okunarak doğrulanır ve doğrulanamazsa geri alınır.
Geri alma da başarısız olabilir; bu durum gizlenmez, raporlanır.

## Akış (`PostmanSyncOrchestrator`)

1. **Preflight** (hiçbir ağ çağrısı yok): karar `Allowed` mı, karar ve ChangeSet aynı özeti taşıyor mu, koleksiyon ChangeSet'teki endpoint'lerle
   uyuşuyor mu, tüm item'larda id var mı, `IdNamespace` doğru mu.
2. **Plan** (salt okunur): workspace erişimi, koleksiyonun var olup olmadığı. `DryRun` burada biter.
3. **Yedek** (yalnızca güncellemede): mevcut koleksiyon okunur, sır benzeri değerler `REDACTED` ile değiştirilir, SHA-256 ile dosyaya yazılır ve
   geri okunarak doğrulanır. **Yedek saklanamazsa hiçbir şey yazılmaz** (`Failed / Untouched`).
4. **Yayın**: bilinen uid ile `PUT` (yoksa ad ile bulunur; ilk kez ise `POST`).
5. **Doğrulama**: koleksiyon yeniden okunur ve `CollectionProjection` (ad, yöntem, URL, gövde, header, auth, değişken, klasör yapısı)
   ile beklenenle karşılaştırılır. Postman'ın kendi eklediği alanlar (uid, updatedAt, id'ler) fark sayılmaz.
6. **Geri alma** (yalnızca güncellemede, doğrulama *başarısızsa*): bellekteki **özgün** (redakte edilmemiş) koleksiyon `PUT` ile geri yazılır,
   yeniden okunur ve yedekle karşılaştırılır.
7. **Rapor**: sonuç, `RemoteState`, adımlar, yalnızca yol içeren farklar ve numaralı kurtarma talimatları. Değer yazılmaz.

## Kararlar

- **Silme yok.** Yeni oluşturulan koleksiyon doğrulanamazsa geri alınamaz (silme yetkisi yok); rapor kullanıcıya elle silme/inceleme talimatı verir.
- **Geri alma kaynağı bellektir, dosya değil.** Dosyadaki yedekte sırlar redakte olduğundan, otomatik geri alma gerçek değerleri bellekten yazar.
  Dosyadan `RestoreAsync` yalnızca `acceptRedactedValues: true` ile ve placeholder'ların yazılacağı bilinerek çalışır.
- **Kanıt yoksa geri alma yok.** Doğrulama okunamazsa (`VerificationInconclusive`) otomatik geri alınmaz; durum `Unknown` ve kullanıcı dikkati gerekir.
- **Yayın hatası da yeniden okunarak değerlendirilir**: yanıtı kaybolan ama uygulanmış bir yazma `Synced`, yarım kalmış bir yazma `RolledBack` olur.
- **Geri alma hataları ayrı raporlanır**: istek reddedildi (`Failed`), kabul edildi ama içerik eski hale gelmedi (`RollbackFailed / Divergent`),
  kontrol edilemedi (`Unknown`). İsteği hata dönen ama içeriği geri gelmiş geri alma `RolledBack` sayılır.
- Yalnızca `GET`, `POST`, `PUT` kullanılır; API anahtarı yalnızca header'dadır, hiçbir mesajda/yedekte/raporda yer almaz.

## Bilinen riskler

- **Normalizasyon**: gerçek Postman bazı alanları değiştiriyorsa doğrulama yanlış fark bildirebilir. Sonuç güvenli taraftadır (geri alma),
  fakat güncellemeleri engelleyebilir. Gerçek Postman ile doğrulanmadı; ilk gerçek yayında rapor incelenmelidir.
- **Koleksiyon değişkenleri**: `PUT` değişkenleri de değiştirir; kullanıcının elle girdiği `token`/`baseUrl` değerleri üretilen varsayılanlarla
  değişir. Sır olanlar yedekte redakte olduğundan dosyadan tam geri yüklenemez.
- **Item id'leri karşılaştırılmaz (2026-11-11 düzeltmesi).** İlk gerçek yayında Postman, gönderdiğimiz klasör ve request id'lerini korumadı ve kendi
  id'lerini atadı; id'ye göre eşleyen doğrulama doğru koleksiyonu "60 fark" diye reddetti. `CollectionProjection` artık öğeleri bulundukları yere
  göre (`Klasör/Request`; aynı adlı kardeşler sırayla `Ad`, `Ad#2`) eşler. Gerçek bir kayıp, yeniden adlandırma ya da içerik farkı yine yakalanır
  (yeniden adlandırma eski yerde `missing`, yenisinde `unexpected` olarak görünür). Sahte Postman, id'leri değiştiren davranışı taklit eder.
- Postman'ın `PUT` ile id'leri korumasına ve yazma yetkisine ait varsayımlar sahada doğrulanmadı.
- Yarış: yedek ile yazma arasında başkası koleksiyonu değiştirirse bu fark edilmez (kilit yok).
- CLI `publish` komutu ve uid'nin snapshot'ta saklanması bu task'ta yok; gerçek Postman'a yazma ayrıca açık onay gerektirir.

## Doğrulama

`PostmanSyncOrchestratorTests` (32), `SecretRedactorAndProjectionTests` (10), `FileCollectionBackupStoreTests` (7). Hata enjeksiyonu: sahte Postman
yazmayı bozar, yanıtı kaybeder, geri almayı reddeder veya sessizce uygulamaz. Mutasyon: yedek hatasını yok saymak testi kırdı.
