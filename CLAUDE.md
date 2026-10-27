# Api Collection Automation

## Scope
.NET 8 solution for analyzing ASP.NET Core controllers and generating/synchronizing Postman collections.
Current phase: skeleton only. Do not implement endpoint discovery, collection generation, or cloud synchronization unless requested.

## Layout and dependencies
- sample-api/SampleApi: independent ASP.NET Core Web API without Swagger.
- src/ApiCollection.Core: domain models, interfaces, diff and policies; no project dependencies.
- src/ApiCollection.Analysis: discovery, routing, DTOs, validation and authentication; depends on Core.
- src/ApiCollection.Postman: generation, serialization and cloud adapter; depends on Core.
- src/ApiCollection.Infrastructure: configuration, filesystem and logging; depends on Core.
- src/ApiCollection.Cli: composition root; references the other src projects.
- tests/: future unit, integration and regression tests.
- fixtures/: future controller, DTO and route scenarios.
- docs/: architecture, ADRs, roadmap and tasks.
- config/: non-secret configuration examples.

## Working rules
- Keep all projects on net8.0.
- Keep Core independent of external adapters.
- Make only requested changes; avoid speculative abstractions and packages.
- Never commit tokens, credentials or real customer data. Use environment variables or user secrets.
- Cloud writes and collection deletion require explicit task authorization.
- Keep sample-api independent of the automation projects.

## Verification
Run from repository root:
- dotnet restore ApiCollection.sln
- dotnet build ApiCollection.sln --no-restore
- dotnet test ApiCollection.sln --no-build (once test projects exist)
Report checks accurately; an empty test directory is not passing test coverage.

## Claude harness
Use /project-check for local verification. The project-conventions skill describes layer rules.
Use the reviewer agent when a review is requested.