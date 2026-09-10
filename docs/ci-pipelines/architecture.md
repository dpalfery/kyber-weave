---
id: ci-pipelines/architecture
title: CI Pipelines architecture
doc-type: architecture
status: current
component: CI Pipelines
source-root: src/KyberWeave.Core/Diagnostics
owner: dpalfery
last-reviewed: 2026-08-15
keywords:
  - diagnostic
  - engine
  - sarif
code-refs:
  - Diagnostic
  - DiagnosticReport
---

# CI Pipelines architecture

Every gate in Kyber-Weave — [documentation](../docgraph/governance.md),
[skills](../context-hygiene/skills.md), and [agents](../context-hygiene/agents.md) — reports through one diagnostic
engine. That is what lets multiple artifact classes share a single CI story, one
SARIF upload, and one suppression mechanism.

## One diagnostic shape

```csharp
Diagnostic(Code, Severity, Message, Subject, Path, Hint)
```

| Field | Purpose |
|---|---|
| `Code` | Stable `KW-*` rule id. Never reused, never renumbered. |
| `Severity` | `Info` < `Warning` < `Error` < `Critical` |
| `Message` | What is wrong, stated as a fact |
| `Subject` | The artifact: a document id, skill name, or agent role |
| `Path` | File the finding anchors to |
| `Hint` | What to do about it — often a nearest-match suggestion |

`DiagnosticReport` accumulates these and exposes the counts every command's exit code is
computed from.

## Stable rule ids

A rule id is a **permanent identifier**, not a line number. It is what a suppression
comment, a SARIF baseline, and a code-scanning alert all key on, so changing one silently
un-suppresses findings across every host repository. Ids are segmented by feature so the
prefix alone says which branch produced a finding:

| Prefix | Produced by |
|---|---|
| `KW-DOC-SPEC-*`, `KW-DOC-DRIFT-*` | [DocGraph](../docgraph/governance.md) |
| `KW-SKILL-SPEC-*`, `KW-SKILL-LINT-*`, `KW-SKILL-SEC-*` | [Skill governance](../context-hygiene/skills.md) |
| `KW-AGENT-SPEC-*`, `KW-AGENT-SYNC-*`, `KW-AGENT-LINT-*`, `KW-AGENT-SEC-*` | [Agent governance](../context-hygiene/agents.md) |
| `KW-PARSE-*`, `KW-CONFIG-*` | Shared parsing and configuration |

The complete list is in the [rule reference](rule-reference.md).

## Output formats

Every analysis command takes `--format`:

| Format | For |
|---|---|
| `table` | humans at a terminal (default) |
| `json` | scripting and custom gates |
| `sarif` | GitHub code scanning, and any SARIF-consuming tool |
| `markdown` | pull-request comments and job summaries |

`--no-info` drops informational findings, which is usually what you want in CI, where an
`Info` row is noise rather than signal.

## Severity gating

Exit codes are deliberately **not uniform across branches**, because the branches carry
different risk:

| Command | Non-zero when |
|---|---|
| `docs validate`, `docs drift` | any error |
| `skill validate`, `skill lint` | any error |
| `agent validate`, `agent sync-check` | any error |
| `skill scan`, `agent scan` | any critical — raise with `--fail-on warning\|error` |
| `skill route` | accuracy below `--min-accuracy` |
| `squad install`, `squad update`, `squad pack` | configuration, prerequisite, integrity, transaction, or pack failure (exit code 1 or 2) |
| `squad uninstall`, `squad doctor` | prerequisite, transaction, or tool failure (exit code 1) |
| `squad status` | deployment absent, partial, or drift detected (exit code 1) |

Scanning defaults to gating on `Critical` alone so that adopting it does not immediately
break every host build; tighten it deliberately once the baseline is clean. For lifecycle operations,
Squad exit codes follow deterministic 0/1/2 semantics (see [Kyber-Squad onboarding](../kyber-squad/onboarding.md)).

Configuration failures surface as `KW-CONFIG-001` rather than a stack trace, so a
malformed `kyber-weave.yml` fails the same structured way everything else does.

## Related

- [Rule reference](rule-reference.md) — every id, severity, and meaning
- [Workflow runbook](workflows-runbook.md) — copy-ready GitHub Actions gates
- [Documentation governance](../docgraph/governance.md) — the `KW-DOC-*` producers
- [Skill governance](../context-hygiene/skills.md) — the `KW-SKILL-*` producers
- [Agent governance](../context-hygiene/agents.md) — the `KW-AGENT-*` producers
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — multi-harness deployment engine
