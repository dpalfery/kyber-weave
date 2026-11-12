---
id: ci-pipelines/rule-reference
title: Rule reference
doc-type: reference
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-11-12
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
| `KW-DOC-LIFECYCLE-005` | Error | A task file (`<plan>.tasks.json` beside a plan, `tasks.json` in a specification folder) is left behind, under the active folders or under the archive — raised only by `docs validate --merge-ready` |

### Plan task files — `docs validate` and the docs MCP validate tool

A plan's task file is the tracked file the architect writes at finalization so the conductor can
dispatch without reading the plan: `docs/plans/<plan>.tasks.json` beside the plan. (The merge-time rule
`KW-DOC-LIFECYCLE-005` also covers `<spec-folder>/tasks.json` inside a specification.) The plan stays the
authority, so every rule here is a disagreement between the file and the plan it sits beside, or a prompt
that cannot be acted on, or a leftover copy of the file. All seven run in `docs validate` and in `docs_validate_governed_document_schema`, not
behind a flag, because the orchestrator has the docs tools and no shell.

| Id | Severity | Meaning |
|---|---|---|
| `KW-DOC-TASKS-001` | Error | The file is not the JSON this build reads, or declares a schema version it does not read |
| `KW-DOC-TASKS-002` | Error | The file's plan, tasks, files or dependencies disagree with the plan's |
| `KW-DOC-TASKS-003` | Error | The execution digest is stale: the plan moved after the file was written. A status or Decisions edit does not move it; a task edit does. Produce the digest with `kyber-weave arbiter plan <plan> -f json` after the last plan edit |
| `KW-DOC-TASKS-004` | Error | A prompt carries no command, or no acceptance criteria |
| `KW-DOC-TASKS-005` | Error | A prompt tells the worker to open a plan, specification or todo — the authoring-time half of `KW-ARB-PLANREAD-001` |
| `KW-DOC-TASKS-006` | Warning | A Ready plan has no task file beside it. Advisory: it never fails a merge, because every plan sits in that state until its architect writes one |
| `KW-DOC-TASKS-007` | Error / Warning | A copy of a task file left in the plans folder under another name, such as `<plan>.tasks.json.new` or `.tmp`, with or without its plan beside it. The name selects the file and its content decides: its top-level JSON keys must include the task file's `execution-digest`, or `schema-version` beside `plan` or `tasks` (a file that cannot be read is reported). Error for a copy. Names ending in `.tasks.json` are `KW-DOC-LIFECYCLE-005`'s. Warning, same code, when `plan-index` names no plans folder (a root-level file), because nothing was checked, and when a plans subfolder cannot be read |

`KW-DOC-TASKS-007` walks the plans folder recursively, skips any `archive` folder, and reads the file system rather than git, so an untracked or git-ignored copy counts.

`KW-DOC-LIFECYCLE-005` in the table above is the merge-time half: the commit that archives a plan
removes the file, and the walk looks under the archive too, so a specification folder that carried
its `tasks.json` into the archive is caught rather than kept as dead weight.

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

### MCP wiring — `docs init`, `docs mcp-wire`, `docs mcp-unwire`, `docs mcp-migrate`

Emitted by the wiring check `docs init` runs, by the two repair verbs, and by the rename
migration. `001`–`004` report a repository's wiring state and never change `docs init`'s exit
code; `005` and `006` are refusals of an operation an operator explicitly requested, and exit 1.
`007` reports a proven old server key the migration can rename, `008` reports an old key that is
not provably this product's and is therefore left alone, and `009` is the migration's refusal.
See the [MCP runbook](../docgraph/mcp-runbook.md#checking-and-wiring).

| Id | Severity | Meaning | Hint |
|---|---|---|---|
| `KW-MCP-WIRE-001` | Warning for a `required` server, Info for a `recommended` one | A declared server is not wired for an in-play harness, or its file could not be read — including a configuration that is a symbolic link, which is not read at all, and one this process may not inspect, which is reported as a gap because a denied file cannot be told from an absent one | `kyber-weave docs mcp-wire --target <harness>`; for a server still wired under a retired key, `docs mcp-migrate` — the wire command would refuse that file rather than add the entry beside the old one; for a harness the repair does not write, a linked configuration, or a file this process may not inspect, the file to fix, the link to replace, or the permission to grant — by hand, because a repair would be refused the same way |
| `KW-MCP-WIRE-002` | Warning for a `required` server, Info for a `recommended` one | A wired entry's launch command differs from the declaration, once the harness's own spelling of the repository root (`${workspaceFolder}` for VS Code, `.` elsewhere) is allowed for; the line names both. Never fixable, so it is never offered as a repair | The edit that makes the entry run the declared command. On a writable harness the hint also quotes the wire command with the caveat that it refuses an entry that already exists, so the entry is edited — or deleted and re-added — by hand |
| `KW-MCP-WIRE-003` | Warning for a `required` server, Info for a `recommended` one | The wired entry's launch binary is not resolvable: a command name is looked up along `PATH` in order, and a command that names a path is resolved against the repository under inspection rather than the working directory | First-party: `kyber-weave update`, because the installation is broken. Third-party: the declaration's `install` text verbatim, or a manual pointer when it carries none — never an installation |
| `KW-MCP-WIRE-004` | Info | The harness is not in play, has no marker to detect it, or its configuration is repository settings rather than a file | `No action unless this repository uses <harness>`; where the repair may write it, the wire command, otherwise the Copilot settings and workflow-step pointer |
| `KW-MCP-WIRE-005` | Error — `docs mcp-wire` only | The target was refused and nothing was written to it: malformed or foreign document, an entry with the same server name already present, a harness the repair does not write, an absent binary, a configuration that is a symbolic link — refused before the read, so not one byte of the target is opened — a configuration this process may not inspect, a configuration that still holds a proven retired MCP server key, and a receipt record that could not be persisted | The file and the reason, plus the snippet to paste for a snippet-only harness, the install repair for an absent binary, or the receipt path and its write failure; a file that still holds a proven retired key is pointed at `docs mcp-migrate`, because the rename is what the file needs, not a second entry |
| `KW-MCP-WIRE-007` | Warning | A proven old MCP server key is present: the entry launches the shipped binary with the argument it needs, so the rename is mechanical. Emitted by the wiring check and by `docs mcp-migrate`, which renames the key's bytes and leaves every other byte as it stands; when the new key is already present with the same launch, the old member is removed byte-exact and reported as a removed duplicate | `kyber-weave docs mcp-migrate` |
| `KW-MCP-WIRE-008` | Info | An old key is present but not provably this product's — no launch proof, another program's binary, or the right binary with the wrong argument — so it was left exactly as it stood. Also the report for a harness no tool writes: Codex, ZCode, and the Copilot repository settings, whose configuration is either snippet-only or repository settings | The manual edit, or the snippet to paste, naming the key and the command it should carry |
| `KW-MCP-WIRE-009` | Error — `docs mcp-migrate` only | The target was refused and zero bytes were written: a slug the catalog does not know, a configuration that is a symbolic link (refused before the read), one this process may not inspect, a malformed or foreign document, an old and a new entry present whose launches differ, a root that is the operator's home directory (refused before the first read, so not one byte of the file is opened), or a receipt record that could not be persisted. The two differing entries are both shown | The file and the reason, both conflicting entries where that is the cause, the repository root to name instead of the home directory where that is the cause, or the receipt path and its write failure |
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
| `KW-REVIEW-008` | Retired | Formerly: A changed path is reserved for human review by policy. Retired 2026-11-02. |
| `KW-REVIEW-009` | Retired | Formerly: The diff exceeds the reviewable size ceiling. Retired 2026-11-02. |
| `KW-REVIEW-010` | Warning | Measured coverage is below the declared floor |
| `KW-REVIEW-011` | Retired | Formerly: No reserved paths are declared, so nothing can escalate on path alone. Retired 2026-11-02. |
| `KW-REVIEW-012` | Warning | A coverage floor is declared but no coverage report was produced |

The Review Council verdict is strictly binary: `0` (`APPROVE`) or `1` (`REQUEST_CHANGES`).
Former rules `-008`, `-009`, and `-011` are retired in place and no longer escalate to a
human review verdict. `-010` and `-012` never block — a verdict driven by a coverage number
rewards padding that number, and a missing report is the same class of signal rather than a
failed gate.

### Gates — `review gates`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-020` | Warning | No gates are declared, so the review has no executed evidence |
| `KW-REVIEW-021` | Info | A gate passed |
| `KW-REVIEW-022` | Error / Warning | A gate failed. Error when blocking, warning otherwise. |
| `KW-REVIEW-023` | Error | A findings or gate document could not be read: missing, empty or blank, malformed, an object the command reads that spells one name twice ignoring case (`severity` and `Severity`, or `failure_scenario` and `failureScenario`) or repeats a name exactly (the object's path and both spellings are named; an exact repeat is refused by the JSON parser, which names the file only), a `null` element in any list at any depth (a `null` inside a property the command does not know is tolerated), a finding whose `severity` is a number or not one of the severity names (matched in any letter case, so `"MAJOR"` is accepted) or whose `confidence` is not a JSON whole number from 1 to 10 (a float such as `9.0` or `1e1`, or a numeric string such as `"9"`, is refused), or a finding with no `id`, `lens` or `file` (or a gate result with no `id` or `summary`); the file is named. `review verdict` also reports here a path-taking argument given an empty value (`[path]`, `--config`, `--seats`, `--findings`, `--gates`, `--prior`, `--out`), `--seats` with `--findings`, and a `--seats` path that is missing, a file, or a symlink loop ("Seat folder not found"; a folder that exists but cannot be listed is `KW-REVIEW-040`) |
| `KW-REVIEW-024` | Info / Error | The computed verdict. Info on approve, error otherwise. |
| `KW-REVIEW-025` | Error | A review report could not be written (`--out`) |
| `KW-REVIEW-026` | Info | A gate does not apply to this change: no changed path matched its `applies-when.paths` or `include`, or its `os` does not match the host, so it was not executed |
| `KW-REVIEW-027` | Warning | No task gates are declared for the named agent, so its change has no executed evidence (`--agent`) |
| `KW-REVIEW-028` | Error | The changed set, `--task` slug, or a `{changed}`, `{task}` or `{tests}` expansion was refused or could not be resolved; nothing ran. Council refusals (`--base`, `--paths`) exit `1`; `--task` or `--fix` without `--agent` is a client-input error (exit `2`) |
| `KW-REVIEW-029` | Warning | A task gate's `fix` command failed. The verify command still decides the gate (`--agent --fix`) |
| `KW-REVIEW-033` | Error / Warning | A gate never started: its program could not start, its working directory does not exist, or a full-suite gate was refused without `--council` outside a terminal (the other gates still run; exit `1`). Error when blocking, warning otherwise. |
| `KW-REVIEW-034` | Info | The changed set is unknown, so gates with `include` ran unscoped. Fetch the full history, or name a base that exists with `--base`. |
| `KW-REVIEW-038` | Info / Warning | The CodeGraph index of the tree under review, prepared before any gate starts. Info: it was created or synced. Warning: it could not be prepared; run `kyber-weave doctor --ensure <tree>`. The gates still run, because a review is a read-only run. A repository without governed docs is not checked. |
| `KW-REVIEW-039` | Warning | A gate was NOT RUN to a result: its own `timeout-seconds` stopped it, or the council deadline (`review.council-deadline-seconds`) stopped it or passed before it started. What it checks is unverified. `review gates` reports it only under `--deadline`, and then leaves it out of the exit code; without `--deadline` a timed-out gate is `KW-REVIEW-022` as before. `review verdict` always treats a timed-out or deadline-cut gate as NOT RUN: it neither requests changes nor raises the risk grade for it. Re-run it alone with `--gate <id>` |
| `KW-REVIEW-040` | Error | The `review verdict --seats` folder could not be listed, a seat output file in it could not be read, or a seat file declares `NO FINDINGS:` or `SKIPPED:` and also holds a seat-schema key followed by a colon anywhere else in the file: at any indent, after any prose, inside or outside a fence, in any letter case, bare or quoted, at a line start after an optional `- `, or after `{`, `,` or a table's `|`. Fail closed: a false refusal costs a re-run, a dropped finding is never acceptable. Prose, a Markdown table, the closing lines and bullets naming no schema key (`- **Checked:**`, `- note: x`) stay allowed. The file, the reason, and the line and column are named; nothing is computed (exit `1`) |
| `KW-REVIEW-041` | Error / Warning / Info | Lens coverage. Error: a catalogue lens has no seat file, no `--not-seated <lens>=<reason>` and no `--prior` coverage; the lenses are named (exit `1`). Error: a `--not-seated` value names no catalogue lens or has no `=<reason>` (exit `1`). Info: a lens was not seated, with its reason, or was carried from the prior cycle. Warning: a seat file that is not a catalogue lens (its findings are kept; `reviewer.md`, the reviewer's own seat, is exempt), or a folder entry that cannot be a seat (a directory named `<x>.md`, a file named `.md`, a dotfile, or a name that is not a lowercase lens id), which is named and ignored |
| `KW-REVIEW-042` | Info / Error | `--drop <id>=<reason>`. Info: the finding was dropped by the reviewer, and the reason is recorded. Error: `--drop` names no finding, or its value has no `<id>=<reason>` form (exit `1`). No option changes a severity or confidence |
| `KW-REVIEW-043` | Info / Warning | A finding cites a line that is not in its file: beyond its end, or below 1. Info: it was moved to the one line equal to its excerpt's first non-blank line, or its file is missing, outside the repository or unreadable, so the line was not checked (one line per file). Warning: no single line matches, so it was dropped. A line inside the file is never moved |
| `KW-REVIEW-044` | Warning | A seat that reported findings or no findings wrote no `Discovery:` line, read files directly with no code index lookup and no `Index fallback:` line, named an `Index fallback:` mode the seat contract does not define, or wrote two different `Index fallback:` lines (both named; the first is kept). Never a verdict input |
| `KW-REVIEW-045` | Warning | `review verdict --findings` read a hand-built findings file, which skips the lens coverage check. Use `--seats` |
| `KW-REVIEW-046` | Info | The accepted critical and major finding ids, the refutation candidates. Listed only when no blocking gate that ran to a result failed |
| `KW-REVIEW-047` | Warning | The `correctness` seat reported findings or `NO FINDINGS:` and wrote no readable `Input sites: N traced, M files with none` line, so its tracing of hostile inputs per parse site is unrecorded. A `SKIPPED` seat is exempt. Raised whenever that seat reported findings or `NO FINDINGS:`, because the seat folder carries no changed paths. A coverage note: never a verdict input |

### Duplicates — `review duplicates`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-030` | Warning | No CodeGraph index was read, so duplicate detection did not run |
| `KW-REVIEW-031` | Warning | A set of symbols shares one normalized body |
| `KW-REVIEW-032` | Warning | The CodeGraph index disagrees with the working tree, so its clusters are stale |

### Suppressions — `review suppressions`

| Id | Severity | Meaning |
|---|---|---|
| `KW-REVIEW-036` | Error | A warning suppression the change added states no reason. The file, line and form are named, and `reason: <why>` on the same or the immediately preceding added line clears it |
| `KW-REVIEW-037` | Error | The change against the base ref could not be read, so no line was scanned. A failure rather than a pass, because a gate that cannot see the change has no evidence either way |
| `KW-REVIEW-028` | Error | The `--base` ref or the path was refused before anything was read (client-input error, exit `2`) |

Only added lines are judged, so a committed suppression is never re-litigated. A suppression on a
context line clears nothing, and an empty change passes.

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
| `KW-ARB-PLANREAD-001` | `delegate` | 0 | Prompt names no planning path once the routing header is removed; a documentation-profile target is exempt |
| `KW-ARB-DIFF-001` | `delegate.returned` | 0 | Delegation changed only its task's files |
| `KW-ARB-GATE-REPORT-001` | `delegate.returned` | 0 | The digest's `TASK_GATE` line agrees with the gate report it names: `verified` needs a `PASS` claim the report corroborates, `disagrees` and `missing` escalate |
| `KW-ARB-PLANNER-001` | `delegate.planner` | 0 | Planner invocation, with four answers: `recognised` (a `PLAN_FILE` under a planning directory, or a marker that needs no `PLAN_FILE`); `malformed` (no recognised marker); `plan-file-invalid` (a `PLAN_FILE` the header rejected: absolute, a `..` segment, repeated, or with a space, tab or backslash; checked before any marker); `plan-file-outside-planning-dirs` (a valid `PLAN_FILE` outside the plan, spec and todo directories) |
| `KW-ARB-PLANNER-DIFF-001` | `delegate.planner.returned` | 0 | Planner wrote only plan, spec, and todo directories |
| `KW-ARB-READONLY-001` | `investigate.returned` | 0 | Read-only dispatch left the tree unchanged |
| `KW-ARB-LENS-001` | `lens.spawn` | 0, then 1 | Lens applies to this change; `correctness` and `test-adequacy` skip only on a non-empty, positively Markdown-only changed set |
| `KW-ARB-QUOTE-001` | `lens.returned` | 0 | Finding excerpt present at its `file:line` |
| `KW-ARB-PREEX-001` | `lens.returned` | 0 | Excerpt inside a changed hunk; reads `finding.changed-lines`, so it answers `in-hunk` or `outside-hunk` (annotate) |
| `KW-ARB-CLAIM-001` | `refute.spawn` | 1 | Quoted code supports the finding's claim |
| `KW-ARB-GATE-CORROBORATED-001` | `refute.spawn` | 0 | A failed gate already corroborates the finding |
| `KW-ARB-GATE-001` | `gate.select` | 0 | A changed path matches the gate's `applies-when.paths` |
| `KW-ARB-LENS-002` | `lens.spawn` | 0 | Is this lens one with nothing to judge in a change with no code in it? Answers: `no-code-change`, which skips the lens; any other result is `undecidable`, which allows. Every changed path must be comment-only, rename-only or Markdown, and the changed set must be non-empty. Names six lenses: `correctness`, `test-adequacy`, `performance`, `di-composition`, `duplicate-implementation`, `model-placement`. Never names `security`, `documentation-accuracy` or `blast-radius-revertibility`. Enforced, not shadow. See [hunk-level lens skipping](../code-review/architecture.md#hunk-level-lens-skipping-kw-arb-lens-002) |
| `KW-ARB-DIFFICULTY-001` | `delegate.model` | 1 (`choice`, ≥ 0.8) | How hard the task is: `easy`, `standard` or `hard`. Each answer retargets the dispatch to its `model-targeting.difficulty-profiles` profile; an answer below 0.8 is `undecidable` and keeps the model. It applies only on a first attempt or after a pass: after a failed attempt that `KW-ARB-LADDER-001` does not step up, its answer is not applied and no `model` block is written. Shadow state comes from `model-targeting.mode` |
| `KW-ARB-LADDER-001` | `delegate.model` | 0 | Did the previous attempt fail below the top of the ladder: `step-up` retargets one rung higher; `returns-unobservable`, `first-attempt`, `profile-unrecorded` and `top-of-ladder` keep the model. Shadow state comes from `model-targeting.mode` |
| `KW-ARB-NITPICK-001` | `lens.returned` | 0, then 1 | Is a lens finding a nitpick or a substantive defect. A `Critical` finding or one a failed gate corroborates is substantive without a question; any other is asked once (≥ 0.85). Only a confident `nitpick` drops it (`ARBITER_DROPPED`); `unsure` keeps it. Shipped in shadow |
| `KW-ARB-NITPICK-002` | `refute.spawn` | 0 | Did the nitpick filter drop this finding: a refutation of a dropped finding is skipped (`verify`). Follows the filter's shadow state, so shipped in shadow |
| `KW-ARB-DUP-001` | `refute.spawn` | 1 | Does an earlier finding of this review name the same defect at the same place (≥ 0.9). The candidates are earlier findings of the review in the same file within 20 lines, at most three, oldest first. A duplicate denies the refutation with `ARBITER_DUPLICATE`. Shipped in shadow |
| `KW-ARB-TRIAGE-001` | `triage.shortcut` | 1 | Is the defect a trivial single-site fix (≥ 0.9). `trivial` answers the investigator with the substitute `VERDICT: TRIVIAL` digest; `needs-architect` keeps the investigate. Shipped in shadow |
| `KW-ARB-INTAKE-001` | `intake.shortcut` | 1 | Does the request call for a plan or a spec (≥ 0.9). Either answer returns the substitute `INTAKE_RECOMMENDATION` digest and withholds the planner. Shipped in shadow |
| `KW-ARB-DOCIMPACT-001` | `docs-impact.shortcut` | 0, then 1 | Does the change need no documentation change. A change that touches a governed path keeps the dispatch and is never asked; `no-doc-change` (≥ 0.9) answers the review with the substitute digest. Shipped in shadow |
| `KW-ARB-FAILURE-001` | `delegate.failure` | 0, then 1 | Is this rework failure the same failure as the previous attempt of its cluster (≥ 0.85). The first failure of a cluster is only recorded; `same` returns the advisory `ARBITER_SAME_FAILURE` and never denies. Shipped in shadow |
| `KW-ARB-TRIM-001` | `sections.select` | 1 | Does this section bear on the task (≥ 0.9). Only a confident `not-relevant` trims the section (`advise`); any unsure answer keeps every section. Shipped in shadow |
| `KW-ARB-COMMENT-001` | `comment.triage` | 1 | Is this bot review comment `real`, a `nitpick`, or `already-fixed` in the current code (≥ 0.85). Only `real` keeps the comment as a fix target; `nitpick` and `already-fixed` are advised. Summary-table rows from a configured bot are classified one by one, at most 50 per call, and every other row stays `real`. Shipped in shadow, so the caller is handed `real` and the class the model gave is reported as `wouldClass`. |

### Hook guards

A hook guard is a stateless check that runs in the harness hook before a tool call, beside the
shipped rules. It is not a rule.

| Id | Harness and tool | Caller | Effect | Meaning |
|---|---|---|---|---|
| `KW-ARB-SHELL-001` | Claude `Bash` and `PowerShell`, project and user scope | `architect` | Deny before the command runs | A shell command outside the architect's documented command set: a command not on the list, a substitution, a redirect other than `2>&1` or `2>/dev/null`, a heredoc, a subshell, a loop, or a `touch` outside the plan, spec and todo directories |

Hook guards are not rules: hosts cannot tune a guard by id, but `arbiter.enabled: false` turns the whole Arbiter off, and with it the guard. Each guard decision writes a hook-log line, not a ledger event.

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
| `KW-ARB-CONFIG-011` | Error | `arbiter.global` is declared in a project config; user-level `arbiter.global.targets` is the only valid global schema |
| `KW-ARB-CONFIG-012` | Error | `model-targeting.mode` is not `off`, `shadow` or `enforce`, or a `difficulty-profiles` key or value is not a semantic profile. The message lists the valid values |
| `KW-ARB-CONFIG-013` | Error | `model-targeting.ladder` is empty, repeats a profile, or names a value that is not a semantic profile. The message shows an example ladder |
| `KW-ARB-CONFIG-014` | Error | `shadow` is set on `KW-ARB-DIFFICULTY-001` or `KW-ARB-LADDER-001`, whose shadow state `model-targeting.mode` owns |
| `KW-ARB-CONFIG-015` | Error | A savings value is out of range or in a forbidden place: `stores.max-item-bytes` outside 1 to 1048576, `stores.retain-days` below 1 (a value above 3650 is clamped, not refused), a blank `comment-triage.bot-logins` entry, a host rule using `for-each`, or a host rule mapping an answer to an effect only shipped rules may use |
| `KW-ARB-MODEL-001` | Warning | Model targeting is not `off`, a hooked global target is installed, and its `model-targets.<harness>.json` is missing, unreadable, or older than the Squad lock. Remedy: `kyber-weave squad update`. Reported by `arbiter doctor` |
| `KW-ARB-INSTALL-001` | Error | `--global` and `--project` were used together, or the target-scope input is otherwise invalid for the CLI; the install defaults to global and project scope is opt-in |
| `KW-ARB-KEY-001` | Warning | Remote provider configured and no key resolves for its origin |
| `KW-ARB-PROJECT-001` | Warning | The project enables Arbiter but a harness is not gated: no project hook, and no live global hook that evaluates under the project. Raised once per ungated harness, naming it; hint is its fix command (`kyber-weave arbiter install --project --target <token>`, or `kyber-weave arbiter install --target claude` for Claude). Reported by `arbiter doctor` |
| `KW-ARB-GLOBAL-001` | Warning | The global Squad receipt owns a hook block, or a harness's own shim hook file, for a harness missing from `arbiter.global.targets`, so `kyber-weave update` never refreshes it. Agent and skill files do not count. Hint: `kyber-weave arbiter install --target <token>`. Reported by `arbiter doctor` |
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
| `KW-CONFIG-002` | Warning | A `review.gates` or `review.task-gates` entry carries an unknown key. The key is ignored and the configuration still loads. |

## Related

- [CI Pipelines architecture](architecture.md) — severity gating and output formats
- [Workflow runbook](workflows-runbook.md) — wiring these into GitHub Actions
