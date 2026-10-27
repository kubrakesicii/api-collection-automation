---
name: implementation
description: Implement requested features, bug fixes and refactors in this .NET repository, including focused verification and a change summary.
---

# Implementation

Read `CLAUDE.md` and `.claude/skills/project-conventions/SKILL.md`. Inspect the relevant code and current working changes before editing; preserve unrelated user work.

Use the supplied plan and acceptance criteria when available. For a small task, derive a short implementation approach from the request and proceed. Revisit the plan if repository evidence contradicts it; clarify only decisions that materially affect the requested outcome.

Implement the smallest complete change within the authorized scope. Keep projects on net8.0, respect the documented dependency direction, and use Cli for composition. Add packages or abstractions only when the task needs them. Keep credentials in environment variables or user secrets, and use synthetic data in fixtures.

For behavior changes, add or update focused tests where they provide meaningful regression protection. Use `tests/` for test projects and `fixtures/` for reusable scenarios. Do not introduce a test project solely to validate documentation or harness edits.

Validate according to the affected surface:
- For .NET changes, run `dotnet restore ApiCollection.sln` when dependencies or restore state require it, then `dotnet build ApiCollection.sln --no-restore`.
- When test projects exist, run the relevant tests; use `dotnet test ApiCollection.sln --no-build` only after building the relevant configuration.
- For documentation or harness changes, check references, instruction consistency and file structure instead of building unrelated code.

Inspect the final diff for scope, accidental secrets and unintended generated files. Report the resulting behavior, actual verification results and remaining limitations. Distinguish checks that passed from checks that could not run; empty test directories do not provide test coverage. Commit, push or perform cloud writes only when the task authorizes them.
