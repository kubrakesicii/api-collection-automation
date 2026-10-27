## Phase 00 — Repository & Environment Setup (AI-assisted + Manuel)
- [ ] Local .NET 8 SDK, VS Code ve Claude Code kurulumu
- [ ] GitHub repository oluşturma
- [ ] Postman hesabı ve workspace oluşturma
- [ ] Postman API key oluşturma ve erişimi doğrulama
- [ ] Repo ve local secret yönetimini belirleme
Definition of Done: GitHub repository, Postman workspace ve local geliştirme ortamı hazır.

## Phase 01 — Project Bootstrap & Claude Harness (Agentic)
- [ ] Sample API projesini oluşturma
- [ ] Automation solution ve katmanları oluşturma
- [ ] CLAUDE.md ve mimari/güvenlik kurallarını tanımlama
- [ ] İlk skill ve reviewer subagent'ını oluşturma
- [ ] Build ve test komutlarını doğrulama
Definition of Done: Claude Code proje sınırlarını biliyor; solution build edilebiliyor.

## Phase 02 — Endpoint Discovery Engine (Agentic)
- [ ] Roslyn ve Reflection için küçük PoC karşılaştırması
- [ ] Analiz motorunu seçme
- [ ] Controller, action, HTTP method ve route keşfi
- [ ] Path/query/body parametrelerini çıkarma
- [ ] Endpoint'lere kararlı kimlik üretme
- [ ] Keşif başarısızlıklarını raporlama
Definition of Done: Örnek API'deki endpoint'ler doğru ve tekrar üretilebilir biçimde keşfediliyor.

## Phase 03 — Request & Response Metadata (Agentic)
- [ ] DTO property ve nested model çözümleme
- [ ] JWT Bearer authentication tanımlama
- [ ] FluentValidation statik kural keşfi
- [ ] Var olan gerçekçi örnekleri çıkarma
- [ ] Eksik örnekler için açık durum raporu
- [ ] Authorization ve schema testleri
Definition of Done: Request URL, params, body ve elde edilebilen örnek bilgiler tutarlı.

## Phase 04 — Postman Collection Generator (AI-assisted + Agentic Review)
- [ ] Postman Collection v2.1 veri modeli ve serializer
- [ ] Controller bazlı folder yapısı
- [ ] baseUrl ve token placeholder'ları
- [ ] Geçerli JSON üretimi
- [ ] Postman import ve schema testleri
Definition of Done: Oluşturulan collection Postman'a import edilebiliyor.

## Phase 05 — Diff & Safe Synchronization (Agentic)
- [ ] Önceki collection ile yeni çıktıyı karşılaştırma
- [ ] Added/Modified/Removed/Unchanged sınıflandırma
- [ ] Endpoint kimliği ve rename davranışı
- [ ] Eksik keşifte silme işlemini engelleme
- [ ] No-change senaryosunda yayın yapmama
- [ ] Dry-run, idempotency ve backup/rollback kontrolleri
Definition of Done: Ekleme, değiştirme, silme ve değişiklik yok senaryoları güvenli.

## Phase 06 — Postman Cloud Integration (Agentic)
- [ ] Postman API adapter'ını geliştirme
- [ ] Collection ilk kez oluşturma
- [ ] Mevcut collection'ı okuma ve güncelleme
- [ ] Network/auth/limit hata yönetimi
- [ ] Güncelleme sonrası tekrar okuma ve doğrulama
- [ ] İlk gerçek Postman senkronizasyon testi
Definition of Done: Local CLI, Postman Cloud'daki aynı collection'ı güncelleyebiliyor.

## Phase 07 — GitHub Actions CI/CD (AI-assisted + Agentic Review)
- [ ] GitHub Actions workflow tanımlama
- [ ] Build ve test adımları
- [ ] GitHub secrets ve workspace/collection ID konfigürasyonu
- [ ] Automation CLI çalıştırma
- [ ] Başarısız senkronizasyon durumunda pipeline'ı fail etme
- [ ] İlk uçtan uca push testi
Definition of Done: Push sonrası pipeline çalışıyor ve Postman collection güncelleniyor.

## Phase 08 — Regression & Production Readiness (Agentic)
- [ ] Karmaşık route ve DTO edge-case testleri
- [ ] FluentValidation sınırlamalarının raporlanması
- [ ] Silme/rename/idempotency regression testleri
- [ ] Secret ve hassas veri taraması
- [ ] Log, diff raporu ve rollback senaryoları
- [ ] FE developer kullanım dokümanı
Definition of Done: Sistem test edilmiş, gözlemlenebilir ve güvenli şekilde tekrar çalıştırılabilir.