---
id: ci-pipelines/rule-reference
title: Rule reference
doc-type: reference
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-08-20
---

# Rule reference

Every rule id Kyber-Weave emits. Ids are stable and suitable for suppression and SARIF
baselines — see [CI Pipelines architecture](architecture.md) for why they never change.

## Documentation — [DocGraph](../docgraph/governance.md)

### Schema — `docs validate`

| Id | Severity | Meaning |
|---|---|---|
| `KW-DOC-SPEC-001` | Error | No frontmatter block, or unparseable YAML |
| `KW-DOC-SPEC-002` | Error | `doc-type`, `status`, or `last-reviewed` outside its vocabulary or format |
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

## Code review — [the review council](../plans/2026-08-20-code-review-council.md)

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

`-008` and `-009` are evaluated before any finding is weighed, and neither can be overridden
by the engine: both say the change is not the engine's to settle, not that it is faulty.
`-010` never blocks — a verdict driven by a coverage number rewards padding that number.

### Gates — `review gates`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-020` | Warning | No gates are declared, so the review has no executed evidence |
| `KW-REVIEW-021` | Info | A gate passed |
| `KW-REVIEW-022` | Error / Warning | A gate failed. Error when blocking, warning otherwise. |
| `KW-REVIEW-023` | Error | A findings or gate document could not be read |
| `KW-REVIEW-024` | Info / Error | The computed verdict. Info on approve, error otherwise. |

### Duplicates — `review duplicates`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-030` | Warning | No CodeGraph index was read, so duplicate detection did not run |
| `KW-REVIEW-031` | Warning | A set of symbols shares one normalized body |
| `KW-REVIEW-032` | Warning | The CodeGraph index disagrees with the working tree, so its clusters are stale |

## Shared

| Id | Severity | Meaning |
|---|---|---|
| `KW-PARSE-000` | Error | An artifact could not be parsed |
| `KW-CONFIG-001` | Error | `kyber-weave.yml` invalid or unreadable |

## Related

- [CI Pipelines architecture](architecture.md) — severity gating and output formats
- [Workflow runbook](workflows-runbook.md) — wiring these into GitHub Actions
