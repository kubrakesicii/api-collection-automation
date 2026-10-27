# API Collection Automation

.NET 8 proje iskeleti. API endpoint analizi ve Postman senkronizasyonu sonraki asamalarda eklenecektir.

## Yapi
- .github/workflows/: build, test ve Postman sync pipeline dosyalari icin yer.
- .claude/: Claude Code ayarlari, komut, skill ve reviewer ajani.
- sample-api/SampleApi/: Swagger icermeyen ornek Web API.
- src/ApiCollection.Cli/: CLI giris noktasi ve gelecekteki komutlar.
- src/ApiCollection.Core/: Models, Interfaces, Diff, Policies.
- src/ApiCollection.Analysis/: ControllerDiscovery, Routing, DtoExtraction, ValidationExtraction, Authentication.
- src/ApiCollection.Postman/: Generation, Serialization, CloudApi.
- src/ApiCollection.Infrastructure/: Configuration, FileSystem, Logging.
- tests/: Unit, Integration, Regression (henuz test projesi yok).
- fixtures/: Controllers, DTOs, Routes.
- docs/: architecture, adr, roadmap, tasks.
- config/: gizli bilgi icermeyen ayarlar.

## Kurulum ve kontrol
.NET SDK 8.0.426 gerekir (global.json).

```sh
dotnet restore ApiCollection.sln
dotnet build ApiCollection.sln --no-restore
dotnet run --project sample-api/SampleApi
```

Controller siniflari bostur; henuz endpoint veya is mantigi yoktur.
CLI yalnizca giris noktasi icerir. NuGet test paketleri ve test projeleri henuz eklenmemistir.
Claude Code talimatlari CLAUDE.md dosyasindadir; /project-check komutu derlemeyi kontrol eder.