---
id: plans/2026-10-28-bluf-sbar-response-format
title: "BLUF/SBAR user-facing response format for Kyber-Squad"
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-28
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberSquad, DocGraph]
---

# BLUF/SBAR user-facing response format for Kyber-Squad

**Status: Archived — complete 2026-10-28.** Delivered in one test-first change: the
`response-format` skill with its `conductor` and `bug-crusher` bindings, the default-off
`intake` flag and its Config Reg mode property, the user-home guidance deploy on
`squad update --global`, the documentation harvest, and the Squad release verification. Every
task T1–T7 is green ([§6](#6-tasks)); the closeout mapping is in the Closeout section at the
end of this file.

**Date:** 2026-10-28. **Archive date:** 2026-10-28. **Scale:** `extends-component`, impact
components KyberSquad and DocGraph — the declared scale was confirmed at the
approve-and-execute gate, and the scale plus components were restated there for the owner.

**Approval:** AP1 — the user chose approve and execute at the conductor-presented gate on
2026-10-28, after [§11](#11-proposed-default-user-home-guidance-text-for-gate-review) was
presented as its own review item and the user's own §11 opening line was folded in.

**Development mode:** `test-first`. Every behaviour scope had a RED task that writes failing
contract tests before any implementation; the GREEN tasks that followed make those tests pass
without weakening them.

**Execution precondition: satisfied 2026-10-28.** The checkout fast-forwarded
`3ec6a3e` → `823358d` — 63 commits, more than the one-commit sync originally
anticipated. The canonical-profiles user-home staging the precondition anticipated exists on
the new base: `src/KyberWeave.Core/Squad/Presets/SquadCanonicalProfileStore.cs` (schema
`kyber-squad.canonical-profiles/v1`, snapshots at `<home>/.kyber-weave/squad/profiles/`),
written by `SquadLifecycleService` and read by `SquadCommandComposition`; T5 mirrors it.
Validation on the new base, run 2026-10-28 by the conductor's runner: release build clean
(0 warnings), docs validate 0 findings, docs drift 0 findings.

---

## 1. Problem and goal

### 1.1 Problem

Running as many as ten tasks at once makes a session hard for a human to hold in mind. The
squad's conversational agents (`conductor`, `bug-crusher`) narrate progress and ask questions
in free-form prose, so the human re-reads the thread to reconstruct what work is happening,
why, and what decision is actually being asked — the most expensive cognitive step in the
loop. Three aggravating factors:

- No format contract exists anywhere in the canonical tree. The only prior art is a personal,
  single-harness guide (`~/.claude/CLAUDE.md`, "How to respond to Dave") that the squad
  neither ships nor enforces on any target, and which names a person.
- The conductor's intake assessment runs on every unstated request by default; at ten
  concurrent tasks that is a per-request round trip the user does not want by default.
- The deployed agent and skill files under `~/.factory/droids/` and `~/.factory/skills/`
  are render outputs of this repository's canonical tree at `products/kyber-squad/`; a
  contract must be authored there and released, not edited in place at the target.

### 1.2 Goal

- Every user-facing response from a primary conversational squad agent states, in order:
  1. **Title** — the work being done, one line.
  2. **Bottom line up front** — maximum 3 sentences, written for a highly experienced
     developer with **no prior involvement**: situation, background, assessment,
     recommendation, in as few words as possible.
  3. **Decisions needed** — what must be decided, whenever a decision is open.
- Bullets for scannability. Status statements follow the same shape as questions.
- The structure is **never labelled or referred to** in the response itself — no `BLUF:`
  tags, no `Situation:`/`Background:` headers, no meta-commentary about the format (A7).
- The format standard is carried by a **canonical squad skill** (`response-format`) that the
  user-facing agents are instructed to load by name (Q1), not by duplicated inline text.
- The how-to-talk-to-the-user guidance **also ships to the global user-level
  `~/.kyber-weave/` folder**, name-neutral, default content plus user override, installed
  and refreshed by the squad CLI the way model profiles are staged today (Q3).
- The conductor's intake assessment is **default-OFF**, enabled per repository by
  `intake: true` in `.kyber-weave/kyber-weave.yml`, rendered to the Config Reg so conductor
  bodies can read it; when off or absent, unstated requests go straight to the plan path
  (A6).
- The session title reflects the work wherever the harness permits an agent to set it (A4).

---

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| A1 | Artifact choice: plan or spec? | **PLAN.** Bounded change to the established Kyber-Squad product plus a small DocGraph config surface; the enforcement surfaces (canonical skills, Config Reg, squad CLI) already exist. | Intake assessment 2026-10-28. The scope grew after intake (CLI + config), but each added surface is a narrow extension of an established mechanism, not new architecture. | ANSWERED. User decision, relayed by conductor 2026-10-28 |
| A2 | Enforcement mechanism: hooks, instructions, or hybrid? | **Instruction-body contracts in the canonical squad tree.** Deterministic hooks deferred; no hooks layer is built. The required ordered response sections make the format structure checkable, which is the accepted partial determinism. | No hook surface exists in the squad's release model: `agent.schema.json` has no hooks field, the capability lattice declares only filesystem/process/network/delegate grants, and Factory `settings.json` has no hooks key. Observed hooks elsewhere (Claude Code `PreToolUse`, Copilot `.github/hooks/`) intercept tool calls, not assistant prose. Instructions are probabilistic; the user accepted this. | ANSWERED. User decision, relayed by conductor 2026-10-28 |
| A3 | Scope: which agents carry the format? | **All user-facing agents; headless/machine-facing specialists exempt.** Frontmatter enumeration: exactly three agents are `invocation: primary` — `conductor`, `bug-crusher`, `product-owner`. The standard applies to `conductor` and `bug-crusher`; `product-owner` is exempt (machine-consumed headless digest). Every specialist digest the conductor relays to the human is presented in the format at the relay point. | Edge case recorded: a human invoking `product-owner` directly receives its raw headless digest. Accepted — its digest schema is machine-consumed by design. All other agents are `invocation: subagent` and exempt. | ANSWERED. User decision, relayed by conductor 2026-10-28; exemption scope confirmed at approve-and-execute gate 2026-10-28 |
| A4 | Session title: set to reflect the work? | **Yes, wherever a harness permits; marked honestly infeasible where it does not.** The skill carries a conditional rule plus a per-harness feasibility matrix. | Factory: no agent-settable title mechanism is evidenced — `sessions-index.json` titles derive from the conversation's first message; no rename tool or config was found. The user's rationale ("if it is set based on the first words, it should be able to be set based on the next words") is recorded; the skill must not fabricate a mechanism. Other targets: unverified — the rule applies only on confirmed mechanisms. | ANSWERED. User decision, relayed by conductor 2026-10-28 |
| A5 | Development mode? | **test-first.** Default; no user opt-out. | RED contract tests exist before any content or code lands (T1); the CLI/config scope carries its own Test contract rows (§5). | ANSWERED. User decision (default), relayed by conductor 2026-10-28 |
| A6 | Intake assessment on by default? | **Default OFF.** `.kyber-weave/kyber-weave.yml` gains an `intake` boolean (absent or false = off). `docs init` renders **<intake-assessment>** into the Config Reg block only when true, as a **mode property** (value `enabled`), because Config Reg values that are paths are path-checked (`KW-CONFIG-REG-001`); a mode entry is exempt from the path check but still covered by the stale-rendering check. Conductor resolution order: read **<intake-assessment>** from the repository root `AGENTS.md` Config Reg; present → run the intake assessment for unstated requests; absent → skip the assessment and take the plan path directly (default to plan). Defect-first precedence is unchanged. | Mirrors the existing **<autonomy>** resolution-order pattern in the conductor body. The Config Reg mode-property kind is a small DocGraph change with its own tests (T1/T4). This repository leaves `intake` unset: the flag ships default-off, and enabling it here is a separate deliberate act. | ANSWERED. User requirement, relayed by conductor 2026-10-28; mechanism derived here |
| A7 | May agents label the style? | **No.** Agents use the title / bottom-line-up-front / decision structure but never state or label the style: no `BLUF:`/`SBAR:` tags, no section headers for situation/background/assessment/recommendation, no meta-commentary about the format. Encoded in the skill's normative rule. | The structure is for the reader; naming it spends the reader's attention on the format instead of the work. | ANSWERED. User requirement, relayed by conductor 2026-10-28 |
| Q1 | One shared format standard, or per-agent copies? The user countered: why not a skill each agent is instructed to use? | **Skill route adopted.** A new canonical squad skill `products/kyber-squad/skills/response-format/` carries the standard; `conductor` and `bug-crusher` bodies instruct loading it **by name** for every user-facing response. | The resource-closure rule does NOT block this: closure governs local *path links* in a body (resolved only under the owning artifact's directory), while skills are invoked by name. Established precedent in the canonical tree: `agents/bug-crusher.md` says "Load the `bug-crusher` skill and follow it", and `skills/bug-crusher/SKILL.md` references `task-gate` and `test-dev` by name. Skills are a deployed artifact class reaching all thirteen renderers, so the standard ships everywhere the agents do. This supersedes the earlier inline-binding and `standards/response-format.md` designs; duplication — the drift the bug-crusher wrapper itself warns about — is avoided. | ANSWERED. User counter-proposal, relayed by conductor 2026-10-28; closure constraint verified against product README §6 and live bodies |
| Q2 | Supersede the personal guide `~/.claude/CLAUDE.md` ("How to respond to Dave")? | **Leave it alone.** It governs the Claude Code root assistant, a surface the squad does not deploy; the overlap with the new standard is reinforcement, not drift. | Superseding would make the squad release own a personal file it does not ship, which would drift on every reinstall. | ANSWERED. User decision, relayed by conductor 2026-10-28 |
| Q3 | Where does the user-customizable how-to-talk-to-the-user guidance live, and how does it ship? | **Global user-home `~/.kyber-weave/response-guidance.md`: name-neutral, default content plus user override, installed and refreshed by the squad CLI.** The canonical default source is a resource inside the `response-format` skill tree (`skills/response-format/user-guidance.md`, linked from `SKILL.md` so closure stays clean). `squad update --global` deploys it to `~/.kyber-weave/response-guidance.md` and refreshes it the way model profiles are staged: if the deployed file is absent or unmodified (digest matches the shipped default), it is replaced with the current default; if the user has edited it, it is preserved and the run reports the file was kept. The skill instructs: where `~/.kyber-weave/response-guidance.md` exists, the user's file overrides the skill's default wording. The proposed default text is in [§11](#11-proposed-default-user-home-guidance-text-for-gate-review) and **was presented for review at the approve-and-execute gate** (2026-10-28; user-authored text folded in — see §11 and AP1). | Model-profile precedent: the squad already stages versioned `kyber-squad.canonical-profiles/v1` snapshots at `~/.kyber-weave/squad/profiles/` (rc.8/rc.9 observed); that staging code was absent at authoring time (behind `origin/main`) and is present on the post-sync base — `SquadCanonicalProfileStore` — per ES1. User-home ownership follows the established owned-file + receipt pattern (ADR 0026's owned-files principle; global squad state per ADR 0037). A dedicated `squad guidance` verb was considered and deferred: `squad update --global` is already the user-home refresh entry point the user cited; one more verb for one file is not warranted until proven needed. | ANSWERED (mechanism). User decision relayed 2026-10-28; default text reviewed and user-authored at the approve-and-execute gate 2026-10-28 |
| AP1 | Approve and execute? | **Approved.** The user chose approve and execute at the conductor-presented gate, 2026-10-28, with the declared scale and components stated (extends-component; KyberSquad, DocGraph) and §11 presented as its own review item; answer: "Fold in my §11 text, then approve and execute." The user's §11 opening line ("Lead with the bottom line up front (BLUF).") was folded into §11 before approval. | A Ready plan is execution authority. This row is the recorded gate approval and permanent state; execution begins only after the conductor's finalization revalidation (§10). | ANSWERED. User approval at conductor-presented gate 2026-10-28 |
| ES1 | Post-sync corrections (environment)? | **Adopted; no gate reopened.** Fast-forward `3ec6a3e` → `823358d` (63 commits, not the one-commit sync the header anticipated). Execution precondition satisfied: `src/KyberWeave.Core/Squad/Presets/SquadCanonicalProfileStore.cs` exists (schema `kyber-squad.canonical-profiles/v1`, snapshots at `<home>/.kyber-weave/squad/profiles/`), written by `SquadLifecycleService`, read by `SquadCommandComposition`. T6's required skill corrected for the upstream 95412c0 skill renames: `kyber-weave-docs` → `kyber-docs-authoring`, and the harvest needs only that renamed authoring skill (`kyber-docs-onboarding-and-assessment`, which absorbed onboarding/assessment scope, is not required for corpus-section authoring). Plan index row disambiguated: the intake assessment **may be** enabled per repository — this repository leaves `intake` unset, per A6. | No scope, mode, or Test-contract change. The primary-agent enumeration was re-verified on the new base (still exactly `conductor`, `bug-crusher`, `product-owner`), `SquadCanonicalContentTests.cs` still exists as T1a's pinning surface (test-dev is authoring RED against it in parallel — untouched here), and the plan body needed only stale-reference corrections. Validation on the new base, run 2026-10-28: build 0 warnings, docs validate 0 findings, docs drift 0 findings. | ANSWERED. Environment sync facts, provenance conductor 2026-10-28 |

---

## Design harvest

**Declared scale:** `documentation-impact: extends-component`,
`impact-components: [KyberSquad, DocGraph]`. The change extends the existing Kyber-Squad
instruction surface (new skill, agent bindings, CLI deploy) and the DocGraph Config Reg
surface (mode property). No new component is created.

| ID | Target component | Target doc type | Destination | Durable design to harvest |
|---|---|---|---|---|
| H1 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` | The response-format contract (required ordered shape, no-labels rule, applicability, conductor relay) and its skill-carrier mechanism: skills invoked by name are not subject to the body resource closure; the user-home `response-guidance.md` default+override deploy semantics on `squad update --global`. |
| H2 | KyberSquad | requirements | `docs/kyber-squad/requirements.md` | Requirement: every user-facing response from a primary conversational squad agent carries the format; specialist digest schemas stay machine-consumed; intake assessment is default-off and repo-opt-in via Config Reg. |
| H3 | KyberSquad | onboarding | `docs/kyber-squad/onboarding.md` | The user-home guidance file: where it lands, how `squad update --global` installs/refreshes it, that user edits are preserved, and how a repo enables the intake assessment. |
| H4 | DocGraph | architecture | `docs/docgraph/architecture.md` | Config Reg mode properties: an entry kind whose value is a mode word, exempt from the path-existence check (`KW-CONFIG-REG-001`) but covered by the stale-rendering check (`KW-CONFIG-REG-002`); `intake-assessment` as the first instance. |

**Harvest status at closeout (2026-10-28):** H1–H4 landed in the four destinations above —
[architecture, the response format and its skill carrier](../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier)
and [home-owned files](../kyber-squad/architecture.md#home-owned-files-and-the-user-home-response-guidance);
[requirements, user-facing response format](../kyber-squad/requirements.md#user-facing-response-format)
and [intake assessment](../kyber-squad/requirements.md#intake-assessment);
[onboarding, user-home response guidance](../kyber-squad/onboarding.md#user-home-response-guidance)
and [intake assessment opt-in](../kyber-squad/onboarding.md#intake-assessment-opt-in-per-repository);
[DocGraph architecture, mode properties](../docgraph/architecture.md#mode-properties-are-not-paths).

---

## 4. Investigation findings

- **Canonical source location.** `products/kyber-squad/` is the canonical source for the
  agents and skills, rendered to thirteen harness targets (product README). The deployed
  `~/.factory/droids/conductor.md`, `~/.factory/skills/bug-crusher/SKILL.md`, etc. are
  render outputs; edits go to the canonical tree.
- **User-facing enumeration (fact, not design).** `invocation: primary` frontmatter scan
  over `products/kyber-squad/agents/*.md` (this checkout) yields exactly `conductor`,
  `bug-crusher`, `product-owner`; all other agents are `invocation: subagent`. Note: the
  released rc.9 model-profile snapshot also lists `go-dev`, `rust-dev`, `typescript-dev` —
  canonical on `origin/main` at authoring time, now present on the synced base; they are
  worker subagents either way and not user-facing. The `invocation: primary` scan was
  re-run on the post-sync base (`823358d`) and still yields exactly the three primary
  agents.
- **Skill invocation by name is established and closure-safe.** `agents/bug-crusher.md`
  instructs "Load the `bug-crusher` skill and follow it"; `skills/bug-crusher/SKILL.md`
  references `task-gate` and `test-dev` by name. The resource-closure rule (product README
  §6) governs local *path links* only — it does not constrain naming a skill, so the
  standard can live in one skill referenced by every user-facing agent (Q1).
- **`bug-crusher` role lives in the skill.** The wrapper agent body carries no role text
  ("the skill is the role"), so the bug-crusher binding is an invocation line in the wrapper
  plus a rule in `skills/bug-crusher/SKILL.md` §6 and the human-question points. The skill
  is one of the six *explicitly evolved* skills, so the Hotshot golden-byte comparison does
  not apply to it.
- **Config Reg values are path-checked.** `ConfigRegValidator` fails any entry whose value
  does not resolve on disk (`KW-CONFIG-REG-001`), so a boolean intake flag cannot ride the
  existing additions mechanism as a free-form value: a **mode-property entry kind** with a
  path-check exemption is required (A6). The stale-rendering check (`KW-CONFIG-REG-002`)
  continues to cover it. Config Reg properties are declared in
  `src/KyberWeave.Core/Configuration/ConfigRegConfig.cs` and rendered into the repo root
  `AGENTS.md` block by `docs init`.
- **Model-profile staging precedent.** `~/.kyber-weave/squad/profiles/` holds versioned
  `kyber-squad.canonical-profiles/v1` snapshots (rc.8, rc.9) written by the squad CLI at
  global scope — the "model profile with the cli" mechanism the user cited. That staging
  code is `src/KyberWeave.Core/Squad/Presets/SquadCanonicalProfileStore.cs` on the
  post-sync base (`3ec6a3e` → `823358d`, 63 commits): execution precondition satisfied
  (header), mirrored by T5.
- **No hooks anywhere in the release model.** `schemas/agent.schema.json`, the capability
  lattice, and Factory `settings.json` carry no hook surface; observed hooks on other
  harnesses intercept tool calls, not prose (A2 evidence).
- **Session title evidence.** Factory `sessions-index.json` carries a `title` per session,
  apparently derived from the conversation's first message; no agent-facing set-title
  mechanism was found. No per-harness title mechanism is verified for any target; the
  skill's matrix starts honest: factory — not agent-settable.
- **Digests are computed, not hand-edited.** The product README's immutable-digest rule
  means body edits recompute digests automatically; `migration/*.md` records are baseline
  import logs, and `SquadCanonicalContentTests` owns roster and provenance expectations
  (the skills roster gains `response-format` in T1).
- **Review policy consequence.** `.kyber-weave/kyber-weave.yml` `review.policy.always-human`
  includes `products/kyber-squad/agents/**`, so the review council escalates this change to
  the human on path alone. Intended policy for instruction-surface edits, not a defect.
- **Discovery:** codegraph 0, docs 0, direct reads 24. CodeGraph and kyber-weave docs MCP
  tools were unavailable in this session (the harness deferred-tool list contained only
  WebSearch and FetchUrl), so direct reads were the permitted fallback, stated here and in
  the intake digest. Direct reads of indexed code, with reasons:
  `src/KyberWeave.Core/Docs/Validation/DocSpecValidator.cs` (confirm plan frontmatter
  vocabulary, per-doc-type requirements, and unknown-key tolerance);
  `src/KyberWeave.Core/Docs/Validation/ConfigRegValidator.cs` (confirm every Config Reg
  value is path-checked, which forces the mode-property design in A6);
  `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` (identify the canonical-content
  pinning pattern and skills roster for the Test contract). Other reads were of the
  unindexed product tree, squad state at `~/.kyber-weave/squad/` (model-profile staging
  evidence), the plan corpus and index, `docs/catalog.md`, `.kyber-weave/kyber-weave.yml`,
  the architect role references, and personal harness configuration under
  `/Users/dave/.factory` and `/Users/dave/.claude`.

---

## 5. Test contract

Development mode: `test-first`. RED precedes GREEN for every behaviour scope; the docs
harvest and release-verification tasks are explicitly no-unit-test tasks whose verification
is named.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1a | `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadCanonicalContentTests"` | The format contract is pinned in canonical sources: the `response-format` skill exists in the expected skills roster; `skills/response-format/SKILL.md` carries the ordered required-response markers, the no-label rule, the user-home override rule, and the session-title matrix; `agents/conductor.md` carries the skill-invocation line, the relay rule, and the **<intake-assessment>** resolution; `agents/conductor/references/intake-path.md` carries the default-off semantics; `agents/bug-crusher.md` and `skills/bug-crusher/SKILL.md` carry the invocation line. | The new pins fail against the unmodified tree — the skill and body sections do not exist — with assert messages naming the missing artifact or section. | The same pins pass after T2/T3 without weakening any assertion; the pre-existing suite is unaffected. |
| T1b | Config Reg / config loading tests (same test project) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ConfigReg"` | With `intake: true`, config resolution produces an **<intake-assessment>** mode entry whose value is `enabled` and `docs init` renders it into the Config Reg block; with the key absent or false, no entry is produced; `ConfigRegValidator` raises no `KW-CONFIG-REG-001` for a mode entry but still catches a stale rendering (`KW-CONFIG-REG-002`). | The new tests fail today: the config key is unknown and no mode entry kind exists, so nothing renders and the path check has no mode exemption. | The same tests pass after T4 without weakening; path-valued entries are still path-checked. |
| T1c | Squad user-home deploy tests (same test project) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Squad"` | `squad update --global` deploys the default guidance to `<home>/.kyber-weave/response-guidance.md`; a second run replaces an unmodified file with the current default; a user-modified file is preserved and reported; the receipt owns the file; all tests use a fake home under the temp directory. | The new tests fail today: the command deploys no guidance file. | The same tests pass after T5 without weakening; no test touches the real home directory. |
| T6 | No unit test — explicit no-test task | `dotnet run --project src/KyberWeave.Cli -c Release --no-build -- docs validate .` and `... docs drift .` | The harvested contract appears in `docs/kyber-squad/architecture.md`, `docs/kyber-squad/requirements.md`, `docs/kyber-squad/onboarding.md`, and `docs/docgraph/architecture.md` conformant with the corpus. | n/a (verification task) | Both checks report zero findings. |
| T7 | No unit test — explicit no-test task | `./scripts/update-loop.sh` (per `docs/distribution.md`) | The Squad release path builds and renders; a factory render of conductor and bug-crusher carries the new bindings; the user-home guidance deploy runs at global scope. | n/a (verification task) | The loop exits clean; rendered bodies contain the invocation lines. |

---

## 6. Tasks

### T1 — RED: all contract tests

- **Objective:** Write the failing contract tests for all three behaviour scopes (T1a
  canonical-content pins including the `response-format` roster addition; T1b intake-flag
  config and Config Reg mode-property tests; T1c user-home guidance deploy tests with
  temp-home isolation), exactly the observable behaviours in §5.
- **Files:** `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`; the Config Reg test
  file(s) for the chosen fixtures; the Squad state/CLI test file(s) housing the deploy tests.
- **Acceptance criteria:** The new tests compile and fail on the current tree for precisely
  the missing behaviour; assert messages name the missing artifact, entry, or section; no
  existing assertion is weakened; no test touches the real home directory (fake home under
  `Path.GetTempPath()`, per the squad-home guard-test pattern).
- **Dependencies:** none.
- **Required skills:** test-dev; task-gate.

### T2 — GREEN: `response-format` skill + conductor binding + intake flag routing

- **Objective:** Author the canonical skill and bind conductor to it; encode the intake
  flag's routing semantics.
- **Files:** `products/kyber-squad/skills/response-format/SKILL.md` (new);
  `products/kyber-squad/skills/response-format/user-guidance.md` (new, linked from
  `SKILL.md` — same directory, so the resource closure stays clean; this file is also the
  deploy source for Q3); `products/kyber-squad/agents/conductor.md`;
  `products/kyber-squad/agents/conductor/references/intake-path.md`.
- **Content requirements:** The skill defines the required ordered response shape (Title →
  bottom line, max 3 sentences, written for a highly experienced developer with no prior
  involvement, no assumed context or unexplained names/ids → open decisions), SBAR writing
  guidance (situation/background/assessment/recommendation in as few words as possible),
  scannability rules (bullets; status statements use the same shape as questions), the
  no-label rule (A7: never tag or refer to the format in a response), the session-title
  matrix (factory: not agent-settable — derived from the conversation's first message; other
  targets: apply only where a mechanism is confirmed), and the user-home override rule
  (where `~/.kyber-weave/response-guidance.md` exists, the user's file overrides the skill's
  default wording). `user-guidance.md` carries the name-neutral default text from §11.
  `conductor.md` gains: a line instructing the agent to load the `response-format` skill and
  follow it for every user-facing response; the relay rule (every specialist digest
  surfaced to the human is presented in the format; digest schemas unchanged); the
  **<intake-assessment>** resolution order (absent → skip the intake assessment, take the
  plan path directly for unstated requests; present → run the assessment; defect-first
  precedence unchanged). `intake-path.md` records the default-off semantics and the flag.
- **Acceptance criteria:** T1a pins pass; agent frontmatter unchanged; the skill's local
  links stay under its own directory (closure clean); no harness-specific machinery beyond
  the conditional session-title rule; bodies render to all thirteen targets.
- **Dependencies:** T1.
- **Required skills:** task-gate.

### T3 — GREEN: bug-crusher binding

- **Objective:** Apply the same binding to the bug-crusher role.
- **Files:** `products/kyber-squad/agents/bug-crusher.md`;
  `products/kyber-squad/skills/bug-crusher/SKILL.md`.
- **Content requirements:** The wrapper gains the same load-the-`response-format`-skill line
  (single wording with T2); the skill's §6 Report and every human-question point adopt the
  required shape; the investigator verdict schema is untouched (machine-consumed); no-label
  rule applies.
- **Acceptance criteria:** T1a pins pass; the skill remains an explicitly evolved skill (no
  golden-byte constraint); investigation/escalation mechanics unchanged.
- **Dependencies:** T1.
- **Required skills:** task-gate.

### T4 — GREEN: intake flag in config and Config Reg (DocGraph)

- **Objective:** Implement the `intake` config key and the Config Reg mode property.
- **Files:** `src/KyberWeave.Core/Configuration/` (config schema and `OntologyYamlSection`
  hyphenated key; `ConfigRegConfig`); `src/KyberWeave.Core/Docs/Validation/ConfigRegValidator.cs`
  (mode entries exempt from the path check); `src/KyberWeave.Core/Docs/Scaffolding/`
  (Config Reg block rendering of mode entries); CLI composition as needed.
- **Acceptance criteria:** T1b tests pass: `intake: true` renders **<intake-assessment>**
  with value `enabled` and only then; absent/false renders nothing; `KW-CONFIG-REG-001`
  does not fire for mode entries; `KW-CONFIG-REG-002` still catches a stale block;
  path-valued entries behave exactly as before; config merges preserve the replacement
  semantics (Core AGENTS.md rule).
- **Dependencies:** T1.
- **Required skills:** csharp-dev; task-gate.

### T5 — GREEN: user-home guidance deploy (squad CLI)

- **Objective:** Deploy and refresh the user-home response-guidance file, mirroring the
  model-profile staging mechanism.
- **Files:** the squad CLI/Core surface for `squad update --global` — on the post-sync
  base, the staging to mirror lives in
  `src/KyberWeave.Core/Squad/Presets/SquadCanonicalProfileStore.cs` (written by
  `SquadLifecycleService`, read by `SquadCommandComposition`); exact edit sites are located
  at execution against that code.
- **Content requirements:** `squad update --global` deploys
  `skills/response-format/user-guidance.md` (canonical default from T2) to
  `<home>/.kyber-weave/response-guidance.md`; refresh replaces the file only when its
  digest matches the previously shipped default; a user-modified file is preserved and the
  run reports it; the file is receipt-owned; the command never writes outside the resolved
  home root.
- **Acceptance criteria:** T1c tests pass (temp-home isolation, refresh, preserve-on-edit,
  receipt ownership); the mechanism mirrors the canonical-profiles staging observed in
  `~/.kyber-weave/squad/profiles/`.
- **Dependencies:** T1, T2 (consumes the canonical default file).
- **Required skills:** csharp-dev; task-gate.

### T6 — Documentation harvest

- **Objective:** Record the durable design per the Design harvest table into the canonical corpus.
- **Files:** `docs/kyber-squad/architecture.md`; `docs/kyber-squad/requirements.md`;
  `docs/kyber-squad/onboarding.md`; `docs/docgraph/architecture.md`.
- **Acceptance criteria:** `docs validate .` and `docs drift .` both report zero findings;
  harvested sections state the contract, skill-carrier mechanism, user-home deploy
  semantics, session-title matrix, and intake flag semantics.
- **Dependencies:** T2, T3, T4, T5 (consumes their final wording and mechanisms).
- **Required skills:** kyber-docs-authoring; task-gate. (Post-sync correction: upstream
  95412c0 renamed `kyber-weave-docs` → `kyber-docs-authoring`; the harvest is
  corpus-section authoring under the current authoring skill, so
  `kyber-docs-onboarding-and-assessment` — which absorbed onboarding/assessment scope — is
  not required.)

### T7 — Release verification

- **Objective:** Verify the Squad release path, because the canonical instruction surface
  and CLI changed (repository AGENTS.md requirement).
- **Files:** none modified; verification only.
- **Acceptance criteria:** `./scripts/update-loop.sh` exits clean (needs `node` and `npm`
  on PATH, per `docs/distribution.md`); a factory render of conductor and bug-crusher under
  the loop's output contains the invocation lines; the global user-home deploy runs.
- **Dependencies:** T2, T3, T4, T5.
- **Required skills:** task-gate.

---

## 7. Dependency graph

```text
T1 (RED: pins + config + deploy tests)
 ├─→ T2 (skill + conductor + intake routing) ─┬─→ T5 (user-home deploy) ─┐
 ├─→ T3 (bug-crusher binding) ───────────────┼──────────────────────────┼─→ T6 (docs harvest)
 └─→ T4 (intake flag: config + Config Reg) ───┴──────────────────────────┴─→ T7 (release verification)
```

- T2, T3, T4 run in parallel after T1 (disjoint file scopes).
- T5 waits for T2 (consumes the canonical default guidance file).
- T6 and T7 run in parallel after T2–T5 (T6 consumes final wording; T7 needs final content
  to render and the CLI to deploy).
- `review.max-concurrent-workers` (default 4) accommodates the widest tier.

---

## 8. Risks and out-of-scope boundaries

### Risks

- **Sync landed.** The 63-commit fast-forward (`3ec6a3e` → `823358d`) satisfied the
  execution precondition: the `SquadCanonicalProfileStore` staging exists (ES1), so T5
  mirrors it as planned. Residual caution stays: if the staging semantics differ from what
  Q3 assumed, T5 adapts to the observed mechanism and the deviation is reported, not
  silently absorbed.
- **Probabilistic enforcement.** Instructions reduce but cannot guarantee adherence. The
  required ordered markers make structure checkable in review, which is the accepted partial
  determinism (A2); a deterministic prose-format gate is explicitly future work.
- **Session-title expectation gap.** Factory has no agent-settable title; the skill must
  say so rather than simulate one. The conditional rule prevents fabrication on targets
  whose mechanisms are unverified.
- **Default-text review.** §11 was the user's explicit gate-review item; the review
  concluded 2026-10-28 with the user's text folded in (AP1).
- **Rendered deployments lag.** Other machines and targets pick up the contract on their
  next `squad install`/`squad update`; this plan verifies the release path, not every
  deployed copy.
- **Environmental release-loop failure.** `update-loop.sh` requires `node`/`npm` and builds
  `kyberdash`; a failure there is a reported blocker, not a plan defect.
- **Review escalation on path.** `products/kyber-squad/agents/**` is in
  `review.policy.always-human`, so the council escalates this change to the human on path
  alone. Intended policy; the gate presentation should expect it.
- **Config Reg mode-property precedent.** `intake-assessment` is the first non-path Config
  Reg value; the validator change is deliberately narrow (an entry kind, not a general
  escape hatch) so the registry's every-value-resolves guarantee survives for path entries.

### Out of scope

- Any hooks layer, including per-target hook fallbacks (A2).
- Enabling `intake` in this or any repository — the flag ships default-off; adoption is a
  per-repository deliberate act.
- The `~/.claude/CLAUDE.md` personal guide (Q2: leave alone).
- Machine-consumed specialist digest schemas (architect, product-owner, review lenses,
  investigator) — the relay carries the format, the schemas do not change.
- Renaming or renumbering anything in the deployed harness trees by hand; all deployment
  flows through the squad CLI.
- KyberDash, KyberUtilities, and every other component; no renderer behaviour changes
  beyond what the new skill and body text require.

---

## 9. Verification gates, review, and closeout

- Every developer task ends with the worker's own `task-gate` run; a `FAIL` returns to the
  same worker with the failing gate ids attached.
- Whole-change review: one `code-reviewer` council over the accumulated diff, never a
  per-task review. Expect the always-human escalation on the agent-surface paths (§8).
- `docs validate .` and `docs drift .` zero findings (T6) — pre-finalization validation
  **passed** on 2026-10-28 (build 0 warnings; docs validate 0 findings; docs drift 0
  findings), re-confirmed on the post-sync base `823358d` (63-commit fast-forward) by the
  conductor's capable runner; the same runner performs the finalization revalidation
  against the Ready tree and records the outcomes in §10.
- Release loop verification (T7).
- Every completed change receives a docs-dev review; plan-backed work completes closeout.
  Closeout maps the durable decisions to the Design harvest destinations. Proposed ADR waiver: the
  contract and mechanisms refine the existing Kyber-Squad instruction-surface architecture
  (skill-as-policy-carrier extends the established skill artifact class) and the existing
  DocGraph Config Reg design (mode property is a narrow kind extension), so no new ADR is
  warranted; the harvest destinations carry the durable design.
- This plan is archived in the same change that finishes it (`KW-DOC-LIFECYCLE-003`: `docs validate
  --merge-ready` fails while a plan remains in `docs/plans/`).

---

## 10. Verification checklist for the conductor

1. Execution precondition: satisfied 2026-10-28 — tree fast-forwarded `3ec6a3e` →
   `823358d` (63 commits); `SquadCanonicalProfileStore` staging present (ES1).
2. Approve-and-execute gate: concluded 2026-10-28 — declared scale confirmed
   (extends-component, KyberSquad + DocGraph), §11 presented as its own review item, the
   user's §11 text folded in, approval recorded (AP1).
3. Finalization revalidation: the conductor runs `docs validate .` and `docs drift .`
   against the finalized Ready tree via its capable runner and records the outcomes.
   Validation passed on the post-sync base `823358d` on 2026-10-28 (build 0 warnings;
   docs validate 0 findings; docs drift 0 findings).

---

## 11. Proposed default user-home guidance text (for gate review)

This is the name-neutral default content for both
`products/kyber-squad/skills/response-format/user-guidance.md` and, as deployed by
`squad update --global`, `~/.kyber-weave/response-guidance.md`. **User-authored at the
approve-and-execute gate, 2026-10-28**: the opening line is the user's verbatim text; the
bullets are the drafted content, unchanged, as the user confirmed (AP1). The deployed copy
is the user's to edit, and edits are preserved on refresh.

```markdown
# How to respond to the user

Lead with the bottom line up front (BLUF). The reader may have several agents running at
once and loses focus past one paragraph.

- **Title first.** Every response opens with one line naming the work being done.
- **Then the bottom line, in at most three sentences**, written for a strong developer who
  has not followed the work so far: what is being built or what broke, why, what you
  conclude, and what you recommend.
- **Then what needs a decision**, whenever a decision is open. One decision at a time where
  possible: yes/no, with a recommendation attached.
- **Bullets for issues, options, and status.** Status reports use the same shape as
  questions.
- **No assumed context.** Do not assume the reader remembers earlier messages or has read
  any file. Plain words; explain every name, id, acronym, or piece of jargon the first time.
  Never use internal labels (plan ids, task numbers) as if the reader knows them.
- **Make low-risk technical calls without asking** and report them in one line. Ask before
  scope changes, spending, publishing, or anything hard to undo.
- **Never label or mention this format.** No tags, no section headers for the structure, no
  meta-commentary. The shape speaks for itself.
- **Name the session after the work** where the harness lets you.
```

---

## Closeout

**Closed 2026-10-28 by `docs-dev`.** Links below are written for the archived location,
`docs/archive/plans/`.

**No ADR — ADR waiver:** every decision refines established architecture rather than adding a
boundary. The skill-as-policy-carrier extends the existing skill artifact class (Q1), the
user-home deploy extends the owned-file and receipt pattern of
[ADR 0026](../../adr/0026-kyber-utilities-owned-files-not-settings.md) and the global squad state
of [ADR 0037](../../adr/0037-squad-state-in-user-home.md) (Q3), and the mode property is a narrow kind extension of the Config Reg design
(A6, H4). No new boundary, dependency or schema appears, so the durable design is carried by the
four Design harvest destinations instead.

### Outcome

Delivered in one change, test-first, all tasks green:

- **T1 (RED)** — contract pins for the canonical content, the `intake` flag and Config Reg mode
  entry, and the user-home deploy, all failing against the unmodified tree first.
- **T2, T3 (GREEN)** — the canonical `response-format` skill (`SKILL.md` plus its
  `user-guidance.md` default) and the `conductor` and `bug-crusher` bindings by name, including
  the conductor relay rule and the intake resolution order in
  `conductor/references/intake-path.md`.
- **T4 (GREEN)** — `ontology.intake` in `.kyber-weave/kyber-weave.yml`, published as the
  `<intake-assessment>` Config Reg mode property only when true; mode entries are exempt from
  the path-existence check and stay covered by the stale-rendering check.
- **T5 (GREEN)** — `SquadResponseGuidanceDeployer` deploys and refreshes
  `<home>/.kyber-weave/response-guidance.md` on `squad update --global`: absent → deployed,
  digest match → replaced, edited or unrecorded → preserved and reported, receipt-owned in
  `homeFiles`, never written outside the home root.
- **T6** — the harvest into the four destinations (H1–H4), landed 2026-10-28.
- **T7** — the Squad release path rendered both bound agents and exercised the global user-home
  deploy.

### Verification evidence

Verified by `docs-dev` at closeout, in this checkout on 2026-10-28:

- `dotnet build KyberWeave.sln -c Release` — 0 warnings, 0 errors.
- `docs validate . --merge-ready` — 0 findings, run after this plan moved to `docs/archive/plans/`.
- `docs drift .` — 0 findings.
- `--filter "FullyQualifiedName~SquadDocsHarvest|FullyQualifiedName~DocGovernance"` — 29 passed,
  0 failed.

Reported by the delivery run, not re-run by `docs-dev` at closeout:

- Full suite 4090 passed, 0 failed.
- `./scripts/update-loop.sh` release loop — 33 checks, 0 failures.
- The review council ran twice. The first council's 3 major findings were remediated in this
  change. The second council, on 2026-10-28, verified the three remediations (the `src/`
  deployer write-ordering fix, the `tests/` ordering pin, and the docs-dev plan closeout)
  resolved: its gate suite passed 27 of 27 and `docs validate . --merge-ready` reported 0
  findings. It raised two new findings on this plan's own documentation. **N1 (major)** — the
  governed deploy contract in [architecture](../kyber-squad/architecture.md#home-owned-files-and-the-user-home-response-guidance)
  and [onboarding](../kyber-squad/onboarding.md#user-home-response-guidance) still described
  the pre-fix ordering and omitted the adopt rule — was remediated in this closeout, in this
  pass. **N2 (minor)** — no test pins the adopt branch, where a file whose bytes already equal
  the shipped default is recorded without a write — is a `tests/` change outside this closeout's
  scope, so it is reported to the owner for decision rather than fixed here.

### Decision provenance

- **AP1** — user approve-and-execute at the conductor-presented gate on 2026-10-28, with the
  declared scale and components restated and [§11](#11-proposed-default-user-home-guidance-text-for-gate-review)
  presented as its own review item.
- **ES1** — environment sync: the checkout fast-forwarded `3ec6a3e` → `823358d` and the plan's
  stale references were corrected. No scope, mode or Test-contract change.

### §11 user-authored guidance text as shipped

[§11](#11-proposed-default-user-home-guidance-text-for-gate-review) is the text the user
approved, and it is what ships as `products/kyber-squad/skills/response-format/user-guidance.md`
and what `squad update --global` deploys to `~/.kyber-weave/response-guidance.md`. It is recorded
once, in §11, and not duplicated here.

### Open items (owner)

These do not block archival. They are recorded because the closeout scope was this plan and the
plan index only.

1. **`A4`'s session-title matrix — resolved 2026-10-28.** The decision is now recorded in
   [architecture §8, session titles are set only where a mechanism is confirmed](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier),
   which states the Factory infeasibility, the confirmed-mechanism-only rule, the never-fabricate
   rule, and names the skill's matrix as the operative copy. The requirement itself stays in
   [requirements R1](../../kyber-squad/requirements.md#user-facing-response-format).
2. **`A2`'s rejected-hooks rationale — resolved 2026-10-28.** Recorded in
   [architecture §8, enforcement is instruction-based, by necessity](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier):
   the absent hooks field in `agent.schema.json`, the four capability-lattice grant families, the
   absent Factory `settings.json` hooks key, the tool-call-versus-prose distinction, the accepted
   partial determinism, and the deferred deterministic gate.
3. **`intake` stays unset in this repository.** Enabling it here is a separate deliberate act
   (A6). Nothing in this change enabled it.
4. **`N2` from the second council: no test pins the adopt branch.** The deployer records a file
   whose bytes already equal the shipped default without writing it, and nothing pins that
   behaviour. It is a `tests/` change, so the owner decides whether to take it.

### DOCS_REVIEW rows

Documentation-impact review over the change, not over this plan's intent.

- `DOCS_REVIEW: update` — asked: does the response-format contract and its skill carrier have a
  canonical home? Decided: yes, architecture plus requirements. Why: the contract binds every
  user-facing reply from two deployed agents, so a reader needs it in the corpus, not only in the
  skill. Files: `docs/kyber-squad/architecture.md` (§8 response format and its skill carrier,
  §4 home-owned files), `docs/kyber-squad/requirements.md` (R1–R5), `docs/kyber-squad/onboarding.md`
  (user-home response guidance).
- `DOCS_REVIEW: update` — asked: is the default-off intake assessment documented where a reader
  adopts it? Decided: yes, requirements, onboarding and configuration. Why: a per-repository opt-in
  is operational, so both the contract and the adoption steps are needed. Files:
  `docs/kyber-squad/requirements.md` (intake assessment), `docs/kyber-squad/onboarding.md`
  (intake assessment opt-in), `docs/configuration.md` (`intake` key), `docs/docgraph/architecture.md`
  (mode properties are not paths).
- `DOCS_REVIEW: update` — asked: does a Config Reg entry kind that is not a path change the
  registry's documented promise? Decided: yes. Why: the every-value-resolves guarantee is stated
  architecture, and exempting mode entries changes it. Files: `docs/docgraph/architecture.md`.
- `DOCS_REVIEW: update` (was `missing`) — asked: is the per-harness session-title matrix (`A4`) in
  any governed document? Decided: it was not, and it is not the skill's to keep alone. Why: a
  corpus reader could not learn that Factory has no agent-settable title or that the rule applies
  only on confirmed mechanisms. Resolved 2026-10-28: landed in `docs/kyber-squad/architecture.md`
  §8, *session titles are set only where a mechanism is confirmed*, which names the skill's matrix
  as the operative copy.
- `DOCS_REVIEW: update` (was `missing`) — asked: does any governed document record why deterministic
  prose enforcement (hooks) was rejected (`A2`)? Decided: no, and enforcement rationale is
  architecture. Why: the capacity of the release model to intercept prose is a boundary someone will
  question again. Resolved 2026-10-28: landed in `docs/kyber-squad/architecture.md` §8,
  *enforcement is instruction-based, by necessity*.

**Correction after the second council (2026-10-28).** The first pass of this closeout reviewed the
change as it stood and the harvest it produced described the deploy ordering the `F1` fix then
replaced: the guidance was said to be written after the transaction committed, and the
adopt-on-identical-bytes rule was absent. Both are stale against the shipped
`SquadResponseGuidanceDeployer` and `SquadLifecycleService`, which write the guidance before the
plan is built and record ownership only from a read-back digest. Corrected in
[architecture §4](../../kyber-squad/architecture.md#home-owned-files-and-the-user-home-response-guidance)
and [onboarding](../../kyber-squad/onboarding.md#user-home-response-guidance) in the closeout
pass. `DOCS_REVIEW: update` — the documentation was behind the implementation, so the
implementation's contract is now what the corpus states, including the fourth refresh rule and
the failure mode the ordering creates.

**Missing-documentation items closed (2026-10-28).** Open items 1 and 2 and their two
`DOCS_REVIEW: missing` rows were the only documentation the second council found absent. Both
landed in [architecture §8](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier)
beside the rest of the response-format contract, the section a reader already consults for this
feature: the session-title feasibility matrix and rule (`A4`), and the instruction-based
enforcement rationale including the deferred deterministic gate (`A2`). The rows above are
updated from `missing` to `update` rather than deleted, so the record shows the gap was real and
where it closed.

## Closeout mapping

ADR waiver for the plan as a whole: see the paragraph above. Documentation waivers are used only
for execution and scope provenance and are marked **waiver** below. The Development mode row (A5)
and the artifact-choice row (A1) are recorded as waivers for the same reason: what survives from
them is the portable conductor contract in `products/kyber-squad/`, not a corpus claim about this
change.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| A1 | **Documentation waiver:** plan-versus-spec artifact choice for this change; historical scope provenance for one artifact, no surviving invariant. | extends-component | KyberSquad, DocGraph | 2026-10-28 | archived |
| A2 | [architecture §8, the response format and its skill carrier](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier) (enforcement is the one skill plus the binding agent bodies). The rejected-hooks rationale and the deferred deterministic gate are open item 2. No ADR — ADR waiver: enforcement detail under an existing carrier mechanism. | extends-component | KyberSquad | 2026-10-28 | archived |
| A3 | [requirements R3, who is bound](../../kyber-squad/requirements.md#user-facing-response-format); [architecture §8, who is bound](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier) (`product-owner` exempt as a headless specialist). | extends-component | KyberSquad | 2026-10-28 | archived |
| A4 | [requirements R1, title line](../../kyber-squad/requirements.md#user-facing-response-format) (the required first line of every response; the per-harness session-title matrix is carried by the skill and is open item 1). | extends-component | KyberSquad | 2026-10-28 | archived |
| A5 | **Documentation waiver:** the development mode chosen for this change; the test-first default is a portable conductor contract in `products/kyber-squad/`, not a corpus claim. | extends-component | KyberSquad, DocGraph | 2026-10-28 | archived |
| A6 | [requirements, intake assessment](../../kyber-squad/requirements.md#intake-assessment); [onboarding, intake assessment opt-in](../../kyber-squad/onboarding.md#intake-assessment-opt-in-per-repository); [DocGraph architecture, mode properties](../../docgraph/architecture.md#mode-properties-are-not-paths); `docs/configuration.md` (`intake`, default `false`). | extends-component | KyberSquad, DocGraph | 2026-10-28 | archived |
| A7 | [requirements R2, the format is never named](../../kyber-squad/requirements.md#user-facing-response-format); [architecture §8, the no-labels rule](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier). | extends-component | KyberSquad | 2026-10-28 | archived |
| Q1 | [requirements R5, one home one override](../../kyber-squad/requirements.md#user-facing-response-format); [architecture §8, why a name and not a link](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier) (the resource-closure rule governs path links, not a skill invoked by name). | extends-component | KyberSquad | 2026-10-28 | archived |
| Q2 | [architecture §4, home-owned files and the user-home response guidance](../../kyber-squad/architecture.md#home-owned-files-and-the-user-home-response-guidance) (Squad owns only the home files it wrote and recorded; a personal harness guide it does not ship is not superseded). | extends-component | KyberSquad | 2026-10-28 | archived |
| Q3 | [architecture §4, home-owned files](../../kyber-squad/architecture.md#home-owned-files-and-the-user-home-response-guidance) (default, digest, preserve-on-edit, receipt `homeFiles`); [onboarding, user-home response guidance](../../kyber-squad/onboarding.md#user-home-response-guidance) (state table and remedies). No ADR — ADR waiver: owns one more home file under the ADR 0026 / ADR 0037 pattern. | extends-component | KyberSquad | 2026-10-28 | archived |
| AP1 | **Documentation waiver:** approval provenance, transient; recorded in the Decisions table and in the Closeout provenance above. | extends-component | KyberSquad, DocGraph | 2026-10-28 | archived |
| ES1 | **Documentation waiver:** environment-sync facts for one checkout (fast-forward range, upstream skill rename, plan-index disambiguation), transient; their durable parts are the harvest above. | extends-component | KyberSquad, DocGraph | 2026-10-28 | archived |
| H1 | [architecture §8, the response format and its skill carrier](../../kyber-squad/architecture.md#the-response-format-and-its-skill-carrier); [architecture §4, home-owned files](../../kyber-squad/architecture.md#home-owned-files-and-the-user-home-response-guidance). | extends-component | KyberSquad | 2026-10-28 | archived |
| H2 | [requirements, user-facing response format](../../kyber-squad/requirements.md#user-facing-response-format); [requirements, intake assessment](../../kyber-squad/requirements.md#intake-assessment). | extends-component | KyberSquad | 2026-10-28 | archived |
| H3 | [onboarding, user-home response guidance](../../kyber-squad/onboarding.md#user-home-response-guidance); [onboarding, intake assessment opt-in](../../kyber-squad/onboarding.md#intake-assessment-opt-in-per-repository). | extends-component | KyberSquad | 2026-10-28 | archived |
| H4 | [DocGraph architecture, mode properties are not paths](../../docgraph/architecture.md#mode-properties-are-not-paths). | extends-component | DocGraph | 2026-10-28 | archived |

