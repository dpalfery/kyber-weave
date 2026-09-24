---
id: plans/2026-09-24-planner-persistence-target-envelopes
title: Restore planner persistence on Claude, Pi and ZCode through target capability envelopes
doc-type: plan
status: draft
owner: dpalfery
last-reviewed: 2026-09-24
component: KyberSquad
development-mode: test-first
---

# Restore planner persistence on Claude, Pi and ZCode through target capability envelopes

**Status:** Draft
**Date:** 2026-09-24
**Development mode:** test-first (the default; no opt-out was supplied)
**Source todo:** [claude-renderer-ask-narrowing.md](../todo/claude-renderer-ask-narrowing.md)
**Goal:** On Claude, Pi and ZCode, `architect` can save its plan and plan-index row and
`product-owner` can save its specification artifacts. `docs-dev` runs the documentation
validation and drift checks after either planner saves. The grant comes from generalising
today's Copilot-only capability envelope (`architect-copilot`) to more targets, and every
target that honours an envelope records it in the receipt. Factory, OpenCode, Antigravity and
Copilot's `product-owner` keep today's narrowing, pinned by tests, with follow-up todos.

---

## 1. Problem and goal

`squad install --target claude` renders `architect` with no `Edit`, `Write` or `Bash`, and
`product-owner` with no `Edit` or `Write`. Both profiles hold `filesystem.write: ask`
(`products/kyber-squad/profiles/capabilities.yml:23` and `:91`), and every allow-list renderer
grants a tool only on `allow`. As a result the conductor's plan path stops at the architect's
first save, and the spec path stops at the product owner's first save. `PiRenderer`
(`PiRenderer.cs:448-470`) and `ZCodeRenderer` (`ZCodeRenderer.cs:764-807`) narrow the same way.

**Known departure from the todo.** The todo asks that a write outside the plan directory be
blocked, including under `acceptEdits` (`claude-renderer-ask-narrowing.md:97-104`). This plan
does not meet that. Under D2 = C the grant covers the whole tree, and the path scope stays
instruction-only. The trade is recorded in the receipt (D7) and in the requirements (task W1).

---

## 2. Approved decisions

**Provenance.** These are the user's answers to intake questions D1 to D8, relayed by the
parent session on 2026-09-24 in the dispatch that commissioned this plan.

- **Path:** PLAN.
- **Development mode:** `test-first`, the default, persisted in this plan's frontmatter.
- **D1 = A.** Claude, Pi and ZCode get the fix for `architect` and `product-owner`. Factory,
  OpenCode and Antigravity, plus Copilot for `product-owner`, get tests pinning today's
  narrowing, and follow-up todos. Those todos are accepted as plan tasks (T1).
- **D2 = C.** Use whole-tree envelopes. The `copilot-capability-profile` / `architect-copilot`
  pattern is extended to Claude, Pi and ZCode for `architect` and `product-owner`. Today that
  pattern is hard-wired to the literal `copilot` at `SquadSourceLoader.cs:254-263` and
  `SquadSourceValidator.cs:59-92`. There are no path scopes and no guard hooks.
- **D4 (home) = `docs-dev`.** `docs-dev` runs `docs validate` and `docs drift` after a planner
  saves a plan or spec. The mechanism that makes this possible is still open (D4a).
- **Dropped or not applicable.** D5 is dropped, because D2 = C has no scopes. D3 and D6 fall
  away with it. D8 does not apply, because D1 = A.
- **Sequencing.** A follow-on change lowers the primary `conductor` on Claude to a
  `.claude/skills/conductor/SKILL.md` entry point. It is not in this plan. §10 records where the
  two changes meet.

**Not yet approved.** Everything in §3 and the parts of §5 that depend on it.

---

## 3. Decision ledger (Draft only)

| Id | Decision | Options | Recommended | Depends on | Status |
|---|---|---|---|---|---|
| D9 | Shape of the generalised envelope | A: agent map plus envelope `targets` list plus a no-deny-raise invariant / B: per-target agent fields / C: A without the invariant | A | D2 | OPEN |
| D4a | How `docs-dev` gets to run the two checks | A: `docs-dev` envelope on the four envelope targets / B: `process.execute: allow` on the shared `documentation` profile / C: read-only MCP validate and drift tools | A | D4; its wording assumes D9-A | OPEN |
| D7 | What the receipt records when an envelope grants a capability | A: new `envelope-granted` code on all four envelope targets, plus an amended `safety-narrowed` definition / B: the same on Claude, Pi and ZCode only / C: reuse `permission-not-expressible` / D: no record | A | D9 | OPEN |
| D10 | Does the change get an ADR? | A: ADR 0023 / B: requirements and architecture only | A | — | OPEN |

### D9 — the shape of the generalised envelope

**A (recommended)**

- **Agent frontmatter.** Replace `copilot-capability-profile` with `target-capability-profiles`, a
  mapping from target token to envelope name. For example:
  `target-capability-profiles: {copilot: architect-plan-writer, claude: architect-plan-writer, pi: architect-plan-writer, zcode: architect-plan-writer}`.
- **Envelope profile.** Replace the scalar `target: copilot` (`capabilities.yml:46`,
  `capability-profiles.schema.json:21`) with `targets: [...]`, a non-empty list of distinct
  tokens.
- **One closed honouring set.** The envelope-honouring targets are `copilot`, `claude`, `pi` and
  `zcode`. Core owns the set, and the loader, the validator and the four renderers all read it.
  A map key or `targets` entry outside the set fails to load. Binding an envelope for a renderer
  that would ignore it is therefore a load error, not a silent no-op.
- **Invariant.** An envelope may resolve a shared `ask` to `allow`, and may narrow any decision.
  It never raises a shared `deny`.

Why A:

- It is one field.
- The next target needs one set entry plus renderer work.
- An envelope that several targets share is declared once.
- The invariant keeps the shared `deny` a ceiling on every target. That is what keeps the amended
  non-broadening guarantee a guarantee.

What A costs:

- A mapping-valued agent field. The agent loader reads only scalars and sequences today
  (`SquadSourceLoader.cs:560-630`).
- Copilot's field is migrated. That touches `CopilotRenderer.cs:293`,
  `SquadSourceValidator.cs:70-92` and the Copilot-envelope fixtures in
  `SquadSourceTests.cs:351-444`.

**B.** Add per-target fields `claude-capability-profile`, `pi-capability-profile` and
`zcode-capability-profile` beside `copilot-capability-profile`. The envelope's `target` scalar
accepts the four tokens instead of the literal `copilot`. Each envelope serves one role on one
target, which means three near-identical blocks each for `architect` and `product-planning`,
plus three for `documentation` under D4a-A. The invariant is the same as A. There is no Copilot
migration, but the change adds three schema fields, three loader entries and a validator branch
for each field.

**C.** A without the invariant: an envelope may also raise a shared `deny`. Under D4a-A this
keeps `documentation.process.execute` at `deny`, so `docs-dev`'s receipt records on the seven
non-honouring targets do not change. The cost is that the shared `deny` stops being a ceiling.

### D4a — how `docs-dev` gets to run `docs validate` and `docs drift`

**The conflict.** `docs-dev` uses the `documentation` profile, which sets
`process.execute: deny` (`capabilities.yml:60`). Its Copilot tools have no `execute`
(`docs-dev.md:8`). It cannot run either check on any target today. **Only `docs-dev` uses the
`documentation` profile**, confirmed from every agent's frontmatter under
`products/kyber-squad/agents/`.

**Common to every option.**

- **`architect`.** It stops running processes on every target. `architect.process.execute`
  becomes `deny` in the shared profile and in the architect envelope, and `execute` leaves
  `architect`'s `copilot-tools`. This is a Copilot behaviour change: Copilot `architect` loses a
  shell it has today (`architect.md:9`, pinned at `CopilotRendererTests.cs:182`). It follows from
  the D4 home choice.
- **`product-owner`.** The inconsistency at `product-owner.md:47` is resolved by removing the
  claim that it validates the corpus. `product-planning` keeps `process.execute: deny`.
- **Body changes.** They are listed in §5.6.
- **`docs-dev` body.** It stays unchanged, so its Hotshot golden body digest still holds. The
  conductor's dispatch packet carries the validation-only instruction.

**A (recommended): a `docs-dev` envelope on the four envelope targets**

- Add envelope `documentation-validator` (`targets: [copilot, claude, pi, zcode]`), which
  resolves `process.execute` to `allow`.
- Change the shared `documentation.process.execute` from `deny` to `ask`, so the envelope may
  resolve it under the D9 invariant.
- Add `execute` to `docs-dev`'s `copilot-tools`. `docs-dev` is not a Hotshot-evolved agent, and
  its rendered Copilot tools are compared with the golden (`HotshotGoldenContractTests.cs:457-476`).
  A tools-only evolution list, mirroring `ModelEvolvedAgentIdentities` at `:54-70`, keeps every
  other golden comparison for `docs-dev`.
- **Reaches** Copilot, Claude, Pi and ZCode: exactly the targets where a planner can save after
  this plan.
- **Elsewhere,** `docs-dev`'s new `ask` narrows. There is a new `safety-narrowed` record on
  Factory, OpenCode and Antigravity, and changed records on Cursor, Codex, Kilo and Warp. There
  is no tool change.
- It also makes `docs-dev.md:79` and `product-owner/references/closeout-phase.md:22` true on those
  targets. Both already claim to run the checks.

**B: `process.execute: allow` on the shared `documentation` profile**

- **Reaches** every target that maps a shell: Claude, Pi, ZCode, Factory, OpenCode, Antigravity
  and Cursor. Codex, Kilo and Warp use their harness default.
- **Copilot** still needs the same `copilot-tools` change and Hotshot entry as A.
- It gives `docs-dev` a shell on seven targets where no planner can save yet.
- It changes the `documentation` profile that ADR 0022 uses as its canonical write-only example.

**C: read-only `docs_validate` and `docs_drift` MCP tools on the `kyber-weave` server**

- **Reaches:**
  - Claude, through `mcp__kyber-weave__*`.
  - Copilot, through `kyber-weave/*`.
  - OpenCode, through `kyber-weave_*` (`OpenCodeRenderer.cs:44-45`).
  - ZCode, once both names are added to `toolchain.yml:14-24` `required-mcp-tools`, which
    `squad doctor` then requires.
- **Does not reach:**
  - Pi (`extensions: false`, `PiRenderer.cs:310`).
  - Factory (`mcpServers: []`, `FactoryRenderer.cs:198-199`).
  - Antigravity, which emits no MCP key.
- **Unverified:** Codex, Kilo, Cursor and Warp depend on the harness's own MCP configuration.
- **No shell anywhere.** `architect` and `product-owner` reach the same tools, so the `docs-dev`
  step becomes optional on the targets C reaches.
- **Extra work:** a new MCP track (tests, `DocsTools.cs`, `docs/docgraph/mcp-runbook.md`). Pi,
  which is a D1 target, stays unvalidated.

### D7 — what the receipt records when an envelope grants a capability

**A (recommended)**

- Add a new code, `envelope-granted`. A shared helper emits it on every envelope-honouring
  target (Copilot, Claude, Pi, ZCode) whenever an envelope resolves a shared `ask` to `allow`.
- Amend `safety-narrowed`'s definition (`requirements.md:51`). "The target cannot prompt the
  user" is wrong for Claude. The new wording: "the target cannot guarantee a per-capability
  confirmation: it cannot prompt, or a session-wide mode the rendered file cannot pin decides
  whether it prompts".
- Copilot's `architect` envelope gains a record. It is silent today (`CopilotRenderer.cs:302-311`
  records only `ask`).

**B.** The same as A, but on Claude, Pi and ZCode only. Copilot's envelopes stay silent, with a
follow-up todo, and the receipt describes the same mechanism differently on different targets.

**C.** Reuse `permission-not-expressible`, with envelope Details. That code means "not
expressed", which does not describe a capability that was granted.

**D.** No record, following today's Copilot precedent. A real whole-tree grant would then be
invisible in the receipt.

**Guard.** The Details text must never contain "widening", and the code must never be
`widened`. `SquadRendererRegistry.cs:255-260` throws on either. R1 asserts that the Details
contain no `widen` substring at all.

### D10 — ADR

**A (recommended): ADR 0023, target capability envelopes.** It would record:

- the D2 = C choice and the D9 shape;
- the no-deny-raise invariant;
- the amended non-broadening guarantee;
- the D7 record;
- the move of validation to `docs-dev`.

It would also record the rejected alternatives: lattice path scopes with runtime guards, a Claude
hook with a shipped guard script, a scope-enforcing MCP authoring server, and the status quo. This
change narrows KS-002's "must not replace or widen the shared capability profile" rule
(`requirements.md:25`). It constrains every future renderer and would be expensive to revisit.

**B:** no ADR, recording the change in `requirements.md` and `architecture.md` only.

If an answer differs from the recommendation, §5, §7 and §8 are revised before `PLAN_READY`.

---

## 4. Investigation findings

### 4.1 Discovery method

These are self-gathered lookups. This dispatch had no governed documentation query tool, no
CodeGraph tool, and no delegation (subagent spawn depth 1). Every citation below was read
directly from the file on 2026-09-24. External harness facts come from the intake digest for
this todo, which read the Claude Code, pi-subagents and ZCode sources on the same day.

### 4.2 Where `filesystem.write: ask` narrows

| Target | `architect` / `product-owner` today | Evidence |
|---|---|---|
| Claude | Write tools withheld; `safety-narrowed` | `ClaudeRenderer.cs:288-356`, `:375-387`; pinned at `ClaudeRendererContractTests.cs:269-273` |
| Pi | `edit`/`write` withheld; `safety-narrowed` | `PiRenderer.cs:448-470`, `:496-508` |
| ZCode | `Edit`/`Write` withheld; `safety-narrowed` | `ZCodeRenderer.cs:764-807`, `:827-839` |
| Factory | Write tools withheld; `safety-narrowed` | `FactoryRendererContractTests.cs:286-318` (dynamic) |
| OpenCode | `edit: deny`; `safety-narrowed` | `OpenCodeRendererContractTests.cs:376-381`, `:526-546` |
| Antigravity | `enable_write_tools: false` | `AntigravityRenderer.cs:235-241`; `AntigravityRendererContractTests.cs:754-755` |
| Copilot | `architect` escapes through `architect-copilot`; `product-owner` has no `edit` tool, and the validator forbids one while write is not `allow` | `capabilities.yml:45-54`; `product-owner.md:8`; `SquadSourceValidator.cs:189-208`; `CopilotRendererTests.cs:192` |
| Cursor | `readonly: true` (not in D1's list) | `CursorRendererContractTests.cs:188-191` |

### 4.3 The Copilot envelope today

- A profile carrying `target: copilot` is rejected as a shared profile
  (`SquadSourceValidator.cs:59-68`).
- An agent binds one through `copilot-capability-profile`, which must name a profile marked
  `target: copilot` (`:70-90`).
- `copilot-tools` validate against the bound profile (`:92`, `:149-211`).
- `CopilotRenderer.BuildPermissionDegradation` reads the bound profile and records only `ask`
  (`CopilotRenderer.cs:289-323`). Because `architect-copilot` holds no `ask`, Copilot's whole-tree
  `architect` grant produces no receipt record.
- KS-002 says the envelope "must not replace or widen the shared capability profile"
  (`requirements.md:25`). The Non-Broadening Guarantee forbids escalating `ask` to `allow` "during
  target rendering" (`requirements.md:80-86`).
- `architecture.md:306-310` describes the Copilot fields as not replacing the shared profile.
- All three statements need amending once the envelope decides a target's grant.

### 4.4 Validation is claimed where no target grants it

There are three instances:

- `architect.md:24` says the architect runs `docs validate` and `docs drift`. `process.execute`
  is `ask` (`capabilities.yml:28`), so only Copilot, through its envelope, grants it.
- `product-owner.md:47` says it validates the corpus at finalization. `product-planning` denies
  `process.execute` (`capabilities.yml:92`).
- `docs-dev.md:79` and `product-owner/references/closeout-phase.md:22` say `docs-dev` runs both
  checks at specification closeout. `documentation` denies `process.execute` (`:60`), and
  `docs-dev`'s `copilot-tools` has no `execute`.

The architect-side claims are repeated in `plan-authoring.md:47-67` (`DOCS_VALIDATE: pass`,
`DOCS_DRIFT: pass`), `conductor/references/plan-path.md:8` and `:17-18`, and `spec-path.md:21`.

### 4.5 Constraints on the change

- **Hotshot golden.** `architect`, `conductor`, `product-owner` and `task-reviewer` are evolved,
  so their bodies and frontmatter may change (`HotshotGoldenContractTests.cs:40-46`). `docs-dev`
  is not evolved. For `docs-dev` the golden test compares the description, body digest,
  `capability-profile` name, delegation, aliases and invocation (`:264-289`), and the rendered
  Copilot frontmatter including tools (`:457-476`). Agent-owned references are not compared;
  only skill resources are.
- **Renderer lookups.** All three renderers read `agent.CapabilityProfile` at every permission
  site:
  - Claude: `ClaudeRenderer.cs:297`, `:362`.
  - Pi: `PiRenderer.cs:454`, `:475`, `:483`, `:618`.
  - ZCode: `ZCodeRenderer.cs:771`, `:814`, `:954`, `:1007`.

  The pure-orchestrator MCP carve-out keys on the profile *name* `orchestrator`
  (`ClaudeRenderer.cs:320-323`, `ZCodeRenderer.cs:1003-1009`).
- **Test expectations.** Contract-test expectations are computed from the shared profile
  (`ClaudeRendererContractTests.cs:331-354`, `PiRendererContractTests.cs:523-543`,
  `ZCodeRendererContractTests.cs:465-488`, `SquadRenderingContractTests.cs:137-142`). Once the
  effective profile is in play, those tests must compute from it.
- **Existing degradation behaviour.**
  - `CapabilityDegradations.BuildCapabilityNotIsolable` (`CapabilityDegradations.cs:47-82`)
    returns null when write is `allow`. None of the three roles triggers it after this change.
  - The registry rejects a degradation whose Details contain "widening"
    (`SquadRendererRegistry.cs:255-260`).
- **Release packaging.** The APM archive ships canonical profiles and agents
  (`SquadPacker.cs:18`), so the new frontmatter and profile shape ship with the release. Version
  lockstep (KS-005) means a CLI never renders a Squad release newer than itself.
- **Internals.** Core exposes internals to the test project, so tests may call an internal
  resolver.

### 4.6 What the targets do with a listed write tool (from the intake digest)

- **Claude.** A listed tool goes through the parent session's permission mode. Default mode
  prompts. `acceptEdits`, `auto` and `bypassPermissions` do not. A non-interactive session cannot
  prompt. A subagent's `permissionMode` cannot override the parent's `acceptEdits`, `auto` or
  `bypassPermissions` (code.claude.com/docs/en/sub-agents; /permission-modes).
- **Pi.** It has no permission prompts at all, so a listed tool is an unconditional grant
  (`PiRenderer.cs:65-68`).
- **ZCode.** It has no per-capability prompt, and it strips `permissionMode` from project
  profiles (`ZCodeRenderer.cs:48-50`, `:96-100`).

---

## 5. Design

This section is proposed. It becomes binding once D9, D4a, D7 and D10 are answered and the plan
is approved. It is written for the recommended options.

### 5.1 Source shape (D9-A)

- **`agent.schema.json`.** Remove `copilot-capability-profile` (`:25`). Add
  `target-capability-profiles`: an object with at least one property, whose property names are
  in `[copilot, claude, pi, zcode]` and whose values are non-empty strings.
- **`capability-profiles.schema.json`.** Replace `"target": { "const": "copilot" }` (`:21`) with
  `targets`: an array with at least one item, unique items, and items in the same four tokens.
- **Model (`SquadSource.cs`).**
  - `SquadAgent.CopilotCapabilityProfile` (`:49`) becomes
    `IReadOnlyDictionary<string, string> TargetCapabilityProfiles`, which is empty when omitted.
  - `SquadCapabilityProfile.Target` (`:96-98`) becomes `IReadOnlyList<string> Targets`, which is
    empty for a shared profile.
- **New internal `SquadCapabilityEnvelopes`** in
  `src/KyberWeave.Core/Squad/Model/SquadCapabilityEnvelopes.cs`. It holds:
  - the honouring set;
  - `Resolve(SquadAgent agent, string targetToken, IReadOnlyDictionary<string, SquadCapabilityProfile> profiles)`,
    which returns the shared profile name and profile, the effective profile name and profile,
    and whether an envelope applied. The effective profile is the bound envelope when
    `targetToken` is a key, and the shared profile otherwise.

### 5.2 Validation (`SquadSourceLoader`, `SquadSourceValidator`)

Every rule fails with `KW-SQUAD-SOURCE-001` and a hint.

1. `copilot-capability-profile` is rejected. The hint names `target-capability-profiles: {copilot: <envelope>}`.
2. A profile-level `target` is rejected. The hint names `targets: [copilot]`.
3. A map key or `targets` entry outside the honouring set is rejected. The hint lists the set,
   and for a declared target such as `factory` it says that target does not honour envelopes.
4. A bound name must be a declared profile that has `targets`, and those `targets` must contain
   the map key.
5. A profile with `targets` cannot be an agent's `capability-profile`. This is today's
   `SquadSourceValidator.cs:59-68` rule, generalised from "Copilot-only".
6. **The invariant.** For every capability where the shared profile says `deny`, the envelope
   must say `deny`. `ask` may become `allow`, and any decision may be lowered.
7. `copilot-tools` validate against the Copilot-effective profile, read through `Resolve`. This is
   today's rule at `:70-92`.

### 5.3 Renderer lowering

In `ClaudeRenderer`, `PiRenderer`, `ZCodeRenderer` and `CopilotRenderer`, every permission
decision reads the effective profile from `SquadCapabilityEnvelopes.Resolve(agent, <token>, profiles)`.
Role identity keeps keying on the shared profile name (`agent.CapabilityProfile`), because that
names the role, not its grant. So the pure-orchestrator MCP carve-out is unchanged.

The degradation records follow from that:

- `safety-narrowed`, `permission-not-expressible` and `capability-not-isolable` are computed from
  the effective profile, since they describe what was rendered.
- `envelope-granted` (D7-A) is computed by a new
  `CapabilityDegradations.BuildEnvelopeGranted(targetToken, agent, resolution, confirmationNote)`.
  It returns a record when at least one capability is `ask` in the shared profile and `allow` in
  the effective profile, and null otherwise.

The sites that change:

| Renderer | Sites |
|---|---|
| Claude | `ResolveTools` (`:288-356`), `BuildDegradationRecords` (`:358-443`), remarks (`:24-34`) |
| Pi | `ResolveTools` (`:448-470`), `IsDelegateAllowed` (`:472-477`), `BuildSubagentDegradations` (`:479-569`), `DescribeCapabilityDecisions` (`:613-630`), remarks (`:65-71`) |
| ZCode | `ResolveTools` (`:764-807`), `BuildCapabilityDegradations` (`:809-901`), `DescribeCapabilityDecisions` (`:949-966`), `GrantsMcp` (`:1003-1009`, reading the decision from the effective profile and the orchestrator name from the shared one), remarks (`:96-100`) |
| Copilot | `BuildPermissionDegradation` (`:289-323`) |

The primary-agent paths on Pi and ZCode also go through `Resolve`. `conductor` binds no envelope,
so its output does not change.

Each target's `confirmationNote` in the Details:

- **Claude:** "Whether each call prompts is decided by the parent session's permission mode,
  which the rendered file cannot pin."
- **Pi:** "Pi has no permission prompts, so the grant is unconditional."
- **ZCode:** "ZCode has no per-capability permission prompt."
- **Copilot:** "Copilot's tool allow-list is binary and cannot prompt."

Every record's Details also name the shared profile, the envelope and the resolved capabilities,
and state that the grant covers the whole tree, so any narrower scope in the agent's instructions
is not enforced by the target.

### 5.4 Canonical source changes

**`capabilities.yml`**

- `architect` (`:15-39`): `process.execute: ask` becomes `deny`. The comment at `:24-28` is
  rewritten: the architect runs no processes, and `docs-dev` validates.
- `architect-copilot` (`:40-54`) is renamed `architect-plan-writer`, with
  `targets: [copilot, claude, pi, zcode]` and `process.execute: deny`. The comment at `:40-44` is
  rewritten to describe envelopes in general.
- A new `product-planning-spec-writer` envelope (`targets: [claude, pi, zcode]`). It is
  `product-planning` with `filesystem.write: allow`.
- Under D4a-A:
  - `documentation.process.execute` changes from `deny` to `ask`.
  - A new `documentation-validator` envelope (`targets: [copilot, claude, pi, zcode]`). It is
    `documentation` with `process.execute: allow`.

**Agents**

- `architect.md:8-9`: bind the map to `architect-plan-writer` for all four targets, and remove
  `execute` from `copilot-tools`.
- `product-owner.md`: bind the map to `product-planning-spec-writer` for `claude`, `pi` and
  `zcode`. Copilot is left unbound, per D1.
- `docs-dev.md:8` (D4a-A): bind the map to `documentation-validator` for all four targets, and add
  `execute` to `copilot-tools`.

### 5.5 What each target emits once an envelope grants

These are the recommended options.

| Target | `architect` | `product-owner` | `docs-dev` (D4a-A) |
|---|---|---|---|
| Claude | Tools gain `Edit`, `Write`, `NotebookEdit` (no `Bash`). Records: `envelope-granted` (`filesystem.write`) and the existing `permission-not-expressible` roster record | Tools gain `Edit`, `Write`, `NotebookEdit`. Records: `envelope-granted` and the existing roster record | Tools gain `Bash`, `PowerShell`. Record: `envelope-granted` (`process.execute`) |
| Pi | `tools: read, grep, find, ls, edit, write`. Records: `envelope-granted` and the existing `network.read` `permission-not-expressible` | The same | `tools: read, grep, find, ls, edit, write, bash`. Record: `envelope-granted` |
| ZCode | Tools gain `Edit`, `Write`. Records: `envelope-granted` and the existing roster record | The same | Tools gain `Bash`. Record: `envelope-granted` |
| Copilot | Tools lose `execute`. Record: `envelope-granted` (`filesystem.write`); silent today | Unchanged: `safety-narrowed` (`filesystem.write`) | Tools gain `execute`. Record: `envelope-granted` (`process.execute`) |
| Factory, OpenCode, Antigravity | Tools unchanged. `safety-narrowed` now names only `filesystem.write` | Unchanged | New `safety-narrowed` (`process.execute`) |

On the honouring targets, none of the three roles keeps a `safety-narrowed` or a
`capability-not-isolable` record: the effective profile has no `ask` and grants write.

### 5.6 Validation routing and body changes (D4)

All four files below belong to evolved agents (§4.5), so their bodies may change.

- **`architect.md:24`** becomes: "You run no processes. After every plan write, the conductor
  runs a `docs-dev` validation pass on the saved plan and returns any findings to you as a
  revision."
- **`architect.md:64`.** `PLAN_READY` means saved, decision-complete and mode-complete.
  Validation is the conductor's `docs-dev` validation pass. `PLAN_FINALIZED` means the plan is
  saved as Ready after explicit approval.
- **`architect/references/plan-authoring.md:47-67`.**
  - Drop `DOCS_VALIDATE` and `DOCS_DRIFT` from both returned blocks.
  - `PLAN_READY` additionally requires that every documentation finding the conductor relayed
    has been fixed.
  - `PLAN_WRITE_ERROR` covers failed writes only. Relayed findings are revision input.
- **`conductor.md:47`** gains an invariant: every save of a plan or specification artifact
  receives a `docs-dev` validation pass, and that pass comes before the artifact's next approval
  gate and before execution. Findings go back to the author as a revision, and the gate waits for
  a clean pass. The dispatch packet is: run the repository's documentation validation and drift
  checks as the root `AGENTS.md` declares them; edit nothing; return `STATUS: READY_FOR_REVIEW`
  with every finding verbatim in `GAPS`, or `GAPS: none`.
- **`conductor.md:53`.** A write or discovery failure still stops the path. A validation finding
  from `docs-dev` does not; it becomes a revision.
- **`conductor/references/plan-path.md:8` and `:17-18`.** "Validated" becomes "its latest `docs-dev`
  validation pass is clean". The pass runs after `PLAN_READY`, before the gate, and again after
  `PLAN_FINALIZED`, before execution.
- **`conductor/references/spec-path.md:16-21`.** The pass runs after each persisted phase
  (`READY_FOR_REVIEW`, `SPEC_READY`) before the gate, and after `SPEC_FINALIZED` before execution.
- **`product-owner.md:47`.** Drop "validate the documentation corpus". `SPEC_WRITE_ERROR` covers
  failed writes.

### 5.7 Documentation, ADR and todos

- **W1** aligns `requirements.md`, `architecture.md`, `onboarding.md` and the product README.
- **A1** writes ADR 0023 (D10-A).
- **T1** writes two follow-up todos:
  - `docs/todo/planner-persistence-factory-opencode-antigravity.md`: those three renderers still
    narrow both planners. The seam is to join the honouring set and consume `Resolve`. It also
    notes the unverified question of whether OpenCode's `edit` permission accepts path patterns.
  - `docs/todo/copilot-product-owner-spec-persistence.md`: bind a Copilot envelope and add the
    edit tools to `copilot-tools`. `product-owner` is Hotshot-evolved, so this needs no golden
    change.

  Both get rows in the todo index.

---

## 6. Scope

**In scope**

- The envelope generalisation: schemas, model, loader, validator and resolver.
- Lowering on Claude, Pi and ZCode, and the Copilot resolver migration.
- The canonical profile and agent frontmatter changes in §5.4.
- The body changes in §5.6.
- The `envelope-granted` helper and code, if D7 is A or B.
- Pinning tests on Factory, OpenCode and Antigravity, and on Copilot `product-owner`.
- Two follow-up todos, ADR 0023, canonical documentation alignment, and live verification.
- The declared gate suite and the `docs-dev` closeout.

**Out of scope**

- **Rejected by D2 = C:** lattice path scopes, runtime guard hooks, a guard subcommand, and an
  MCP authoring tool.
- **Deferred:** Factory, OpenCode, Antigravity and Copilot `product-owner` fixes (follow-up todos).
- **Not in D1:** Cursor's `readonly: true` narrowing.
- **Not in this change:**
  - The `reviewer` findings-JSON `ask` narrowing (`capabilities.yml:118-125`).
  - The conductor skill-lowering follow-on (§10).
  - Refreshing the stale root `.github/` self-deployment (`requirements.md:100-103`).
  - Changing `docs-dev`'s body.
- **Done at closeout, not here:** archiving the source todo. The conductor's intake-path
  promotion step and G2 own it.

---

## 7. Test contract

The runner prefix for every row is
`dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| K1 | `FactoryRendererContractTests.cs`, `OpenCodeRendererContractTests.cs`, `AntigravityRendererContractTests.cs`, `Squad/CopilotRendererTests.cs`, `SquadSourceTests.cs` | `"FullyQualifiedName~FactoryRendererContractTests\|FullyQualifiedName~OpenCodeRendererContractTests\|FullyQualifiedName~AntigravityRendererContractTests\|FullyQualifiedName~CopilotRendererTests\|FullyQualifiedName~SquadSourceTests"` | For `architect` and `product-owner` on Factory: no `Create`, `Edit` or `ApplyPatch`. On OpenCode: `edit: deny`. On Antigravity: `enable_write_tools: false` and no write tool. On all three: a `safety-narrowed` record whose Details contain `filesystem.write`. Copilot `product-owner`: no edit tool, a `safety-narrowed` record naming `filesystem.write`, and adding `edit` to its `copilot-tools` fails `KW-SQUAD-SOURCE-001` naming `filesystem.write`. | Characterization test with no RED/GREEN pairing. It must pass on its first run against the unmodified tree; a first-run failure is new information and returns the plan to Draft. | Passes on its first run and stays green, unmodified, through R2 and G1. |
| E1/E2 | `SquadSourceTests.cs` (Copilot-envelope cases at `:351-444` rewritten, new cases added); resolver call sites in `Squad/CopilotRendererTests.cs:54-83` and `SquadRenderingContractTests.cs:134-156` | `"FullyQualifiedName~SquadSourceTests\|FullyQualifiedName~CopilotRendererTests\|FullyQualifiedName~SquadRenderingContractTests\|FullyQualifiedName~HotshotGoldenContractTests"` | The map parses into `TargetCapabilityProfiles`. `targets` parses. Rules 1-7 of §5.2 each fail closed with the stated hint. The envelope-as-shared-profile rule still holds. `Resolve` returns the envelope for a bound target and the shared profile otherwise, always reporting the shared name. Copilot validation reads the Copilot-effective profile. | E1 is written against the unmodified loader. A compile failure on `TargetCapabilityProfiles`, `Targets` and `SquadCapabilityEnvelopes` is accepted RED evidence, with the failing symbols listed. | E2 passes E1 unmodified. Copilot output is byte-identical: `CopilotRendererTests`, `HotshotGoldenContractTests` and `SquadRenderingContractTests` are green. |
| R1/R2 | `ClaudeRendererContractTests.cs` (`:269-277`, `:328-413`), `PiRendererContractTests.cs` (`:523-543` and the tools assertions), `ZCodeRendererContractTests.cs` (`:465-488`), `Squad/CopilotRendererTests.cs` (`:182`), `SquadRenderingContractTests.cs` (`:134-156`), `CapabilityDegradationsTests.cs`, `HotshotGoldenContractTests.cs` (tools-only evolution list with a divergence guard, D4a-A), and any non-honouring contract test whose `docs-dev` expectation follows `documentation.process.execute: ask` (D4a-A) | `"FullyQualifiedName~ClaudeRendererContractTests\|FullyQualifiedName~PiRendererContractTests\|FullyQualifiedName~ZCodeRendererContractTests\|FullyQualifiedName~CopilotRendererTests\|FullyQualifiedName~SquadRenderingContractTests\|FullyQualifiedName~CapabilityDegradationsTests\|FullyQualifiedName~HotshotGoldenContractTests"`, then the full suite | Exact tool lists per §5.5. For example, Claude `architect` is exactly `[TodoWrite, Skill, Read, mcp__codegraph__*, mcp__kyber-weave__*, mcp__context7__*, Grep, Glob, Edit, Write, NotebookEdit, WebFetch, WebSearch, Agent(azure-reader, research-agent)]`. Pi `architect` is `read, grep, find, ls, edit, write`, and Pi `docs-dev` is the same plus `bash`. Each of the three roles on Claude, Pi and ZCode, plus `architect` and `docs-dev` on Copilot, gets exactly one `envelope-granted` record: the Details name the shared profile, the envelope and the resolved capabilities, and contain no `widen` substring. None has `safety-narrowed` or `capability-not-isolable`. Existing `permission-not-expressible` records are unchanged. Expected sets are computed through `Resolve`. Each honouring target's output changes when a fixture envelope is bound. Factory, OpenCode and Antigravity emit no `envelope-granted`. The helper returns null when no shared `ask` becomes `allow`. | R1 is written against E2's tree. The missing `BuildEnvelopeGranted` compile failure is accepted, along with every per-agent assertion failing: `architect` still lacks `Edit` and `Write` on Claude. | R2 passes R1 unmodified, K1 stays green, and the full suite is green. |
| B1/B2 | `SquadCanonicalContentTests.cs` | `"FullyQualifiedName~SquadCanonicalContentTests"` | `architect.md` contains "You run no processes" and names `docs-dev`. `plan-authoring.md` contains no `DOCS_VALIDATE: pass`. `conductor.md`, `plan-path.md` and `spec-path.md` each contain "`docs-dev` validation pass". `product-owner.md` does not contain "validate the documentation corpus". | B1 fails against the current bodies. | B2 passes B1 unmodified. |
| T1, A1, W1 | No test (documentation tasks) | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` then `-- docs drift .` | Named no-test tasks. The corpus stays at zero findings. | n/a | Both checks report zero findings. |
| L1, L2 | No test (live verification) | Manual, in a scratch repository (§8) | Named no-test tasks. A manual live check replaces a test, as described in §8. | n/a | Evidence is recorded in the completion digest. L2 may close as a recorded residual. |

**Compile-state rule.** A RED task can leave the test project uncompilable until its GREEN task
lands, so RED/GREEN pairs run one at a time in the build lane. The second concurrency slot takes
work that does not build the test project.

---

## 8. Tasks

| # | Phase | Required skills | Depends on |
|---|---|---|---|
| K1 | Characterization | `test-dev` | — |
| T1 | Todos | `app-docs-standard` | — |
| E1 | RED | `test-dev` | K1 (sequencing) |
| E2 | GREEN | `csharp-dev` | E1 |
| R1 | RED | `test-dev` | E2 |
| R2 | GREEN | `csharp-dev` | R1 |
| B1 | RED | `test-dev` | R2 (sequencing: compile state and `architect.md`) |
| B2 | GREEN | none; canonical instruction Markdown | B1 |
| A1 | ADR | `architecture-decision-record` | R2 |
| W1 | Docs alignment | `app-docs-standard` | A1, B2 |
| L1 | Live, Claude | none; needs a shell and the Claude Code CLI | B2 |
| L2 | Live, Pi and ZCode | none; owner-run | B2 |
| G1 | Gate suite | `csharp-dev`, `test-dev` | W1, T1 |
| G2 | Closeout | `app-docs-standard` | G1, L1, L2 |

### K1 — Pin today's narrowing on the non-honouring targets

- **Files:** `tests/KyberWeave.Tests/FactoryRendererContractTests.cs`,
  `OpenCodeRendererContractTests.cs`, `AntigravityRendererContractTests.cs`,
  `Squad/CopilotRendererTests.cs`, `SquadSourceTests.cs`.
- **Objective:** the §7 K1 row, asserted by agent name.
- **Assertion rule:** assert that the record's Details *contain* `filesystem.write`, not the full
  narrowed list. R2 removes `process.execute` from `architect`'s shared `ask` set, and the pins
  must survive that.
- **Acceptance:** green on the first run. No production edit.

### T1 — Follow-up todos (D1)

- **Files:** the two new todos in §5.7, and their rows in `docs/todo/README.md`.
- **Content:**
  - Each todo carries `doc-type: todo`, `component: KyberSquad` and `status: draft`.
  - Each cites K1's pinning tests and this plan.
  - The Factory/OpenCode/Antigravity todo names each renderer's narrowing site from §4.2.
- **Acceptance:** `docs validate` and `docs drift` report zero findings.

### E1 — RED: envelope source contract

- **Files:** as in §7.
- **Objective:** assert §5.1 and §5.2 with `SquadFixture`, including:
  - an `allow`-over-`deny` envelope is rejected (rule 6);
  - a map key `factory` is rejected with the honouring-set hint (rule 3);
  - `copilot-capability-profile` is rejected with the migration hint (rule 1).
- **Acceptance:** fails only for the missing members. No existing assertion is weakened.

### E2 — GREEN: model, loader, validator, schemas and resolver (behaviour-preserving)

- **Files:**
  - `src/KyberWeave.Core/Squad/Model/SquadSource.cs`
  - new `src/KyberWeave.Core/Squad/Model/SquadCapabilityEnvelopes.cs`
  - `src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs` (`:253-263`, `:566`, `:621`)
  - `src/KyberWeave.Core/Squad/Validation/SquadSourceValidator.cs` (`:59-92`)
  - `src/KyberWeave.Core/Squad/Rendering/CopilotRenderer.cs` (`:289-323`)
  - `products/kyber-squad/schemas/agent.schema.json`
  - `products/kyber-squad/schemas/capability-profiles.schema.json`
  - `products/kyber-squad/profiles/capabilities.yml` (`architect-copilot` gets
    `targets: [copilot]`, permissions unchanged)
  - `products/kyber-squad/agents/architect.md:8` (`target-capability-profiles: {copilot: architect-copilot}`)
- **Objective:** E1 green with Copilot output unchanged. No other renderer is touched.
- **Acceptance:**
  - The §7 E1/E2 GREEN column.
  - `dotnet build KyberWeave.sln -c Release --no-restore` is warning-clean.
  - Both `dotnet format … --verify-no-changes` gates pass.

### R1 — RED: per-target lowering and records

- **Files:** as in §7.
- **Objective:** the §7 R1/R2 observable behaviour.
  - Under D4a-A, also add the `docs-dev` tools-only Hotshot list. Its guard must fail while
    `docs-dev`'s Copilot tools still equal the golden.
  - Update every non-honouring contract test that hard-codes `docs-dev`'s records.
- **Acceptance:** fails for the stated reasons.

### R2 — GREEN: lowering, helper and canonical source

- **Files:**
  - Code: `ClaudeRenderer.cs`, `PiRenderer.cs`, `ZCodeRenderer.cs`, `CopilotRenderer.cs` and
    `CapabilityDegradations.cs`, at the sites listed in §5.3.
  - Canonical source: `capabilities.yml`, and the frontmatter of `architect.md`,
    `product-owner.md` and `docs-dev.md`, as in §5.4.
- **Objective:** R1 green without weakening an assertion. The class remarks explain *why*
  permissions come from the effective profile and role identity from the shared name.
- **Acceptance:**
  - R1 and K1 are green, unmodified.
  - The full suite is green.
  - The build is warning-clean and the format gates are clean.

### B1 — RED: body contract

- **Files:** `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`.
- **Objective:** the §7 B1/B2 row.
- **Acceptance:** fails against the current bodies.

### B2 — GREEN: validation routing in the bodies

- **Files:**
  - `products/kyber-squad/agents/architect.md` (`:24`, `:64`)
  - `products/kyber-squad/agents/architect/references/plan-authoring.md` (`:45-67`)
  - `products/kyber-squad/agents/conductor.md` (`:47`, `:53`)
  - `products/kyber-squad/agents/conductor/references/plan-path.md` (`:8`, `:17-18`)
  - `products/kyber-squad/agents/conductor/references/spec-path.md` (`:16-21`)
  - `products/kyber-squad/agents/product-owner.md` (`:47`)
- **Objective:** the wording in §5.6. It uses Config Reg names, never a hard-coded
  documentation root.
- **Acceptance:** B1 is green. `docs-dev.md` is unchanged.

### A1 — ADR 0023 (D10-A)

- **Files:** new `docs/adr/0023-target-capability-envelopes.md`, and its row in
  `docs/adr/README.md`.
- **Objective:** the D10-A outline in §3. It relates to ADRs 0017, 0019, 0021 and 0022, and
  notes that ADR 0022's `documentation` write-only example now holds `process.execute: ask`
  (D4a-A).
- **Acceptance:** both documentation checks report zero findings.

### W1 — Canonical documentation alignment

- **Files:**
  - `docs/kyber-squad/requirements.md`:
    - KS-002 (`:25`): the envelope clause from D9-A.
    - Taxonomy (`:51`): amend `safety-narrowed`, and add an `envelope-granted` row (D7-A).
    - Matrix rows `:65`, `:66`, `:70` and `:73`: add "target capability envelopes honoured".
    - Non-Broadening Guarantee (`:80-86`): an envelope is canonical source, not rendering. It
      can resolve `ask` to `allow`, never lifts `deny`, and is always recorded.
    - `decided-by` gains ADR 0023.
  - `docs/kyber-squad/architecture.md`:
    - `:97-98`: the loader validates envelope bindings.
    - §2 (`:114-119`): a new envelope rule.
    - `:306-310`: the Copilot-only bullet becomes "Target capability envelopes".
  - `docs/kyber-squad/onboarding.md`: a new subsection on planner and validator access on Claude,
    Pi and ZCode. It covers:
    - the whole-tree grant, bounded only by instructions;
    - on Claude, that the parent session's permission mode decides prompting, so run the conductor
      in default mode for confirmation;
    - that Pi grants silently;
    - that `kyber-weave` must be on PATH for `docs-dev`;
    - removing hand-edited workaround files such as `.claude/agents/architect.md` and its hook
      before `squad update`, which otherwise preserves them as locally modified.
  - `products/kyber-squad/README.md:98-112`: the frontmatter example.
- **Acceptance:** both documentation checks report zero findings.

### L1 — Live verification on Claude (no-test task)

- **Setup:** a scratch git repository with `docs init` Config Reg, with this build's CLI on PATH
  as `kyber-weave`.
- **Steps:**
  1. Run `kyber-weave squad install --target claude`. Confirm the rendered tools for
     `architect`, `product-owner` and `docs-dev` against §5.5, and the `envelope-granted` receipt
     records.
  2. In an interactive default-mode session, have `architect` save a Draft plan and its index
     row. Observe the prompt and the successful write.
  3. Repeat under `acceptEdits`. Observe the write succeeding without a prompt.
  4. Record whether a write outside the plan directory is prevented. The expected answer is no;
     this is the D2 = C trade. Remove that file afterwards.
  5. Dispatch `docs-dev` with the validation-only packet from §5.6. Observe `docs validate` and
     `docs drift` running through `Bash`.
  6. Record what `claude -p` does with the architect's `Write`.
- **Evidence:** harness version, commands and outcomes, in the completion digest.

### L2 — Live verification on Pi and ZCode (owner-run)

- **Scope:** L1's steps 1, 2 and 5 on Pi (pi-coding-agent 0.84.0 or later, pi-subagents 0.19.0 or
  later) and on ZCode 3.14.x.
- **Fallback:** a harness that is unavailable is recorded as a residual in closeout, not claimed.

### G1 — Declared gate suite

- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`
  - the full `dotnet test` suite
  - `docs validate .` and `docs drift .`
- **Acceptance:** nothing blocks merge.

### G2 — Closeout (`docs-dev`)

- **When:** after the single end-of-run `code-reviewer` returns `APPROVE`.
- **Work:**
  - Verify the acceptance criteria.
  - Harvest durable facts that W1 missed.
  - Record the L2 residuals.
  - Confirm the source todo was promoted and archive it with a superseded row naming this plan.
  - Archive this plan and synchronise the plan index.

---

## 9. Dependency graph and `MAX_CONCURRENCY`

```text
K1 ─► E1 ─► E2 ─► R1 ─► R2 ─┬─► B1 ─► B2 ─┬─► L1 ─────────┐
                             │             ├─► L2 ─────────┤
                             └─► A1 ───────┴─► W1 ─► G1 ─► G2
T1 ──────────────────────────────────────────────────► G1
```

| Level | Tasks |
|---|---|
| 1 | K1, T1 |
| 2 | E1 |
| 3 | E2 |
| 4 | R1 |
| 5 | R2 |
| 6 | B1, A1 |
| 7 | B2 |
| 8 | W1, L1, L2 |
| 9 | G1 |
| 10 | G2 |

**`MAX_CONCURRENCY: 2`.**

- The build lane (K1 through B2) is serial by the compile-state rule.
- The second slot takes T1, A1 and W1. These build only `src/`, for the docs CLI.
- L2 is owner-run and holds no worker slot. At level 8, W1 and L1 fill the two slots.
- The dependencies on K1 and R2 for E1 and B1 are sequencing only. `architect.md` is edited by
  E2, R2 and B2, so those tasks must not overlap.

---

## 10. Sequencing with the conductor skill-lowering follow-on

The follow-on is not planned here. It lowers the primary `conductor` on Claude to
`.claude/skills/conductor/SKILL.md`, keeps the subagent, and is driven by the fallback profile's
`no-primary-agent`. It meets this plan in five places:

- **`ClaudeRenderer.cs`.** This plan changes `ResolveTools` (`:288-356`),
  `BuildDegradationRecords` (`:358-443`) and the remarks (`:24-34`). The follow-on changes the
  agent loop in `RenderAsync` (`:169-180`) to emit a skill for a `Primary` agent, and adds that
  lowering's records.
  - Those records enumerate capability decisions, as `PiRenderer.cs:571-620` and
    `ZCodeRenderer.cs:903-942` already do. They must read decisions through
    `SquadCapabilityEnvelopes.Resolve`. `conductor` binds no envelope, so the result is the shared
    `orchestrator` profile, but that keeps one source of truth.
- **`ClaudeRendererContractTests.RenderAsync_Claude_RendersTheRealCanonicalCorpus`**
  (`:123-413`). Both changes alter its expected sets.
- **`SquadRendererRegistry.cs:160-198`**, the native single-projection checks. The follow-on emits
  a skill for an agent identity on a native target. This plan does not touch that code.
- **`docs/kyber-squad/requirements.md:65`** (the Claude matrix row, "Not lowered"),
  `architecture.md`'s rendering table, and `onboarding.md`. Both changes edit them.
- **Merge order.** Land this plan first. The follow-on is smaller in `ClaudeRenderer.cs` and can
  rebase on the resolver. Whichever lands second rebases. Never run the two RED phases at the
  same time.
- **Interaction.** Once the conductor runs in the main thread, `architect`, `product-owner` and
  `docs-dev` are depth-1 subagents. Their envelope grants then go through the main session's
  permission mode, which is exactly the Claude sentence in the `envelope-granted` Details. The
  architect's own delegations to `azure-reader` and `research-agent` run at depth 2. This
  environment's `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1` blocks them, and neither change
  addresses that.

---

## 11. Risks

- **The whole-tree planner grant is instruction-bounded.** On Claude it is silent under
  `acceptEdits`, `auto` or `bypassPermissions`. On Pi it is always silent. This is accepted under
  D2 = C, recorded by D7, and documented in onboarding.
- **`docs-dev` gains a whole shell on four targets under D4a-A.** It is a `fast`-model role, and
  only instructions confine it to the two checks. D4a-C avoids any shell but leaves Pi and
  Factory unvalidated.
- **Copilot behaviour changes.** `architect` loses `execute`, `docs-dev` gains it (D4a-A), and
  both gain `envelope-granted` records (D7-A).
- **Receipt churn.** There are new records on the four honouring targets. Under D4a-A,
  `docs-dev`'s records also change on the seven other targets.
- **A breaking canonical-source shape.** `copilot-capability-profile` and `target` are removed.
  They ship in the APM archive, and KS-005 lockstep contains the impact. No external fork is known.
- **Workaround files survive an update.** The hand-edited `.claude/agents/architect.md` and its
  hook in the originating worktree would be preserved by `squad update` as locally modified. W1's
  onboarding note covers this.
- **Merge overlap** with the conductor follow-on (§10).

---

## 12. Verification gates, review and closeout

- **Gates:** the declared gate suite (G1), the full test suite, and `docs validate .` plus
  `docs drift .` at zero findings after every `docs/` edit.
- **Live evidence:** L1 is required before closeout. L2 is required, or recorded as a residual.
- **Review:** `task-reviewer` gives up to three passes per task. After that, `code-reviewer` runs
  exactly once over the whole run. There is no commit or push before `APPROVE`.
- **Closeout:** `docs-dev` performs G2.

---

## Related

- [Source todo](../todo/claude-renderer-ask-narrowing.md)
- [Kyber-Squad requirements and degradation contract](../kyber-squad/requirements.md)
- [Kyber-Squad architecture](../kyber-squad/architecture.md)
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md)
- [ADR 0017](../adr/0017-copilot-deterministic-tool-order.md): the Copilot tool order that `docs-dev`'s `execute` follows
- [ADR 0019](../adr/0019-pi-native-subagents-and-primary-lowering.md), [ADR 0021](../adr/0021-zcode-command-lowering-and-resource-relocation.md) and [ADR 0022](../adr/0022-antigravity-native-agents.md): renderer precedents
- [Archived Pi thinking and Antigravity plan](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md): the `capability-not-isolable` precedent and the compile-state operating rule
- [Shell-implies-write live verification todo](../todo/shell-implies-write-live-verification-other-targets.md)
- [Kyber-Squad product README](../../products/kyber-squad/README.md)
