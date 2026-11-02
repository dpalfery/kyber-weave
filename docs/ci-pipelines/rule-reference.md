---
id: ci-pipelines/rule-reference
title: Rule reference
doc-type: reference
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-11-01
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
| `KW-DOC-LIFECYCLE-004` | Error | An archived entry the change touches without closeout evidence (`C0`–`C9`) — raised only by `docs validate --merge-ready` |

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

### MCP wiring — `docs init`, `docs mcp-wire`, `docs mcp-unwire`

Emitted by the wiring check `docs init` runs and by the two repair verbs. `001`–`004` report
a repository's wiring state and never change `docs init`'s exit code; `005` and `006` are
refusals of an operation an operator explicitly requested, and exit 1. See the [MCP
runbook](../docgraph/mcp-runbook.md#checking-and-wiring).

| Id | Severity | Meaning | Hint |
|---|---|---|---|
| `KW-MCP-WIRE-001` | Warning for a `required` server, Info for a `recommended` one | A declared server is not wired for an in-play harness, or its file could not be read — including a configuration that is a symbolic link, which is not read at all, and one this process may not inspect, which is reported as a gap because a denied file cannot be told from an absent one | `kyber-weave docs mcp-wire --target <harness>`; for a harness the repair does not write, a linked configuration, or a file this process may not inspect, the file to fix, the link to replace, or the permission to grant — by hand, because a repair would be refused the same way |
| `KW-MCP-WIRE-002` | Warning for a `required` server, Info for a `recommended` one | A wired entry's launch command differs from the declaration, once the harness's own spelling of the repository root (`${workspaceFolder}` for VS Code, `.` elsewhere) is allowed for; the line names both. Never fixable, so it is never offered as a repair | The edit that makes the entry run the declared command. On a writable harness the hint also quotes the wire command with the caveat that it refuses an entry that already exists, so the entry is edited — or deleted and re-added — by hand |
| `KW-MCP-WIRE-003` | Warning for a `required` server, Info for a `recommended` one | The wired entry's launch binary is not resolvable: a command name is looked up along `PATH` in order, and a command that names a path is resolved against the repository under inspection rather than the working directory | First-party: `kyber-weave update`, because the installation is broken. Third-party: the declaration's `install` text verbatim, or a manual pointer when it carries none — never an installation |
| `KW-MCP-WIRE-004` | Info | The harness is not in play, has no marker to detect it, or its configuration is repository settings rather than a file | `No action unless this repository uses <harness>`; where the repair may write it, the wire command, otherwise the Copilot settings and workflow-step pointer |
| `KW-MCP-WIRE-005` | Error — `docs mcp-wire` only | The target was refused and nothing was written to it: malformed or foreign document, an entry with the same server name already present, a harness the repair does not write, an absent binary, a configuration that is a symbolic link — refused before the read, so not one byte of the target is opened — a configuration this process may not inspect, or a receipt record that could not be persisted | The file and the reason, plus the snippet to paste for a snippet-only harness, the install repair for an absent binary, or the receipt path and its write failure |
| `KW-MCP-WIRE-006` | Error — `docs mcp-unwire` only | Removal was refused: no receipt record for the file, a file that is gone, a current digest that differs from the receipt's post-write digest, a file that is a symbolic link — refused ahead of the receipt lookup — or a file this process may not read. A file still holding the receipt's pre-write digest is the other case, an interrupted write rather than drift, and is reported without a refusal | The receipt path, both digests, and the choice to make by hand — the removal never restores over a later edit; for a denial, the permission to grant |

`docs init` prints the hint beneath a finding so it can be copied, with one exception: a
not-detectable line is advice about a harness nobody claimed to use, so its hint is withheld.
The Copilot coding agent is the exception to that exception — its configuration is repository
settings rather than a file, so its hint *is* its report and is printed, while every other
undetectable harness still reports one `info` line and nothing more.

## Skills — [Skill governance](../context-hygiene/skills.md)

| Id range | Tier | Meaning |
|---|---|---|
| `KW-SKILL-SPEC-001`…`-012` | Spec | Agent Skills open-format conformance. Mostly errors; `-007` and `-009` warn, `-010` informs. |
| `KW-SKILL-LINT-001`…`-006` | Routing | Description quality dimensions that reduce routing reliability |
| `KW-SKILL-LINT-007` | Routing | Description is an action summary rather than a trigger specification (Warning) |
| `KW-SKILL-LINT-008` | Routing | Description contains excessive filler phrases or unrouted verbosity (Warning) |
| `KW-SKILL-LINT-010` | Routing | **Name collision** — the only error in this tier |
| `KW-SKILL-LINT-011` | Routing | Description overlap between two skills |
| `KW-SKILL-REVIEW-001` | Review | Skill/agent review verdict payload is malformed or invalid (Error). Emitted by the review exchange; no CLI verb invokes it today. |

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
| `KW-REVIEW-026` | Info | A gate was skipped: its `os` does not match the host, or no changed file matches its `include` |
| `KW-REVIEW-027` | Warning | No task gates are declared for the named agent, so its change has no executed evidence (`--agent`) |
| `KW-REVIEW-028` | Error | The changed set, `--task` slug, or a `{changed}` or `{task}` expansion was refused or could not be resolved; nothing ran. Council refusals (`--base`, `--paths`) exit `1`; `--task` or `--fix` without `--agent` is a client-input error (exit `2`) |
| `KW-REVIEW-029` | Warning | A task gate's `fix` command failed. The verify command still decides the gate (`--agent --fix`) |
| `KW-REVIEW-033` | Error / Warning | A gate never started: its program could not start, or its working directory does not exist. Error when blocking, warning otherwise. |
| `KW-REVIEW-034` | Info | The changed set is unknown, so gates with `include` ran unscoped. Fetch the full history, or name a base that exists with `--base`. |

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
| `KW-CONFIG-002` | Warning | A `review.gates` or `review.task-gates` entry carries an unknown key. The key is ignored and the configuration still loads. |

## Related

- [CI Pipelines architecture](architecture.md) — severity gating and output formats
- [Workflow runbook](workflows-runbook.md) — wiring these into GitHub Actions
