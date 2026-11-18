---
id: archive/specs/role-based-worker-agents/index
title: Role-based worker agents specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-11-17
documentation-impact: extends-component
impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]
---

# Role-based worker agents specification

**Status:** Archived, 2026-11-17

Replaces the eleven per-technology worker agents with three developer agents that differ only in
model strength (`dev-easy`, `dev-medium`, `dev-hard`). Each task carries a difficulty and its
technology. Everything a technology agent knows moves into that technology's folder (standard,
rules, skills, Arbiter rules), which ships as a template. One docs tool builds the whole prompt for
a task. The Arbiter reads its rules from role and technology folders. A read-only
`platform-researcher` replaces `azure-reader`, and a platform-neutral `devops` agent replaces
`github-devops`, with a folder per platform (GitHub, Azure DevOps, GitLab, Jira). The fourteen old
agents are removed: the comparison this specification planned was not used to decide, and anyone who
wants them installs an older version
([ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md)).

It absorbs the standards plan first drafted on
[pull request 625](https://github.com/dpalfery/kyber-weave/pull/625), which is closed.

| Document | Covers | Closeout state |
|---|---|---|
| [Requirements](requirements.md) | 13 requirements in EARS form, R1–R13 | Archived; every criterion traced to delivered work through the [tasks](tasks.md) and their tests |
| [Design](design.md) | Evidence from the code, where the evidence is against the idea, the roster, the developer agent's instructions, difficulty and the step-up, technology, role and platform folders, the task prompt tool and its budget, one technology per task, technology-resolved task gates, Arbiter rules by folder, five phases, and decisions D1–D28 | Archived; the [Decisions](design.md#decisions) table is the permanent record; delivered as designed except where [Deviations as built](#deviations-as-built) says otherwise |
| [Tasks](tasks.md) | 48 tasks in five shippable phases plus closeout, test-first, with the Test contract | Archived; every task ticked against delivered work. The machine task list was removed with the archive, per the closeout policy |

Durable content is harvested into the canonical documents named in the
[Closeout mapping](#closeout-mapping). The requirements, design and tasks remain the record of what
was intended; the canonical documents describe what ships.

## Decisions

The permanent decision table is in [design](design.md#decisions). Each decision is recorded in one
owning canonical document, linked below.

| ID | Question | Answer / decision | Recorded in |
|---|---|---|---|
| D1 | What replaces the technology agents? | Three developer agents by difficulty: `dev-easy`, `dev-medium`, `dev-hard`. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#the-agents-of-the-roster) |
| D2 | Who rates difficulty? | The architect; the decision model instead when the Arbiter runs with one. No gate checks the rating. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#how-a-tier-is-chosen) |
| D3 | What happens when a task fails? | The next attempt goes one tier up; the three-dispatch limit stays. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#how-a-tier-is-chosen) |
| D4 | Which agents are removed? | The ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader`, `github-devops`. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#the-agents-of-the-roster) |
| D5 | Are the old agents deleted at once? | Yes, on the owner's decision of 2026-11-17, without a completed comparison. | [ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md) |
| D6 | How is the model assigned? | By the agent: each agent names a model profile. The conductor has no profile. | [Model profiles](../../../kyber-squad/model-profiles.md#the-six-profiles) |
| D7 | Which model profiles change? | `dev-hard` is added; `orchestration` is removed; nothing is renamed. | [Model profiles](../../../kyber-squad/model-profiles.md#the-six-profiles) |
| D8 | What does a technology or role folder hold? | `<name>.standards.md`, `<name>.rules.md`, `<name>.skills.md`, `<name>.arbiter-rules.yml`. | [The documentation ontology](../../../documentation-ontology.md#a-technology-role-or-platform-folder) |
| D9 | What are the standard's headings? | Naming and layout; Coding standards; Testing; Commands; Security and data handling; Done checklist. | [The documentation ontology](../../../documentation-ontology.md#a-technology-role-or-platform-folder) |
| D10 | Where do the 36 Arbiter rules go? | Into `conductor`, `architect` and `reviewer` role folders, split by trigger. | [Kyber Arbiter architecture](../../../kyber-arbiter/architecture.md#where-the-rules-come-from) |
| D11 | Is any Arbiter rule locked? | No. | [Kyber Arbiter architecture](../../../kyber-arbiter/architecture.md#where-the-rules-come-from) |
| D12 | How does the task prompt reach the worker? | A dispatch hook replaces the task-id line with the full prompt; a file where the hook cannot rewrite. | [Kyber Arbiter architecture](../../../kyber-arbiter/architecture.md#dispatch-delivery-path) |
| D13 | How are task gates keyed? | By the task's one technology, then its parent, with a fallback to the old agent-keyed entries. | [Review council architecture](../../../code-review/architecture.md#task-gates) |
| D14 | Which file format? | YAML for every data file added or changed, the task file included, extension `.yml`. | [ADR 0086](../../../adr/0086-the-task-file-is-yaml.md) |
| D15 | What replaces `azure-reader`? | `platform-researcher`, which loads the skills and rules of the technology its task names. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#the-agents-of-the-roster) |
| D16 | Where does testing guidance live? | A Testing section in every standard; cross-technology rules in `shared-rules.md`. | [Coding standards](../../../standards/README.md) |
| D17 | How many specifications? | One, in five phases. | Documentation waiver (process) |
| D18 | Does this need ADRs? | Yes: three records, amending ADR 0080, ADR 0082 and ADR 0063. | [ADR 0085](../../../adr/0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md) |
| D19 | What replaces `github-devops`? | A platform-neutral `devops` agent, a `devops` role folder, and a folder per platform. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#the-agents-of-the-roster) |
| D20 | How large is the prompt budget? | 60,000 characters by default; a task must fit with nothing cut. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) |
| D21 | How many technologies may a task name? | One. A template may name one parent it builds on. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) |
| D22 | How are the prompt's parts separated? | Named tags around each part; Markdown inside. | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) |
| D23 | Is there a test role folder? | No. | [Coding standards](../../../standards/README.md) |
| D24 | Is the plan on pull request 625 a separate piece of work? | No: absorbed into phase 1, and the pull request is closed. | Documentation waiver (process) |
| D25 | How is a skills file typed and scoped? | A `skill` document type; rules and skills may name a technology or role only inside that folder. | [The documentation ontology](../../../documentation-ontology.md#a-technology-role-or-platform-folder) |
| D26 | When is the bundle stitched? | When asked, from one index lookup shared by the standards tool and the task prompt. | [Coding standards](../../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) |
| D27 | How do standards rank in search? | Standards, skills and technology rules at 0.55; the shared rules file at 1.0. | [Retrieval and ranking](../../../docgraph/retrieval.md#authority) |
| D28 | Was the comparison used to decide the removal? | No. Both rosters cost too much; the comparison is retired with its runbook. | [ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md) |

## Design harvest

Scale: `documentation-impact: extends-component`, `impact-components: [KyberSquad, ReviewCouncil,
DocGraph, KyberArbiter]`. The design's own table is in [design](design.md#design-harvest); every row
is executed, and its destination is named in the [Closeout mapping](#closeout-mapping).

| ID | Design item (section) | Target component | Target doc type | Harvested |
|---|---|---|---|---|
| H1 | The roster, the developer tiers and the removed agents (Roster) | KyberSquad | architecture | yes |
| H2 | Difficulty, its sources and the step-up (Difficulty; Choosing the agent and stepping up) | KyberSquad | architecture | yes |
| H3 | The `dev-hard` profile and the conductor without a profile (Models) | KyberSquad | reference | yes |
| H4 | Technology and role folders, the six headings, templates and contribution (Technology folders and templates) | DocGraph | reference | yes |
| H5 | Installing templates and comparing a host folder with its template (Technology folders and templates) | DocGraph | onboarding | yes |
| H6 | The task prompt, hook insertion, the file fallback, order and budget (The task prompt) | DocGraph | runbook | yes |
| H7 | Technology-resolved task gates and `review.technology-gates` (Task gates by technology) | ReviewCouncil | architecture | yes |
| H8 | Rules by folder, loading order and overrides (Arbiter rules from folders) | KyberArbiter | architecture | yes |
| H9 | The platform research agent, the `devops` agent and platform folders (The platform research agent; Roster) | KyberSquad | runbook | yes |
| H10 | The roster that is left, and why the comparison did not decide the removal (Roster switch and comparison; Removal) | KyberSquad | adr | yes |
| H11 | Migration from agent-keyed gates and removed agent names (Removal) | ReviewCouncil | runbook | yes |
| H12 | Tiers and the task prompt; rules by folder; the task file format (Decisions D1, D10, D12, D14, D18) | KyberSquad | adr | yes |
| H13 | The skill document type and where rules and skills may name a technology or role (Document types, scoping and the standards bundle) | DocGraph | reference | yes |
| H14 | The standards bundle's parts, order and search weight (Document types, scoping and the standards bundle) | DocGraph | runbook | yes |

## Closeout mapping

Every decision D1–D28 and harvest item H1–H14 maps to the living document that now carries it.

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| D1 | [Roster and developer tiers](../../../kyber-squad/architecture.md#the-agents-of-the-roster), [ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md) | extends-component | archived |
| D2 | [How a tier is chosen](../../../kyber-squad/architecture.md#how-a-tier-is-chosen), [ADR 0085](../../../adr/0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md) | extends-component | archived |
| D3 | [How a tier is chosen](../../../kyber-squad/architecture.md#how-a-tier-is-chosen) | extends-component | archived |
| D4 | [Roster and developer tiers](../../../kyber-squad/architecture.md#the-agents-of-the-roster), [ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md) | extends-component | archived |
| D5 | [ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md), [Roster switch](../../../kyber-squad/architecture.md#the-roster-switch) | extends-component | archived |
| D6 | [Model profiles](../../../kyber-squad/model-profiles.md#the-six-profiles). ADR waiver: the model already came from the agent; this restates it and adds no new contract | extends-component | archived |
| D7 | [Model profiles](../../../kyber-squad/model-profiles.md#the-six-profiles), [How a tier is chosen](../../../kyber-squad/architecture.md#how-a-tier-is-chosen) | extends-component | archived |
| D8 | [A technology, role or platform folder](../../../documentation-ontology.md#a-technology-role-or-platform-folder), [Coding standards](../../../standards/README.md) | extends-component | archived |
| D9 | [A technology, role or platform folder](../../../documentation-ontology.md#a-technology-role-or-platform-folder) | extends-component | archived |
| D10 | [Where the rules come from](../../../kyber-arbiter/architecture.md#where-the-rules-come-from), [ADR 0087](../../../adr/0087-arbiter-rules-live-in-role-and-technology-folders.md) | extends-component | archived |
| D11 | [Where the rules come from](../../../kyber-arbiter/architecture.md#where-the-rules-come-from) | extends-component | archived |
| D12 | [Dispatch delivery path](../../../kyber-arbiter/architecture.md#dispatch-delivery-path), [The inserted task prompt](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt), [ADR 0085](../../../adr/0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md) | extends-component | archived |
| D13 | [Task gates](../../../code-review/architecture.md#task-gates) | extends-component | archived |
| D14 | [ADR 0086](../../../adr/0086-the-task-file-is-yaml.md), [The inserted task prompt](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) | extends-component | archived |
| D15 | [Roster and developer tiers](../../../kyber-squad/architecture.md#the-agents-of-the-roster) | extends-component | archived |
| D16 | [Coding standards](../../../standards/README.md) | extends-component | archived |
| D17 | Documentation waiver — process: how a specification is split into phases leaves no lasting architectural, operational or behavioural invariant | waiver (process) | archived |
| D18 | [ADR 0085](../../../adr/0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md), [ADR 0087](../../../adr/0087-arbiter-rules-live-in-role-and-technology-folders.md), [ADR 0086](../../../adr/0086-the-task-file-is-yaml.md) | extends-component | archived |
| D19 | [Roster and developer tiers](../../../kyber-squad/architecture.md#the-agents-of-the-roster) | extends-component | archived |
| D20 | [The inserted task prompt](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt), [Task prompt budget](../../../configuration.md#task-prompt-budget) | extends-component | archived |
| D21 | [The inserted task prompt](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) | extends-component | archived |
| D22 | [The inserted task prompt](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) | extends-component | archived |
| D23 | [Coding standards](../../../standards/README.md) | extends-component | archived |
| D24 | Documentation waiver — process: absorbing a closed pull request's plan into a phase is scheduling, and the plan itself is closed | waiver (process) | archived |
| D25 | [A technology, role or platform folder](../../../documentation-ontology.md#a-technology-role-or-platform-folder) | extends-component | archived |
| D26 | [A standard is read through the tool](../../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) | extends-component | archived |
| D27 | [Authority](../../../docgraph/retrieval.md#authority) | extends-component | archived |
| D28 | [ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md), [Roster and developer tiers](../../../kyber-squad/architecture.md#12-the-worker-roster-and-developer-tiers) | extends-component | archived |
| H1 | [Roster and developer tiers](../../../kyber-squad/architecture.md#the-agents-of-the-roster) | extends-component | archived |
| H2 | [How a tier is chosen](../../../kyber-squad/architecture.md#how-a-tier-is-chosen) | extends-component | archived |
| H3 | [Model profiles](../../../kyber-squad/model-profiles.md#the-six-profiles) | extends-component | archived |
| H4 | [A technology, role or platform folder](../../../documentation-ontology.md#a-technology-role-or-platform-folder), [Coding standards](../../../standards/README.md) | extends-component | archived |
| H5 | [Initialize](../../../docgraph/onboarding.md#1-initialize) | extends-component | archived |
| H6 | [The inserted task prompt](../../../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt), [Task prompt budget](../../../configuration.md#task-prompt-budget), [Prompt delivery by harness](../../../kyber-squad/architecture.md#prompt-delivery-by-harness) | extends-component | archived |
| H7 | [Task gates](../../../code-review/architecture.md#task-gates) | extends-component | archived |
| H8 | [Where the rules come from](../../../kyber-arbiter/architecture.md#where-the-rules-come-from) | extends-component | archived |
| H9 | [Roster and developer tiers](../../../kyber-squad/architecture.md#the-agents-of-the-roster) | extends-component | archived |
| H10 | [ADR 0088](../../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md), [Roster comparison runbook](../../../kyber-squad/roster-comparison-runbook.md) (superseded, kept for history) | extends-component | archived |
| H11 | [Task gates](../../../code-review/architecture.md#task-gates), [Configuration](../../../configuration.md) | extends-component | archived |
| H12 | [ADR 0085](../../../adr/0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md), [ADR 0087](../../../adr/0087-arbiter-rules-live-in-role-and-technology-folders.md), [ADR 0086](../../../adr/0086-the-task-file-is-yaml.md) | extends-component | archived |
| H13 | [A technology, role or platform folder](../../../documentation-ontology.md#a-technology-role-or-platform-folder) | extends-component | archived |
| H14 | [Authority](../../../docgraph/retrieval.md#authority), [A standard is read through the tool](../../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) | extends-component | archived |
| RESIDUAL | none: every follow-up the delivery found is either shipped or recorded in an ADR | — | archived |

## Deviations as built

Checked against the shipped tree at closeout. Where the implementation differs from the design, the
implementation is the truth and the canonical documents describe it.

| Area | Specified | As built |
|---|---|---|
| `worker-roster` in the Config Reg | The design's data-model table gives it a `<worker-roster>` property in the generated block | The constant exists (`ConfigRegConfig.WorkerRosterProperty`), but the generated `AGENTS.md` block publishes no line and no instruction resolves one; the task file is the record |
| `devops` role folder | A `devops` role folder ships and a host may override it | The folder ships, but only `conductor`, `architect` and `reviewer` role files are read — `ArbiterConfigLoader` holds that list and `ontology.roles` does not feed it |
| Antigravity prompt insertion | The design counts it as a rewrite | It inserts through `overwrite`; the live check that the harness applies the shallow merge has not run, so the row says so |
| Legacy JSON task file | Read for two minor releases | Read, with `KW-DOC-TASKS-014` and `docs tasks <plan> --convert` |
| Test-first second author | `test-dev` removed; the council is the independent check | Delivered: the developer tier writes code and tests, and the council's `test-adequacy` lens reads every added test as an outsider |
| Delivery platforms | A folder per platform for GitHub, Azure DevOps, GitLab, Jira | Folders ship; product code still delivers to GitHub only, so the other three are instruction until a later change |

## Content judged obsolete

These parts of [requirements](requirements.md) and [design](design.md) are not harvested: each was
true of the tree or the plan when written and has no surviving invariant a future reader must
consult. They stay in this archive as the record of the case that was made.

| Content | Why it is obsolete |
|---|---|
| Design, *Evidence* → *The agents today*, *Difficulty, retries and models today*, *Rules, gates and the task prompt today*, *Size of the references* | They count the tree at `b26ca736` before the change; every count is now false, and the decisions they supported are in ADR 0085 and ADR 0088 |
| Design, *Where the evidence is against the idea* 8 ("routing was rebuilt three days ago") | A statement about the authoring week, not about the product. The durable half — difficulty now picks the tier — is in [How a tier is chosen](../../../kyber-squad/architecture.md#how-a-tier-is-chosen) |
| Design, *Developer agent instructions* (the whole proposed agent file) | Superseded by the shipped `products/kyber-squad/agents/dev-medium.md`, which is the file three tiers render. The durable rules inside it — no technology named, the prompt is the brief, the stopping rule that feeds the step-up — are in [Roster and developer tiers](../../../kyber-squad/architecture.md#the-agents-of-the-roster) |
| Design, *Phases* and *Sizes* | Planning estimates for delivery; the phases shipped and merged, and the estimates describe effort rather than behaviour |
| Design, *Testing strategy* | Estimates of new and changed test classes; the shipped classes are the truth and are pinned by the test projects |
| Requirements, *Owner decisions* list and *Terms* | The decisions are D1–D28 and the terms are defined in [the ontology](../../../documentation-ontology.md#a-technology-role-or-platform-folder) and the roster section; a second copy would drift |
| Both artifacts, *Phase status* approval provenance | Process record. The approval it names is the owner's, stated in this archive |
