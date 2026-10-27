---
name: review
description: Review a diff, commit or selected files for correctness, regressions, layer violations and secret exposure. Use when a code review or implementation assessment is requested.
---

# Review

Read `CLAUDE.md` and `.claude/skills/project-conventions/SKILL.md`. Establish the requested review scope and inspect the diff alongside surrounding code and callers. If no scope is supplied, review current tracked changes and relevant untracked source files; state that scope. If there are no changes, ask which files or commit to review.

Review without editing files unless fixes are also requested. Treat existing user changes as review input. The existing `.claude/agents/reviewer.md` agent may be used when delegation is requested or appropriate; do not require delegation to perform a review.

Prioritize concrete defects introduced or exposed by the change:
- Incorrect behavior, error handling, regressions and missing meaningful regression tests.
- Violations of net8.0 targets, Core independence, adapter dependency direction or SampleApi isolation.
- Credential exposure, unsafe handling of input and cloud writes or deletion beyond task authorization.
- Incomplete acceptance criteria or changes outside the requested scope.

Support each finding with a specific file and line, the triggering condition, its impact and a concise correction direction. Distinguish observed defects from assumptions and questions. Avoid speculative findings, style-only preferences and unrelated pre-existing issues unless they block the requested change.

Report findings first, ordered by severity: critical, high, medium, low. Follow with open questions and a brief verification summary. If no actionable defects are found, say so explicitly and note any material testing gaps. Never claim checks were run without evidence; a read-only reviewer must report execution limits rather than invent results.
