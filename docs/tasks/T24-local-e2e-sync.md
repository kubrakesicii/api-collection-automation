# T24: Yerel uçtan uca sync testi

Durum: komut hazır (`sync --publish`), senaryolar kullanıcı tarafından çalıştırılacak. Gerçek Postman'a yazma yalnızca bu komutu çalıştırdığında olur.

## Önkoşul
`postman-check` başarılı olmalı (API key ve workspace id user secrets ya da ortam değişkeninde; komut satırında asla):

```
dotnet run --project src/ApiCollection.Cli -- postman-check
```

## Komut
Çıktı klasörü repo ve Sample API dışında olmalı (Sample API içine yazmak reddedilir). Aşağıdaki klasör örnektir.

```
set E2E=%USERPROFILE%\api-e2e
dotnet run --project src/ApiCollection.Cli -- sync sample-api/SampleApi ^
  --baseline %E2E%\api.snapshot.json --out %E2E%\api.postman_collection.json ^
  --name "Sample API" --examples config/examples.sample-api.json --publish
```

- `--dry-run` ekle: Postman'dan yalnızca okur, planı gösterir, hiçbir şey yazmaz.
- `--workspace <id>`: workspace'i seçenekle verir (varsayılan: `POSTMAN_WORKSPACE_ID` / `Postman:WorkspaceId`).
- `--backup-dir <klasör>`: yedekler (varsayılan: `--out` yanında `backups`).
- `--no-rollback`: doğrulama başarısız olursa otomatik geri alma yapılmaz.

Çıkış kodları: 0 başarılı, 1 hata, 3 güvenlik kapısı engelledi, 5 senkronize edilmedi (Postman değişmedi ya da geri alındı),
6 Postman'daki koleksiyon yanlış olabilir (raporun "Recovery" adımlarını izle).

## Senaryolar
Her adımdan sonra Postman Desktop'ta koleksiyonu kontrol et.

| # | Sample API'de yapılan | Beklenen rapor | Postman'da beklenen |
|---|---|---|---|
| 1 | Hiçbiri (ilk çalıştırma) | `Postman sync: SYNCED`, hepsi create | "Sample API" koleksiyonu oluşur |
| 2 | Hiçbiri (tekrar) | `Sync decision: NO CHANGES`, `Postman sync: NO CHANGES` | Değişiklik yok, yedek alınmaz |
| 3 | Yeni endpoint ekle (Added) | `1 to create`, `SYNCED`, bir yedek dosyası | Yeni request görünür, diğerleri aynı |
| 4 | Bir endpoint'in sözleşmesini değiştir, örn. DTO'ya alan ekle (Modified) | `1 to update`, `SYNCED` | Aynı koleksiyonda request güncellenir |
| 5 | Bir endpoint'i sil (Removed) | `1 to delete`, `SYNCED` | Request kaybolur, eski hali yedekte |
| 6 | Hiçbiri (tekrar) | `NO CHANGES` | Değişiklik yok |

Silme sınırı (varsayılan 10 endpoint / %30) aşılırsa komut `BLOCKED` döner ve Postman'a dokunmaz; bilerek gerekiyorsa `--max-removed` / `--max-removed-percent` ile yükselt.

## Bilinen noktalar
- Yerel `--out` ve `--baseline` dosyaları yalnızca doğrulanmış bir yayından sonra güncellenir. Yayın başarısız ya da geri alınmışsa değişmez; komutu tekrar çalıştırmak aynı değişiklikleri yeniden hesaplar.
- Koleksiyon `--name` ile bulunur (uid saklanmaz). Workspace ya da ad değişirse id'ler değişir, yeni koleksiyon oluşur.
- `PUT` koleksiyon değişkenlerini üretilen varsayılanlarla değiştirir (Postman'da elle girilen `baseUrl` / `token` değerleri sıfırlanır).
- Gerçek Postman, gönderilenden farklı normalize ederse doğrulama yanlış fark bulup geri alabilir; rapor farkları yol olarak listeler (bkz. ADR 0005).
- Koleksiyon hiçbir zaman silinmez; yeni oluşturulan koleksiyon doğrulanamazsa rapor elle inceleme talimatı verir.

## Rapor
Sonuçlarını (her senaryo için komut çıktısı özeti, çıkış kodu, Postman Desktop'ta gördüğün) bu dosyanın altına ekle ya da bana ilet; ben E2E raporunu yazarım.
