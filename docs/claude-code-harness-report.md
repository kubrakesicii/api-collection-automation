# Claude Code ile geliştirme: harness, yöntem ve katkı raporu

Tarih: 2026-11-10 · Hedef okuyucu: projeyi inceleyen, benzer bir işi Claude Code ile yürütmek isteyen mühendis ve yöneticiler.

Bu rapor dosya dosya ne yapıldığını değil, projenin nasıl yürütüldüğünü anlatır: Claude Code'un rolü, projeye kurulan "harness" (rehber ve sınır katmanı), hangi adımın AI destekli, agentic ya da manuel ilerlediği, neyin işe yaradığı ve neyin eksik kaldığı. Sayılar ve durumlar rapor tarihindeki repo ve test çalıştırmalarından alınmıştır; ölçülmemiş bir verim iddiası yoktur.

## 1. Proje ve amaç
Bir ASP.NET Core API'sinin controller'larını kaynak koddan okuyup (Swagger/OpenAPI olmadan) Postman koleksiyonunu üreten ve Postman Cloud'daki koleksiyonla güvenli biçimde senkronize eden bir .NET 8 aracı. Uçtan uca hedef: koda push → GitHub Actions → Postman'daki koleksiyonun otomatik ve güvenli güncellenmesi.

### Katmanlar
Proje adları repodaki gibidir. Bağımlılık yönü tek yönlüdür: `Analysis`, `Postman` ve `Infrastructure` yalnızca `Core`'a bağlıdır; hepsini `Cli` birleştirir. Bu ayrım hem mimariyi hem de Claude'a verilen kuralları (aşağıda) belirledi.

| Katman | Sorumluluk | Neleri içerir |
|---|---|---|
| `SampleApi` | Analiz edilen, bağımsız örnek ASP.NET Core Web API (Swagger yok). Otomasyon projelerine hiçbir bağımlılığı yok; araç onu yalnızca kaynak kod olarak okur, derlemez ve çalıştırmaz. | Customers, Orders ve Products controller'ları, DTO'lar, bellek içi depo. |
| `ApiCollection.Core` | Alanın (domain) kalbi. Hiçbir proje ya da dış pakete bağımlı değil. | Endpoint tanımı modelleri; kararlı endpoint kimliği ve analiz snapshot'ı; diff motoru ve ChangeSet (Added/Modified/Removed/Unchanged); sync güvenlik kapısı (silme sınırı, eksik keşifte engelleme); yedek (backup) sözleşmesi. |
| `ApiCollection.Analysis` | Kaynak koddan endpoint çıkaran Roslyn tabanlı keşif (yalnızca sözdizimi analizi). | Controller ve action keşfi, route birleştirme, parametre ve DTO çözümleme, JWT/yetkilendirme bilgisi, doğrulama kuralları, örnek veri ve keşif hata/uyarı raporu. |
| `ApiCollection.Postman` | Core'daki modelleri Postman'a çeviren ve Postman ile konuşan adaptör. | Postman Collection v2.1 üretimi ve serializer (deterministik id'ler); Postman API istemcisi ve bağlantı kontrolü (`CloudApi`); güvenli sync orkestratörü: yedekle, yayınla, tekrar okuyup doğrula, gerekirse geri al; sır maskeleme. |
| `ApiCollection.Infrastructure` | Dış dünya ile temas: dosya sistemi ve yapılandırma. (Loglama bu katmanda planlıdır, henüz ayrı bir uygulaması yok.) | Snapshot, ChangeSet ve sync kararının JSON'a yazılıp okunması; atomik ve üzerine yazmayan dosya yazımı (yedekler dahil); örnek metadata yükleme; user secrets okuma. |
| `ApiCollection.Cli` | Composition root: katmanları birleştirir ve komutları sunar. | `discover`, `generate`, `diff`, `sync` (`--dry-run`, `--publish`) ve `postman-check` komutları; raporlar ve çıkış kodları. |

Destekleyici klasörler: `tests/` (her katman için ayrı test projesi), `fixtures/` (controller/DTO/route senaryoları), `docs/` (ADR'ler ve görev belgeleri), `config/` (gizli olmayan örnek yapılandırma), `.github/workflows/` (CI/CD).

## 2. Bu projede Claude harness nasıl kuruldu?
Harness, Faz 01'de ("Project Bootstrap & Claude Harness") iskelet projeyle birlikte kuruldu ve iki commit'te toplandı: önce proje yapısı, sonra `Claude harness | skills,subagents | is implemented`. Bundan sonraki fazların hepsi (keşif motorundan GitHub Actions'a kadar) bu tek kurulumun üzerinde yürüdü. Harness repo içinde sürümlenir; yani aynı kurallar her oturumda, her geliştiricide aynı şekilde yüklenir.

### Kurulan yapı
```
CLAUDE.md                              proje yönergeleri (her oturumda otomatik yüklenir)
.claude/
  settings.json                        boş ({}): izin listesi ya da hook yok
  commands/project-check.md            /project-check komutu
  skills/
    project-conventions/SKILL.md       ortak taban kurallar
    planning/SKILL.md                  /planning
    implementation/SKILL.md            /implementation
    review/SKILL.md                    /review
  agents/
    repository-explorer.md             salt okunur araştırma
    reviewer.md                        statik kod incelemesi
    qa-reviewer.md                     kabul kriteri doğrulama + yerel build/test
```

### `CLAUDE.md`: projenin sınırları
Beş bölümden oluşur ve harness'ın geri kalanı hep buna bağlanır:
- **Scope:** Başlangıç fazı "yalnızca iskelet"; endpoint keşfi, koleksiyon üretimi ve bulut senkronizasyonu **istenmedikçe** yazılmaz. Bu cümle, her fazın kullanıcı tarafından açıkça istenip kapsamının genişletilmesi düzenini kurdu.
- **Layout and dependencies:** Altı projenin sorumluluğu ve bağımlılık yönü (`Core` bağımsız; `Analysis`, `Postman`, `Infrastructure` yalnızca `Core`'a bağlı; `Cli` hepsini birleştirir; `SampleApi` bağımsız).
- **Working rules:** net8.0, Core'u adaptörlerden bağımsız tutmak, yalnızca istenen değişikliği yapmak (spekülatif soyutlama ve paket yok), token ve kimlik bilgisini asla commit'lememek, buluta yazma ve koleksiyon silmeyi açık yetkiye bağlamak, `SampleApi`'yi otomasyondan bağımsız tutmak.
- **Verification:** Repo kökünden `restore`, `build --no-restore`, `test --no-build` sırası. "Boş test klasörü test kapsamı sayılmaz" ve "kontroller doğru raporlanır" kuralları buradan geliyor.
- **Claude harness:** Skill'lerin ve subagent'ların ne zaman kullanılacağı, her ajanın rolü ve nasıl görev devredileceği (örnek istek cümleleriyle).

### Skill'ler
Hepsi önce `CLAUDE.md` ve `project-conventions`'ı okur; böylece kurallar tek yerde durur.

| Skill | Bu projedeki işlevi |
|---|---|
| `project-conventions` | Ortak taban: net8.0, ortak nullable ve implicit using ayarları, katman bağımlılık yönü, testlerin `tests/`, senaryoların `fixtures/` altında durması, iskelet fazında paket ya da endpoint eklememek, değişiklikleri `dotnet build ApiCollection.sln` ile doğrulamak. |
| `planning` | İsteği gözlenebilir kabul kriterlerine çevirir; mevcut davranışı, etkilenen projeleri ve bağımlılık sınırlarını çıkarır; doğrulanmış gerçeklerle varsayımları ayırır; en küçük tasarımı seçer. Çıktı: amaç ve kriterler, sıralı adımlar, testler, riskler ve kapsam dışı olanlar. Plan, yalnızca istenirse `docs/tasks/` altına kaydedilir. |
| `implementation` | Önce mevcut çalışma değişikliklerine bakar ve kullanıcının işini korur; en küçük tam değişikliği yapar; anlamlı yerde test ekler; değişen yüzeye göre doğrular (kod için build ve test, doküman için referans ve tutarlılık kontrolü); sonunda diff'i kapsam ve sızan sır açısından inceler. Commit, push ve bulut yazma yalnızca görev yetkilendiriyorsa yapılır. |
| `review` | Kapsamı belirler ve diff'i çevresindeki kodla birlikte inceler; bulguları ciddiyet sırasıyla (critical, high, medium, low), dosya ve satır, tetikleyici koşul, etki ve düzeltme yönüyle yazar; stil tercihlerini ve spekülatif bulguları dışarıda bırakır. Doğrulanmayan bir kontrolü "çalıştırıldı" diye raporlamaz. |

### Slash komutu
`/project-check`: `restore` ve `build`'i, test projesi varsa `test`'i çalıştırır. Başarısızlıkları ve **gerçekten test bulunup bulunmadığını** raporlar; özellik yazmaz ve Postman'a dokunmaz. Yani yeşil build'in test kapsamı anlamına gelmediği davranışı komutun içine yazılmış.

### Subagent'lar: yetkiyle ayrışan üç rol
| Subagent | Araçları | Ne zaman, neyi yapar |
|---|---|---|
| `repository-explorer` | Read, Glob, Grep | Plan, uygulama ya da hata ayıklama öncesi salt okunur araştırma. Komut çalıştıramaz ve dosya değiştiremez. İlgili dosyaları, çağrı akışını ve bağımlılık yönünü kanıtla (satır referansıyla) döndürür; uygulanmış davranışla boş klasör ve yol haritasını ayırır. |
| `reviewer` | Read, Glob, Grep | Hafif statik inceleme: net8.0 hedefleri, bağımlılık yönü, görev kapsamı ve sızan sır. Komut çalıştırmaz, dosyaları düzenlemez. |
| `qa-reviewer` | Read, Glob, Grep, **Bash** | Tamamlanmış değişikliği kabul kriterlerine karşı doğrular: her kriteri bir testle, kod incelemesiyle ya da "doğrulama boşluğu" olarak eşler ve yerel build ve testleri çalıştırır. Kaynak dosya düzenlemez, stage ya da commit yapmaz, araç kurmaz, kalıcı servis başlatmaz, Postman'a dokunmaz, buluta yazmaz, koleksiyon silmez. Atlanan ya da engellenen kontrolü "geçti" diye raporlamaz. |

Yetki tasarımı bilinçli: araştırma ve inceleme rolleri komut çalıştıramaz; komut çalıştırabilen tek rol (`qa-reviewer`) buluta ve dosya düzenlemeye kapalı. Böylece "doğrulayan" ile "değiştiren" ayrıldı.

### Kuralların davranışa yansıması
Harness kuralları bu projede somut kararlara dönüştü:
- "Bulut yazma açık yetki ister" → araçta silme işlemi hiç yok; Postman'a yazma ayrı bir `--publish` seçeneği ve (CI'da) onay kapısı arkasında.
- "Token commit'lenmez" → anahtar yalnızca ortam değişkeni ve user secrets'tan okunur, komut satırında kabul edilmez.
- "Kontroller doğru raporlanır" → başarısız ya da yapılamayan canlı testler (Postman 500) raporlarda açıkça belirtildi; test sayıları gerçek çalıştırmadan verildi.
- "Spekülatif soyutlama yok" → her task istenen kapsamla sınırlı kaldı, görev tamamlanınca sonraki göreve kullanıcı isteyince geçildi.

### Harness'ın bugünkü durumu
- **Faz 01'den beri değişmedi:** `CLAUDE.md` ve `.claude/` yalnızca iki commit'te kuruldu. `CLAUDE.md`'deki "Current phase: skeleton only" ifadesi proje çok ilerledikten sonra da duruyor. "İstenmedikçe" şartı sayesinde yanıltıcı olmadı, ama güncel değil ve güncellenmeli.
- **Ayar katmanı boş:** İzin listesi ve hook tanımlı değil.
- **Kullanım kanıtı sınırlı:** Kurallar her oturumda uygulandı, ama bu oturum kayıtlarında skill ve subagent'ların açıkça çağrıldığı görünmüyor (bkz. Bölüm 6).
## 3. Çalışma döngüsü
Her task aşağıdaki döngüyle ilerledi (Türkçe iletişim, T-numaralı görevler):

1. **Görev:** kullanıcı görevi (amaç, yapılacak iş, çıktı, kabul kriteri) yapıştırır.
2. **Plan / karar:** gerekirse plan modu ya da soru; belirsizlik ve riskler baştan söylenir.
3. **Uygulama:** kod, birim ve entegrasyon testleri, mümkünse sahte bir Postman ile uçtan uca senaryo.
4. **Doğrulama:** `restore` / `build` / `test`; kural bozulunca testin kırıldığını gösteren mutasyon kontrolleri.
5. **Belgeleme:** karar kayıtları (ADR), kullanım ve çalıştırma kılavuzları.
6. **Rapor:** Türkçe özet, bilinen riskler ve doğrulanamayanlar açıkça yazılır.
7. **İnsan adımı:** commit mesajı sohbette hazırlanır; commit, push, onay ve gerçek ortam işlemleri kullanıcıdadır.

## 4. Adımların yürütülme biçimi
Etiketler proje yol haritasındaki (`checklist.md`) fazlardan alınmıştır. "Gerçekleşen" sütunu o fazda fiilen olanı gösterir.

| Faz | Etiket | Gerçekleşen | Durum |
|---|---|---|---|
| 00 Ortam ve hesaplar | AI destekli + manuel | Kullanıcı Postman hesabı, workspace, API key ve GitHub repo'sunu kendisi açtı; AI, secret'ların nerede tutulacağını (user secrets) ve erişimin nasıl doğrulanacağını (`postman-check`) belirledi. | Tamam |
| 01 İskelet ve harness | Agentic | Çözüm ve katmanlar, `CLAUDE.md`, skill'ler, subagent'lar, build/test doğrulaması. | Tamam |
| 02 Endpoint keşfi | Agentic | Roslyn ve Reflection karşılaştırması (yalnızca analiz) ve Roslyn seçimi; controller/route/parametre keşfi, kararlı endpoint kimliği, keşif hata raporu. | Tamam |
| 03 İstek/yanıt metaverisi | Agentic | DTO ve iç içe model çözümleme, JWT, doğrulama kuralları, örnek verisi. | Tamam |
| 04 Koleksiyon üretici | AI destekli + agentic inceleme | Postman v2.1 üretimi ve serializer, klasör yapısı, şema ve içe aktarma kontrolleri. Kullanıcı CLI çıktısını ve içe aktarmayı kendi gözüyle denedi. | Tamam; canlı içe aktarma (Newman) kontrolü açık |
| 05 Diff ve güvenli sync | Agentic | Added/Modified/Removed/Unchanged diff, eksik keşifte silme engeli, no-change'de yayın yok, dry-run ve idempotency; yedek ve geri alma (T23). | Tamam |
| 06 Postman Cloud | Agentic | Adaptör (oluştur, oku, güncelle), hata yönetimi, yayın sonrası okuyup doğrulama, CLI'dan `sync --publish`. İlk gerçek yazma testi **henüz yapılmadı** (Postman API'sinin 500 hatası). | Kod tamam; canlı test bekliyor |
| 07 GitHub Actions | AI destekli + agentic inceleme | Workflow yazıldı ve ilk çalıştırmalarla (Linux'ta test hatası, eksik secret) iyileştirildi; GitHub secret/variable tanımı kullanıcıda. Uçtan uca push testi (T27) kullanıcı tarafından yürütülecek. | Kısmen; canlı yazma bekliyor |
| 08 Regresyon ve üretim hazırlığı | Agentic | Henüz başlanmadı (FE kullanım dokümanı, ek regresyon, sır taraması dahil). | Bekliyor |

Genel dağılım: kod, test, tasarım kararı belgeleri ve tanı AI tarafında; hesaplar, anahtarlar, GitHub ayarları, commit/push, onaylar, Postman Desktop'ta gözle doğrulama ve gerçek yazma kararı insan tarafında kaldı. İkisinin kesiştiği yerlerde (örn. CI hatası) kullanıcı log/ekran görüntüsü getirdi, AI nedeni bulup düzeltti.

## 5. Verim sağlayan harness özellikleri
- **`CLAUDE.md` + `project-conventions`:** her oturumda aynı sınırlar (katman, kapsam, sır) yeniden anlatılmadan uygulandı. Oturumlar arası bağlam kaybında kurallar kayıp gitmedi.
- **Plan modu ve etkileşimli sorular:** büyük kararlar (Roslyn seçimi, proje referansı ekleme, state'in nerede tutulacağı) kod yazılmadan önce kullanıcıya gitti; yanlış yöne giden işin maliyeti azaldı.
- **"Dürüst rapor" kuralı:** Postman'ın 500 hatası, canlı testin yapılamaması ve Linux'ta düşen test gibi durumlar "tamamlandı" diye saklanmadı.
- **Sahte Postman ile test:** gerçek bulutu etkilemeden başarılı yayın, bozuk yayın, geri alma ve geri almanın başarısız olması senaryoları denendi. Test sayıları (rapor tarihinde, son çalıştırma): Core 125, Analysis 77, Infrastructure 47, Postman 195, Cli 120.
- **Mutasyon kontrolleri:** bir güvenlik kuralı bozulduğunda testin kırıldığı gösterilerek testlerin gerçekten koruduğu doğrulandı.
- **Karar kayıtları (ADR 0001-0005):** kararlar ve bilinen riskler kodla birlikte duruyor.
- **Güvenlik tasarımı:** key yalnızca ortam/user secrets'tan, komut satırında asla; silme işlemi hiç yok; yazma öncesi yedek ve sonrası doğrulama.
- **Web araması:** Postman API davranışı (örn. PUT'ın koleksiyonu tamamen değiştirmesi) resmi dokümandan teyit edildi.

## 6. Sınırlar ve dersler
- **CI, yerelde yakalanmayanı yakaladı:** bir test yalnızca Windows'ta anlamlı bir yol kullanıyordu; Linux'taki ilk workflow çalıştırması bunu ortaya çıkardı ve düzeltildi. Yerel Windows testi tek başına yeterli güvence değildi.
- **Dış servis belirsizliği:** Postman API'sinin aralıklı 500 hatası canlı doğrulamayı engelledi. Araç bunu güvenli şekilde ele aldı (yazmadı), ama canlı kabul kriteri hâlâ açık.
- **Subagent'lar tanımlı ama bu kayıtta kullanılmadı:** bu oturumda görünen işlerde inceleme doğrudan yapıldı; `qa-reviewer`/`reviewer`/`repository-explorer` kullanımı önceki oturumlar için teyit edilmedi.
- **Ayar katmanı boş:** `settings.json` içinde izin listesi ve hook yok; tekrarlayan onay istemleri azaltılmadı.
- **İnsan hijyeni:** bir ekran görüntüsünde API key'in bir kısmı göründü ve key'in yenilenmesi gerekti. Harness bunu engelleyemez; ekran görüntüsü paylaşımı kuralı gerekir.
- **Uzun oturumlar:** bağlam sıkıştırması oldu; `CLAUDE.md`, ADR'ler ve görev belgeleri sayesinde süreklilik korundu.

## 7. Öneriler (uygulanmadı, yalnızca öneri)
1. Her faz sonunda `qa-reviewer` ile bağımsız doğrulama istemek (Faz 07 için "agentic inceleme" etiketi bunu bekliyor).
2. `.claude/settings.json`'a güvenli, salt okunur komutlar için izin listesi eklemek.
3. Hook: commit öncesi `PMAK-` ve benzeri anahtar desenlerini tarayan bir kontrol.
4. Commit mesajı üretimi için küçük bir skill (kullanıcı daha önce bunu sormuştu).
5. `.gitignore` ve çalışma dosyaları: yerel deneme çıktıları (`plan.json`, `changes.json`) repoya girmesin.
6. Faz 08'i tamamlamak: sır taraması, regresyon testleri ve FE geliştirici kullanım dokümanı.
7. Canlı Postman doğrulaması (Faz 06/07) tamamlanınca bu raporun "Durum" sütununu güncellemek.
