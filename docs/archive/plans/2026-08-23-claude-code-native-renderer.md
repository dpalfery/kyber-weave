---
id: archive/plans/2026-08-23-claude-code-native-renderer
title: Add a native Claude Code renderer to Kyber-Squad
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-08-23
component: KyberSquad
---

# Add a native Claude Code renderer to Kyber-Squad

**Status:** Archived
**Archive Date:** 2026-08-23
**Date:** 2026-08-23
**Goal:** Implement and register an `ISquadRenderer` for `SquadTarget.Claude` so `squad install --target claude` succeeds with Claude Code layouts verified against official docs (2026-08-23).

## 1. Problem / Motivation

`squad install --target claude` fails in the renderer coverage gate before any network call:
`SquadRendererRegistry` only dispatches targets claimed by registered renderers. Today
`SquadCommandComposition.ResolveRenderer()` registers `CopilotRenderer`, `CursorRenderer`,
and `AntigravityRenderer` — not Claude. Requesting `claude` returns a closed failure naming
the gap and pointing at `docs/todo/<target>.md`.

Claude is classified as a **native agent target** (detection marker `.claude/`, no aliases).
Canonical agents and skills already load through `SquadSourceLoader` into the same
`SquadSource` model Copilot/Cursor render from. The missing piece is a Claude-specific
`ISquadRenderer` plus registration, contract tests, and the small docs/doctor surface that
still lists `claude` as pending.

## 2. Approved decisions

These are the implementation contract for this Draft plan (subject to user approval before
implementation). They were resolved from live source and official Claude Code docs; no
open ledger rows remain.

- **D1 (Paths):** Emit project-scope files at `.claude/agents/<name>.md` and
  `.claude/skills/<name>/SKILL.md`. File stem equals the canonical identity. Do not use
  Copilot's `.agent.md` double extension.
- **D2 (Agent frontmatter):** Required keys `name`, `description`. Optional `model` from
  `models.yml` harness key `claude` (aliases `opus` / `sonnet` / `haiku` / `inherit` are
  valid per docs). Omit `model` when resolution yields `inherit` or no profile value (docs
  default is inherit). Body is the agent instruction Markdown after the closing `---`.
- **D3 (Skill frontmatter):** Emit `name`, `description`, and `license: MIT` (accepted by
  Claude Code; part of Agent Skills). Collapse multi-line descriptions to a single line
  (same as `CursorRenderer`) so listing truncation stays predictable. Suppress skill
  projection for `conductor` and `conductor-v3` (native single-projection rule).
- **D4 (Tools allowlist required):** Always emit agent `tools` as an explicit allowlist.
  Official docs state that omitting `tools` inherits every tool available to subagents —
  silent permission widening for any canonical `deny`. That violates KS-003 /
  registry anti-widening intent. Do **not** ship an unset-`tools` +
  `permission-not-expressible`-only approach for Claude.
- **D5 (Capability → tool mapping, verified 2026-08-23):** Grant a tool only when the
  capability is `allow`. Both `ask` and `deny` withhold (non-broadening). Mapping:

  | Capability | Claude Code tools |
  |---|---|
  | `filesystem.read` | `Read` |
  | `filesystem.search` | `Grep`, `Glob` |
  | `filesystem.write` | `Edit`, `Write`, `NotebookEdit` |
  | `process.execute` | `Bash`, `PowerShell` |
  | `network.read` | `WebFetch`, `WebSearch` |
  | `network.publish` | _(none — no built-in publish tool)_ |
  | `delegate` | `Agent` (see D7) |

  Base ungoverned tools on every agent: `TodoWrite`, `Skill` (Skill must be listed under an
  allowlist or the agent cannot invoke project/user/plugin skills).
- **D6 (`ask` → safety-narrowed):** Claude's `permissionMode` is session-wide for the
  subagent, not per-capability. Mirror Copilot: withhold tools for `ask`, record
  `SquadDegradationRecord` with code `safety-narrowed` naming the narrowed capabilities.
  Do not set `permissionMode` (D8).
- **D7 (Delegation):** When `delegate: allow`, include `Agent` in `tools`. If
  `DelegatesTo` is non-empty, emit `Agent(name1, name2, …)` so conductors used via
  `claude --agent` get a roster allowlist. Official docs (2026-08-23): parentheses roster
  is **ignored when the definition runs as a nested subagent** — record that limitation
  on agents with a non-empty `DelegatesTo` as part of a `permission-not-expressible`
  (or combined details) degradation, without claiming roster enforcement for nested
  spawns.
- **D8 (`permissionMode`):** Omit. Leaving it unset inherits the parent session mode.
  Setting `bypassPermissions` / `acceptEdits` / `dontAsk` would risk widening; setting
  `default` would override user/session preference for every granted tool.
- **D9 (MCP wildcards):** When `filesystem.read: allow` and the agent is not a pure
  orchestrator (`capability-profile == orchestrator` or name in
  `{conductor, conductor-v3}`), also grant `mcp__codegraph__*`, `mcp__kyber-weave__*`,
  `mcp__context7__*` (Claude MCP server-level pattern verified as `mcp__<server>` /
  `mcp__<server>__*` on 2026-08-23). Withhold from pure orchestrators (same PM separation
  as Copilot).
- **D10 (Serialization):** Emit `tools` as a YAML flow sequence with deterministic order.
  Entries containing `(` or `*` must be single-quoted scalars so YamlDotNet / consumers
  do not misparse. Reuse `SquadMarkdownDocument.Compose` for document assembly.
- **D11 (Registration):** Add `new ClaudeRenderer()` to
  `SquadCommandComposition.ResolveRenderer()` beside the existing three renderers.
- **D12 (File count):** Assert expected native file count as
  `source.Agents.Count + source.Skills.Count - |{conductor, conductor-v3}|` from the
  loaded corpus — **not** the todo's hardcoded 45. Current corpus: 22 agents + 26 skills
  − 2 suppressed = **46** files.

## 2a. Open questions (decision ledger)

| Q# | Question | Options | Recommended | Depends on | Status |
|----|----------|---------|-------------|------------|--------|
| — | _(none open)_ | | | — | — |

## 3. Investigation findings

### Code seam (self-gathered; no `.codegraph/` present)

- `ISquadRenderer` / models: `src/KyberWeave.Core/Squad/Rendering/SquadRenderModels.cs`.
- Registry coverage gate + native single-projection validation (conductor skill forbidden,
  no `role-` on native): `SquadRendererRegistry.cs`. Claude is already in the native set
  (`SquadTarget.Claude`).
- Composition: `SquadCommandComposition.ResolveRenderer()` currently
  `[CopilotRenderer, CursorRenderer, AntigravityRenderer]`.
- Closest native sibling: `CursorRenderer` (paths `.cursor/agents/<name>.md`,
  `SquadMarkdownDocument`, conductor skill suppression, degradation pattern). Closest
  permission sibling: `CopilotRenderer` (explicit tools allowlist + `safety-narrowed` for
  `ask` + MCP wildcards + orchestrator withhold).
- Detection: `SquadTargetResolver` maps strong marker `.claude/` → `Claude`; no aliases.
- Profiles: `products/kyber-squad/profiles/models.yml` already has `claude: opus|haiku|sonnet`
  on deep-planning / fast / general. `capabilities.yml` is target-neutral.
- Fake paths already anticipate Claude: `FakeSquadRenderer` emits
  `.claude/agents/<name>.md` and `.claude/skills/<name>/SKILL.md`.
- Doctor: `SquadDoctorCommand` derives available vs pending from
  `ResolveRenderer().SupportedTargets`. CLI test currently expects `claude` in pending
  (`SquadCliCommandTests`).
- Contract-test pattern to copy: `tests/KyberWeave.Tests/CursorRendererContractTests.cs`
  (real `products/kyber-squad` corpus, assert against loaded `SquadSource`, not literals).

### Official Claude Code docs (verified 2026-08-23)

**Subagents** — [code.claude.com/docs/en/sub-agents](https://code.claude.com/docs/en/sub-agents):

- Location: `.claude/agents/` (project), `~/.claude/agents/` (user).
- Format: Markdown + YAML frontmatter; only `name` and `description` required.
- Optional fields used by this plan: `tools`, `disallowedTools`, `model`, `permissionMode`, …
- `tools` omitted ⇒ inherit all subagent tools (widening if we omit).
- `tools` is an allowlist; `disallowedTools` is a denylist.
- Model aliases: `sonnet`, `opus`, `haiku`, `fable`, full IDs, or `inherit`.
- MCP patterns: `mcp__<server>` / `mcp__<server>__*` (example `mcp__github`).
- `Agent(type1, type2)` roster allowlist applies for `claude --agent` main thread;
  **parentheses ignored when the same file runs as a nested subagent**.

**Skills** — [code.claude.com/docs/en/skills](https://code.claude.com/docs/en/skills):

- Location: `.claude/skills/<dir>/SKILL.md`.
- Frontmatter: `name` optional (defaults to directory), `description` recommended;
  `license` accepted (Agent Skills); Claude Code does not act on license contents.
- Skill `allowed-tools` is a **turn-scoped pre-approval** grant, not an allowlist that
  removes other tools — do not use it to enforce the capability lattice on skills.

### Docs corpus

- [architecture.md §8](../../kyber-squad/architecture.md#8-rendering): lists Copilot / Cursor /
  Antigravity only; must gain Claude when implementing.
- [onboarding.md](../../kyber-squad/onboarding.md#harness-targets-and-auto-detection):
  `claude` / `.claude/` / Native agents already tabulated; coverage blurb still says
  copilot+cursor+antigravity.
- Coverage index todo: `docs/todo/kyber-squad-renderer-coverage.md` still lists `claude`
  as pending (implementer updates after ship; not blocking renderer code).

### Todo acceptance delta

The todo's "45 files (22 agents + 23 non-conductor skills)" assumed 25 skills. The checked-in
bundle (`products/kyber-squad/bundles/full.yml`) lists **26** skills → **46** native files
after conductor suppression. Prefer D12's derived count over the stale literal.

## 4. Task list

Each task lists skills (not agents). Acceptance criteria are binary checks for the
implementer / verifier.

| # | Phase | Component | Description | Skills |
|---|-------|-----------|-------------|--------|
| 1 | Test-first | `KyberWeave.Tests` | Add `ClaudeRendererContractTests.cs` mirroring `CursorRendererContractTests`: registry supports only Claude when wrapped alone; unsupported-target preflight; guard rejects non-Claude; render real `products/kyber-squad` corpus; assert paths, frontmatter `name`/`description`/`model`, tools ⊆ documented vocabulary, conductor skill suppression, degradation codes (`safety-narrowed` / `permission-not-expressible`), digest match, file count per D12. | `test-dev`, `csharp-dev` |
| 2 | Implementation | `KyberWeave.Core` | Add `ClaudeRenderer` implementing `ISquadRenderer` per D1–D10. Document verification date and source URLs in `<remarks>` (Copilot/Cursor style). Use `SquadMarkdownDocument.Compose`. Deterministic tool order. | `csharp-dev` |
| 3 | Composition | `KyberWeave.Cli` | Register `new ClaudeRenderer()` in `SquadCommandComposition.ResolveRenderer()`. Update the method remarks to name Claude as a native renderer. | `csharp-dev` |
| 4 | Tests (doctor / CLI) | `KyberWeave.Tests` | Update `SquadCliCommandTests` doctor assertion: `claude` moves from pending section to available section (alongside cursor/antigravity/copilot as applicable). Keep other pending targets asserted if the test covers them. | `test-dev`, `csharp-dev` |
| 5 | Docs | `docs/kyber-squad` | Update `architecture.md` §8 dispatch + coverage bullets to include `ClaudeRenderer` (`.claude/agents/*.md`, `.claude/skills/*/SKILL.md`). Update onboarding coverage sentence that still says only copilot/cursor/antigravity. After edits: `docs validate` + `docs drift`. | `app-docs-standard` |
| 6 | Verification | Gates | `dotnet format` (whitespace + style), `dotnet build -c Release`, `dotnet test` (at least Squad rendering + CLI doctor tests), `kyber-weave squad install --target claude --dry-run` against a temp root planning 46 files (or derived count), `kyber-weave squad doctor` shows `claude` under renderers available, `docs validate` / `docs drift` clean. | `csharp-dev`, `test-dev` |

### Per-task acceptance criteria

**Task 1**

- New test file fails before Task 2 (red) and passes after Tasks 2–3 (green).
- Assertions bind to `SquadSourceLoader.Load(ProductRoot)`, not hardcoded agent/skill name lists
  (except the shared conductor suppression set).
- Expected file count uses D12 formula.

**Task 2**

- `ClaudeRenderer.SupportedTargets` is exactly `[SquadTarget.Claude]`.
- Every corpus agent produces `.claude/agents/<name>.md` with valid frontmatter.
- Every non-conductor skill produces `.claude/skills/<name>/SKILL.md`.
- No conductor/conductor-v3 skill files; registry validation does not throw.
- `tools` present on every agent; only `allow` capabilities contribute governed tools.
- Each `ask` capability produces a `safety-narrowed` degradation (or is listed in one
  combined record per agent) with matching `InstructionDigest`.
- Nested-subagent roster limitation recorded when `DelegatesTo` is non-empty.
- Zero warnings under `TreatWarningsAsErrors` / `AnalysisMode=all`.

**Task 3**

- `ResolveRenderer().SupportedTargets` contains `Claude`.
- Multi-target request including `claude` no longer fails coverage for Claude alone.

**Task 4**

- Doctor test expects `claude` in available, not pending.

**Task 5**

- §8 names ClaudeRenderer and paths; onboarding coverage text matches reality.
- `docs validate .` and `docs drift .` exit 0.

**Task 6**

- Dry-run plans exactly the D12 file count for `--target claude`.
- Doctor lists `claude` under renderers available.
- Full local gate subset for touched projects is green.

## 5. Sequencing / dependency graph

```text
T1 (failing contract tests)
 └─► T2 (ClaudeRenderer)
      └─► T3 (register in composition)
           ├─► T4 (doctor CLI test update)
           ├─► T5 (architecture / onboarding docs)
           └─► T6 (gates / dry-run / doctor)  [after T2–T5]
```

T4 and T5 may proceed in parallel after T3. T1 may be authored first (red) then
kept green after T2–T3.

## 6. Residual decisions / risks

| Risk | Owner / condition |
|---|---|
| Claude Code may add/rename built-in tools; a stale allowlist entry can zero-resolve and refuse to launch the subagent | Implementer pins vocabulary in test comments to the 2026-08-23 docs snapshot; unknown tools must not be invented later without re-verification |
| `Agent(roster)` ignored for nested subagents — conductors used only via Task/Agent delegation do not get roster enforcement | Accepted in D7; degradation must say so honestly |
| Parent session `permissionMode` (e.g. auto/bypass) can still dominate subagent prompts even with a tight tools list | Inherent harness behavior; out of renderer control (D8) |
| Todo `docs/todo/claude-code.md` and coverage index still describe the gap after ship | Close/update those todos in a follow-up docs hygiene pass (out of scope here unless implementer includes Task 5 adjacent cleanup) |
| Stale "45 files" acceptance in the todo | Use D12; do not fail CI on the literal 45 |

## 7. Out of scope

- Other pending targets (`codex`, `opencode`, `kilo`, `factory`, `gemini`, `warp`) — separate todos.
- Changing canonical agents/skills or `models.yml` / `capabilities.yml` contents (Claude model keys already exist).
- Emitting `disallowedTools`, `hooks`, `mcpServers` inline configs, `isolation`, `memory`, `effort`, or skill `allowed-tools` pre-approvals.
- User-global path rewriting beyond what `SquadLifecycleService` already does with
  `UserScopeDirectory` (renderers today emit project-relative `.claude/...` paths like Cursor).
- Empirical hand-load of a sample agent inside an interactive Claude Code session (recommended
  smoke after merge; not a CI gate).
- Marking `docs/todo/claude-code.md` completed / removing it from the coverage table
  (optional tidy; not required for renderer correctness).

## 8. Required skills

- `csharp-dev` — renderer, composition, C# test fixes
- `test-dev` — contract and CLI doctor tests
- `app-docs-standard` — kyber-squad architecture/onboarding corpus edits

## 9. Verification harness

- Unit/contract: `ClaudeRendererContractTests` against real `products/kyber-squad`.
- Regression: existing Copilot / Cursor / Antigravity contract suites still green.
- CLI: `squad doctor` available list; `squad install --target claude --dry-run` file plan.
- Build: `TreatWarningsAsErrors` clean on Core + Cli + Tests.
- Docs: `docs validate` + `docs drift` after Task 5.
- Review: `code-review` skill / code-reviewer agent on the renderer diff; security-review if
  permission-lowering logic is questioned — confirm no canonical `deny` becomes an emitted
  tool grant.
