---
name: project-conventions
description: Apply repository conventions when adding or changing .NET projects and layers.
---
Read CLAUDE.md before changing code.
Target net8.0 and use the shared nullable and implicit-using settings.
Keep Core dependency-free; Analysis, Postman and Infrastructure reference Core.
Use Cli as the composition root. Keep SampleApi independent.
Place tests and fixtures in their designated root folders.
Do not add packages, endpoints or cloud behavior during skeleton-only tasks.
Validate relevant changes with dotnet build ApiCollection.sln.