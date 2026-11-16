---
id: specs/role-based-worker-agents/design
title: Role-based worker agents design
doc-type: spec
status: draft
owner: dpalfery
last-reviewed: 2026-11-16
component: KyberSquad
documentation-impact: extends-component
impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]
keywords:
  - role agents
  - worker roster
  - task technologies
  - technology-keyed task gates
  - standards bundle
---

# Role-based worker agents design

**Phase status:** Draft

## Overview

Add three role agents (`server-dev`, `client-dev`, `devops-dev`) beside today's eleven technology
agents. A host picks one worker roster per run. Each task names its technologies; the packet tells
the worker which standards bundles to fetch; task gates resolve from the task's technologies instead
of the agent's name. Nothing is deleted. Retiring the technology agents is a later decision that
rests on a measured comparison.

This design covers the [requirements](requirements.md). It depends on the stitched standards bundle
planned on [pull request 625](https://github.com/dpalfery/kyber-weave/pull/625), and it amends
[ADR 0080](../../adr/0080-plan-tasks-name-capabilities-and-the-conductor-chooses-the-agent.md) and
[ADR 0082](../../adr/0082-the-decision-model-picks-task-agents-and-headerless-dispatches-are-delegations.md).

## Evidence

Read from the tree at `b26ca736` on 2026-11-16. Counts are from text search and are exact unless
marked as estimates.

### What the technology agents share and where they differ

| Fact | Evidence |
|---|---|
| Twelve workers carry the identical Copilot tool list | The `copilot-tools` line of `csharp-dev`, `dal-dev`, `react-dev`, `typescript-dev`, `python-dev`, `go-dev`, `rust-dev`, `maui-dev`, `tauri-dev`, `pulumi-dev`, `sql-database-architect` and `test-dev` hashes to one value. `docs-dev` and `github-devops` differ. |
| The same twelve share one capability profile | All name `capability-profile: worker`. `docs-dev` is `documentation`; `github-devops` is `publishing-worker`, which adds network read and ask-to-publish. |
| Model tiers differ by technology | `fast`: `csharp-dev`, `dal-dev`, `react-dev`, `python-dev`, `maui-dev`, `test-dev`, `docs-dev`. `general`: `typescript-dev`, `go-dev`, `rust-dev`, `tauri-dev`, `pulumi-dev`, `github-devops`. `deep-planning`: `sql-database-architect`. |
| `fast` and `general` are different models on five harnesses | `products/kyber-squad/profiles/models.yml`: Claude, Codex, Copilot, Cursor and Devin name different models; Pi differs in thinking level only; Antigravity, Factory, Kilo, OpenCode and ZCode name the same model. |
| Most bodies are pointers; three are not | Nine agents run 67 to 82 lines. `tauri-dev` is 207 lines and `sql-database-architect` is 182, each with security and platform rules in the body. `csharp-dev`, one of the nine, carries a 13-line completion gate on build isolation and diagnostics. |
| Four technologies have no skill | Skills exist for `csharp-dev`, `dal-dev`, `go-dev`, `maui-dev`, `python-dev`, `rust-dev`, `typescript-dev`, `github-devops` and `testing`. There is none for React, Tauri, Pulumi or SQL. |
| Fourteen shipped standards exist | `products/kyber-squad/standards/`: azure, csharp, data-access-layer, github-actions, go, maui, pulumi, python, react, rust, sql, tauri, test, typescript. |

### Where agent names are wired into code

| Coupling | Evidence |
|---|---|
| Host task gates are keyed by agent name | `ReviewConfigParser.ParseTaskGates` validates `review.task-gates` keys as agent slugs. Four call sites look a key up by agent name: `ReviewGatesService` (the gate run), `PacketHostFacts.AppendGates`, `PacketTestCommandRenderer` and `TriggerFactBuilder` (the `config.task-gates-declared` fact). |
| An unknown agent name loses gating quietly | `ReviewGatesService` answers an agent with no entry with a warning, an empty report and exit code 0. |
| Council gates are not keyed by agent | `review.gates` entries are keyed by gate id and selected by `include` globs. The brief's statement that `review.gates` is agent-keyed does not match the code; only `review.task-gates` is. |
| The shipped gate catalogue names agents | `GateCatalogueRenderer` holds `AgentOrder` and an `AgentTechnology` map (for example `dal-dev`, `maui-dev` and `pulumi-dev` all map to `csharp`); each family file lists `agents:` per gate, and `test-dev` receives a copy of format and lint gates. |
| Some task gates have no `include` | The catalogue's `build` gate for C# has none, so it runs for every task of the agent it is listed under. |
| Gates shared between agents are identical here | In this repository's configuration every gate id that two agents share (`react-dev` and `typescript-dev`, `rust-dev` and `tauri-dev`, `csharp-dev` and `dal-dev`) has the same command and globs. Other hosts may differ: the catalogue makes ids unique per agent, not across agents. |
| `docs init` never rewrites a host's review block | Stated in the block's own header, written by `GateCatalogueRenderer.BuildYaml`. |
| Routing already ignores technology names in plans | ADR 0080: a task carries a `Capabilities:` line in plain words. ADR 0082: the decision model picks the agent once, when the task file is written, from every embedded agent whose class is `implementation` (`PlanTaskAgentPick.ImplementationAgents`), keeping picks at 0.8 confidence or more. |
| Arbiter rules key on profile, with three names | `default-rules.yml` reads `delegation.target-profile` and `delegation.target-class`. It names only `test-dev` (`KW-ARB-MODE-002`). `docs-dev` and `github-devops` are named in delivery and closeout messages (`RunDelivery`, `PlanLifecycleService`, `ArbiterRunStartRemedy`, `SecondaryTriggers`). No rule names a technology agent. |
| Renderers do not name agents | No file under `src/KyberWeave.Domain/Squad/Rendering` names a technology agent in code. |
| Agent `aliases` exist but do almost nothing | `SquadSourceValidator` checks them for collisions and `CopilotRenderer` writes them as metadata. No lookup resolves an alias to an agent. |
| Standards already reach workers through a tool | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md): a standard is read through `docs_get_coding_standard_for_technology`, by topic. Each technology agent hard-codes which technology to ask for. |

### Size of the references

| Area | Files naming a worker agent | Note |
|---|---|---|
| `src/` | 22 | 13 are comments or messages about `docs-dev`, `github-devops` or `test-dev`. Real technology-agent coupling is `GateCatalogueRenderer` and the family `gates.yml` files. |
| `tests/KyberWeave.Tests` | 165 files, 2,264 occurrences | 144 files name `csharp-dev`, mostly as an example slug in gate, packet and Arbiter fixtures. |
| Other test projects and fixtures | 9 files | Prompt-content and repository-policy tests. |
| `products/kyber-squad` | 84 | Agents, skills, standards, the bundle and the conductor references. |
| Live `docs/` | 39 | `docs/archive` holds 175 more and is not edited. |

## Where the evidence is against the idea

These are stated so the owner can weigh them; the design still follows the owner's direction.

1. **"The agents differ only in which standard they point at" is true for eight of eleven.**
   `tauri-dev`, `sql-database-architect` and `csharp-dev` hold rules that exist nowhere else a role
   agent can reach. Requirement 9 exists because of this. Skipping it makes the role roster weaker.
2. **"All Copilot workers already run the same model" does not match the shipped profiles.**
   `fast` is `GPT-6.1 Terra (copilot)` and `general` is `Grok 4.7 (copilot)`. The claim can only be
   true under a user preset that maps both. One profile per role removes the per-technology tier on
   five harnesses.
3. **Server versus client is not a mechanical boundary.** C#, Rust and TypeScript each appear on both
   sides. With technology moved to the task, `server-dev` and `client-dev` have the same tools,
   the same permissions and the same gate resolution. They differ only in description and a few
   lines of working habit. A single `dev` agent would behave the same to the machine. The split is
   kept because the owner asked for it and because it gives parallel work a readable boundary;
   the comparison should say whether it earns its place (decision D2).
4. **The misrouting that prompted this may be designed behaviour.** `csharp-dev`'s own "Data
   layer" section tells it to take repository, SQL and migration files when its task names them.
   The run itself was not examined for this specification. Either way, role agents remove the
   question instead of answering it.
5. **Routing was rebuilt three days ago.** ADR 0080 (2026-11-13) and ADR 0082 (2026-11-14) moved
   routing to capabilities and a model pick, which measured 0.99 to 1.00 confidence on six tasks.
   Fewer, broader agents make that pick easier, but they also remove the description as a place to
   say "do not use for X".
6. **Host standards are host-owned.** A rule moved from an agent body into a shipped standard
   reaches new hosts only. Existing hosts keep the standard text they have (Requirement 9.4).

## Architecture

### Roster

| Agent | Roster | Folds in (retirement only) | Capability profile | Model profile |
|---|---|---|---|---|
| `server-dev` (new) | role | `csharp-dev`, `dal-dev`, `typescript-dev`, `python-dev`, `go-dev`, `rust-dev` | `worker` | `general` |
| `client-dev` (new) | role | `react-dev`, `maui-dev`, `tauri-dev` | `worker` | `general` |
| `devops-dev` (new) | role | `pulumi-dev`, and pipeline authoring from `github-devops` | `worker` | `general` |
| `code-reviewer` | both | unchanged; it is already a role agent | `reviewer` | `reviewer` |
| `test-dev` | both | stays: tests-only work, and `KW-ARB-MODE-002` exempts it by name | `worker` | `fast` |
| `docs-dev` | both | stays: sole holder of the `documentation` profile; closeout names it | `documentation` | `fast` |
| `github-devops` | both | stays as the delivery agent: run start, push, pull request | `publishing-worker` | `general` |
| `sql-database-architect` | both | stays for now: the only `deep-planning` worker (decision D3) | `worker` | `deep-planning` |
| Ten technology agents (all but `sql-database-architect`) | technology | unchanged | unchanged | unchanged |

`devops-dev` takes the `worker` profile, not `publishing-worker`. Folding Pulumi work into
`github-devops` would give infrastructure code network read and ask-to-publish it does not have
today. Publishing stays with `github-devops`.

An agent declares its roster with a new optional frontmatter key, `worker-roster: technology | role`.
An agent without the key serves both rosters. The ten technology agents other than
`sql-database-architect` gain `worker-roster: technology`; that one line is the only edit made to
them.

### Choosing the roster

A new top-level host configuration key, `worker-roster`, takes `technology` (the default) or `role`.
It is published in the Config Reg as `<worker-roster>`, the same way `<autonomy>` is.

The per-run override is given where the task file is written: `docs finalize`, the finalize tool
and `docs tasks --write` accept a roster option. The task file's `worker-roster` field is then the
single record of the run's roster. The ready queue, validation and the Arbiter fact read it from
the task file; where no task file resolves they use the host configuration. The gate resolver does
not need the roster: it keys on whether the agent has its own entry.

- `PlanTaskAgentPick.ImplementationAgents` filters candidates to the active roster plus agents with
  no roster key.
- The task file gains a top-level `worker-roster` field, written by `PlanTaskFileGenerator`. An
  unchanged rewrite keeps the recorded roster unless the option is given again.
- The conductor's `delegates-to` gains the three role agents. Its instructions say to dispatch only
  agents of the roster the ready queue names.
- A `Role:` line naming an agent of the other roster is kept and reported as a warning, new id
  `KW-DOC-TASKS-0NN` (the next free number, assigned in the task).

### Task technologies

A plan or specification task gains an optional line:

```text
Technologies: csharp, data-access-layer
```

`PlanDocumentParser` reads it, `PlanTaskFileEntry` gains `technologies` (a list), and the task
file's schema version rises by one; a reader of the older version sees no field and behaves as
today. The line is outside the capability text, so ADR 0080's rule that capabilities have no fixed
vocabulary stands. The values are checked against the host's `ontology.technologies`, the list that
already creates each standard's folder and Config Reg property.

Validation, in `PlanTaskFileValidator`:

- unknown technology: error;
- roster `role`, task with files and no technologies: error;
- roster `technology`, no technologies: accepted;
- a file in scope that no named technology's gate `include` globs select, but another declared
  technology's do: warning. Technologies share extensions (`.cs`, `.ts`, `.rs`), so a file a named
  technology already covers is never reported. The globs are the ones the host already wrote; no
  second table of extensions is kept. This check uses the gate resolver, so it ships in phase 2.

### Packet delivery

`PacketHostFacts.Render` gains the task's technologies and appends, for each:

```text
- Standards for csharp: call docs_get_coding_standard_for_technology("csharp", topic) before your first edit.
```

No standards text is inlined, which keeps ADR 0064's by-topic, budgeted read. Once the plan on pull
request 625 ships, that one call returns shared rules, technology rules, technology skills and the
standard as one bundle, and a role agent needs nothing else. Until then, the role agent body holds
one table mapping a technology to today's skill name (`csharp` to `csharp-dev`, `data-access-layer`
to `dal-dev`, `go` to `go-dev`, `maui` to `maui-dev`, `python` to `python-dev`, `rust` to
`rust-dev`, `typescript` to `typescript-dev`, `github-actions` to `github-devops`, `test` to
`testing`). The table is deleted by the task that follows pull request 625.

### Architect and product-owner

`architect.md` today says to resolve "applicable standards" through the Config Reg, which leads it
to open the files. The instruction changes to: call `docs_get_coding_standard_for_technology` for a
technology only when a task of the plan names it, with a topic where one applies; read a standards
file directly only when the tool is missing or fails. The same sentence goes into the
`product-owner` tasks-phase reference. Both also gain the instruction to write a `Technologies:`
line on every task that changes files.

### Task gates by technology

One resolver, `TaskGateResolver`, replaces the four direct lookups. Its input is the configuration,
the agent name and the task's technologies.

1. If the agent has its own `review.task-gates` entry, return it. Technology agents and shared
   workers always take this path, so their behaviour is unchanged.
2. Otherwise, for each technology of the task: take `review.technology-gates.<technology>` when the
   host declares it; else take the entry of the technology agent the shipped map names for that
   technology.
3. Merge the lists in technology order. The same id with the same command and globs runs once. The
   same id with a different command runs as well, and the report names each as
   `<technology>:<id>`, so a clash between two technologies' gates never stops a run.
4. Nothing resolved for a role agent whose task names technologies: an error with a new
   `KW-REVIEW-0NN` id and a non-zero exit, because a silent empty report is how gating would be lost.

The shipped map is the inverse of `GateCatalogueRenderer.AgentTechnology`, extended to every shipped
standard: `csharp` to `csharp-dev`, `data-access-layer` to `dal-dev`, `maui` to `maui-dev`, `pulumi`
to `pulumi-dev`, `typescript` to `typescript-dev`, `react` to `react-dev`, `rust` to `rust-dev`,
`tauri` to `tauri-dev`, `go` to `go-dev`, `python` to `python-dev`, `sql` to
`sql-database-architect`, `github-actions` to `github-devops`, `test` to `test-dev`. It moves to one
shared type that both the renderer and the resolver read.

A host that already has agent-keyed gates therefore needs no edit to try the role roster.
`review.technology-gates` is the new, preferred form; `ReviewConfigParser` validates its keys
against `ontology.technologies`. `docs init` keeps writing agent-keyed entries until retirement is
decided, so a fresh host works under both rosters.

The council's `review.gates` do not change.

### Arbiter

No rule changes for the role agents: they are `implementation` class with the `worker` profile, so
the budget, read-guard, run-writer and scope rules apply through `WorkerGuardRules`,
`RunWriterProfiles` and `ReadGuard` as they do for any worker. `ArbiterSquadCatalog` reads the new
`worker-roster` key. One fact is added, `delegation.target-roster-active` (true, false or absent),
and one rule with a new id that answers from it and ships with every answer set to allow, so a
dispatch to the inactive roster is logged, not blocked. That follows
[ADR 0072](../../adr/0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md).

### Harness renderers

The thirteen renderers iterate the bundle's agents and need no agent-specific change. The work is
content and tests: three agent files, three more rendered outputs per harness, the `full` bundle
list, and the tests that pin the shipped roster. Copilot grants a worker exactly the tools its
`copilot-tools` line lists, so the role agents copy the technology agents' line unchanged.
The parser must accept the new frontmatter key and every renderer must leave it out of rendered
frontmatter.

### Models

All three role agents name `general`. Against today, that raises the tier for C#, data-access,
Python, React and MAUI work (now `fast`) on Claude, Codex, Copilot, Cursor and Devin, and changes
nothing for TypeScript, Go, Rust, Tauri and Pulumi work. Schema design keeps `deep-planning` because
`sql-database-architect` stays. User presets are keyed by profile, so they keep working. Where a
host has turned on the Arbiter's difficulty retarget, it keys on profile and is unaffected.

### Comparison

A runbook defines the procedure of Requirement 10: three plans (server-only, client-only, mixed),
each run once per roster from one base commit, on one harness, under a preset that maps `fast` and
`general` to the same model. The intended sources are the run's gate reports, the council's findings, the task file's picks and
confidence, and KyberDash's session records for tokens, time and tool calls. The runbook task
confirms each source and names any measurement that has none. The result is written as an ADR.

### Retirement (not approved here)

If the ADR decides to retire technology agents, a follow-up specification delivers it. This design
fixes only its shape, so nothing built now blocks it:

- **Alias period.** Agent-keyed gates keep resolving through the shipped map for two minor
  releases. `docs validate` warns on each entry for a retired agent and names the migration command.
- **Migration command.** `kyber-weave review migrate-task-gates` rewrites agent-keyed entries as
  `review.technology-gates`, shows the change first, and touches nothing else. Entries for
  `dal-dev`, `maui-dev` and `pulumi-dev` that equal the `csharp-dev` entry collapse into `csharp`;
  ones that differ move to their own technology.
- **Old names in plans.** A `Role:` or recorded pick naming a retired agent resolves to its role
  agent through the agent's `aliases`, which gain a real lookup.
- **Rendered files.** A Squad update removes a retired agent's files only where the lock shows the
  Squad wrote them unedited.
- **Catalogue.** Family `gates.yml` files drop `agents:` and `docs init` writes technology keys.

## Data models

| Model | Change |
|---|---|
| Agent frontmatter (`kyber-squad.agent/v1`) | New optional `worker-roster: technology \| role`. |
| Host configuration | New top-level `worker-roster`; new `review.technology-gates` map of technology to gate list, same gate shape as `review.task-gates`. |
| Task file | Schema version plus one; top-level `worker-roster`; per-entry `technologies`. |
| Plan and specification task text | New optional `Technologies:` line. |
| Config Reg | New `<worker-roster>` property. |
| Arbiter facts | New `delegation.target-roster-active`. |

## Error handling

| Case | Behaviour |
|---|---|
| Unknown technology on a task | `docs validate` error naming the task, the value and the declared list. |
| Role roster, file-changing task without technologies | `docs validate` error naming the task. |
| Role agent, technologies named, no gate resolves | Gate run error, non-zero exit, hint naming `review.technology-gates`. |
| Same gate id, different commands, across a task's technologies | Both run; each is reported as `<technology>:<id>`. |
| `worker-roster` holds another value | Configuration load error listing the two legal values. |
| `Role:` names an agent of the inactive roster | Kept; warning when the task file is written. |
| Docs tool has no standard for a listed technology | Worker reports an open item; it does not improvise. |
| Older task file without the new fields | Read as roster `technology`, no technologies. |

## Testing strategy

Test-first, with targeted classes only; no task runs the full suite.

- **New test classes** (about ten): roster key parsing and validation; picker candidate filtering;
  `Technologies:` parsing, task-file round trip and the four validation cases; packet host facts for
  technologies; `TaskGateResolver` (own entry, technology key, legacy fallback, merge, same-id clash,
  nothing resolved); `review.technology-gates` parsing; the Arbiter fact and rule; role-agent content
  (no technology named in the body); rendered output for one harness per renderer family.
- **Existing tests that change** (estimate: 12 to 18 files): the classes that pin the shipped
  roster and bundle, chiefly `SquadCanonicalContentTests`, `SquadSourceTests`,
  `SquadPackAndReleaseTests`, `CopilotRendererTests`, the conductor and architect prompt-content
  tests, and `GateCatalogueToolchainTests` for the moved map.
- **Existing tests that do not change**: the roughly 150 files that use `csharp-dev` as an example
  slug stay valid, because technology agents keep their names and their gate resolution path.
- **Retirement, if decided** (estimate): 40 to 60 test files need real edits; the rest keep passing
  while the alias period lasts.

## Sizes

Estimates, for planning only.

| Phase | Source | Tests | Docs and prompts |
|---|---|---|---|
| 1. Task technologies, packet, just-in-time architect | 5 to 7 files | 3 new classes, 2 changed | 4 prompt files, 3 docs |
| 2. Technology-resolved task gates | 6 to 8 files | 3 new classes, 3 changed | 2 docs |
| 3. Role agents and the roster switch | 6 to 8 files | 4 new classes, 8 to 12 changed | 3 new agents, 11 one-line agent edits, 3 prompt files, 4 docs |
| 4. Parity inventory, comparison runbook and runs | none | none | 1 inventory, up to 14 standards, 1 runbook, 1 ADR |
| Retirement (separate specification) | 10 to 15 files | 40 to 60 changed | 39 live docs, 84 product files |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Replace the technology agents or add role agents beside them? | Add beside them; delete nothing. | Makes every phase additive and lets the two rosters be compared on the same work. | user: explicit decision 2026-11-16 |
| D2 | Which role agents? | `server-dev`, `client-dev`, `devops-dev`, beside the existing `code-reviewer`. | The owner's proposal. The evidence shows server and client are mechanically identical once technology is on the task; a single `dev` agent is the alternative the comparison should test. | user: proposal 2026-11-16; alternative recorded for the comparison |
| D3 | Does `sql-database-architect` fold into `server-dev`? | No, not in these phases. | It is the only worker on `deep-planning` and holds 182 lines of security and platform rules. Folding it drops the model tier for schema design. Revisit in the comparison ADR. | autonomous: recommendation, pending owner approval |
| D4 | Do `test-dev`, `docs-dev` and `github-devops` stay? | Yes, serving both rosters. | `KW-ARB-MODE-002` exempts `test-dev` by name; `docs-dev` is the sole `documentation` profile and closeout names it; `github-devops` holds the publishing permission and run start. They are already roles, not technologies. | autonomous: recommendation, pending owner approval |
| D5 | Does `devops-dev` replace `github-devops`? | No. `devops-dev` writes infrastructure and pipeline files as a `worker`; `github-devops` keeps delivery. | Merging them would grant infrastructure work network read and ask-to-publish. | autonomous: recommendation, pending owner approval |
| D6 | Where does technology knowledge come from? | The stitched standards bundle of pull request 625, fetched through the docs tool. | One delivery route. The interim skill table exists only until that plan ships. | user: explicit decision 2026-11-16 |
| D7 | How does a task declare technologies? | A `Technologies:` line checked against `ontology.technologies`. | A closed list the host already owns. It does not constrain `Capabilities:`, so ADR 0080's no-vocabulary rule for capabilities stands. Inferring from file extensions alone cannot tell React from Node TypeScript, or Tauri from plain Rust. | autonomous: recommendation, pending owner approval |
| D8 | How are task gates keyed for role agents? | By the task's technologies, with fallback to the mapped technology agent's entry. | Server and client share languages, so a role key would run the wrong gates. The fallback means no host edit is needed to try the role roster. | autonomous: recommendation, pending owner approval |
| D9 | How is the roster chosen? | Host key `worker-roster`, default `technology`, with a per-run override given when the task file is written and recorded in it. | Mirrors `autonomy`. One roster per run keeps the model pick unambiguous, and the task file is the one place every later reader already opens. | autonomous: low-confidence, no authoritative source; cheap to reverse |
| D10 | How does an agent declare its roster? | Optional frontmatter key `worker-roster`; absent means both. | One line per agent, read by the embedded catalogue the picker already uses. A separate roster file was the alternative; it would put one fact in two places. | autonomous: low-confidence, no authoritative source; cheap to reverse |
| D11 | Which model profile do role agents use? | `general` for all three. | Raises the tier for five technologies on five harnesses and lowers none. The comparison uses a same-model preset, so tier does not confound it. | autonomous: recommendation, pending owner approval |
| D12 | How does the architect read standards? | Through the docs tool, per technology a task names, just in time. | Owner direction; matches ADR 0064 for workers. | user: explicit decision 2026-11-16 |
| D13 | Is retirement part of this specification? | No. Its shape is fixed here; its delivery waits for the comparison ADR and a follow-up specification. | Keeps every approved phase reversible by one configuration value. | autonomous: recommendation, pending owner approval |
| D14 | Does this need an ADR? | Yes, two: one for task technologies and technology-resolved gates (amending ADR 0080 and ADR 0082), one for the comparison result. | Both are contracts hosts depend on. | autonomous: recommendation, pending owner approval |

## Open questions

1. **D2**: keep the server and client split, or ship one `dev` agent? Recommendation: ship the split
   as asked and let the comparison decide.
2. **D3**: keep `sql-database-architect` out of `server-dev`? Recommendation: yes.
3. **D11**: `general` for all three role agents? Recommendation: yes.
4. **Order against pull request 625**: land that plan first, or accept the interim skill table?
   Recommendation: land it first if it is close; phases 1 and 2 here do not depend on it.

## Scale

`documentation-impact: extends-component`, `impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]`.

## Design harvest

| ID | Design item (section) | Target component | Target doc type |
|---|---|---|---|
| H1 | Roster, role agents, the `worker-roster` key and frontmatter key (Roster; Choosing the roster) | KyberSquad | architecture |
| H2 | Choosing and switching the roster on a host (Choosing the roster) | KyberSquad | onboarding |
| H3 | Task technologies and their validation (Task technologies) | DocGraph | reference |
| H4 | Packet standards lines and the just-in-time rule for planners (Packet delivery; Architect and product-owner) | DocGraph | runbook |
| H5 | Technology-resolved task gates and `review.technology-gates` (Task gates by technology) | ReviewCouncil | architecture |
| H6 | The new host configuration keys (Data models) | ReviewCouncil | reference |
| H7 | The roster fact and its rule (Arbiter) | KyberArbiter | architecture |
| H8 | The comparison procedure (Comparison) | KyberSquad | runbook |
| H9 | Task technologies and technology-resolved gates as a decision; the comparison result (Decisions D7, D8, D14) | KyberSquad | adr |
| H10 | Requirements 1 to 12 as delivered behaviour | KyberSquad | architecture |
