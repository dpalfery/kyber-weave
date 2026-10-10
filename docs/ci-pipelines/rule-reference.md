---
id: ci-pipelines/rule-reference
title: Rule reference
doc-type: reference
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-09-24
---

# Rule reference

Every rule id Kyber-Weave emits. Ids are stable and suitable for suppression and SARIF
baselines — see [CI Pipelines architecture](architecture.md) for why they never change.

## Documentation — [DocGraph](../docgraph/governance.md)

### Schema — `docs validate`

| Id | Severity | Meaning |
|---|---|---|
| `KW-DOC-SPEC-001` | Error | No frontmatter block, or unparseable YAML |
| `KW-DOC-SPEC-002` | Error | `doc-type`, `status`, `last-reviewed`, or an undeclared `technology` outside its vocabulary or format |
| `KW-DOC-SPEC-003` | Error | Required key missing or empty for this doc-type |
| `KW-DOC-SPEC-004` | Error | `component` or `owner` absent from the catalog |
| `KW-DOC-SPEC-005` | Error | `source-root` path does not exist |
| `KW-DOC-SPEC-006` | Error | Duplicate `id`, or reference to an unknown id |
| `KW-DOC-SPEC-007` | Error | `technology` on a document that is not a coding standard, or naming a different technology than its folder |

### Configuration registry — `docs validate`

Reported only once a repository has adopted the registry: its `AGENTS.md` carries the
generated block, or it declared `config-reg` entries of its own.

| Id | Severity | Meaning |
|---|---|---|
| `KW-CONFIG-REG-001` | Error | A registry property names a path that does not exist |
| `KW-CONFIG-REG-002` | Error | The rendered `AGENTS.md` block no longer matches configuration |

### Plan and todo lifecycle — `docs validate`

Reported only when the `plan-index` or `todo-index` registry property names a document in the corpus.

| Id | Severity | Meaning |
|---|---|---|
| `KW-DOC-LIFECYCLE-001` | Error | A `plan` document in the plans folder that the plan index does not reach by links inside that folder |
| `KW-DOC-LIFECYCLE-002` | Error | A `todo` document in the todo folder that the todo index cannot reach through links inside that folder |
| `KW-DOC-LIFECYCLE-003` | Error | A plan or specification still in its active folder — raised only by `docs validate --merge-ready` |

### Drift — `docs drift`

| Id | Severity | Meaning |
|---|---|---|
| `KW-DOC-DRIFT-001` | Error / Critical | `code-refs` symbol unresolved. Critical when the index itself is missing. |
| `KW-DOC-DRIFT-002` | Error | `api-endpoints` route matches no indexed route |
| `KW-DOC-DRIFT-003` | Warning | `source-root` exists but nothing beneath it is indexed |

### Analysis — `docs integrity-check`

| Id | Severity | Meaning |
|---|---|---|
| `KW-DOC-ANALYSIS-001` | Info / Warning | Duplicate cluster. Pending near duplicates inform; exact or high-confidence confirmed duplicates warn. |
| `KW-DOC-ANALYSIS-002` | Info / Error | Potential conflict. Only a high-confidence imported conflict verdict errors. |
| `KW-DOC-ANALYSIS-003` | Warning | Ambiguous terminology not fully explained by approved scoped senses. |
| `KW-DOC-ANALYSIS-004` | Operational Error | Malformed, nested, unknown, or cross-boundary ignore markup. |
| `KW-DOC-ANALYSIS-005` | Warning | CodeGraph unavailable; document relationships and bounded lexical search continue. |
| `KW-DOC-ANALYSIS-006` | Warning / Operational Error | Embeddings unavailable: warning in `prefer`, error in `required`. |

### Review and managed glossary

| Id | Severity | Meaning |
|---|---|---|
| `KW-DOC-REVIEW-001` | Operational Error | Verdict bundle is invalid/stale, or safe atomic persistence is unavailable. |
| `KW-DOC-GLOSSARY-001` | Operational Error | Configured managed glossary has invalid structure, status, definition, or scope. |

Analysis findings respect `docs integrity-check --fail-on`; operational errors always return
non-zero. See [analysis and review](../docgraph/analysis.md) for classifier and lifecycle
details.

## Skills — [Skill governance](../context-hygiene/skills.md)

| Id range | Tier | Meaning |
|---|---|---|
| `KW-SKILL-SPEC-001`…`-012` | Spec | Agent Skills open-format conformance. Mostly errors; `-007` and `-009` warn, `-010` informs. |
| `KW-SKILL-LINT-001`…`-006` | Routing | Description quality dimensions that reduce routing reliability |
| `KW-SKILL-LINT-007` | Routing | Description is an action summary rather than a trigger specification (Warning) |
| `KW-SKILL-LINT-008` | Routing | Description contains excessive filler phrases or unrouted verbosity (Warning) |
| `KW-SKILL-LINT-010` | Routing | **Name collision** — the only error in this tier |
| `KW-SKILL-LINT-011` | Routing | Description overlap between two skills |
| `KW-SKILL-REVIEW-001` | Review | Skill/agent review verdict payload is malformed or invalid (Error) |

### Skill security — `skill scan`

| Id | Meaning |
|---|---|
| `KW-SKILL-SEC-001` | Ignore-previous-instructions |
| `KW-SKILL-SEC-002` | Disregard-guidelines |
| `KW-SKILL-SEC-003` | System-prompt override |
| `KW-SKILL-SEC-004` | Persona hijack |
| `KW-SKILL-SEC-005` | Exfiltration phrasing |
| `KW-SKILL-SEC-006` | HTML comment concealment |
| `KW-SKILL-SEC-007` | Base64 blob |
| `KW-SKILL-SEC-008` | Sandbox bypass |
| `KW-SKILL-SEC-010` | `curl \| sh` |
| `KW-SKILL-SEC-011` | `wget \| sh` |
| `KW-SKILL-SEC-012` | `eval` of base64 |
| `KW-SKILL-SEC-013` | Destructive command |
| `KW-SKILL-SEC-020` | AWS key |
| `KW-SKILL-SEC-021` | GitHub token |
| `KW-SKILL-SEC-022` | Private key |
| `KW-SKILL-SEC-023` | Slack token |
| `KW-SKILL-SEC-024` | OpenAI key |
| `KW-SKILL-SEC-025` | Password assignment |
| `KW-SKILL-SEC-030` | Missing author |
| `KW-SKILL-SEC-031` | Missing version |
| `KW-SKILL-SEC-032` | Missing license |

## Agents — [Agent harness governance](../context-hygiene/agents.md)

| Id | Severity | Meaning |
|---|---|---|
| `KW-AGENT-SPEC-001` | Error | Missing name |
| `KW-AGENT-SPEC-002` | Error | Missing description |
| `KW-AGENT-SPEC-003` | Error | Missing instructions |
| `KW-AGENT-SPEC-004` | Error | Broken file reference |
| `KW-AGENT-SYNC-001` | Error | Role not present in every harness |
| `KW-AGENT-SYNC-002` | Error | Instruction drift between harness copies |
| `KW-AGENT-LINT-001` | Info | Routing score too low (< 50/100) |
| `KW-AGENT-LINT-002` | Warning | Agent description is an action summary or lacks trigger conditions |

### Agent security — `agent scan`

`KW-AGENT-SEC-001`…`-008`, `-020`…`-025`, `-030`…`-032` mirror the skill security codes
above, one-for-one. Both come from the same
[instruction-surface engine](../context-hygiene/security-scanning.md); the prefixes differ
only so hosts can gate the two artifact classes at different severities.

## Code review — [the review council](../code-review/architecture.md)

The verdict tier is the one place in the product where a rule decides something a model
proposed. `review verdict` computes the outcome from the council's findings and the gate
results by fixed rule, so the same inputs produce the same verdict every time and each
decision names the id that made it.

### Verdict — `review verdict`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-001` | Warning | A finding arrived without an excerpt, evidence, or a failure scenario, and was dropped |
| `KW-REVIEW-002` | Info | A finding fell below the configured confidence floor |
| `KW-REVIEW-003` | Info | A finding was removed by an active suppression |
| `KW-REVIEW-004` | Warning | A suppression passed its expiry and no longer applies |
| `KW-REVIEW-005` | Error | A blocking gate failed |
| `KW-REVIEW-006` | Error | A surviving critical finding |
| `KW-REVIEW-007` | Error | Surviving major findings reached the blocking threshold |
| `KW-REVIEW-008` | Error | A changed path is reserved for human review by policy |
| `KW-REVIEW-009` | Error | The diff exceeds the reviewable size ceiling |
| `KW-REVIEW-010` | Warning | Measured coverage is below the declared floor |
| `KW-REVIEW-011` | Info | No reserved paths are declared, so nothing can escalate on path alone |
| `KW-REVIEW-012` | Warning | A coverage floor is declared but no coverage report was produced |

`-008` and `-009` are evaluated before any finding is weighed, and neither can be overridden
by the engine: both say the change is not the engine's to settle, not that it is faulty.
`-010` and `-012` never block — a verdict driven by a coverage number rewards padding that
number, and a missing report is the same class of signal rather than a failed gate.

### Gates — `review gates`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-020` | Warning | No gates are declared, so the review has no executed evidence |
| `KW-REVIEW-021` | Info | A gate passed |
| `KW-REVIEW-022` | Error / Warning | A gate failed. Error when blocking, warning otherwise. |
| `KW-REVIEW-023` | Error | A findings or gate document could not be read |
| `KW-REVIEW-024` | Info / Error | The computed verdict. Info on approve, error otherwise. |
| `KW-REVIEW-025` | Error | A review report could not be written (`--out`) |
| `KW-REVIEW-026` | Info | A gate does not apply to this change: no changed path since `--base` matched its `applies-when.paths`, so it was not executed |

### Duplicates — `review duplicates`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-030` | Warning | No CodeGraph index was read, so duplicate detection did not run |
| `KW-REVIEW-031` | Warning | A set of symbols shares one normalized body |
| `KW-REVIEW-032` | Warning | The CodeGraph index disagrees with the working tree, so its clusters are stale |

## Arbiter — [Kyber Arbiter](../kyber-arbiter/architecture.md)

All Arbiter rule and diagnostic ids are permanent: hosts tune shipped rules by id, and
receipts and logs key on them, so an id is never reused or renumbered.

### Shipped rules — `default-rules.yml`

| Id | Trigger | Step | Meaning |
|---|---|---|---|
| `KW-ARB-PLAN-001` | `delegate` | 0 | `PLAN_FILE` names an artifact with parseable tasks |
| `KW-ARB-SCOPE-001` | `delegate` | 0 | `TASK` names a plan task; named paths stay inside its files |
| `KW-ARB-SCOPE-002` | `delegate` | 1 | Delegation asks only for work the task describes |
| `KW-ARB-READY-001` | `delegate` | 0 | Dependencies complete; files clear of work in flight |
| `KW-ARB-OWNER-001` | `delegate` | 0 | Target is the named specialist or the file-kind-map owner |
| `KW-ARB-OWNER-002` | `delegate` | 1 | Roster specialist owning this task |
| `KW-ARB-MODE-001` | `delegate` | 0 | RED evidence exists before an implementation dispatch in test-first mode |
| `KW-ARB-ROSTER-001` | `delegate`, `investigate` | 0 | Target is in the caller's `delegates-to` |
| `KW-ARB-DIFF-001` | `delegate.returned` | 0 | Delegation changed only its task's files |
| `KW-ARB-PLANNER-001` | `delegate.planner` | 0 | Recognised planner invocation |
| `KW-ARB-PLANNER-DIFF-001` | `delegate.planner.returned` | 0 | Planner wrote only plan, spec, and todo directories |
| `KW-ARB-READONLY-001` | `investigate.returned` | 0 | Read-only dispatch left the tree unchanged |
| `KW-ARB-LENS-001` | `lens.spawn` | 0, then 1 | Lens applies to this change |
| `KW-ARB-QUOTE-001` | `lens.returned` | 0 | Finding excerpt present at its `file:line` |
| `KW-ARB-PREEX-001` | `lens.returned` | 0 | Excerpt inside a changed hunk |
| `KW-ARB-CLAIM-001` | `refute.spawn` | 1 | Quoted code supports the finding's claim |
| `KW-ARB-GATE-CORROBORATED-001` | `refute.spawn` | 0 | A failed gate already corroborates the finding |
| `KW-ARB-GATE-001` | `gate.select` | 0 | A changed path matches the gate's `applies-when.paths` |

### Configuration, key, log, binary, guard, parse — `arbiter validate`, `setup`, `status`, `doctor`, `plan`

| Id | Severity | Meaning |
|---|---|---|
| `KW-ARB-CONFIG-001` | Error | The `arbiter:` section or the user override is malformed; the file is named |
| `KW-ARB-CONFIG-002` | Error | A rule reads a fact its trigger does not supply |
| `KW-ARB-CONFIG-003` | Error | Unknown lens in `instructions-from` |
| `KW-ARB-CONFIG-004` | Error | An answer with no effect |
| `KW-ARB-CONFIG-005` | Error | Duplicate, retired, or reserved-prefix id, or a non-overridable field on a shipped rule |
| `KW-ARB-CONFIG-006` | Warning | Model rules switched off: provider is `none` |
| `KW-ARB-CONFIG-007` | Warning | Configured model is not in an enabled step-1 rule's `tuned-for`, or has no known budget |
| `KW-ARB-CONFIG-008` | Error | Effect not allowed for the rule's trigger family, or a remapped `undecidable` |
| `KW-ARB-CONFIG-009` | Error | User override holds a key other than `provider` |
| `KW-ARB-CONFIG-010` | Error | Non-loopback endpoint uses `http` |
| `KW-ARB-KEY-001` | Warning | Remote provider configured and no key resolves for its origin |
| `KW-ARB-LOG-001` | Warning | `artifacts/arbiter/` is not ignored by git |
| `KW-ARB-BIN-001` | Warning | `kyber-weave-arbiter` missing from `PATH`, or version differs from the CLI's |
| `KW-ARB-GUARD-001` | Warning | No plan, spec, or todo index declared, so the Read guard protects nothing |
| `KW-ARB-PARSE-001` | Warning | A dependency names no task in the artifact |
| `KW-ARB-PARSE-002` | Info | A task lists no files, so its file-scope checks are skipped |
| `KW-ARB-AUDIT-001` | Warning | Ledger event with no decision: the hook was killed, timed out, or crashed after recording the event |
| `KW-ARB-AUDIT-002` | Warning | Unmarked dispatch to a Squad agent with an unidentified caller |
| `KW-ARB-AUDIT-003` | Warning | Unpaired event: a `pre` with no `post`, or a `post` with no `pre` |
| `KW-ARB-AUDIT-004` | Warning | Dispatch to an implementation specialist whose packet body names a planning path |
| `KW-ARB-HOOK-001` | Error | Internal hook error produced a fail-closed block |

## Shared

| Id | Severity | Meaning |
|---|---|---|
| `KW-PARSE-000` | Error | An artifact could not be parsed |
| `KW-CONFIG-001` | Error | `kyber-weave.yml` invalid or unreadable |

## Related

- [CI Pipelines architecture](architecture.md) — severity gating and output formats
- [Workflow runbook](workflows-runbook.md) — wiring these into GitHub Actions
