---
name: planning
description: Plan a requested feature, refactor or bug fix in this repository before implementation. Use for architecture decisions, task breakdown and implementation plans.
---

# Planning

Read `CLAUDE.md` and `.claude/skills/project-conventions/SKILL.md`, then inspect the relevant source files and existing documentation. Treat the repository as a skeleton until the requested task explicitly expands it.

Turn the request into observable acceptance criteria. Identify current behavior, affected projects and dependency boundaries. Separate verified facts from assumptions; ask only about missing information that materially changes the solution.

Choose the smallest design that satisfies the request. Identify any package, public contract, configuration or cloud-operation changes and explain why they are needed. Keep Core independent and SampleApi separate from the automation projects.

Produce a plan containing:
- The intended behavior and acceptance criteria.
- Ordered implementation steps with affected files or projects.
- Relevant tests and verification commands, including prerequisites.
- Material risks, unresolved decisions and explicit exclusions.

For a planning-only request, return the plan without editing implementation files. Save a plan under `docs/tasks/` only when requested. If implementation is already authorized, continue into the implementation workflow without adding a separate approval gate.
