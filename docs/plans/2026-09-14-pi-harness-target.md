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
**Date:** 2026-09-14
**Approval:** Approved by the owner (dpalfery) on 2026-09-14 through the explicit **approve and execute** choice ("now execute the plan"), relayed by the conductor. The approval covers sections 2, 3, 6, and 7 as saved.
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
- **R3 (Conductor lowers to a skill):** Every agent with `invocation: primary` is rendered
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
- **R12 (Model):**
  - Add `pi` to `SquadSourceLoader.ModelProfileFields` and to `model-profiles.schema.json`. Every
    other declared target, fallback targets included, already has a field there.
  - Add **no** `pi:` values to `models.yml`. Pi model tokens are `provider/modelId` values tied to
    each user's configured providers. The owner's default is `zai/glm-5.3-flash` (P15, P19), so no
    portable value exists.
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

## 4. Approval record

- **Approved by:** the owner (dpalfery).
- **Approved:** the **approve and execute** gate for this plan as saved: decisions U1–U6, Q1, and Q2
  (section 2); R1–R17 (section 3); the rendering contract (section 6); and the test-first Test
  contract (section 7).
- **Date:** 2026-09-14.
- **Channel:** the owner's explicit instruction "now execute the plan", relayed by the conductor.
- **Ledger:** the Draft decision ledger is closed. Q1 and Q2 were answered A by the owner on
  2026-09-14 and are recorded in section 2. No decision remains open.

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
  | `docs/kyber-squad/onboarding.md` | Line 18. Roster row at lines 51–63: `pi`, no aliases, *explicit or configured target only*, native agents via `@tintinweb/pi-subagents` with the conductor lowered to a skill. Coverage sentence. A Pi notes subsection: prerequisite (R16), project trust for `.pi/skills` (P3), override and collision behaviour (R4, R5), `fallbackSubagent: none` recommendation (R14), depth cap (P14), Antigravity coexistence (R15). |
  | `docs/kyber-squad/requirements.md` | Pi row in the target matrix; "nine rows" becomes "ten" and the coverage sentence changes |
  | `products/kyber-squad/README.md` | Line 8 |
  | `docs/code-review/architecture.md` | Correct the stale count and "Only `CopilotRenderer` exists" sentence at lines 391–392 to current coverage |

- **Acceptance:** every count and roster matches the code after T5. No new todo is created. Both
  docs checks pass.
- **Depends on:** T5. Can run in parallel with T7.

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
- **Evidence:** the observed Pi file-count literal, which must equal T2's formula (113 expected),
  and exit code 0 for both dry runs.
- **Depends on:** T5.

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
  1. `/agents` lists the 20 Squad agents as project (`•`) entries, with no "Skipping agent file"
     warnings.
  2. Startup shows the `conductor` skill collision with the global file skipped.
  3. `/skill:conductor` loads the Squad conductor.
- **If the owner declines:** record the declined check as a residual risk on this plan and proceed
  to T9.
- **Depends on:** T7.

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
T5 ─┬─► T6 (docs) ─────────────────────────────┐
    └─► T7 (verify) ─► T8 (live, owner-gated) ─┴─► T9 (council) ─► T10 (closeout) ─► T11 (PR)
```

**MAX_CONCURRENCY: 2.** The parallel pairs are T2 with T3, and T6 with T7 (or T6 with T8).

- **T1 with T2 or T3:** these are not parallelised. The build must compile for T4's GREEN evidence,
  and a T2 or T3 test that references `PiRenderer` would break it.
- **Q1:** gates T2 and T5, because it changes emitted keys. T1, T3, and T4 do not depend on it. Resolved: A.
- **Q2:** gates only T10. Resolved: A (widen `docs/todo/claude-renderer-ask-narrowing.md` at closeout).

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

## 11. Out of scope

- `--global` Pi installs that map to `~/.pi/agent/agents/` and `~/.pi/agent/skills/`.
- Writing or merging `.pi/subagents.json` or `.pi/settings.json`.
- `squad doctor` detection of the pi-subagents extension.
- Emitting `thinking`, `max_turns`, `color`, `memory`, `isolation`, `skills`, `prompt_mode`,
  `disallowed_tools`, or `run_in_background`.
- Adding `pi:` values to `models.yml`.
- Changes to `capabilities.yml`, canonical agents, or the `ask`-narrowing fix itself (Q2 records it
  only).
- `HarnessKind` and ContextHygiene agent linting for Pi.
- KyberDash's Pi provider.
- Other pending targets: `opencode`, `kilo`, `warp`, `factory`.
- Refreshing the repository's stale self-deployment.

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
- **G7:** How `SquadLifecycleService` roots rendered paths for `--global` was not traced for Pi, which
  is out of scope.
- **G8:** The architect cannot run `docs validate` or `docs drift` in this environment. The
  conductor reported zero findings from both on the saved Draft; it reruns both on this Ready save.
