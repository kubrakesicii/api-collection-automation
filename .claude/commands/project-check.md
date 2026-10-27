---
description: Check the .NET solution and summarize results
---
Read CLAUDE.md. Run dotnet restore ApiCollection.sln and dotnet build ApiCollection.sln --no-restore.
If test projects exist, run dotnet test ApiCollection.sln --no-build.
Report failures and whether actual tests were discovered. Do not implement features or contact Postman.