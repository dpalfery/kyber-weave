---
id: plans/2026-09-14-pi-harness-target
title: Add Pi as a Kyber-Squad harness target with a pi-subagents renderer
doc-type: plan
status: current
owner: dpalfery
last-reviewed: 2026-09-14
component: KyberSquad
development-mode: test-first
keywords:
  - pi
  - pi-subagents
  - PiRenderer
  - pi coding agent
  - harness target
---

# Add Pi as a Kyber-Squad harness target with a pi-subagents renderer

**Status:** Ready
**Date:** 2026-09-14 (revision of the 2026-09-14 Ready plan, reopened same day)
**Approval:** Approved by the owner (dpalfery) on 2026-09-14 through the explicit **approve and execute** choice ("now execute the plan"), relayed by the conductor, covering sections 2, 3, 6, and 7 **as they stood at that save**. That approval is **reopened** as of 2026-09-14 — see section 4, "Reopening (2026-09-14)" — because the owner's T8 live-Pi observations changed two requirements after the approved Test contract and scope were already executing. Re-approval is required before implementation resumes.
**Development mode:** test-first
**Source todo:** [pi.md](../todo/pi.md)
**Goal:** Declare `pi` in the Squad target catalog and register a `PiRenderer`, so that
`kyber-weave squad install --target pi` renders the canonical corpus into `.pi/agents/` and
`.pi/skills/` end to end. Update the Kyber-Squad docs, then close the todo out.

## 1. Problem and goal

Today `squad install --target pi` fails while arguments are parsed: `SquadTargetCatalog` has no `pi`
token. Pi does not reach the coverage preflight that points at `docs/todo/<target>.md`. The only Pi
code in the repository is KyberDash's session reader, which deploys nothing.

The todo assumed that Pi has no agent primitive, so every agent would be lowered to a skill.
That assumption no longer holds. Pi core still ships no sub-agents. However, the owner runs Pi with
the `@tintinweb/pi-subagents` extension, which defines a Claude Code-like custom-agent file format.
The owner decided that this format is the rendering target (U5). Pi therefore becomes a
**native** target in `SquadRendererRegistry`:

- The 20 subagent-invocation agents render as `.pi/agents/<name>.md`.
- The `invocation: primary` conductor is the one exception. It lowers to a skill, following the
  canonical fallback profile (R3).

When the work is done:

- `pi` is a permanent catalog token that is selected only by explicit or configured targets.
- The renderer is registered and covered by contract tests that render the real
  `products/kyber-squad` corpus.
- `squad doctor` lists `pi` as available.
- `--target antigravity,pi` deploys disjoint trees.
- The docs state ten declared targets and six registered renderers.
- The todo is superseded, and the work ships as a pushed PR.

## 2. Approved decisions

The owner made these decisions, and the conductor relayed them to the architect on 2026-09-14.
They are execution constraints, not open questions.

- **U1 (Artifact):** This work is a plan, not a spec.
- **U2 (Mode):** `development-mode: test-first`. `test-dev` writes RED, the implementation
  specialist makes it GREEN, and refactoring happens while the tests stay green. Every
  implementation task has a Test-contract row (section 7).
- **U3 (Skill output directory):** `.pi/skills/`, not `.agents/skills/`. Pi therefore never shares
  output paths with Antigravity.
- **U4 (Detection):** Explicit or configured targets only. `SquadTargetResolver.Markers` gets no
  `.pi/` entry, which matches Antigravity.
- **U5 (Classification):** Treat Pi as having the subagents extension configured on the owner's
  machine. Discover its real agent-file format and render the canonical agents natively, not as
  pure role-skill fallback. Record what was verified, and when, in this plan and in the
  renderer's `<remarks>`. List unverifiable facts under GAPS (section 14).
- **U6 (End state):** The work finishes with a pushed PR. It uses the standard endings: a final
  `code-reviewer` council, then a `docs-dev` closeout. The closeout migrates durable facts,
  archives this plan, supersedes and archives the todo, and updates the todo and
  renderer-coverage indexes.
- **Q1 (Extension tools):** Option A, chosen by the owner on 2026-09-14. Every rendered Pi agent
  emits `extensions: false`, so only built-in tools are available, and `network.read: allow`
  records `permission-not-expressible`. Squad agents on Pi therefore get no CodeGraph, docs MCP,
  or web tools.
- **Q2 (`ask`-narrowing follow-up):** Option A, chosen by the owner on 2026-09-14. At closeout
  (T10), add a Pi section to the existing
  [claude-renderer-ask-narrowing todo](../todo/claude-renderer-ask-narrowing.md) and widen its
  title and scope to cover both renderers. The owner's choice is the acceptance AGENTS.md requires
  before a todo is widened.

**Reopening additions (2026-09-14), full detail in section 4:**

- **U7 (Conductor `/agents` visibility):** A — R3 stands unchanged; the conductor stays skill-only.
- **U8 (`--global` scope):** B — `--global` is fixed for all six registered targets, each mapped to
  its own verified global root, not only Pi.
- **U9 (Pre-existing unmanaged global files):** A — an onboarding pre-migration step plus a
  `squad doctor --global` collision warning; Squad never deletes an unmanaged file.
- **U10 (models work — profile × harness values, revised 2026-09-14):** resolved the mechanism as a
  new `reviewer` model profile (members: code-reviewer, review-lens, review-triage, task-reviewer;
  section 6b). **U10a RESOLVED:** `test-dev` moves to `fast` (not a new or different profile);
  `mai-code-flash` is deleted as a profile key from `models.yml` entirely — it was always a Copilot
  model name (`MAI-Code-1.1-Flash (copilot)`), never a profile, and is never referred to as a profile
  again anywhere in this plan. That model becomes `fast`'s Copilot value, replacing `GPT-5.6 Luna
  (copilot)`. **U11 (cost principle: avoid Sonnet on API-billed plans) RESOLVED** and reapplied per
  the owner's exact per-harness Claude values (section 4). **G14 (fallback model) REMOVED**: the
  owner ruled the harnesses do not support a fallback-model concept, so Muse Spark's free token is
  the only value — no fallback mechanism, task, or schema change exists anywhere in this plan.
  **U12 (Copilot's `reviewer` model) RESOLVED A:** the owner named "Kimi K2.6 Code", which GitHub's
  own supported-models page (P47) does not list; the owner chose the recommended alternative on
  2026-09-14: `Kimi K2.7 Code (copilot)` (section 4).

## 3. Resolved by evidence

The architect settled these questions from canonical policy or verified harness behaviour. They
are part of the contract that the **approve and execute** gate approves. Evidence ids (P*n*)
refer to section 5.1.

- **R1 (Token and ordering):** The token is `pi`, with no aliases. `SquadTarget.Pi` is appended
  after `Factory` in the enum and in `ApprovedTargets`. Existing members keep their positions,
  and `--target all` now includes `pi`. Receipts persist tokens, so the token is permanent.
- **R2 (Native classification):** `SquadTarget.Pi` joins the `isNative` roster in
  `SquadRendererRegistry.ValidateRenderResult`. No Pi output may carry a `role-` prefix. Agents and
  skills live in separate namespaces (`.pi/agents/` and `.pi/skills/`), so the seven
  agent/skill name intersections are not collisions on Pi (architecture section 3, native branch).
- **R3 (Conductor lowers to a skill) — outcome reopened 2026-09-14, see U7 (section 4).** Its
  predicate facts (P7, P14) were re-verified 2026-09-14 and stand; only the *adopted outcome*
  (skill-only) is contested. Every agent with `invocation: primary` is rendered
  according to its fallback profile's `no-primary-agent` value, read from the loaded source.
  - The current corpus has one such agent, `conductor`, and its value is `skill`.
  - Its output is `.pi/skills/conductor/SKILL.md`, with a `role-skill-fallback` degradation.
  - **Why lower it:** Pi core has no primary-agent selection. `.pi/SYSTEM.md` and `--system-prompt`
    replace the prompt for the whole session (P7). A conductor rendered as a subagent would run at
    depth 1, which puts its specialists at depth 2. The default `maxSubagentDepth: 2` then removes
    the nested delegation that `architect` and `code-reviewer` need (P14). A top-level skill runs
    at depth 0, where the extension's `Agent` tool is available, and the specialists' nested
    rosters still work.
  - **Omit value:** `no-primary-agent: omit` emits nothing and records `omitted`.
  - **Fail closed:** If a lowered identity is already occupied by a canonical skill, rendering throws
    `SquadRenderValidationException`. That case cannot use a `role-` prefix on a native target.
- **R4 (Skill collision with the owner's global `conductor` skill is safe):** The project's
  `.pi/skills/conductor` takes precedence over `~/.pi/agent/skills/conductor` (P4).
  - Pi prints a collision diagnostic that names the skipped global file.
  - `/skill:conductor` in that project loads the Squad conductor.
  - `conductor-v3` has a different name and is unaffected.
  - Outside the project, the global skill is unchanged.
- **R5 (Project agents override global agents with the same name):** The owner has
  OpenCode-format global agents in `~/.pi/agent/agents/`, such as `architect.md` and `test-dev.md`.
  They lack `name` and `tools`, so today they register with all seven built-in tools (P19). Project
  `.pi/agents/<name>.md` files override them silently (P9). This narrows those agents inside Squad
  projects, and it is the intended project authority. Onboarding documents it.
- **R6 (Agent frontmatter contract):** Section 6 defines it: `name`, a single-line
  `description`, an optional `model`, a required `tools`, `extensions` (per Q1), and a
  conditional `allowed_subagents`. No other extension keys are emitted.
- **R7 (`tools` is always emitted):** Omitting `tools` grants all seven built-ins (P12), which
  would widen the canonical profile. The same rule appears as D4 of the archived
  [Claude renderer plan](../archive/plans/2026-08-23-claude-code-native-renderer.md).
- **R8 (Capability-to-tool lowering):** Only `allow` grants a tool (section 6 table).
  - `ls` counts as `filesystem.search`, because enumerating directories is going looking
    (see the `capabilities.yml` comment).
  - `network.read` and `network.publish` have no Pi built-in tool.
- **R9 (`ask` narrows to deny):** Pi has no permission prompts (P2), and `tools` is binary. Every
  `ask` withholds its tools and records `safety-narrowed`. This follows architecture section 2,
  rule 2, and the `safety-narrowed` definition in requirements.md. Q2 covers the known consequence
  for `architect` and `product-owner`.
- **R10 (Delegation):** When `delegate: allow` and `delegates-to` is non-empty, the renderer emits
  `allowed_subagents` as the roster, comma-separated in canonical order. Pi enforces this list at
  runtime and rejects out-of-list types without falling back (P14).
  - `delegate: allow` with an empty roster emits nothing and records `permission-not-expressible`,
    because `all` would reach `general-purpose`, which holds every tool.
  - `delegate: ask` records `safety-narrowed`.
  - No corpus agent hits either of these two cases today.
- **R11 (`prompt_mode`):** Omitted, so the default `replace` applies. Canonical bodies are
  self-contained and direct the agent to read the root `AGENTS.md`. `append` would prepend the
  parent session's whole prompt, including a loaded conductor skill, to every specialist (P16).
- **R12 (Model) — "add no `pi:` values" reopened 2026-09-14, see U10 (section 4).**
  - Add `pi` to `SquadSourceLoader.ModelProfileFields` and to `model-profiles.schema.json`. Every
    other declared target, fallback targets included, already has a field there. **Unaffected by
    U10; stands as shipped.**
  - ~~Add **no** `pi:` values to `models.yml`. Pi model tokens are `provider/modelId` values tied to
    each user's configured providers. The owner's default is `zai/glm-5.3-flash` (P15, P19), so no
    portable value exists.~~ **Superseded 2026-09-14:** this conclusion evaluated only the literal
    `provider/modelId` pin form; it did not evaluate pi-subagents' documented fuzzy-name resolution
    (P34), which degrades per-user rather than requiring a portable literal. The owner has since
    named actual values (U10); `models.yml` gains `pi:` entries once U10 is answered.
  - The renderer resolves `pi`, then a non-`inherit` `default`, exactly as `ResolveClaudeModel`
    does. It emits `model` only when the result is not `inherit`, so today no agent carries `model`.
- **R13 (Packer and CLI text):**
  - `SquadPacker.CollectApmEntries` excludes `.pi/`.
  - The `--target` descriptions in `SquadSettings` (install and update) list `pi`.
  - `SquadCommandComposition.ResolveRenderer()` registers `new PiRenderer()`, and its remarks name
    Pi as native.
- **R14 (This repository's `.pi/subagents.json`):**
  - Explicit-only detection means a `.pi/` directory, including this repository's, never selects
    Pi on its own.
  - The renderer emits paths only under `.pi/agents/` and `.pi/skills/`. It never touches
    `.pi/subagents.json`, `.pi/settings.json`, or any other `.pi/` file. Tests assert both.
  - The renderer does not write `subagents.json`. Onboarding recommends `fallbackSubagent: none`
    for strict dispatch (P17).
- **R15 (Coexistence with Antigravity):** `--target antigravity,pi` produces disjoint path sets
  (`.agents/skills/**` and `.pi/**`), so neither target can overwrite or orphan the other.
  - Pi also reads the project's `.agents/skills/` (P3). With both targets installed, Pi keeps its
    `.pi/skills` copies, warns on each duplicate name, and also lists Antigravity's lowered agent
    skills.
  - The same exposure already exists with `--target antigravity` alone. Onboarding documents it;
    the renderer cannot prevent it.
- **R16 (Extension prerequisite):** Pi agent output needs `@tintinweb/pi-subagents` 0.19.0 or
  later, which requires Pi 0.84.0 or later.
  - Onboarding and the renderer remarks state this prerequisite.
  - `squad doctor` is not extended to detect the extension; that is out of scope.
  - The owner overrode the todo's "first-party convention only" caution in U5.
- **R17 (File count):** Test assertions derive the count from the loaded corpus:
  `Agents.Count + Σ agent resources + Skills.Count − shared-identity skills + Σ non-suppressed
  skill resources`.
  - The conductor contributes one principal whether it renders as an agent or a skill.
  - That formula equals the Claude and Copilot formula on this corpus, so the expected literal is
    the one requirements.md records for a fresh Copilot render (113). T7 confirms it.
  - The todo's figures of 45 files and seven `role-` files are withdrawn. Pi emits zero `role-`
    files and 45 principals: 20 agent files plus 25 skill files.
- **R18 (Cross-target `--global` physical-root mapping) — added 2026-09-14, resolves U8-B.**
  `SquadDeploymentPlan`/`SquadTransaction` gain a **per-file physical root**, keyed by
  `SquadDeploymentFile.Target`, consulted only when `Scope == Global`:
  - **Project scope is unchanged for every target** — one shared `plan.PhysicalRootPath`, exactly as
    it ships today. This is the only branch the five already-shipped contract suites exercise, so
    their assertions are unaffected.
  - **Global scope** resolves each file's target to a harness-specific root (section 6 table,
    P40–P44), reading that harness's override environment variable first, falling back to its
    documented default. Every one of the six targets has a verified global root (section 5.1
    P40–P44, plus Pi's existing P9/P3) — none needs the "fails preflight, no root exists" fallback
    the reopening dispatch anticipated.
  - Every renderer becomes `Scope`-aware for its relative-path *prefix* only: under Project scope it
    keeps emitting `.{harness}/agents/…` / `.{harness}/skills/…` (or `.agents/skills/…` for
    Antigravity) exactly as today; under Global scope it drops the `.{harness}/` wrapper and emits
    `agents/…` / `skills/…` directly, because the physical root itself *is* the harness's global
    directory. No renderer's Project-scope output changes by one byte.
  - State bookkeeping (`squad.lock.yml`, `squad.receipt.json`) is unaffected — it was already
    scope-correct (P27) and needs no change.
  - Containment is enforced by the same `SquadPathPolicy`/`SquadFileSystemPathSemantics` machinery
    already governing Project scope, applied per-target-root instead of per-plan-root; no new
    containment logic is introduced.
  - Tests inject a fake global-root resolver (mirroring the existing `ISquadUserPaths` fake pattern)
    so no test ever touches the real home directory (section 7, T12).

## 4. Approval record

- **Approved by:** the owner (dpalfery).
- **Approved:** the **approve and execute** gate for this plan as saved: decisions U1–U6, Q1, and Q2
  (section 2); R1–R17 (section 3); the rendering contract (section 6); and the test-first Test
  contract (section 7).
- **Date:** 2026-09-14.
- **Channel:** the owner's explicit instruction "now execute the plan", relayed by the conductor.
- **Ledger (original):** closed at approval. Q1 and Q2 were answered A by the owner on 2026-09-14
  and are recorded in section 2.

### Reopening (2026-09-14)

During T8 (live Pi check), the owner reviewed Pi's `/agents` in the scratch install and changed
three requirements, relayed by the conductor:

1. **Conductor visibility** — verbatim: *"i dont see a conductor ? pi allows for root custom agents
   so the combined conductor should be there."* This bears directly on the approved Test contract:
   section 7 row **T2, criterion 5** (the conductor's rendering assertions) and section 3 **R3**
   (conductor lowers to a skill only, never an agent file).
2. **Pi `--global`** — verbatim: *"add the --global that allows for a global install from kyber
   cli."* This moves an item out of section 11 "Out of scope" (`--global Pi installs`) into scope.
3. **Missing agent models** — verbatim: *"we have an issue with the pi harness, the renderer did
   not add the agent models."* Raised by the conductor mid-revision, not by the owner directly in
   the T8 session, but carrying the same weight: it bears on the approved Test contract row **T2,
   criterion 3** (`model` presence/absence) and section 3 **R12** (deliberately no `pi:` values in
   `models.yml`), and moves a second item out of section 11 ("Adding `pi:` values to `models.yml`")
   into scope.

Because each change alters an approved Test-contract row and/or the approved scope boundary
(section 11), approval is reopened under the test-first contract's own rule ("Changing an approved
Test contract... is a scope change. Return the plan to Draft and require the conductor to relay
reapproval before implementation resumes.") and under the standing rule that a scope change reopens
approval. The plan returns to **Draft** (this save): body Status, frontmatter `status`, and the
section 11 boundary all move together; the index row moves to Draft in the same save.

**What stands unchanged from the original approval:** U1–U6, Q1, Q2 (section 2); R1, R2, R4–R11,
R13–R17 (section 3, unaffected by any of the three changes); everything already implemented and
audited PASS under T1–T7 and T8's headless part (see the dispatch's "What is already done"). Nothing
is redone; sections 5–14 below record only the delta this reopening requires. R3's *predicate facts*
(P7, P14) were re-verified on 2026-09-14 and stand unchanged (see section 5.1, P20–P25); its
*adopted outcome* (skill-only, no `.pi/agents/conductor.md`) was reopened by the owner's question and
is resolved unchanged by **U7** below. R12's "add no `pi:` values" clause is similarly reopened and
resolved (superseded, not silently rewritten) by **U10** below; the rest of R12 (adding the `pi`
field to `ModelProfileFields` and the schema, and the `ResolveClaudeModel`-pattern resolution order)
is unaffected and stands.

### Resolved during reopening (2026-09-14) — U7, U8, U9

The owner answered U7, U8, and U9 on 2026-09-14, given the evidence above. They are recorded here as
resolved and are no longer open; each becomes execution-ready scope once section 6/7/8 below spell
out the delta.

**[U7] — RESOLVED: A.** The conductor stays skill-only. R3 stands exactly as approved: no
`.pi/agents/conductor.md` is emitted, `conductor` remains invisible in `/agents`, and section 7 row
T2 criterion 5 needs no delta. The owner reviewed the evidence in P20–P25 (no root/primary-agent
mechanism exists in Pi 0.84.4 or pi-subagents 0.19.0; the owner's global `conductor-v2.md`/
`conductor-v3.md` are why "conductor-v2"/"conductor-v3" but not "conductor" show in `/agents`, per
P22) and confirmed the skill-only design is correct on those terms. No code, test, or docs delta
follows from U7 beyond recording this in the plan.

**[U8] — RESOLVED: B.** `--global` is fixed uniformly for all six registered targets (`copilot`,
`cursor`, `claude`, `codex`, `antigravity`, `pi`), each mapped to its own verified real global root,
not only Squad's state. Full investigation, contract, and tasks below (section 5.1 P40–P54, section
3 R18, section 6 "Global-scope rendering contract," section 7 rows T12/T13, section 8 T12/T13).

**[U9] — RESOLVED: A.** Onboarding gains a pre-migration step for pre-existing unmanaged global
files, and `squad doctor --global` gains a warning line listing every such collision under the
resolved global roots before install is attempted. Squad never deletes an unmanaged file. Full
contract below (section 7 rows T14/T15, section 8 T14/T15).

---

### Execution amendment (2026-09-14, T17)

During **T17 GREEN** (2026-09-14), after applying the approved `models.yml` and agent `model-profile` changes, the full suite had three failures that §7 row T16→T17 and §8 T17 did not anticipate:

1. `SquadCanonicalContentTests.LoadUnifiedOrchestrationStackDeclaresCanonicalDelegationAndModelProfiles` — hardcoded `task-reviewer`'s old `model-profile` value. Its expected value is updated to `reviewer`.
2. `HotshotGoldenContractTests.CanonicalSourcePreservesGoldenContractOutsideReviewedEvolution` — failed on Copilot `model` values (`GPT-5.6 Luna → MAI-Code-1.1-Flash`, `Grok 4.5 → Grok 4.6`, `Kimi K2.7 Code`).
3. `HotshotGoldenContractTests.CopilotRenderMatchesCheckedInHotshotGoldenContract` — failed on the same Copilot `model` values.

**Resolution (conductor decision, consistent with owner decisions U10–U12; no requirement change):**

- The pinned golden fixture `tests/KyberWeave.Tests/Fixtures/kyber-squad-hotshot-golden.json` is **not** edited.
- Affected agents are **not** added to `EvolvedAgentIdentities`, which would skip all their checks.
- Instead, a narrow `ModelEvolvedAgentIdentities` reviewed-evolution list is added. For those agents only the Copilot `model` comparison is exempt; description, body, tools, capability, delegation, and paths are still asserted.
- A guard assertion fails if any listed agent's model no longer differs from the golden.
- `SquadCanonicalContentTests` gets the approved expected value.
- These edits are performed as T17 rework by test-dev and audited with T17.

---

**[U10] — RESOLVED.** The owner chose the mechanism (a new `reviewer` model profile) and, on
2026-09-14, gave exact per-harness values for every profile (Claude harness values verbatim; Pi
values from the earlier answer; OpenCode, Copilot, and Cursor rules with the architect required to
verify exact vendor strings before applying them). Full detail, including the final per-harness
table with before → after values and evidence citations, is in section 6b. Summarized here:

**Settled, not open:**
- **Mechanism:** a new `reviewer` profile in `models.yml`, holding `code-reviewer`, `review-lens`,
  `review-triage`, and `task-reviewer`. The owner explicitly authorizes the `model-profile`
  reassignment this needs in `products/kyber-squad/agents/{code-reviewer,review-lens,review-triage,
  task-reviewer}/*.md` — an always-human-review path (section 5.2) — and this authorization is that
  review, recorded here rather than inferred.
- **`test-dev` moves to `fast`** (U10a, below) — not to a new profile, and not to `general`.
- **`mai-code-flash` is deleted as a profile key.** It was never a profile — only a Copilot model
  name (`MAI-Code-1.1-Flash (copilot)`). From here on this plan describes it only as a Copilot model
  value, never as a profile.
- **No fallback model concept** (G14, removed — see below).
- **Schema/loader:** confirmed by reading `SquadSourceLoader.ParseModelProfiles` (2026-09-14) that
  `ModelProfileFields` is the fixed set of *harness columns* (`default`, `codex`, `cursor`, `claude`,
  `copilot`, `opencode`, `kilo`, `antigravity`, `warp`, `factory`, `pi`), not a closed set of profile
  *names* — profile ids are ordinary YAML map keys under `profiles:`, parsed generically. Adding a
  `reviewer` profile id is therefore a **canonical-source data change only**: no change to
  `SquadSourceLoader`, `model-profiles.schema.json`, or any renderer's resolution code.
  `SquadSourceValidator.Validate` already takes `agents` and `models` together, so an agent's
  `model-profile` referencing an unknown profile is expected to already fail there — a new
  RED-then-GREEN test proves this generically rather than assuming it (section 7, T16).

---

**[U10a] — RESOLVED: `test-dev` moves to `fast`.** The owner corrected: `mai-code-flash` is NOT a
profile name — it is a Copilot model (`MAI-Code-1.1-Flash (copilot)`). `mai-code-flash` is removed
as a profile key from `models.yml` entirely. `task-reviewer` moves to the new `reviewer` profile.
`test-dev` moves to `fast`, joining csharp-dev, react-dev, research-agent, python-dev, azure-reader,
maui-dev, and docs-dev. `MAI-Code-1.1-Flash (copilot)` becomes `fast`'s Copilot value, replacing
`GPT-5.6 Luna (copilot)` — this resolves both former sub-questions (destination profile, and the
Copilot model's disposition) in one instruction (section 6b).

---

**[U11] — RESOLVED: Cost principle, applied per the owner's exact Claude-harness values.** The owner
directed, verbatim: *"on claude use sonnet for reviewers. test-dev, and coding agents can use haiku,
architect should use opus, conductor could be sonnet or haiku."* This resolves to:
- `reviewer.claude: sonnet` — the owner's sole named exception to "avoid Sonnet."
- `fast.claude: haiku` (unchanged).
- `general.claude: haiku` (changed from `sonnet`) — dal-dev, github-devops, pulumi-dev, and
  tauri-dev are the coding agents the owner meant; product-owner also sits on `general` and rides
  this value as a disclosed side effect, becoming `haiku`.
- `deep-planning.claude: opus` (unchanged).
- `orchestration.claude: haiku` (new field; conductor) — the owner allowed either sonnet or haiku
  for the conductor ("could be sonnet or haiku"); haiku is chosen under the owner's own cost
  principle (avoid Sonnet on API-billed plans except the one named reviewer exception).

The general principle — **avoid Sonnet on API-billed plans**, except the owner's explicit
Claude-harness `reviewer` choice above — extends to every other multi-vendor harness: Copilot's and
Cursor's `reviewer` values (section 6b) use Kimi, not Sonnet.

---

**[U10b] — FULLY RESOLVED** (including U12, answered 2026-09-14). The final per-harness
`reviewer`-profile table, with evidence:

| Harness | Value | Status |
|---|---|---|
| `claude` | `sonnet` | Owner's explicit exception (U11). |
| `codex` | `gpt-5.6-terra` | RESOLVED. Owner: Codex keeps current values unless a newer version of the same model line is documented; none is. `reviewer.codex` equals `general.codex` (owner instruction). |
| `opencode` | `opencode-go/kimi-k2.7-code` | VERIFIED against OpenCode's own Go documentation (P50): `opencode-go/kimi-k2.7-code` is a listed id. Owner: reviewer gets Kimi on the Go plan. |
| `copilot` | **`Kimi K2.7 Code (copilot)`** (U12 resolved A, 2026-09-14; P47) | GitHub's own supported-models page (P47) lists `Kimi K2.7 Code` and `Kimi K3` from Moonshot AI; it does not list `Kimi K2.6 Code`, which the owner originally named. Owner chose A: `Kimi K2.7 Code (copilot)`. |
| `cursor` | `kimi-k2.7-code[]` (architect's choice, accepted by owner 2026-09-14) | Cursor's own docs (P48) confirm Kimi K2.7 Code is in Cursor's model pool — a different family from Cursor's dev values (Composer, Grok), cheap, and code-specialised, per the owner's stated criteria. The exact bracket-suffix config string was not published on the Cursor docs pages fetched; `kimi-k2.7-code[]` follows the same convention already used for every other Cursor token in this file (`composer-2.5[]`, `grok-4.5[]`). The owner delegated this choice to the architect; no objection raised, so accepted. |
| `pi` | `opencode-go/kimi-k2.7-code` | Owner's own explicit choice ("the go plan"), unchanged from the first answer. |

*All items resolved.* Independent of U10a, which is fully resolved.

---

**G14 — REMOVED.** The owner ruled out a fallback-model concept: *"the harnesses don't support the
concept of a fall back model so just ignore that requirement."* Muse Spark's free token
(`opencode/muse-spark-1.3-contributor-free`) is used alone, with no fallback task, schema field, or
document reference anywhere in this plan. The prior G14 NEEDS_DECISION is withdrawn.

---

**[U12] — RESOLVED: A.** The owner named `Kimi K2.6 Code` for Copilot's `reviewer` profile.
GitHub's own supported-models documentation (`docs.github.com`, P47, fetched and re-checked 2026-09-14)
lists exactly two Moonshot AI models with GA status: `Kimi K2.7 Code` and `Kimi K3`. No `Kimi K2.6` of
any kind — with or without a "Code" suffix — appears anywhere on that page. The owner chose option A
on 2026-09-14: `Kimi K2.7 Code (copilot)`, the verified, code-specialised Moonshot model on Copilot;
closest match to the owner's intent and matching the value already used for `reviewer` on OpenCode,
Cursor, and Pi (section 6b), so all five model-capable harnesses carry the same Kimi generation. The
owner also accepted the architect's Cursor value `kimi-k2.7-code[]` (decision #12, section 4).

- **Status:** RESOLVED A.
- **Owner answer:** 2026-09-14, "a" (option A).
- **Resolved value:** `Kimi K2.7 Code (copilot)` (P47).
- **Citation:** GitHub's official supported-models page, P47.

### Re-approval (2026-09-14)

- **Approved by:** the owner (dpalfery).
- **Approved:** the **approve and execute** gate for this plan as re-opened: resolutions U7–U12 (section 4); the rendering contract (section 6); and the test-first Test contract (section 7).
- **Date:** 2026-09-14.
- **Channel:** the owner's explicit instruction "approve", relayed by the conductor.
- **Ledger:** closed at re-approval. No OPEN items remain.

## 5. Investigation findings

**Exploration provenance.**

- **Code:** one `codegraph_explore` call covered the registry, `ClaudeRenderer`, and
  composition. Other code facts came from targeted Read and Grep. No query failed.
- **Docs:** `docs_explore` and `docs_for_symbol` answered. `SquadRendererRegistry` is claimed only
  by `docs/kyber-squad/architecture.md`, and `SquadTargetCatalog` has no `code-refs` owner.
- **Pi facts:** read from the installed packages on this machine, 2026-09-14. The Pi binary was
  not executed, because the architect has no shell.

### 5.1 Verified Pi facts (source, 2026-09-14)

Paths are abbreviated as follows:

- `PCA` is `/opt/homebrew/Cellar/pi-coding-agent/0.84.1/libexec/lib/node_modules/@earendil-works/pi-coding-agent`.
- `PSA` is `~/.pi/agent/npm/node_modules/@tintinweb/pi-subagents`.

| # | Fact | Source |
|---|---|---|
| P1 | The installed Pi is `@earendil-works/pi-coding-agent` **0.84.4**, although its Homebrew keg directory is labelled 0.84.1. | `PCA/package.json` (`"version": "0.84.4"`); `~/.pi/agent/settings.json` `lastChangelogVersion: 0.84.4` |
| P2 | Pi core has no sub-agents ("No sub-agents") and no permission prompts ("No permission popups"). | `PCA/README.md` lines 501 and 503 |
| P3 | Skill roots: global `~/.pi/agent/skills/` and `~/.agents/skills/`. Project `.pi/skills/` and `.agents/skills/` (cwd and ancestors up to the git root) load **only after the project is trusted**. | `PCA/docs/skills.md` "Locations"; `PCA/dist/core/package-manager.js` 1976–2017 |
| P4 | Skill name collisions: first found wins, and a diagnostic names the skipped path. Precedence ranks: project settings 0, project auto-discovered 1, user settings 2, user auto-discovered 3, package 4. Within the project, `.pi/skills` is added before `.agents/skills`. | `package-manager.js` 49–66, 1992, 2001, 2077; `PCA/dist/core/skills.js` 327–339; `PCA/dist/modes/interactive/interactive-mode.js` 1250–1272 |
| P5 | A directory that contains `SKILL.md` is one skill, and discovery does not descend into it. Root-level `.md` files directly in `.pi/skills/` are discovered as skills. | `package-manager.js` 203–257; `docs/skills.md` "Discovery rules" |
| P6 | Skill frontmatter: `name` is required (1–64 characters, lowercase a–z, 0–9, and hyphens, no leading, trailing, or doubled hyphen) and need not match the directory. `description` is required, up to 1024 characters. `license` is optional. Unknown fields are ignored. Frontmatter is parsed with the `yaml` package. | `docs/skills.md` "Frontmatter", "Validation"; `PCA/dist/utils/frontmatter.js` line 1 |
| P7 | Pi core has no primary-agent selection. `.pi/SYSTEM.md` or `--system-prompt` replaces the whole session prompt. | `PCA/README.md` lines 336 and 610 |
| P8 | The extension is `@tintinweb/pi-subagents` **0.19.0**, released 2026-08-25. Its peer dependency is `@earendil-works/pi-coding-agent >=0.84.0`. It is installed through `settings.json` `packages: npm:@tintinweb/pi-subagents`. | `PSA/package.json`; `PSA/CHANGELOG.md`; `~/.pi/agent/settings.json` |
| P9 | Agent discovery order, lowest to highest precedence: `$PI_CODING_AGENT_DIR/agents/*.md` (default `~/.pi/agent/agents/`), then `<cwd>/.agents/agents/*.md`, then `<cwd>/.pi/agents/*.md`, with later loads overwriting earlier ones. Only direct `*.md` children are read. The loader has no trust check. | `PSA/src/custom-agents.ts` 44–71; `PSA/README.md` "Custom Agents" |
| P10 | The agent type is frontmatter `name`, falling back to the filename. A name containing `:` is skipped. Type resolution is case-insensitive, and an ambiguous or unknown type falls back to the fallback agent. | `custom-agents.ts` 82–103; `PSA/src/agent-types.ts` 123–219 |
| P11 | Accepted agent keys: `description`, `name`, `display_name`, `color`, `tools`, `extensions`, `exclude_extensions`, `skills`, `memory`, `disallowed_tools`, `isolation`, `model`, `thinking`, `max_turns`, `persist_session`, `output_transcript`, `session_dir`, `allowed_subagents`, `prompt_mode`, `inherit_context`, `run_in_background`, `isolated`, `enabled`. Other keys are never read. | `custom-agents.ts` 107–138; `PSA/README.md` "Frontmatter Fields" |
| P12 | `tools` accepts CSV or an array. Omitted means all seven built-ins (`read, bash, edit, write, grep, find, ls`), and `none` means zero. `ext:<extension>/<tool>` selectors turn extension tools into an explicit allowlist; with no selector, every loaded extension's tools surface. Unknown built-in names fail loudly. | `agent-types.ts` 12–22; `custom-agents.ts` 262–278; `PSA/README.md` "Tool & extension scoping" |
| P13 | `extensions: false` loads no extensions. It combines with `allowed_subagents`, because nested tools are injected directly. | `PSA/README.md` "Nested subagents" example and "Tool & extension scoping" |
| P14 | `allowed_subagents` is off by default and runtime-enforced. Out-of-list, unknown, and disabled types are rejected with no fallback. The depth cap `maxSubagentDepth` defaults to 2 (main 0, subagent 1, nested child 2). This repository's `.pi/subagents.json` sets 2. | `PSA/README.md` "Nested subagents", "Persistent Settings"; `.pi/subagents.json` |
| P15 | `model` takes `provider/modelId` or a fuzzy name. An unresolvable pin inherits the parent model and is flagged in `/agents`. | `PSA/README.md` "Frontmatter Fields" and the forgiving-resolution paragraph |
| P16 | `prompt_mode` defaults to `replace`: an environment header plus the body, with no `AGENTS.md` or `CLAUDE.md` inheritance. `append` prepends the parent's full system prompt. | `PSA/src/prompts.ts` 101–135 |
| P17 | A top-level `Agent` call with an unresolvable type falls back to `general-purpose` (all tools) unless `fallbackSubagent: none` is set. Settings merge `~/.pi/agent/subagents.json` with the project's `.pi/subagents.json`, and the project wins. | `agent-types.ts` 174–219; `PSA/README.md` "Persistent Settings" |
| P18 | MCP through `pi-mcp-adapter`: by default every MCP tool is reached through one `mcp` proxy tool. Per-server `directTools` is opt-in configuration. | `~/.pi/agent/npm/node_modules/pi-mcp-adapter/README.md` "Direct Tools" |
| P19 | Owner's machine state: <ul><li>Global skills `~/.pi/agent/skills/conductor` (metadata version 2.0.0) and `conductor-v3`.</li><li>Global agents `~/.pi/agent/agents/*.md` in OpenCode format (`mode`, `permission`) without `name` or `tools`: architect, architect-v3, azure-reader, bug-crusher-investigator, code-reviewer, conductor-v2, conductor-v3, dal-dev, docs-dev, dotnet-dev, github-devops, maui-dev, product-owner, pulumi-dev, python-dev, react-dev, research-agent, sql-database-architect, tauri-dev, test-dev.</li><li>Default model `zai/glm-5.3-flash`.</li><li>`~/.pi/agent/trust.json` trusts `/Users/dave/git`.</li></ul> | Files listed |

**Reopening evidence, verified 2026-09-14 (P20–P46).** Added for U7–U10; nothing above is
rewritten.

| # | Fact | Source |
|---|---|---|
| P20 | Pi core's stated philosophy is explicit no-orchestration: "No sub-agents. There's many ways to do this. Spawn pi instances via tmux, or build your own with extensions, or install a package that does it your way." Core ships nothing resembling a root/primary custom-agent selector. | `PCA/README.md` "Philosophy" section, lines ~495–511 |
| P21 | pi-subagents' `/agents` command is documented as "Interactive agent management menu" over **subagent types** only, reached through the `Agent` tool, `@handle` mentions, or the menu itself. FleetView's `● main` row is the actual running Pi process (driven by `.pi/SYSTEM.md` / `--system-prompt`, P7) — structurally distinct from every `○ <type>` subagent row beneath it; nothing promotes a subagent row to replace `main`. | `PSA/index.ts` lines 1–11 (command doc comment); `PSA/README.md` "FleetView" section |
| P22 | The owner's global `~/.pi/agent/agents/conductor-v2.md` and `conductor-v3.md` declare OpenCode-only frontmatter (`mode: primary`, `permission: {...}`, `reasoningEffort`). `mode` is outside pi-subagents' accepted key list (P11); per P11's "every other key is silently ignored" rule it is inert. Neither file declares `name`, so each types from its filename (P10): they load as ordinary spawnable subagent types `conductor-v2` and `conductor-v3` — not as any kind of root or main-session agent. | `~/.pi/agent/agents/conductor-v2.md`, `~/.pi/agent/agents/conductor-v3.md` (read in full); `PSA/src/custom-agents.ts` 82–138 |
| P23 | No package under `~/.pi/agent/npm/node_modules` or `~/.pi/agent/extensions` other than `@tintinweb/pi-subagents` defines any agent-selection mechanism. Installed packages (`settings.json`): `pi-mcp-adapter`, `@tintinweb/pi-subagents`, `@nguyenquangthai/pi-todo`, a local `agent-session-analysis-dashboard` collector, `@pi9/context`. Local extensions: `glm-usage` (a usage tracker) and `lm-studio.ts` (registers an LM Studio model provider) — neither touches agent selection. | `~/.pi/agent/settings.json`; `~/.pi/agent/extensions/` listing |
| P24 | `nested-tools.ts` enforces `maxSubagentDepth` (default 2: main=0, subagent=1, nested child=2) with an explicit refusal, not silent degradation: a nested `Agent`/`get_subagent_result`/`steer_subagent` call at or past the cap returns the tool-error text "Nested subagent call blocked (depth=…, max=…). Complete the task directly." to the calling agent. | `PSA/src/nested-tools.ts` lines 45–48, 195–199; `PSA/src/agent-runner.ts` line 847 |
| P25 | Conclusion drawn from P2, P7, P20–P24: no mechanism in pi-coding-agent 0.84.4 or pi-subagents 0.19.0 lets a custom agent definition become the root/main session's driver. The owner's "pi allows for root custom agents" premise does not hold as literally stated; the *underlying wish* (conductor visible in `/agents`) is nonetheless real and unaddressed by R3 as approved. | Derived; see P2, P7, P20–P24 |
| P26 | `-g\|--global` is already a working, symmetric option across `SquadInstallSettings`, `SquadUpdateSettings`, `SquadUninstallSettings`, `SquadStatusSettings`, and `SquadDoctorSettings`, wired through `SquadCommandComposition.ResolveScope`/`SquadInstallCommand.Execute` for every declared target including `pi`. No new flag is needed. | `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs` (all five settings classes); `SquadInstallCommand.cs` lines 40–109 |
| P27 | `SquadDeploymentScope.Global` today redirects **only** Squad's own `squad.lock.yml`/`squad.receipt.json` bookkeeping (to `ISquadUserPaths.ApplicationDataDirectory`, keyed by a hash of the physical target root). It does **not** redirect where rendered files are written: `SquadDeploymentPlan.CreateInstall`/`CreateUpdate` and `SquadTransaction` always resolve every file against `plan.PhysicalRootPath`, derived from `SquadInstallRequest.TargetRoot` (the CLI `path`/cwd) regardless of scope. `SquadRenderRequest.UserScopeDirectory` is threaded to every renderer's request but read by **no renderer today**, Pi's `PiRenderer` included (0 matches for `Scope`/`UserScopeDirectory` in `src/KyberWeave.Core/Squad/Rendering/*.cs` outside the two `SquadLifecycleService` call sites). `squad install --global --target <any target>` therefore writes files exactly where project scope would, today. | `SquadLifecycleService.cs` lines 132–143, 266–276; `SquadDeploymentPlan.cs` lines 58–121; `SquadTransaction.cs` lines 80–88, 540; `SquadStateStore.cs` lines 35–45, 226–250; grep of `Rendering/*.cs` |
| P28 | `docs/kyber-squad/onboarding.md` ("2. Global Scope (`--global`)") and every `--global` `[Description]` in `SquadSettings.cs` describe the flag as targeting "the user's home/global environment" — true, per P27, only of the state files, not of the rendered agent/skill files, for every target implemented so far. | `docs/kyber-squad/onboarding.md`; `SquadSettings.cs` lines 31–34, 55–58, 95–98, 114–117, 128–131 |
| P29 | Pi's real global roots are independent of `ApplicationDataDirectory`: agents default to `$PI_CODING_AGENT_DIR/agents/*.md` (default `~/.pi/agent/agents/`, P9); skills load from `~/.pi/agent/skills/` and `~/.agents/skills/` (P3), of which only the former is Pi's own namespace (U3's project-scope symmetry). `SquadUserPaths.ApplicationDataDirectory` uses `Environment.SpecialFolder.ApplicationData` (e.g. macOS `~/Library/Application Support`), which is neither path. | P3, P9 (already verified); `SquadUserPaths.cs` lines 15–17 |
| P30 | The owner's `~/.pi/agent/agents/` holds 20 hand-copied OpenCode-format files, most sharing a **name** with a canonical Squad agent identity but never its bytes (OpenCode's `mode`/`permission` shape vs. pi-subagents' accepted keys, P19, P11). `~/.pi/agent/skills/conductor` likewise shares a name with the canonical `conductor` identity. `SquadDeploymentPlan.CreateInstall`'s existing, target-agnostic `UnmanagedCollision` rule (throws unless `--adopt` finds identical bytes) would reject the first such collision it encounters; `-v2`/`-v3` variants and `dotnet-dev` have no canonical counterpart and are correctly left untouched, mirroring R14's project-scope precedent. | Dispatch-supplied inventory cross-checked against §5.3; `SquadDeploymentPlan.cs` lines 77–97 |
| P31 | `~/.pi/agent/settings.json` confirms `defaultProvider: zai`, `defaultModel: glm-5.3-flash` as the currently active default model — the one Pi model pin on this machine confirmed resolvable today without inspecting `auth.json`. | `~/.pi/agent/settings.json` |
| P32 | `~/.pi/agent/models-store.json` (model/provider names only; `auth.json` and all keys/tokens were not read) shows five providers with a cached model catalog: `zai`, `zai-coding-cn`, `opencode-go`, `opencode`, `wafer-custom`. `opencode` serves Claude-branded ids (`claude-opus-4-5`, `claude-sonnet-4-5`, `claude-haiku-4-5`, …) — what a bare `opus`/`sonnet`/`haiku` fuzzy query would match if that provider is available. A cached catalog is evidence the provider was queried at some point, **not** proof of currently valid auth. | `~/.pi/agent/models-store.json` (provider keys only) |
| P33 | `~/.pi/agent/extensions/lm-studio.ts` registers a local, zero-cost `lm-studio` provider (`microsoft/phi-4-reasoning-plus`, two Qwen3.6 variants) via `pi.registerProvider`. Unrelated to any populated `models.yml` column; not a `pi:` value candidate. | `~/.pi/agent/extensions/lm-studio.ts` |
| P34 | pi-subagents' `model-resolver.ts` `resolveModel`: (1) exact `provider/modelId`, only if currently available; (2) else fuzzy id/name-substring match restricted to available/configured models — the README documents this by name ("Fuzzy model selection — specify models by name (`"haiku"`, `"sonnet"`)… automatic filtering to only available/configured models"); (3) else a bare-modelId retry against every provider; (4) else unresolved, which P15 already establishes inherits the parent model and is flagged in `/agents` rather than erroring. A fuzzy, provider-agnostic value therefore degrades gracefully across arbitrary Pi users' provider configurations in a way a literal `provider/modelId` pin cannot. | `PSA/src/model-resolver.ts` (full file); `PSA/README.md` "Fuzzy model selection" bullet |
| P35 | `models.yml`'s existing `claude:` column already uses bare fuzzy names (`opus`, `haiku`, `sonnet`, no `anthropic/` prefix) for the same three profiles; `opencode:` already uses literal `provider/modelId` pins (`zai-coding-plan/glm-5.2`, `opencode-go/gpt-5.6-luna`, `opencode/big-pickle`) for a *different* harness (the not-yet-implemented `opencode` Squad target, whose provider/model catalog is that harness's own fixed marketplace, unlike Pi's fully user-defined provider set). the `mai-code-flash` key in models.yml carries only `copilot: MAI-Code-1.1-Flash (copilot)`, a Copilot-exclusive model with no cross-harness analogue; `orchestration`'s only populated column is `opencode: opencode/big-pickle`, the zero-cost coordinator-tier model on that different harness. | `products/kyber-squad/profiles/models.yml` (full file, read 2026-09-14) |
| P36 | R12's original "no portable value exists" conclusion evaluated only the literal `provider/modelId` pin form (which is genuinely non-portable, P29's provider-set argument) and did not evaluate the fuzzy form P34 documents as a first-class pi-subagents feature. The fuzzy form is not "no portable value" but "a value that resolves differently, safely, per user" — the same trade-off `claude:`'s bare names already accept. | Derived; see P34, P35, and R12 (section 3) |
| P37 | `glm-5.3` and `kimi-k2.7-code` both appear as exact `id` values in `models-store.json`. `glm-5.3` exists under `provider: "zai"` (matching the confirmed-active default, P31) and separately under `opencode-go` and `opencode`. `kimi-k2.7-code` exists under both `opencode-go` and `opencode`, not under `zai`. | `~/.pi/agent/models-store.json` lines ~196 (opencode-go), ~426 (opencode-go), ~1050 (zai), ~1840 (opencode), ~2715 (opencode) |
| P38 | "Muse Spark 1.3" resolves to two distinct tokens under two different providers: `opencode/muse-spark-1.3-contributor-free` (name "Muse Spark 1.3 Free") and `opencode-go/muse-spark-1.3-contributor` (name "Muse Spark 1.3 Contributor", no `-free` suffix). The owner's own `~/.pi/agent/agents/conductor-v2.md` already pins `opencode/muse-spark-1.2-contributor-free` — same provider, same `-contributor-free` naming, one minor version down — the strongest available evidence that the `opencode` provider specifically is configured with working auth on this machine (short of reading `auth.json`, which this plan does not do). | `~/.pi/agent/models-store.json` lines ~634 (opencode-go/muse-spark-1.3-contributor), ~2993 (opencode/muse-spark-1.3-contributor-free); `~/.pi/agent/agents/conductor-v2.md` line 4 |
| P39 | The coordinator-supplied `model-profile` audit of `products/kyber-squad/agents/*.md` groups agents as: `deep-planning` = architect, bug-crusher-investigator, sql-database-architect; `orchestration` = conductor; task-reviewer and test-dev point at the `mai-code-flash` key in models.yml (a model name misused as a profile id, removed by this plan); `fast` = azure-reader, csharp-dev, docs-dev, maui-dev, python-dev, react-dev, research-agent, review-triage; `general` = code-reviewer, dal-dev, github-devops, product-owner, pulumi-dev, review-lens, tauri-dev. Review-named agents (code-reviewer, review-lens, review-triage, task-reviewer) and dev-named agents span three and two profiles respectively, with `fast` and `general` each mixing both categories — no per-profile `pi:` value can express the owner's "dev" vs. "review" grouping without misapplying to at least one profile member. Not independently re-derived from the corpus by the architect in this pass; taken as supplied and used as the basis for U10 Part 2. | Coordinator relay, 2026-09-14; cross-referenced against §5.3's corpus facts, which are consistent with it |

**Global-root evidence for U8 (P40–P46), verified 2026-09-14.** Vendor docs for the five non-Pi
targets plus this machine's own installed config, since none of the five global roots had been
investigated before this reopening (former G7/section 11 boundary).

| # | Fact | Source |
|---|---|---|
| P40 | **Claude Code**: global custom subagents live at `~/.claude/agents/`; global skills at `~/.claude/skills/` ("Personal skills available in every project… same structure as project skills"). `CLAUDE_CONFIG_DIR` overrides the *entire* `~/.claude` root — "every ~/.claude path lives under that directory instead" — so both agents and skills move together, there is no independent per-component override. Matches `ClaudeRenderer`'s existing project paths (`.claude/agents/*.md`, `.claude/skills/*/SKILL.md`) with the `.claude/` wrapper dropped at the global root. | `https://code.claude.com/docs/en/claude-directory`, fetched 2026-09-14 (verbatim tree entries `global-agents`/`agents/` and `global-skills`/`skills/`) |
| P41 | **Codex**: global custom agent TOML definitions live at `~/.codex/agents/` (project: `.codex/agents/`, already the target `CodexRenderer` renders); global skills at `~/.codex/skills/` (SKILL.md, cross-agent standard). `CODEX_HOME` overrides the default `~/.codex` root ("useful for CI/CD or multi-account setups"). Independently confirmed on this machine: the owner's own `~/.codex/config.toml` sets `CODEX_HOME = "/Users/dave/.codex"` inside an MCP server's env block — the exact default, proving the variable is real and live, not merely documented. | Codex custom-agent TOML: `https://codex.danielvaughan.com/2026/04/27/codex-cli-custom-agent-definitions-toml-specialised-subagents/`; Codex skills: multiple corroborating sources (agensi.io, blog.fsck.com) converging on `~/.codex/skills/`; `CODEX_HOME`: `developers.openai.com/codex/guides/agents-md` (project-level `AGENTS.md` walk, distinct concern) plus local `/Users/dave/.codex/config.toml` line 160, all fetched/read 2026-09-14 |
| P42 | **Cursor**: global custom subagents live at `~/.cursor/agents/` — documented verbatim as "User subagents \| `~/.cursor/agents/` \| All projects for current user" (project: `.cursor/agents/`, already `CursorRenderer`'s target). Global skills at `~/.cursor/skills/`, and Cursor additionally reads `~/.claude/skills/`, `~/.codex/skills/`, and the shared `~/.agents/skills/` for compatibility — none of which this renderer emits to or needs to read. `CURSOR_CONFIG_DIR` overrides the CLI configuration directory; `XDG_CONFIG_HOME` is a secondary Linux/BSD fallback. Cursor's separate "User Rules" feature (Settings UI, not a file) is a different concept from custom subagents and does not apply here. | `https://cursor.com/docs/context/subagents` (subagents table), fetched 2026-09-14; skills: `cursor.com/help/customization/skills` per WebSearch corroboration; `CURSOR_CONFIG_DIR`: `cursor.com/docs/cli/reference/configuration` per WebSearch corroboration, 2026-09-14 |
| P43 | **GitHub Copilot CLI**: global custom agents at `~/.copilot/agents/` — "the global directory for personal custom agents… agents placed in this directory are available in all your sessions," with project agents (`.github/agents/`, already `CopilotRenderer`'s target) taking precedence on a name collision *only* when both exist in the same session (a personal agent otherwise wins if the project has none by that name — the doc's own wording is the source of truth, not this summary). Global skills at `~/.copilot/skills/` (also reads the shared `~/.agents/skills/`). `COPILOT_HOME` overrides the default `~/.copilot` ("$HOME/.copilot"). | `docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli` and `.../add-skills`, per WebSearch corroboration; `COPILOT_HOME`: `docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference`, fetched 2026-09-14 |
| P44 | **Antigravity**: global scope for skills is `~/.gemini/config/skills/`, documented as "available across all Antigravity products… and projects." Project scope is `.agents/skills/` (with legacy `.agent/skills/` support) — already `AntigravityRenderer`'s exact target. Antigravity is a fallback target with no native agent-file identity (role-skill lowering only, R2/existing architecture), so it has no "global agents" root to map — only skills. No override environment variable was found in this investigation; none is assumed. | `antigravity.google/docs/skills/`; `codelabs.developers.google.com/getting-started-with-antigravity-skills`; `medium.com/google-cloud/where-does-antigravity-look-for-agent-skills-*`, all per WebSearch corroboration, fetched 2026-09-14 |
| P45 | This machine has `~/.claude/`, `~/.codex/`, `~/.cursor/`, and `~/.agents/` all genuinely present and populated (plugin caches, session history, `~/.claude/settings.json.bak`, `~/.codex/config.toml`), confirming these are real, actively-used installations, not merely documented products — though none currently has user-created files directly under a top-level `agents/` or `skills/` folder (the owner has not created global custom agents in these three harnesses the way they have for Pi), which is expected and does not contradict P40–P42's documented conventions. | `Glob` of `/Users/dave/.claude`, `/Users/dave/.codex`, `/Users/dave/.cursor`, `/Users/dave/.agents`, 2026-09-14 |
| P46 | `SquadSourceLoader.ParseModelProfiles` (`SquadSourceLoader.cs` lines 44–48, 206–237) treats `ModelProfileFields` as the fixed set of *harness column names* only (`default`, `codex`, `cursor`, `claude`, `copilot`, `opencode`, `kilo`, `antigravity`, `warp`, `factory`, `pi`); profile **ids** (`deep-planning`, `fast`, …) are ordinary `SortedDictionary` keys parsed generically from `profiles:` in `models.yml`, with no closed enum. Adding a new profile id (`reviewer`) is therefore a canonical-source data change with no `SquadSourceLoader`, schema, or renderer code change required. | `src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs` lines 44–48, 206–237, read via `codegraph_explore` 2026-09-14 |

**Vendor model-string evidence (P47–P52), fetched 2026-09-14 against official pages only, for the
"models work" revision.** Every string below was checked live; none is taken from a third-party
mirror, changelog, or aggregator site.

| # | Fact | Source |
|---|---|---|
| P47 | GitHub Copilot's own supported-models page lists, with GA status: OpenAI (`GPT-5 mini`, `GPT-5.3-Codex`, `GPT-5.4`, `GPT-5.4 mini`, `GPT-5.4 nano`, `GPT-5.5`, `GPT-5.6 Luna`, `GPT-5.6 Sol`, `GPT-5.6 Terra`, `GPT-6 Astra`); Anthropic (`Claude Fable 5`, `Claude Fable 5.1`, `Claude Haiku 4.5`, `Claude Opus 4.7`, `Claude Opus 4.8`, `Claude Opus 4.8 (fast mode) (preview)`, `Claude Opus 5`, `Claude Sonnet 4.6`, `Claude Sonnet 5`); Google (`Gemini 3.5/3.6/3.7/3.8 Flash`); Microsoft (`MAI-Code-1.1-Flash`); xAI (`Grok 4.5`, `Grok 4.6`); Moonshot AI (`Kimi K2.7 Code`, `Kimi K3`). A second, targeted re-check of the same page for every "Kimi" occurrence confirms only `Kimi K2.7 Code` and `Kimi K3` appear, in the primary supported-models table and (K3 only) the extended-capabilities table — no `Kimi K2.6`, with or without a "Code" suffix, is listed anywhere on the page. `MAI-Code-1.1-Flash` matches the token already used in `models.yml` exactly. `Grok 4.6` is present and is newer than the `Grok 4.5` currently in `general`'s copilot/cursor values. | `https://docs.github.com/en/copilot/reference/ai-models/supported-models`, fetched and re-checked 2026-09-14 |
| P48 | Cursor's own models documentation lists, in Cursor's first-party pool: `Grok 4.6`, `Grok 4.6 (Fast)`, `Grok 4.5`, `Grok 4.5 (Fast)`, `Composer 2.5`, `Composer 2.5 (Fast)`; in the third-party pool: Moonshot's `Kimi K2.7 Code` and `Kimi K3` (via Cursor's inference partner Fireworks; Cursor's own docs recommend K3 for new work but keep K2.7 Code available as the lower-cost Moonshot option), plus Z.ai's `GLM 5.2` and Meta's `Muse Spark 1.3`, among Anthropic/Google/OpenAI entries. Cursor's docs give display names only; no page fetched (`cursor.com/docs/models-and-pricing`, `cursor.com/docs/models/grok-4-6`, `cursor.com/docs/models/kimi-k2-7-code`) states the bracket-suffix CLI/config id string (e.g. whether Kimi K2.7 Code is addressed as `kimi-k2.7-code[]`) — that syntax is inferred from this file's own existing, already-established Cursor tokens (`composer-2.5[]`, `grok-4.5[]`), not read verbatim off a vendor page. | `https://cursor.com/docs/models-and-pricing`, `https://cursor.com/docs/models/grok-4-6`, `https://cursor.com/docs/models/kimi-k2-7-code`, fetched 2026-09-14 |
| P49 | OpenCode's own Zen documentation lists, in the `opencode/<model-id>` namespace: `muse-spark-1.3`, `muse-spark-1.2`, `muse-spark-1.3-contributor-free`, and GLM models `glm-5.3-flash`, `glm-5.3`, `glm-5.2`, `glm-5.1`, `glm-5`. Confirms `opencode/muse-spark-1.3-contributor-free` (the owner's dev-family Pi and OpenCode token) is a real, currently listed id. | `https://opencode.ai/docs/zen/`, fetched 2026-09-14 |
| P50 | OpenCode's own Go documentation lists, in the `opencode-go/<model-id>` namespace: Kimi models `kimi-k3`, `kimi-k2.7-code`, `kimi-k2.6` (no "Code" suffix — corroborates P47's finding that a Code-suffixed K2.6 does not exist); Muse Spark `muse-spark-1.3-contributor`, `muse-spark-1.2-contributor`; plus `grok-4.6`, `gpt-5.6-luna`, `glm-5.3-flash`, `glm-5.3`, `glm-5.2`, and others. Confirms `opencode-go/kimi-k2.7-code` (the owner's `reviewer` value on OpenCode and Pi) is a real, currently listed id. | `https://opencode.ai/docs/go/`, fetched 2026-09-14 |
| P51 | xAI's own release notes name Grok 4.6 (released 2026-08-12) as the current flagship, superseding Grok 4.5 — corroborated independently by Cursor's own `Grok 4.6` doc page (P48) and GitHub Copilot's own supported-models page (P47), both of which list Grok 4.6 as GA. This confirms Grok 4.6, not 4.5, is "the latest Grok" the owner referred to. | `https://docs.x.ai/developers/release-notes`, fetched 2026-09-14; corroborated by P47, P48 |
| P52 | Z.ai's own developer documentation states "All plans support GLM-5.3, GLM-5.3-Flash" under its GLM Coding Plan, and that the plan supports OpenCode (among Claude Code and Cline) — confirming GLM 5.3 is the current, live model for OpenCode's `zai-coding-plan` provider. The page does not spell out the literal joined id string `zai-coding-plan/glm-5.3`; that string is derived by combining this page's confirmed model name with the `zai-coding-plan/` provider prefix already established and verified as this file's own pre-existing convention (`zai-coding-plan/glm-5.2`, present in `models.yml` before this revision) — a derivation, not a single vendor page showing the complete joined string, and flagged as such in section 6b. | `https://docs.z.ai/devpack/overview`, fetched 2026-09-14 |

### 5.2 Code seam (confirmed 2026-09-14)

Every location the todo named still exists. `CodexRenderer` has since been registered.

- **`src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs`:** the enum, `ApprovedTargets`,
  `TargetsByToken`, and `GetToken`. It declares nine targets, with `all` expanding to
  `ApprovedTargets`.
- **`src/KyberWeave.Core/Squad/Deployment/SquadTargetResolver.cs`:** `Markers`. Antigravity has no
  entry, and Pi gets none (U4).
- **`src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`:** `ModelProfileFields` (line 44),
  enforced through `EnsureOnlyFields` (line 222). `products/kyber-squad/schemas/model-profiles.schema.json`
  uses `additionalProperties: false`.
- **`src/KyberWeave.Core/Squad/Packaging/SquadPacker.cs`:** `CollectApmEntries` target-tree
  exclusions (lines 104–114).
- **`src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`:** `--target` descriptions (lines 15, 17,
  and 62).
- **`src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs`:**
  - `isNative` (lines 164–165).
  - `AgentOutputPath` and `SkillOutputPath` (lines 355–381), used by
    `ValidateResourcePrincipalCollisions`. `AgentOutputPath` receives only a name today, so it needs
    the agent's invocation and fallback profile to place a lowered primary agent.
  - Native `role-` and shared-identity checks (lines 171–202).
- **`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`:** `ResolveRenderer()` registers
  Copilot, Cursor, Claude, Antigravity, and Codex. `SquadDoctorCommand` derives the available and
  pending lists from it.
- **Reference renderers:**
  - `ClaudeRenderer` supplies the always-emitted allowlist, `safety-narrowed`, model resolution,
    and `SquadMarkdownDocument.Compose`.
  - `AntigravityRenderer` supplies skill frontmatter (`name`, `description`, `license: MIT`), the
    single-line description, and lowered-agent resource projection under the skill directory.
- **Tests:**
  - `SquadTargetResolutionTests` (`NineTargets`, `CatalogContainsExactlyNineTargetsInStableOrder`,
    and the Antigravity explicit-only test at line 178).
  - `Fakes/FakeSquadRenderer.cs` (fallback roster at line 105, plus path switches that throw for
    unknown native targets).
  - `SquadPackAndReleaseTests` (line 162, target-tree exclusion).
  - `SquadCliCommandTests.Doctor_ReportsRendererCoverageAndMcpProbeStatus` (line 372).
  - `ClaudeRendererContractTests` (the derived-count pattern at lines 137–146).
  - `FakeSquadReleaseSource`, which writes only `squad.yml` and `toolchain.yml`, so a
    corpus-copying fake is needed for lifecycle dry-runs.
- **`src/KyberWeave.Core/Squad/Deployment/SquadLifecycleService.cs`:** the `InstallAsync` dry-run
  returns `Plan` and `Receipt` without mutating the tree. A real CLI install downloads a release,
  which `KYBER_WEAVE_RELEASE_ORIGIN` can redirect to the local loop ([distribution](../distribution.md)).
- **Always-human review paths:** this plan changes no file under `capabilities.yml`,
  `products/kyber-squad/agents/**`, or `.kyber-weave/kyber-weave.yml`.

### 5.3 Corpus facts

| Fact | Detail |
|---|---|
| Size | 21 agents and 24 skills. `shared-identities: []`. |
| Agent resources | 10: architect 4, conductor 4, task-reviewer 2. |
| Skill resources | 64 on disk. The loader's validated closures decide how many render; requirements.md records 113 files for a fresh Copilot render. |
| Profiles with `ask` | `architect` (`filesystem.write`, `process.execute`), `product-planning` (`filesystem.write`), `reviewer` (`filesystem.write`), `publishing-worker` (`network.publish`) |
| Roles with `network.read: allow` | architect, investigator, product-planning, publishing-worker, read-only, and reviewer profiles |
| Non-empty `delegates-to` on subagents | `architect` (azure-reader, research-agent), `code-reviewer` (azure-reader, review-lens, review-triage), `product-owner` (research-agent) |
| Descriptions | Every skill description is one line of fewer than 900 characters, within Pi's 1024 limit. Every name satisfies Pi's name rule. |

### 5.4 Deviations from the todo

| Todo claim | Now |
|---|---|
| Fallback classification | Native (U5), with primary-agent lowering (R3) |
| 45 files with seven `role-` files | 45 principals, zero `role-` files, derived count (R17) |
| `permission-not-expressible` on every non-deny decision | Real `tools` and `allowed_subagents` enforcement, with `safety-narrowed` for `ask` (R7–R10) |
| Output directory undecided | `.pi/skills/` (U3) |
| Detection undecided | Explicit-only (U4) |

## 6. Rendering contract (`PiRenderer`)

**Output paths** (target token `pi`):

- **Subagent-invocation agent:** `.pi/agents/<name>.md`. Resources project beside it through
  `SquadResourceProjection` under `.pi/agents/<agent-relative path>`, for example
  `.pi/agents/architect/references/plan-authoring.md`. They are never direct `*.md` children of
  `.pi/agents/` (P9).
- **Primary-invocation agent with `no-primary-agent: skill`:** `.pi/skills/<name>/SKILL.md`. Resources
  go under `.pi/skills/<name>/<agent-relative path>`, as in Antigravity.
- **Canonical skill:** `.pi/skills/<name>/SKILL.md`, unless the skill is a profile-declared shared
  identity; those are suppressed as in Claude. Resources go under `.pi/skills/<name>/`. No emitted
  file is a direct `*.md` child of `.pi/skills/` (P5).

**Agent frontmatter.** Keys appear in this order, and output is byte-stable:

| Key | Rule |
|---|---|
| `name` | Canonical name |
| `description` | Canonical description collapsed to one line |
| `model` | Only when the resolved Pi model (R12) is not `inherit` |
| `tools` | Always present. Granted built-ins as CSV in the fixed order `read, grep, find, ls, edit, write, bash`, or `none` when empty |
| `extensions` | `false` on every agent (Q1-A, owner decision 2026-09-14) |
| `allowed_subagents` | The canonical `delegates-to` roster as CSV, only when `delegate: allow` and the roster is non-empty |

The body is the canonical instruction body. No other key from P11 is emitted.

**Capability lowering.** Only `allow` grants.

| Capability | Pi built-in tools |
|---|---|
| `filesystem.read` | `read` |
| `filesystem.search` | `grep`, `find`, `ls` |
| `filesystem.write` | `edit`, `write` |
| `process.execute` | `bash` |
| `network.read` | none |
| `network.publish` | none |
| `delegate` | `allowed_subagents` roster (R10) |

**Skill frontmatter** (canonical skills and lowered primary agents): `name`, a single-line
`description`, and `license: MIT`.

**Degradation records.** Every record carries `Target: "pi"`, the canonical identity, the output
identity, and `InstructionDigest` equal to the agent's `BodyDigest`. No `Details` text contains
"widen".

| Code | When | Details must state |
|---|---|---|
| `safety-narrowed` | Per agent with any `ask` capability | The sorted `ask` capabilities; that Pi has no permission prompt; that `tools` is binary, so the tools are withheld |
| `permission-not-expressible` | Per agent with `network.read` or `network.publish` set to `allow`, or with `delegate: allow` and an empty roster | Each such capability |
| `role-skill-fallback` | Lowered primary agent | That Pi has no primary-agent primitive; the fallback profile value |
| `permission-not-expressible` | Lowered primary agent | Every vocabulary capability with its decision; that a top-level skill runs under the harness default tool set; that the `delegates-to` roster is instruction-only |
| `omitted` | Primary agent with `no-primary-agent: omit` | Not applicable in the corpus |

**`<remarks>` requirement.** The class remarks record the facts verified on 2026-09-14 against
pi-coding-agent 0.84.4 and @tintinweb/pi-subagents 0.19.0:

- agent and skill discovery roots and their precedence;
- the accepted keys used;
- the seven-tool vocabulary;
- that `allowed_subagents` is enforced, and the depth cap;
- the `extensions` behaviour;
- that `.pi/skills` loads only for trusted projects;
- the third-party extension prerequisite.

This mirrors the `AntigravityRenderer` and `ClaudeRenderer` remarks.

## 6a. Global-scope rendering contract (all six targets — added 2026-09-14, resolves U8-B)

**Per-target global root, verified 2026-09-14 (section 5.1 P9, P3, P40–P44):**

| Target | Global agents root | Global skills root | Override env var | Verified default |
|---|---|---|---|---|
| `claude` | `~/.claude/agents/` | `~/.claude/skills/` | `CLAUDE_CONFIG_DIR` | `~/.claude` |
| `codex` | `~/.codex/agents/` | `~/.codex/skills/` | `CODEX_HOME` | `~/.codex` (confirmed live in the owner's own `config.toml`) |
| `cursor` | `~/.cursor/agents/` | `~/.cursor/skills/` | `CURSOR_CONFIG_DIR` | `~/.cursor` |
| `copilot` | `~/.copilot/agents/` | `~/.copilot/skills/` | `COPILOT_HOME` | `~/.copilot` |
| `antigravity` | *(none — fallback target, skills only)* | `~/.gemini/config/` (root), relative path `skills/<name>/SKILL.md` | none found | `~/.gemini/config` |
| `pi` | `$PI_CODING_AGENT_DIR/agents/` | `$PI_CODING_AGENT_DIR/skills/` | `PI_CODING_AGENT_DIR` | `~/.pi/agent` |

**Relative-path shape under Global scope**, per target, replacing the Project-scope `.{harness}/`
prefix with the bare `agents/` / `skills/` root-relative form (unchanged from today under Project
scope):

| Target | Project-scope path (unchanged) | Global-scope path |
|---|---|---|
| `claude` | `.claude/agents/<name>.md`, `.claude/skills/<name>/SKILL.md` | `agents/<name>.md`, `skills/<name>/SKILL.md` |
| `codex` | `.codex/agents/<name>.toml`, `.codex/skills/<name>/SKILL.md` | `agents/<name>.toml`, `skills/<name>/SKILL.md` |
| `cursor` | `.cursor/agents/<name>.md`, `.cursor/skills/<name>/SKILL.md` | `agents/<name>.md`, `skills/<name>/SKILL.md` |
| `copilot` | `.github/agents/<name>.agent.md`, `.github/skills/<name>/SKILL.md` | `agents/<name>.agent.md`, `skills/<name>/SKILL.md` |
| `antigravity` | `.agents/skills/<role->name>/SKILL.md` | `skills/<role->name>/SKILL.md` |
| `pi` | `.pi/agents/<name>.md`, `.pi/skills/<name>/SKILL.md` | `agents/<name>.md`, `skills/<name>/SKILL.md` |

**Physical-root resolution** (R18): `SquadDeploymentPlan`/`SquadTransaction` resolve each
`SquadDeploymentFile`'s physical root by its `Target` field — `plan.PhysicalRootPath` (the project
`TargetRoot`) under `Scope: Project`, or the matching row above (env var, else default) under
`Scope: Global`. Every renderer's `RenderAsync` reads `request.Scope` and switches its relative-path
prefix accordingly; nothing else about a renderer's logic (frontmatter, capability lowering,
degradation records) changes. State bookkeeping is untouched (already scope-correct, P27).

**Unmanaged-collision behavior under Global scope** is the existing, target-agnostic
`SquadDeploymentPlan.CreateInstall`/`CreateUpdate` rule, unchanged: a pre-existing file at the
resolved root whose digest does not match throws `SquadDeploymentConflictException` unless
`--adopt` finds identical bytes. Squad never deletes an unmanaged file (U9).

**`squad doctor --global` collision warning** (resolves U9-A): before any mutation, `doctor` lists
every file at each requested target's resolved global root whose name matches a canonical Squad
identity but whose digest is unmanaged — reusing the same digest comparison `CreateInstall` already
performs, surfaced as a warning line per file rather than as an install-time exception, so an
operator sees the whole set before running `install`.

## 6b. Model resolution — the `reviewer` profile, final tokens (revised 2026-09-14, resolves U10)

**New profile.** `models.yml` gains a `reviewer` profile. Its members — `code-reviewer`, `review-lens`, `review-triage`, `task-reviewer` — move their `model-profile` frontmatter field in `products/kyber-squad/agents/{code-reviewer,review-lens,review-triage,task-reviewer}/*.md` from their current values (`general`, `general`, `fast`, and — for task-reviewer — the `mai-code-flash` key in models.yml) to `reviewer`. This is the owner's explicit, recorded authorization to touch `products/kyber-squad/agents/**` (section 5.2's always-human-review path) for exactly these four files' `model-profile` field, and nothing else in them. No `SquadSourceLoader`, schema, or renderer code changes (P46).

**Removed: `mai-code-flash` as a profile key.** `mai-code-flash` was never a profile name — it names one Copilot model, `MAI-Code-1.1-Flash (copilot)` (confirmed on GitHub's own page, P47). The `mai-code-flash` key is removed from `products/kyber-squad/profiles/models.yml` entirely; nowhere in this plan is it described as a profile again. `test-dev`, its sole former member, moves to `fast` (U10a). That model's only prior value, `copilot: MAI-Code-1.1-Flash (copilot)`, becomes `fast`'s Copilot value, replacing `GPT-5.6 Luna (copilot)`.

**Profile × harness table — final, before → after.** Every new or changed token cites the P-id that
verified it. "(unchanged)" cells are shown for completeness, not because they were re-verified beyond
what section 5.1 already established.

| Profile | `claude` | `codex` | `copilot` | `cursor` | `opencode` | `pi` |
|---|---|---|---|---|---|---|
| **deep-planning** | `opus` (unchanged) | `gpt-5.6-sol` (unchanged) | `GPT-5.6 Sol (copilot)` (unchanged) | `gpt-5.6-sol[context=272k,reasoning=high,fast=false]` (unchanged) | `zai-coding-plan/glm-5.2` → **`zai-coding-plan/glm-5.3`** (P52, derived — see note below) | *(none)* → **`zai/glm-5.3`** (P31, P37; owner U10) |
| **fast** | `haiku` (unchanged) | `gpt-5.6-luna` (unchanged) | `GPT-5.6 Luna (copilot)` → **`MAI-Code-1.1-Flash (copilot)`** (P47; owner decision #3) | `composer-2.5[]` (unchanged) | `opencode-go/gpt-5.6-luna` → **`opencode/muse-spark-1.3-contributor-free`** (P49; owner decision #9) | *(none)* → **`opencode/muse-spark-1.3-contributor-free`** (P38; owner U10) |
| **general** | `sonnet` → **`haiku`** (owner decision #5 / U11) | `gpt-5.6-terra` (unchanged) | `Grok 4.5 (copilot)` → **`Grok 4.6 (copilot)`** (P47, P51; owner decision #11) | `grok-4.5[]` → **`grok-4.6[]`** (P48, P51; owner decision #12) | `zai-coding-plan/glm-5.2` → **`opencode/muse-spark-1.3-contributor-free`** (owner decision #9 — "general dev agents follow the same dev family," superseding the GLM latest-version bump for this cell specifically) | *(none)* → **`opencode/muse-spark-1.3-contributor-free`** (same dev-family value as `fast`) |
| **reviewer** (NEW) | *(none)* → **`sonnet`** (owner decision #5 / U11 — the one named Sonnet exception) | *(none)* → **`gpt-5.6-terra`** (= `general.codex`; owner decision #13) | *(none)* → **`Kimi K2.7 Code (copilot)`** (U12, resolved A, owner answer 2026-09-14; P47) | *(none)* → **`kimi-k2.7-code[]`** (architect's choice per owner decision #12's criteria; P48, accepted by owner) | *(none)* → **`opencode-go/kimi-k2.7-code`** (P50; owner decision #9, "Go plan") | *(none)* → **`opencode-go/kimi-k2.7-code`** (owner U10, "the go plan") |
| **orchestration** | *(none)* → **`haiku`** (new field; owner decision #5 / U11 — conductor) | *(none, unset — not specified by owner)* | *(none, unset)* | *(none, unset)* | `opencode/big-pickle` (unchanged) | *(none)* → **`inherit`** (owner U10) |

**mai-code-flash (REMOVED as a profile key).** Its sole value, `copilot: MAI-Code-1.1-Flash (copilot)`, is not deleted from the file — it moves to `fast.copilot` (row above). The key `mai-code-flash:` itself no longer appears in `models.yml`.

**On the `zai-coding-plan/glm-5.3` derivation (deep-planning.opencode):** Z.ai's own devpack
documentation (P52) confirms GLM 5.3 is the current, live model and that the GLM Coding Plan supports
OpenCode, but does not spell out the complete joined id string. `zai-coding-plan/` is this file's own
pre-existing, already-verified provider prefix (present before this revision as `zai-coding-plan/glm-5.2`).
The proposed value combines a verified prefix with a verified model name rather than reading one vendor
page's literal string — flagged for owner awareness, not blocking, since no owner instruction singled
out this cell for confirmation the way U12 was.

**Agents not directly named by the owner, resolved by profile membership:** `product-owner`,
`dal-dev`, `github-devops`, `pulumi-dev`, `tauri-dev` are on `general` (all coding agents per owner
decision #5) and take `general`'s row above — `product-owner` rides this as a disclosed side effect,
the same disclosed-overlap pattern used throughout this section. `azure-reader`, `research-agent`,
`docs-dev`, `csharp-dev`, `react-dev`, `python-dev`, `maui-dev`, and `test-dev` are on `fast` and take
`fast`'s row. `sql-database-architect` and `bug-crusher-investigator` stay on `deep-planning`.
`conductor` stays on `orchestration`. `code-reviewer`, `review-lens`, `review-triage`, and
`task-reviewer` move to the new `reviewer` profile and take its row.

## 6c. Per-agent resulting models (added 2026-09-14) — all 21 agents × 6 harnesses

Every cell is the agent's *resolved* value via its profile membership (section 6b). **Bold** marks a
cell whose value changed from what `models.yml` declares today, on one of the five **non-Pi**
harnesses — the set the dispatch asked to be flagged explicitly. Every Pi cell is new (no profile has
ever had a `pi:` column populated before this plan), so Pi cells are not separately flagged as
"changed"; they are shown for completeness against section 6b.

| Agent | Profile (before → after) | `claude` | `codex` | `copilot` | `cursor` | `opencode` | `pi` |
|---|---|---|---|---|---|---|---|
| architect | deep-planning | opus | gpt-5.6-sol | GPT-5.6 Sol (copilot) | gpt-5.6-sol[…] | **zai-coding-plan/glm-5.3** | zai/glm-5.3 |
| bug-crusher-investigator | deep-planning | opus | gpt-5.6-sol | GPT-5.6 Sol (copilot) | gpt-5.6-sol[…] | **zai-coding-plan/glm-5.3** | zai/glm-5.3 |
| sql-database-architect | deep-planning | opus | gpt-5.6-sol | GPT-5.6 Sol (copilot) | gpt-5.6-sol[…] | **zai-coding-plan/glm-5.3** | zai/glm-5.3 |
| csharp-dev | fast (unchanged) | haiku | gpt-5.6-luna | **MAI-Code-1.1-Flash (copilot)** | composer-2.5[] | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| react-dev | fast (unchanged) | haiku | gpt-5.6-luna | **MAI-Code-1.1-Flash (copilot)** | composer-2.5[] | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| research-agent | fast (unchanged) | haiku | gpt-5.6-luna | **MAI-Code-1.1-Flash (copilot)** | composer-2.5[] | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| python-dev | fast (unchanged) | haiku | gpt-5.6-luna | **MAI-Code-1.1-Flash (copilot)** | composer-2.5[] | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| azure-reader | fast (unchanged) | haiku | gpt-5.6-luna | **MAI-Code-1.1-Flash (copilot)** | composer-2.5[] | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| maui-dev | fast (unchanged) | haiku | gpt-5.6-luna | **MAI-Code-1.1-Flash (copilot)** | composer-2.5[] | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| docs-dev | fast (unchanged) | haiku | gpt-5.6-luna | **MAI-Code-1.1-Flash (copilot)** | composer-2.5[] | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| test-dev | **`mai-code-flash` key → fast** | **haiku** (was inherit) | **gpt-5.6-luna** (was inherit) | MAI-Code-1.1-Flash (copilot) (unchanged value, new profile path) | **composer-2.5[]** (was inherit) | **opencode/muse-spark-1.3-contributor-free** (was inherit) | opencode/muse-spark-1.3-contributor-free (new) |
| dal-dev | general (unchanged) | **haiku** | gpt-5.6-terra | **Grok 4.6 (copilot)** | **grok-4.6[]** | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| github-devops | general (unchanged) | **haiku** | gpt-5.6-terra | **Grok 4.6 (copilot)** | **grok-4.6[]** | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| pulumi-dev | general (unchanged) | **haiku** | gpt-5.6-terra | **Grok 4.6 (copilot)** | **grok-4.6[]** | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| tauri-dev | general (unchanged) | **haiku** | gpt-5.6-terra | **Grok 4.6 (copilot)** | **grok-4.6[]** | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| product-owner | general (unchanged) | **haiku** | gpt-5.6-terra | **Grok 4.6 (copilot)** | **grok-4.6[]** | **opencode/muse-spark-1.3-contributor-free** | opencode/muse-spark-1.3-contributor-free |
| code-reviewer | **general → reviewer** | sonnet (unchanged value, new profile path) | gpt-5.6-terra (unchanged value, new profile path) | **Kimi K2.7 Code (copilot)** (U12 resolved A, P47; was Grok 4.5) | **kimi-k2.7-code[]** (was grok-4.5[]) | **opencode-go/kimi-k2.7-code** (was zai-coding-plan/glm-5.2) | opencode-go/kimi-k2.7-code (new) |
| review-lens | **general → reviewer** | sonnet (unchanged value, new profile path) | gpt-5.6-terra (unchanged value, new profile path) | **Kimi K2.7 Code (copilot)** (U12 resolved A, P47; was Grok 4.5) | **kimi-k2.7-code[]** (was grok-4.5[]) | **opencode-go/kimi-k2.7-code** (was zai-coding-plan/glm-5.2) | opencode-go/kimi-k2.7-code (new) |
| review-triage | **fast → reviewer** | **sonnet** (was haiku) | **gpt-5.6-terra** (was gpt-5.6-luna) | **Kimi K2.7 Code (copilot)** (U12 resolved A, P47; was GPT-5.6 Luna) | **kimi-k2.7-code[]** (was composer-2.5[]) | **opencode-go/kimi-k2.7-code** (was opencode-go/gpt-5.6-luna) | opencode-go/kimi-k2.7-code (new) |
| task-reviewer | **`mai-code-flash` key → reviewer** | **sonnet** (was inherit) | **gpt-5.6-terra** (was inherit) | **Kimi K2.7 Code (copilot)** (U12 resolved A, P47; was MAI-Code-1.1-Flash) | **kimi-k2.7-code[]** (was inherit) | **opencode-go/kimi-k2.7-code** (was inherit) | opencode-go/kimi-k2.7-code (new) |
| conductor | orchestration (unchanged) | **haiku** (was inherit) | inherit (unset, unchanged) | inherit (unset, unchanged) | inherit (unset, unchanged) | opencode/big-pickle (unchanged) | inherit (new field, no change in effect) |

**Copilot `reviewer` cells** show `Kimi K2.7 Code (copilot)` — resolved U12 (answer A, 2026-09-14, P47).

## 7. Test contract

These are runner commands. Each is run from the repository root, and each builds first:

- **C1:** `dotnet build KyberWeave.sln -c Release && dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadTargetResolutionTests|FullyQualifiedName~SquadPackAndReleaseTests|FullyQualifiedName~SquadSourceTests"`
- **C2:** `dotnet build KyberWeave.sln -c Release && dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PiRendererContractTests"`
- **C3:** `dotnet build KyberWeave.sln -c Release && dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PiSquadLifecycleTests|FullyQualifiedName~SquadCliCommandTests"`
- **CF (full suite):** `dotnet build KyberWeave.sln -c Release && dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`

For every row, the assertions bind to `SquadSourceLoader.Load` on the checked-in
`products/kyber-squad`. Hardcoded rosters are not allowed, except the test's own independent
capability-to-tool table from section 6.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 → T4 | `tests/KyberWeave.Tests/SquadTargetResolutionTests.cs`, `SquadPackAndReleaseTests.cs`, `SquadSourceTests.cs` (plus Pi paths in `Fakes/FakeSquadRenderer.cs` as infrastructure) | C1 | <ol><li>`SquadTargetCatalog.All` has ten targets, ending with `Pi`, and its tokens end with `pi`.</li><li>`Parse` accepts `pi` and `PI`, `all` includes `Pi`, and the unknown-target message lists `pi`.</li><li>In a root containing `.pi/subagents.json` and `.pi/agents/` and no other marker, install resolution without explicit targets does **not** select `Pi`. Explicit `pi` resolves from `Explicit`, and configured `[Pi]` resolves from `Configuration`.</li><li>An APM pack of a source containing `.pi/agents/x.md` has no entry starting `.pi/`.</li><li>A `models.yml` profile with `pi: provider/model` loads and exposes the `pi` harness model, and `model-profiles.schema.json` declares `pi`.</li></ol> | The build fails with CS0117 (`SquadTarget` has no `Pi`), or the named assertions fail. The log is captured before T4 starts. | C1 passes with unchanged assertions, and CF passes. |
| T2 → T5 | `tests/KyberWeave.Tests/PiRendererContractTests.cs` (new) | C2 | <ol><li>`SupportedTargets` is exactly `[Pi]`. A registry holding only `PiRenderer` fails preflight for `[Pi, Cursor]`, naming `cursor` and `docs/todo`. The renderer guard throws `ArgumentException` for a non-Pi target.</li><li>The real corpus renders successfully. The file count equals R17's formula. Every `Target` is `pi`. Every path starts with `.pi/agents/` or `.pi/skills/`. No path contains `role-`. No path is `.pi/subagents.json` or `.pi/settings.json`.</li><li>Each subagent-invocation agent has exactly one `.pi/agents/<name>.md`. `name` and the single-line `description` match. `tools` is present, uses only `read, grep, find, ls, edit, write, bash` or `none`, and equals the independently computed allow-only set in fixed order. `extensions` is `false` (Q1-A). `allowed_subagents` equals the CSV roster exactly when `delegate: allow` and the roster is non-empty, and is absent otherwise. `model` is absent. No `name` contains `:`. The body contains the canonical body.</li><li>Every direct child of `.pi/agents/` is an agent principal, and resources live only in subdirectories. No emitted file is a direct `*.md` child of `.pi/skills/`.</li><li>The primary agent (`conductor`) has no `.pi/agents/conductor.md` and exactly one `.pi/skills/conductor/SKILL.md` with `name`, `description`, and `license: MIT`. Its resources sit under `.pi/skills/conductor/`. It records `role-skill-fallback` and `permission-not-expressible`.</li><li>Every canonical skill has `.pi/skills/<name>/SKILL.md`, with a name matching `^[a-z0-9]+(-[a-z0-9]+)*$` and at most 64 characters, a single-line description of at most 1024 characters, and `license: MIT`.</li><li>Every agent with an `ask` capability has exactly one `safety-narrowed` record naming each `ask` capability. `permission-not-expressible` appears exactly where section 6 requires it. Every digest equals `BodyDigest`, and no `Details` contains "widen".</li><li>A corpus fixture copy with `pi: provider/model` on one profile emits `model: provider/model` for that profile's agents. `pi: inherit` emits no `model`.</li><li>A fixture copy that adds a canonical skill named `conductor` throws `SquadRenderValidationException` naming `conductor`.</li><li>Two renders produce byte-identical output.</li></ol> | The build fails with CS0246 (`PiRenderer` not found), captured after T4 is green and before T5 starts. | C2 passes with unchanged assertions. CF passes. The existing Claude, Copilot, Cursor, Codex, and Antigravity contract suites pass unmodified. |
| T3 → T5 | `tests/KyberWeave.Tests/PiSquadLifecycleTests.cs` (new), `tests/KyberWeave.Tests/Fakes/CorpusSquadReleaseSource.cs` (new; copies `products/kyber-squad` into the extraction destination), `tests/KyberWeave.Tests/SquadCliCommandTests.cs` | C3 | <ol><li>`SquadLifecycleService.InstallAsync` with `DryRun`, `[Pi]`, and `SquadCommandComposition.ResolveRenderer()` succeeds. The plan's file count equals the R17 formula, every file targets `pi`, and nothing is created under the root.</li><li>A dry run for `[Antigravity, Pi]` succeeds. The two targets' relative path sets are disjoint, and the total equals the sum of the two single-target counts.</li><li>A real install of `[Pi]` into a temp root that already holds `.pi/subagents.json` succeeds, leaves that file byte-identical, and does not list it in the receipt.</li><li>The doctor output's "Renderers available" section contains the whole-word token `pi` (`\bpi\b`, so `copilot` does not match), and "Not yet implemented" does not.</li></ol> | The build fails with CS0246 (`PiRenderer`) or CS0117 (`SquadTarget.Pi`), or, if only the doctor assertion compiles, it fails. Captured before T5. | C3 passes with unchanged assertions, and CF passes. |

**Resolution note (2026-09-14 reopening; models work revised 2026-09-14).** U7 resolved to "R3
unchanged" — no row above changes. T1–T7's rows stand exactly as audited PASS; nothing in them is
redone. The reopening adds three new row-pairs, T12–T17 below. U10 is now fully resolved (U12 resolved
A on 2026-09-14; Copilot's `reviewer` value is `Kimi K2.7 Code (copilot)`, P47). T7 and T8 must be
re-run once T12–T17 land, because both integrate the whole corpus; T9–T11 are unaffected in ordering
and run after, as before.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T12 → T13 | `tests/KyberWeave.Tests/SquadGlobalRootTests.cs` (new); a new `IFakeGlobalRootResolver`/equivalent test seam so no test touches the real home directory | New filter added to C1 (or a new C4) | <ol><li>For each of the six targets, a dry-run install with `Scope: Global` into a temp "home" resolves every file's physical root to that target's row in section 6a's table, honoring an injected override "env var" before the verified default.</li><li>A dry-run install with `Scope: Project` is byte-identical to today's output for all six targets — no regression.</li><li>Global-scope relative paths for each target match section 6a's table exactly (no `.{harness}/` wrapper).</li><li>A real (non-dry-run) install under `Scope: Global` into a temp "home" writes files only inside that temp tree, never touches `SquadPathPolicy` containment failures, and the five already-shipped contract suites (`ClaudeRendererContractTests`, `CopilotRendererContractTests` if present, `CursorRendererContractTests`, `CodexRendererContractTests`, `AntigravityRendererContractTests`) pass unmodified.</li></ol> | Compiles against today's `SquadDeploymentPlan`/`SquadTransaction`/renderers and fails: every rendered file's physical root is `plan.PhysicalRootPath` (the project root) regardless of `Scope`, and every renderer's relative-path prefix is unconditional — captured before T13 starts. | New assertions pass; the five existing contract suites and CF pass unmodified. |
| T14 → T15 | `tests/KyberWeave.Tests/SquadCliCommandTests.cs` (doctor section) | Existing C3 filter, extended | `squad doctor --global` against a fixture global root containing a name-colliding, byte-different file lists it in a warning line naming the file and the colliding canonical identity; `install --global` still refuses it via the existing `UnmanagedCollision` rule. | `squad doctor` has no `--global` collision-warning path today; the new assertion fails against the current `SquadDoctorCommand`. | New assertion passes; existing doctor assertions (T3 row) pass unmodified. |
| T16 → T17 | `tests/KyberWeave.Tests/SquadSourceTests.cs` (models.yml/profile section), `tests/KyberWeave.Tests/PiRendererContractTests.cs` (T2 criterion 3, narrowly extended), `tests/KyberWeave.Tests/Squad/CopilotRendererTests.cs` (two named tests, below) | C1 (source), C2 (Pi renderer), and the Copilot filter within CF | <ol><li>`models.yml` declares a `reviewer` profile with `default: inherit` and the resolved `pi:`/`claude:`/`codex:`/`cursor:`/`opencode:` values from section 6b's table exactly, and the `copilot:` value once **NEEDS_DECISION U12** resolves. `mai-code-flash` is removed as a profile key (it was a Copilot model name only, never referred to as a profile again). `fast.copilot` equals `MAI-Code-1.1-Flash (copilot)`, `general.claude` equals `haiku`, `general.copilot`/`general.cursor` equal the latest-Grok values, `deep-planning.opencode` equals `zai-coding-plan/glm-5.3`, `fast.opencode`/`general.opencode` equal `opencode/muse-spark-1.3-contributor-free`, and `orchestration.claude` equals `haiku` — every cell in section 6b's table.</li><li>`code-reviewer`, `review-lens`, `review-triage`, and `task-reviewer` declare `model-profile: reviewer` in their canonical frontmatter; `test-dev` declares `model-profile: fast`. `SquadSourceLoader.Load` resolves each without error.</li><li>Against the real corpus, `PiRenderer` emits `model: zai/glm-5.3` for every agent on `deep-planning`, `model: opencode/muse-spark-1.3-contributor-free` for every agent on `fast` and `general`, `model: opencode-go/kimi-k2.7-code` for every agent on `reviewer`, and no `model` key for `conductor` (`orchestration`, `pi: inherit`) — updating T2 criterion 3's absence assertion to an equality assertion scoped to exactly these profiles.</li><li>**Existing non-Pi contract tests whose asserted literal model value changes must be updated in this RED task, not left to silently fail or be weakened:** `tests/KyberWeave.Tests/Squad/CopilotRendererTests.cs`'s `RenderAsync_LensSeatsResolveToTheirDeclaredModelTier` (`InlineData("review-lens", "Grok 4.5 (copilot)")`, `InlineData("review-triage", "GPT-5.6 Luna (copilot)")`) hardcodes two *different* tiers for review-lens and review-triage; since both now share the single `reviewer` profile, its premise no longer holds and it must be redesigned (not merely have its literals swapped) to assert they resolve to the *same* `reviewer.copilot` value. `MaiCodeProfileResolvesToExactCopilotModel` (`InlineData("task-reviewer")`, `InlineData("test-dev")`) hardcodes both agents to `MAI-Code-1.1-Flash (copilot)`; task-reviewer now resolves to `reviewer.copilot` (pending U12) while test-dev still resolves to `fast.copilot` (`MAI-Code-1.1-Flash (copilot)`, unchanged value via a new profile path) — this test must be renamed (it names the deleted `mai-code-flash` profile) and split into two separate, correctly-scoped assertions. Every other registered renderer's contract test (`ClaudeRendererContractTests`, `CursorRendererContractTests`, `CodexRendererContractTests`) derives its expected model dynamically from the loaded `models.yml` (`source.ModelProfiles.Profiles[agent.ModelProfile].HarnessModels[...]`) and needs no edit — confirmed by reading each file 2026-09-14.</li><li>**Hotshot golden contract tests fail on Copilot model values; amendment in section 4:** `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` contains two tests that compare against the checked-in fixture `tests/KyberWeave.Tests/Fixtures/kyber-squad-hotshot-golden.json` (a 24-agent external "Hotshot" snapshot); `CanonicalSourcePreservesGoldenContractOutsideReviewedEvolution` and `CopilotRenderMatchesCheckedInHotshotGoldenContract` fail on Copilot model changes that T17 introduces. Resolution: narrow `ModelEvolvedAgentIdentities` list added for reviewed agents only; Copilot model comparison exempted for listed agents; all other contract checks (description, body, tools, capability, delegation, paths) remain asserted. Fixture is not edited; the golden pins immutable past values. See section 4 "Execution amendment (2026-09-14, T17)" for full detail.</li></ol> | `models.yml` still has a `mai-code-flash` key today, no `reviewer` key exists, `general.claude` is still `sonnet`, `general.copilot`/`general.cursor` still carry Grok 4.5, `fast.copilot` still carries `GPT-5.6 Luna (copilot)`, `deep-planning.opencode`/`general.opencode` still carry `glm-5.2`, `orchestration` has no `claude` field, and `code-reviewer`/`review-lens`/`review-triage`/`task-reviewer`/`test-dev` still declare their pre-move `model-profile` values — every such assertion fails against the corpus and file as they stand today. `RenderAsync_LensSeatsResolveToTheirDeclaredModelTier` and `MaiCodeProfileResolvesToExactCopilotModel` are captured failing under their *updated* (not original) expectations before T17 starts. **Additionally, `HotshotGoldenContractTests` will fail on Copilot model values for the reviewed agents.** | New assertions pass; `mai-code-flash` is deleted; `reviewer` exists with the exact section 6b values (Copilot cell pending U12); `test-dev` is on `fast`; the two updated `CopilotRendererTests.cs` tests pass under their new expectations; T2's other criteria (1, 2, 4–10) pass unmodified; the Hotshot golden contract tests pass with the `ModelEvolvedAgentIdentities` narrowed exemption applied; CF passes. |

**No-test tasks**, with the verification that replaces tests:

| Task | Replacement verification |
|---|---|
| T6 (docs) | `docs validate .` and `docs drift .` |
| T7 (verification) | Command evidence |
| T8 (live Pi check) | Owner-observed manual evidence |
| T9 (council) | The council verdict |
| T10 (closeout) | `docs validate .` and `docs drift .` |
| T11 (PR) | CI checks green on the pushed branch |

## 8. Tasks

Dependencies reflect a shared-worktree compile coupling. A RED test that references a missing
symbol breaks the build for every other task's evidence run. So catalog RED and GREEN (T1 → T4)
precede renderer RED (T2, T3).

**T1: RED, target declaration** (skills: `test-dev`)

- **Objective:** write the C1 contract tests before any production change.
- **Files:** `tests/KyberWeave.Tests/SquadTargetResolutionTests.cs`,
  `tests/KyberWeave.Tests/SquadPackAndReleaseTests.cs`, `tests/KyberWeave.Tests/SquadSourceTests.cs`,
  `tests/KyberWeave.Tests/Fakes/FakeSquadRenderer.cs`.
  - Rename `NineTargets` to a ten-target roster.
  - Add a negative marker test for `.pi` beside the Antigravity explicit-only test.
  - Add `.pi/agents` and `.pi/skills` cases to the fake's native path switches so catalog-wide
    lifecycle tests keep rendering.
- **Acceptance:** section 7, row T1. RED evidence is recorded, and no production file is touched.
- **Depends on:** none.

**T4: GREEN, declare the target** (skills: `csharp-dev`)

- **Objective:** make C1 pass (R1, R12, R13 declaration parts, U4).
- **Files:**
  - `src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs` (`Pi` appended; `ApprovedTargets`,
    `TargetsByToken["pi"]`, `GetToken`).
  - `src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs` (`ModelProfileFields` gains `pi`).
  - `products/kyber-squad/schemas/model-profiles.schema.json` (`pi` property).
  - `src/KyberWeave.Core/Squad/Packaging/SquadPacker.cs` (`.pi/` exclusion).
  - `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs` (install and update `--target` text).
  - `SquadTargetResolver.Markers` is **unchanged**.
- **Acceptance:** section 7, row T1 GREEN. Refactor while green. Both format gates pass, and the
  Release build has zero warnings.
- **Depends on:** T1.

**T2: RED, renderer contract** (skills: `test-dev`)

- **Objective:** write `PiRendererContractTests` to section 6 and section 7, row T2, including the
  corpus-copy fixture tests.
- **Files:** `tests/KyberWeave.Tests/PiRendererContractTests.cs` (new).
- **Acceptance:** section 7, row T2. The RED evidence is recorded. The class `<summary>` cites the
  2026-09-14 verification sources.
- **Depends on:** T4. Q1 is resolved (A, section 2).

**T3: RED, lifecycle, coexistence, and doctor** (skills: `test-dev`)

- **Objective:** write the C3 tests and the corpus-copying release-source fake.
- **Files:** `tests/KyberWeave.Tests/PiSquadLifecycleTests.cs` (new),
  `tests/KyberWeave.Tests/Fakes/CorpusSquadReleaseSource.cs` (new),
  `tests/KyberWeave.Tests/SquadCliCommandTests.cs`.
- **Acceptance:** section 7, row T3.
- **Depends on:** T4. T3 can run in parallel with T2 because their file scopes are disjoint.

**T5: GREEN, `PiRenderer`, registry, and composition** (skills: `csharp-dev`)

- **Objective:** implement section 6 and make C2 and C3 pass.
- **Files:**
  - `src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs` (new; `<remarks>` per section 6).
  - `src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs`: Pi in `isNative`. A Pi case in
    `AgentOutputPath`, whose signature changes to take the agent and fallback profiles so a
    lowered primary agent maps to `.pi/skills/<name>/SKILL.md`. A Pi case in `SkillOutputPath`.
    Existing targets' results stay unchanged.
  - `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs` (register `new PiRenderer()`
    and update the remarks).
- **Acceptance:**
  - Section 7, rows T2 and T3 GREEN.
  - Refactor while green: reuse `SquadMarkdownDocument.Compose` and the existing
    resource-projection helper, and add no second copy of an existing helper.
  - Zero warnings under `TreatWarningsAsErrors` and `AnalysisMode=all`.
  - Both format gates pass, and CF passes.
- **Depends on:** T2, T3.

**T12: RED, cross-target global root** (skills: `test-dev`)

- **Objective:** write `SquadGlobalRootTests` (or equivalent) per section 7 row T12, including the
  fake global-root resolver test seam so no test touches the real home directory.
- **Files:** `tests/KyberWeave.Tests/SquadGlobalRootTests.cs` (new); a new
  `tests/KyberWeave.Tests/Fakes/FakeSquadGlobalRoots.cs` (new) implementing whatever injection point
  T13 introduces.
- **Acceptance:** section 7, row T12. RED evidence recorded before T13 starts.
- **Depends on:** T5.

**T13: GREEN, cross-target global root** (skills: `csharp-dev`)

- **Objective:** implement R18/section 6a — the per-target physical-root override under `Scope:
  Global` — and make every renderer `Scope`-aware for its relative-path prefix only.
- **Files:**
  - `src/KyberWeave.Core/Squad/Deployment/SquadDeploymentPlan.cs`,
    `src/KyberWeave.Core/Squad/Deployment/SquadTransaction.cs`: per-file physical root keyed by
    `SquadDeploymentFile.Target`, consulted only under `Scope: Global`; `Scope: Project` path is
    byte-for-byte unchanged.
  - A new global-root resolution component (e.g.
    `src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs`) implementing section 6a's table: per
    target, read the override env var, else the verified default.
  - `src/KyberWeave.Core/Squad/Rendering/ClaudeRenderer.cs`, `CopilotRenderer.cs`, `CursorRenderer.cs`,
    `CodexRenderer.cs`, `AntigravityRenderer.cs`, `PiRenderer.cs`: each reads `request.Scope` and
    switches its relative-path prefix per section 6a's table; every other emission rule (frontmatter,
    capability lowering, degradation records) is unchanged.
  - `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`: preflight for `--global` uses the same
    resolver (needed by T15 too).
- **Acceptance:** section 7, row T13 GREEN. Refactor while green: one resolver, not six copies.
  Zero warnings; both format gates pass; CF passes; the five already-shipped contract suites pass
  unmodified (Project-scope behavior untouched).
- **Depends on:** T12.

**T14: RED, doctor unmanaged-collision warning** (skills: `test-dev`)

- **Objective:** write the `squad doctor --global` collision-warning assertions per section 7 row
  T14.
- **Files:** `tests/KyberWeave.Tests/SquadCliCommandTests.cs` (doctor section, extended).
- **Acceptance:** section 7, row T14. RED evidence recorded before T15 starts.
- **Depends on:** T13 (needs the resolved global root to know where to look).

**T15: GREEN, doctor unmanaged-collision warning** (skills: `csharp-dev`)

- **Objective:** implement U9-A — list every unmanaged, name-colliding file at the resolved global
  root before `install` is attempted; delete nothing.
- **Files:** `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs` (reuses
  `SquadDeploymentPlan`'s existing digest-comparison logic rather than duplicating it — refactor that
  logic into a shared, target-agnostic helper if it is not already reachable from `SquadDoctorCommand`
  without duplication).
- **Acceptance:** section 7, row T15 GREEN. Zero warnings; both format gates pass; CF passes.
- **Depends on:** T14.

**T16: RED, `reviewer` model profile and `test-dev` → `fast`** (skills: `test-dev`)

- **Objective:** write the `models.yml`/frontmatter/rendering assertions per section 7 row T16,
  using the exact per-harness table in section 6b. Include RED evidence showing today's
  `mai-code-flash` key still exists in `models.yml`, that `general.claude` is still `sonnet`,
  `general.copilot`/`general.cursor` still carry Grok 4.5, `fast.copilot` still carries `GPT-5.6 Luna
  (copilot)`, `deep-planning.opencode`/`general.opencode` still carry `glm-5.2`, `orchestration` has
  no `claude` field, and that `code-reviewer`, `review-lens`, `review-triage`, `task-reviewer`, and
  `test-dev` still reference their pre-move profiles. Update, in this same task,
  `tests/KyberWeave.Tests/Squad/CopilotRendererTests.cs`'s `RenderAsync_LensSeatsResolveToTheirDeclaredModelTier`
  (redesigned, not just re-valued, since review-lens and review-triage now share one profile) and
  `MaiCodeProfileResolvesToExactCopilotModel` (renamed and split, since task-reviewer and test-dev no
  longer share a profile or Copilot value — U12 resolved; all review agents use `Kimi K2.7 Code (copilot)`).
- **Files:** `tests/KyberWeave.Tests/SquadSourceTests.cs`,
  `tests/KyberWeave.Tests/PiRendererContractTests.cs` (T2 criterion 3, narrowly extended, not
  weakened elsewhere), `tests/KyberWeave.Tests/Squad/CopilotRendererTests.cs` (the two named tests).
  Do **not** touch `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` or
  `tests/KyberWeave.Tests/Fixtures/kyber-squad-hotshot-golden.json` — confirmed unaffected (section 7,
  T16 row).
- **Acceptance:** section 7, row T16. RED evidence recorded before T17 starts.
- **Depends on:** T5. U12 resolved A (Copilot's `reviewer` model = `Kimi K2.7 Code (copilot)`, P47);
  every harness's values are final and unblocked.

**T17: GREEN, `reviewer` model profile and `test-dev` → `fast`** (skills: `csharp-dev`, with the
`architecture-decision-record` skill's always-human-review discipline since this task edits
`products/kyber-squad/agents/**`)

- **Objective:** (a) remove the `mai-code-flash` key from `products/kyber-squad/profiles/models.yml`;
  (b) add the `reviewer` profile and apply every other changed cell per section 6b's table
  (`general.claude`, `general.copilot`/`general.cursor`, `fast.copilot`,
  `deep-planning.opencode`/`general.opencode`, `orchestration.claude`); (c) move `code-reviewer`,
  `review-lens`, `review-triage`, and `task-reviewer` to `model-profile: reviewer`; (d) move
  `test-dev` to `model-profile: fast`. No `SquadSourceLoader`, schema, or renderer code changes
  (P46).
- **Files:** `products/kyber-squad/profiles/models.yml`;
  `products/kyber-squad/agents/{code-reviewer,review-lens,review-triage,task-reviewer,test-dev}/*.md`
  — an always-human-review path per section 5.2, satisfied by the owner's explicit 2026-09-14
  authorization (section 6b) rather than task-reviewer discretion.
- **Acceptance:** section 7, row T17 GREEN. CF passes. `docs validate`/`docs drift` unaffected (no
  doc-governed file touched). `ClaudeRendererContractTests`, `CursorRendererContractTests`, and
  `CodexRendererContractTests` pass unmodified (self-deriving from `models.yml`); the two updated
  `CopilotRendererTests.cs` tests pass under their new expectations; the Hotshot golden contract tests
  pass with the `ModelEvolvedAgentIdentities` narrowed exemption (Copilot model comparison only;
  section 4, execution amendment).
- **Depends on:** T16.

**T6: Canonical docs** (skills: `app-docs-standard`)

- **Objective:** document the shipped behaviour. This is a no-test task, verified by
  `docs validate .` and `docs drift .`.
- **Files and changes:**

  | File | Change |
  |---|---|
  | `README.md` | Harness counts at lines 31, 150, and 166, stated accurately: ten declared targets, with auto-detection covering only marker targets. Add a Pi row to the harness table at line 173. |
  | `docs/README.md` | Lines 39, 121, and 132 |
  | `docs/catalog.md` | KyberSquad row: ten declared targets, six registered renderers |
  | `docs/context-hygiene/skills.md` | Line 152 |
  | `docs/kyber-squad/README.md` | Lines 12, 18, and 43 |
  | `docs/kyber-squad/architecture.md` | Section 1 count and diagram target lists. Section 3: native Pi, plus primary-agent lowering on a native target. Section 8: dispatch bullet for `PiRenderer` (`.pi/agents/*.md`, `.pi/skills/*/SKILL.md`), Pi lowering and degradation, and coverage today. |
  | `docs/kyber-squad/onboarding.md` | Line 18. Roster row at lines 51–63: `pi`, no aliases, *explicit or configured target only*, native agents via `@tintinweb/pi-subagents` with the conductor lowered to a skill. Coverage sentence. A Pi notes subsection: prerequisite (R16), project trust for `.pi/skills` (P3), override and collision behaviour (R4, R5), `fallbackSubagent: none` recommendation (R14), depth cap (P14), Antigravity coexistence (R15). **2026-09-14 additions:** correct the "Global Scope (`--global`)" section's overclaim (P28) with the real per-target mapping (section 6a's table, R18); a pre-migration step for operators with pre-existing unmanaged global files (U9); a model-pin note that pins are user-provider specific and an unresolvable one inherits silently rather than erroring (P15, P34). |
  | `docs/kyber-squad/requirements.md` | Pi row in the target matrix; "nine rows" becomes "ten" and the coverage sentence changes |
  | `products/kyber-squad/README.md` | Line 8 |
  | `docs/code-review/architecture.md` | Correct the stale count and "Only `CopilotRenderer` exists" sentence at lines 391–392 to current coverage |

- **Acceptance:** every count and roster matches the code after T5, T13, T15, and T17. No new todo
  is created. Both docs checks pass.
- **Depends on:** T5, T13, T15, T17 (the 2026-09-14 reopening additions above need their code
  landed first). Can run in parallel with T7.

**T7: Integrated verification** (skills: `csharp-dev`)

- **Objective:** produce command evidence. No new tests.
- **Steps:**
  1. Run every AGENTS.md "Commands" gate: restore, format whitespace, format style, Release
     build, test, skill validate, skill lint, skill scan, docs validate, and docs drift. Running
     `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json` also
     satisfies this step.
  2. `dotnet run --project src/KyberWeave.Cli -c Release --no-build -- squad doctor <scratch-dir>`
     lists `pi` under renderers available.
  3. Use the local release loop from [distribution](../distribution.md) (`scripts/release-local.sh`,
     `scripts/local-release-server.py`, `KYBER_WEAVE_RELEASE_ORIGIN`; verify each script's real
     flags before use) to serve the working tree. Then run the published binary's
     `squad install <scratch-dir> --target pi --dry-run` and `--target antigravity,pi --dry-run`.
- **Evidence:** the observed Pi file-count literal, which must equal T2's formula (unchanged — U7
  resolved to no Pi renderer delta), and exit code 0 for both dry runs. Add a dry run of
  `--target pi --global` whose planned paths resolve under the Pi global root (`$PI_CODING_AGENT_DIR`
  default `~/.pi/agent/`), not `<scratch-dir>`, per T13.
- **Depends on:** T5, T13, T15, T17 (re-runs after the reopening's code lands).

**T8: Live Pi check** (skills: `csharp-dev`)

- **Gate:** requires explicit owner approval before it runs, because it uses the owner's Pi auth
  and model spend. This is a no-test task, replaced by owner-observed evidence.
- **Setup:** with the T7 release loop, install `--target pi` into a scratch git repository under a
  trusted path (for example `/Users/dave/git/…`, per P19). Pi requires
  `@tintinweb/pi-subagents` 0.19.0 or later.
- **Headless checks, by the specialist:**
  1. `pi -p '@research-agent list the files under .pi/agents and stop'` starts the project
     `research-agent` and completes.
  2. The transcript shows only `read`, `grep`, `find`, and `ls` tool use.
- **TUI observations, by the owner and relayed by the conductor:**
  1. `/agents` lists the 20 Squad subagent-invocation agents as project (`•`) entries, with no
     "Skipping agent file" warnings. `conductor` is correctly **absent** from this list (U7
     resolved: skill-only, R3 unchanged) — the owner already reviewed this reasoning (P20–P25) when
     answering U7, so this step is a final live confirmation, not a new question.
  2. Startup shows the `conductor` skill collision with the global file skipped.
  3. `/skill:conductor` loads the Squad conductor.
  4. The running agent(s) on `deep-planning`, `fast`/`general`, and `reviewer` (and `test-dev` on its
     assigned profile per NEEDS_DECISION U10a, post-T17) show the resolved model from section 6b's table
     (or, for an unresolvable pin, the P15 inherit-and-flag behavior) in `/agents` or the widget.
  5. `--global` install: `/agents` in a *separate*, real Pi session pointed at `$PI_CODING_AGENT_DIR`
     (not the scratch project) shows the same 20 agents, confirming T13's global-root redirection
     actually lands where Pi itself reads from.
- **If the owner declines:** record the declined check as a residual risk on this plan and proceed
  to T9.
- **Depends on:** T7, and therefore transitively on T12, T13, T14, T15, T16, T17.

**T9: Final `code-reviewer` council** (skills: `code-review`)

- **Objective:** review the whole accumulated change with `review gates`. The verdict must be
  APPROVE; REQUEST_CHANGES findings loop back to the owning task.
- **Depends on:** T6, T7, and T8 (or its recorded decline).

**T10: `docs-dev` closeout** (skills: `app-docs-standard`, `architecture-decision-record`)

- **Objective:** close the plan lifecycle.
- **Steps:**
  1. Migrate durable facts (sections 3, 5.1, and 6) into `docs/kyber-squad/architecture.md`,
     `docs/kyber-squad/onboarding.md`, and `docs/kyber-squad/requirements.md`, including T7's
     observed file count.
  2. Evaluate an ADR for "native Pi projection through a third-party subagents extension, with
     primary-agent lowering on a native target". Create `docs/adr/0019-…` if it meets the skill's
     criteria (constrains future work, rejected alternatives, expensive to revisit).
  3. Archive this plan to `docs/archive/plans/`. Move its index row to Archived Plans, with the
     harvest links and any T8 residual.
  4. Supersede `docs/todo/pi.md`: move it to `docs/archive/todo/pi.md` with superseded status,
     following the `archive/todo/claude-code.md` precedent.
  5. Remove the `pi` row from `docs/todo/README.md` and from the
     `docs/todo/kyber-squad-renderer-coverage.md` table. Add `pi` to that page's covered-renderers
     sentence.
  6. Apply the Q2 outcome (A, section 2).
- **Acceptance:** both docs checks pass, and no active plan or todo still describes Pi as
  undeclared or as a fallback target.
- **Depends on:** T9.

**T11: Commit, push, and PR** (skills: `create-pull-request-github`, `github-cli`)

- **Objective:** commit on `claude/pi-renderer-conductor-skill-46ac0f`, push, and open a PR against
  `main` whose body summarises U1–U6, R1–R17, and the Q1 and Q2 outcomes, and links the
  archived plan.
- **Acceptance:** the PR is open and the required CI checks are green.
- **Depends on:** T10.

## 9. Dependency graph and MAX_CONCURRENCY

```text
T1 (RED catalog) ─► T4 (GREEN catalog) ─┬─► T2 (RED renderer) ─┐
                                        └─► T3 (RED lifecycle) ─┴─► T5 (GREEN renderer)
T5 ─┬─► T12 (RED global-root) ─► T13 (GREEN global-root) ─► T14 (RED doctor warn) ─► T15 (GREEN doctor warn) ─┐
    ├─► T16 (RED review profile) ─► T17 (GREEN review profile) ───────────────────────────────────────────────┤
    │                                                                                                          │
    ├─► T6 (docs) ────────────────────────────────────────────────────────────────────────────────────────┐  │
    └────────────────────────────────────────────────────────────────────────────────────────────────────►┴─►T7 (verify, re-run) ─► T8 (live, owner-gated) ─► T9 (council) ─► T10 (closeout) ─► T11 (PR)
```

**MAX_CONCURRENCY: 2.** The parallel pairs are T2 with T3; T12 with T16 (disjoint file scopes —
global-root deployment code vs. `models.yml`/agent frontmatter); T14 with nothing (depends on T13);
and T6 with T7 (or T6 with T8).

- **T1 with T2 or T3:** these are not parallelised. The build must compile for T4's GREEN evidence,
  and a T2 or T3 test that references `PiRenderer` would break it.
- **Q1:** gates T2 and T5, because it changes emitted keys. T1, T3, and T4 do not depend on it. Resolved: A.
- **Q2:** gates only T10. Resolved: A (widen `docs/todo/claude-renderer-ask-narrowing.md` at closeout).
- **U7 (2026-09-14 reopening):** resolved A. No task follows from it.
- **U8, U9 (2026-09-14 reopening):** resolved B, A. Produce T12–T15, sequential (T14 needs T13's
  resolved root to know where to look).
- **U10 (2026-09-14 reopening, revised 2026-09-14):** resolved to the `reviewer`-profile mechanism
  with the full per-harness table (section 6b); produces T16–T17. U12 resolved A (Copilot's `reviewer`
  model = `Kimi K2.7 Code (copilot)`, P47); all harness values are final and unblocked.
- T12–T17 all gate the T7/T8 re-run and everything after it, since those steps integrate the whole
  corpus. T9, T10, T11 remain last, in the same order.

## 10. Risks

| Risk | Mitigation or owner |
|---|---|
| pi-subagents is third-party and changes quickly (0.19.0 is three weeks old); a key could be renamed | The version-pinned `<remarks>` and the C2 vocabulary table. Re-verify before changing the frontmatter. T8 catches runtime drift. |
| Q1-A leaves Squad agents on Pi without MCP or web tools | The degradation records say so. Q1-B remains a follow-up if the owner wants it. |
| The top-level conductor skill cannot enforce the orchestrator's denies, and unknown `Agent` types fall back to `general-purpose` (all tools) | The `permission-not-expressible` record, plus the onboarding `fallbackSubagent: none` guidance |
| An untrusted project shows the agents (the agent loader has no trust check) but hides `.pi/skills`, including the conductor | Onboarding trust note. T8 uses a trusted path. |
| `maxSubagentDepth` below 2 silently disables `architect` and `code-reviewer` delegation | Onboarding depth note |
| Global OpenCode-format agents with Squad names are overridden in Squad projects (R5) | Intended project authority; documented |
| Adding `pi` to `ApprovedTargets` changes `--target all` | `all` already fails preflight for the four unimplemented targets, so behaviour is unchanged |
| The `AgentOutputPath` signature change could shift another target's validation | Existing contract suites must pass unmodified (T5 acceptance) |
| **2026-09-14 reopening (U8-B):** a per-target physical root in `SquadDeploymentPlan`/`SquadTransaction` is new surface all six renderers now depend on for `--global`; a bug there risks writing a file outside the intended harness's global root, or worse, under another harness's root | Reuse `SquadPathPolicy`/`SquadFileSystemPathSemantics` containment checks unchanged, applied per-target instead of per-plan; T12's contract tests assert containment and correct target-root pairing for all six targets; T12 never touches the real home directory |
| Five already-shipped renderer contract suites (Claude, Copilot, Cursor, Codex, Antigravity) must stay green through the U8-B change; a mistake in the Scope-aware relative-path switch could silently alter their Project-scope output | T13's acceptance requires those five suites pass unmodified; Project-scope code paths are untouched by construction (only the `Scope: Global` branch is new) |
| The owner's own `~/.pi/agent/agents/`/`skills/` already hold 16–17 name-colliding, byte-different legacy files (P30); the first real `--global --target pi` install will hard-fail on the first one unless the owner follows U9's pre-migration step | T15's `squad doctor --global` warning surfaces the whole list before `install` is attempted |
| A `pi:` model value resolved under U10 may not resolve on a *different* Pi user's machine (no provider evidence exists for anyone but the owner) | P15's inherit-and-flag behavior degrades gracefully rather than failing the spawn; the onboarding note (T6) states pins are user-provider specific |
| T17 touches `products/kyber-squad/agents/**`, an always-human-review path (section 5.2) this plan otherwise never touches, and changes Copilot's and Cursor's `review`-agent models per section 6b's table | The owner's 2026-09-14 authorization (section 6b) is that human review; T17's acceptance updates the affected non-Pi contract suites in the same task rather than leaving them stale |
| Env-var overrides for five harnesses (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `CURSOR_CONFIG_DIR`, `COPILOT_HOME`, `PI_CODING_AGENT_DIR`) were verified from vendor docs, not exercised against a real installed override on this machine except `CODEX_HOME` (confirmed live in the owner's own `config.toml`, P41) | Filed as G12 (section 14); T12's fake resolver still proves the override-reading *code path* works even though the specific documented variable name for the other four could not be exercised live |
| **2026-09-14 (models work revision):** Copilot's `reviewer` value was genuinely open (U12) — GitHub's own page does not list the owner-named "Kimi K2.6 Code" | Resolved A on 2026-09-14; owner chose `Kimi K2.7 Code (copilot)` (P47). T16/T17 proceed unblocked. |
| **2026-09-14 (models work revision):** `general.claude` moves from `sonnet` to `haiku`, a real capability/quality change for five agents (dal-dev, product-owner, pulumi-dev, github-devops, tauri-dev) already in production use | The owner's own explicit verbatim instruction (section 4, U11); `ClaudeRendererContractTests` derives its expectation from `models.yml` directly, so this is exercised by the existing suite with no test drift risk |
| **2026-09-14 (models work revision):** the Cursor `reviewer` value (`kimi-k2.7-code[]`) is an architect derivation accepted by the owner 2026-09-14; the OpenCode `deep-planning` value (`zai-coding-plan/glm-5.3`) is an architect derivation not verbatim from vendor pages | Cursor value accepted (section 6b); OpenCode value flagged explicitly (section 6b, section 4 U10b) for owner awareness at the gate — neither blocks T16/T17 |
| **2026-09-14 (execution amendment, T17):** the Hotshot golden contract tests failed on Copilot model values for reviewed agents; the pinned golden fixture records a snapshot where the `mai-code-flash` key in `models.yml` was still the Copilot value, and those model divergences are intentional (U10–U12 mechanism) | `ModelEvolvedAgentIdentities` narrowed list exempts Copilot model comparison only for listed reviewed agents; all other contract checks (description, body, tools, capability, delegation, paths) remain asserted; a guard assertion fails if any listed agent's model no longer differs from the golden (section 4, execution amendment) |

## 11. Out of scope

- **Moved into scope by the 2026-09-14 reopening** (see section 4): `--global` for all six targets
  (U8, resolved B — larger than the original "Pi only" framing, per the owner's explicit instruction)
  and a new `reviewer` model profile touching `products/kyber-squad/agents/**` (U10's mechanism) —
  both listed here in the original approval and struck through by the owner's own later instructions
  rather than silently.
- A per-agent, per-target model-override schema (U10's option C, not chosen — the owner picked the
  `reviewer`-profile mechanism instead).
- A fallback-model mechanism for Muse Spark or any other token — the owner ruled this out explicitly
  (G14, removed; section 4).
- `squad doctor` detection of the pi-subagents extension (unrelated to the new `--global` collision
  warning, which is a different check).
- Writing or merging `.pi/subagents.json` or `.pi/settings.json`.
- Emitting `thinking`, `max_turns`, `color`, `memory`, `isolation`, `skills`, `prompt_mode`,
  `disallowed_tools`, or `run_in_background`.
- Reassigning any `model-profile` other than the four moving to `reviewer` and `test-dev` moving to
  `fast` (T17). No other agent's profile membership changes.
- Changes to `capabilities.yml` or the `ask`-narrowing fix itself (Q2 records it only).
- `HarnessKind` and ContextHygiene agent linting for Pi.
- KyberDash's Pi provider.
- Registering the `opencode`, `kilo`, `warp`, or `factory` Squad targets themselves (no renderer, no
  catalog entry) — **note:** `opencode`'s `models.yml` *column values* are touched by this revision
  (section 6b, T17), even though the `opencode` Squad target remains unregistered; no renderer reads
  those values today, so this is a data-only change with no observable runtime effect until that
  target is implemented.
- Refreshing the repository's stale self-deployment.
- Live-exercising the four non-`CODEX_HOME` global-root override environment variables
  (`CLAUDE_CONFIG_DIR`, `CURSOR_CONFIG_DIR`, `COPILOT_HOME`, and re-confirming `PI_CODING_AGENT_DIR`)
  against a real installation — verified from vendor docs only (G12).

## 12. Verification gates

Every code task passes the AGENTS.md "Commands" gates that apply to its scope before it completes.
The whole change passes the full list, or `review gates`, in T7 and again in T9. Every docs task
passes `docs validate .` and `docs drift .`.

## 13. Review and closeout

`code-reviewer` runs once over the whole change (T9). `docs-dev` closeout (T10) follows the
review, and the PR (T11) comes last. `task-reviewer` audits each task against its section 7 row.

## 14. GAPS

- **G1:** The Pi version comes from `package.json` and settings, because the binary could not be
  executed. The Homebrew keg directory is labelled 0.84.1, while the package reports 0.84.4.
- **G2:** Upstream GitHub docs for pi-subagents and pi-mono were not fetched. Every extension fact
  comes from the installed npm package 0.19.0, and a newer release may differ.
- **G3:** Runtime behaviour in an untrusted project comes from source, not observation: the agents
  are visible and `.pi/skills` is hidden.
- **G4:** Relative links in agent bodies (`architect/references/…`) are unverified under a
  pi-subagents `replace` system prompt. The same gap exists on Claude.
- **G5:** The exact dry-run literal (113 expected) is unconfirmed until T2 and T7 run.
- **G6:** `pi -p` with a leading `@agent` mention is documented by the extension README but was not
  executed. T8 exercises it.
- **G7 (answered by the 2026-09-14 reopening, P27):** `SquadLifecycleService`'s rendered-path
  rooting for `--global` has now been traced for every target, not only Pi: `Scope: Global` today
  redirects only Squad's own state bookkeeping, never rendered files, for all six targets. What
  remains open is not the trace but the fix (U8).
- **G8:** The architect cannot run `docs validate` or `docs drift` in this environment. The
  conductor reported zero findings from both on the saved Draft; it reruns both on this Ready save.
- **G9 (2026-09-14 reopening, corrected 2026-09-14):** Whether the `opencode`/`opencode-go` providers
  (used by Pi's `fast`/`general`/`reviewer` tokens, and by the not-yet-implemented `opencode` Squad
  target's `deep-planning`/`fast`/`general`/`reviewer` tokens) currently have working auth on this
  machine is unverified — `models-store.json` shows a cached catalog, not current auth, and
  `auth.json` was deliberately not read. This does **not** apply to Copilot's or Cursor's `reviewer`
  values: both are hosted, first-party marketplace selections resolved by GitHub/Cursor themselves,
  with no dependency on the owner's local `opencode` provider auth. P15's inherit-and-flag behavior is
  the safety net for Pi; T8's live check confirms the actually-resolved model, not just the absence of
  an error.
- **G10:** Whether a relative or otherwise malformed override-env-var value (any of the five in
  section 6a's table) should be rejected outright or fall back to the default was not investigated —
  T13's design assumes the common case (unset, default applies).
- **G11:** No per-target physical-root mechanism existed anywhere in the renderer registry before
  T13 — it is new surface, sized only qualitatively in section 10, not against a working prototype in
  this codebase.
- **G12 (2026-09-14 reopening):** Of the six global-root override environment variables in section
  6a's table, only `CODEX_HOME` was confirmed *live* on this machine (in the owner's own
  `~/.codex/config.toml`). `CLAUDE_CONFIG_DIR`, `CURSOR_CONFIG_DIR`, `COPILOT_HOME`, and
  `PI_CODING_AGENT_DIR` are verified from vendor documentation only, not exercised against a real
  installation with that variable actually set. T13's tests cover the override-reading *code path*
  generically (a fake resolver, not the real env var), which does not close this gap.
- **G14 — REMOVED (2026-09-14 revision).** The owner ruled out a fallback-model concept entirely
  ("the harnesses don't support the concept of a fall back model so just ignore that requirement";
  section 4). No two-tier selection, schema change, or fallback task exists in this plan. Muse
  Spark's free token is used alone.
- **G13 — FULLY RESOLVED (2026-09-14 revision, final answer 2026-09-14).** Re-verified against
  official vendor pages (section 5.1, P47–P52), not third-party mirrors:
  - **Copilot `reviewer`:** the owner named "Kimi K2.6 Code"; GitHub's own supported-models page
    (P47) does not list it — only `Kimi K2.7 Code` and `Kimi K3` (Moonshot AI) appear, confirmed by a
    second, targeted re-check of every "Kimi" occurrence on the page. **U12 resolved A** (section 4);
    owner chose `Kimi K2.7 Code (copilot)` on 2026-09-14.
  - **Cursor `reviewer`:** resolved to `kimi-k2.7-code[]`. Cursor's own docs (P48) confirm Kimi K2.7
    Code is in Cursor's model pool, satisfying the owner's stated criteria (different family, cheap,
    code-specialised, never Sonnet). The bracket-suffix CLI/config syntax itself was not published on
    the fetched Cursor docs pages; the value follows this file's own pre-existing convention. Not
    blocking — the owner delegated this specific choice to the architect (decision #12) and asked
    only that it be listed for gate confirmation (section 4, section 6b).
  - **Grok for `general` profile:** resolved. The owner named "grok 4.6 or 7" as latest; xAI's own
    release notes (P51), corroborated by Cursor's and GitHub Copilot's own pages (P48, P47), confirm
    Grok 4.6 (not 4.7) is current. `general.copilot` and `general.cursor` are updated to Grok 4.6 in
    this revision (section 6b) — no longer deferred as a follow-up.
  - **OpenCode `deep-planning` (GLM):** resolved to `zai-coding-plan/glm-5.3`. Z.ai's own devpack docs
    (P52) confirm GLM 5.3 is current and OpenCode-supported; the complete joined id string is derived
    from that confirmation plus this file's own pre-existing `zai-coding-plan/` provider prefix, not
    read verbatim off one page — flagged in section 6b for owner awareness, not blocking.
