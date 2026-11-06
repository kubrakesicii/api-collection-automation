# Postman bağlantısı: API key ve workspace ID

Bu belge, automation CLI'ın Postman Cloud'a bağlanması için gereken iki değeri nerede tutacağınızı ve bağlantıyı nasıl
doğrulayacağınızı anlatır. Hedef okuyucu: projeyi yerel makinesinde çalıştıracak geliştirici.

## Nerede tutulur

| Değer | Yerel makinede (önerilen) | CI'da |
|---|---|---|
| Postman API key (**gizli**) | user secrets: `Postman:ApiKey` | `POSTMAN_API_KEY` ortam değişkeni |
| Workspace ID | user secrets: `Postman:WorkspaceId` | `POSTMAN_WORKSPACE_ID` ortam değişkeni (ya da `--workspace <id>`) |

**User secrets**, .NET'in standart `secrets.json` dosyasıdır. Repo klasörünün **dışında**, kullanıcı profilinizde durur:

- Windows: `%APPDATA%\Microsoft\UserSecrets\30c05448-c2e4-4e2a-8769-e0c3708d0c07\secrets.json`
- Linux/macOS: `~/.microsoft/usersecrets/30c05448-c2e4-4e2a-8769-e0c3708d0c07/secrets.json`

Dosya repoda olmadığı için yanlışlıkla commit edilemez. **Repo içine bir `secrets.json` (ya da `.env`) koymayın**; CLI zaten oradan okumaz.

Öncelik sırası: `--workspace` seçeneği, sonra ortam değişkeni, sonra user secrets. Her değer bağımsız çözülür. Ortam değişkeni doluysa
`secrets.json` hiç okunmaz.

Hiçbirini kaynak koda, `appsettings*.json`'a, `config/` altına, README'ye, test dosyalarına ya da commit mesajına yazmayın.
API key'i **komut satırı argümanı olarak da vermeyin**: CLI bunu bilerek kabul etmez (kabuk geçmişine ve süreç listesine sızar).

> **API key'i sohbete veya bir mesaja yapıştırmayın.** Yapıştırdıysanız Postman'da (Settings, API keys) o key'i silip yenisini oluşturun.

## User secrets'a yazma

**Yöntem 1: dosyayı elle düzenleyin (key kabuk geçmişine düşmez, önerilen):**

```powershell
$dir = "$env:APPDATA\Microsoft\UserSecrets\30c05448-c2e4-4e2a-8769-e0c3708d0c07"
New-Item -ItemType Directory -Force $dir | Out-Null
notepad "$dir\secrets.json"
```

Açılan dosyaya şunu yazıp kaydedin:

```json
{
  "Postman:ApiKey": "PMAK-...",
  "Postman:WorkspaceId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
}
```

**Yöntem 2: `dotnet user-secrets` aracı** (SDK ile gelir, ek paket gerekmez):

```
dotnet user-secrets set "Postman:ApiKey" "PMAK-..." --project src/ApiCollection.Cli
dotnet user-secrets set "Postman:WorkspaceId" "<workspace-id>" --project src/ApiCollection.Cli
dotnet user-secrets list --project src/ApiCollection.Cli
```

Dikkat: bu komutta key komut satırına yazıldığı için PowerShell geçmişine (`ConsoleHost_history.txt`) düz metin olarak kaydedilir. Tercihen
Yöntem 1'i kullanın. `list` değerleri **gösterir**; ekranı paylaşırken dikkat edin.

Dosya, profilinizdeki diğer programların da okuyabileceği **şifrelenmemiş** bir dosyadır. Yerel geliştirme için yeterlidir; paylaşılan
makinelerde ve CI'da ortam değişkeni (GitHub Actions secrets, Phase 07) kullanın.

## Ortam değişkeniyle ayarlama (CI veya tercih)

**Arayüz:** Başlat menüsüne "Edit the environment variables for your account" yazın, **New** ile `POSTMAN_API_KEY` ve `POSTMAN_WORKSPACE_ID` ekleyin.
Kullanıcı düzeyindeki değişken yalnızca **yeni açılan** programlarda görünür; VS Code'u kapatıp yeniden açın.

**PowerShell ile, key geçmişe düşmeden:**

```powershell
$key = Read-Host "Postman API key" -AsSecureString
[Environment]::SetEnvironmentVariable("POSTMAN_API_KEY", [System.Net.NetworkCredential]::new("", $key).Password, "User")
```

Key'i doğrudan komuta yazmayın (`SetEnvironmentVariable("POSTMAN_API_KEY", "PMAK-...")` ya da `$env:POSTMAN_API_KEY = "PMAK-..."`): geçmişe düşer.

Workspace ID, Postman'da workspace'inizin ayarlarında görünür (arayüz sürümüne göre yeri değişebilir). Adı değil, UUID biçimindeki kimliğidir.

## Bağlantıyı doğrulama

```
dotnet run --project src/ApiCollection.Cli -- postman-check
dotnet run --project src/ApiCollection.Cli -- postman-check --workspace <workspace-id>
```

Komut yalnızca **GET** istekleri gönderir (`/me`, ardından workspace verilmişse `/workspaces/{id}`); hiçbir şeyi oluşturmaz, değiştirmez veya silmez.
Anahtar çıktıda hiçbir zaman görünmez (yalnızca "var", uzunluğu ve **nereden okunduğu** gösterilir). Yönlendirmeler takip edilmez ve yalnızca `https` kullanılır.

Başarılı çıktı:

```
Postman connection check (read-only: only GET requests are sent)
  API key:         PMAK-… (64 characters) (from user secrets)
  Workspace id:    xxxxxxxx-... (from user secrets)
  Authentication:  OK, signed in as <kullanıcı adı>
  Workspace:       OK, "<workspace adı>" (<tür>)
Result: CONNECTED
```

### Çıkış kodları

| Kod | Anlamı |
|---|---|
| 0 | Bağlandı |
| 1 | Ayar eksik ya da bozuk (key yok, key bozuk kopyalanmış, workspace ID geçersiz, `secrets.json` okunamıyor, hatalı argüman). Hiçbir istek gönderilmedi. |
| 4 | Postman reddetti ya da ulaşılamadı |

### Hata nedenleri

| Sonuç | Anlamı | Ne yapmalı |
|---|---|---|
| `NotConfigured` | Key yok / workspace ID geçersiz / adres https değil | Key'i ayarlayın (yukarıya bakın) |
| `InvalidApiKey` | Key bozuk (boşluk, satır sonu) ya da Postman reddetti (HTTP 401) | Key'in güncel olduğunu kontrol edin; gerekirse yenisini oluşturun |
| `AccessDenied` | HTTP 403: key geçerli olabilir ama erişim izni yok | Key'in sahibini ve workspace paylaşımını kontrol edin |
| `WorkspaceNotFound` | HTTP 404: workspace yok ya da bu key'e görünmüyor | Ad değil ID girdiğinizden emin olun |
| `RateLimited`, `ServerError`, `NetworkError`, `Timeout` | Geçici olabilir | Biraz sonra tekrar deneyin; kurumsal proxy/TLS ayarlarını kontrol edin |
| `UnexpectedResponse` | Postman'ın göndermediği bir yanıt (yönlendirme, JSON olmayan gövde) | Proxy veya captive portal olabilir |

## Sınırlamalar

- Doğrulama yalnızca durum kodlarına ve yanıtın JSON nesnesi olmasına dayanır; `/me` ve `/workspaces/{id}` yanıtlarının alanları
  (`user.username`, `workspace.name`, `workspace.type`) isteğe bağlı okunur, yoksa yalnızca gösterilmez.
- Testler gerçek Postman'a bağlanmaz (sahte HTTP işleyicisi) ve gerçek `secrets.json`'a ya da ortam değişkenlerine dokunmaz.
  Gerçek anahtarla ilk doğrulamayı yukarıdaki komutla siz çalıştırırsınız.
- Bu komut collection'a **yazma yetkisini** sınamaz; yalnızca okuma erişimini doğrular. Yazma, ayrı ve açık onaylı bir adımdır.
