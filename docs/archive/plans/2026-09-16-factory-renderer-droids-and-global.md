---
id: archive/plans/2026-09-16-factory-renderer-droids-and-global
title: Correct Factory droid paths and enable Factory --global
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-09-16
component: KyberSquad
development-mode: test-first
keywords:
  - factory
  - factory-droids
  - FactoryRenderer
  - SquadGlobalRoots
  - .factory/droids
---

# Correct Factory droid paths and enable Factory `--global`

**Status:** Archived
**Archive Date:** 2026-09-16
**Date:** 2026-09-16
**Development mode:** test-first (conductor default; the user did not opt out)
**Source:** shipped `FactoryRenderer` failed acceptance; archived plan
[2026-09-14-factory-native-renderer](2026-09-14-factory-native-renderer.md)
**Goal:** Deploy Squad agents as Factory custom droids at the paths and YAML schema
documented on docs.factory.ai (2026-09-16), and make `--global` write under `~/.factory`.

This Draft is decision-complete. Every layout and YAML field below is taken from live
Factory docs fetched 2026-09-16, or from this repository's existing Squad renderer policy
where Factory is silent. The conductor presents **approve and execute**.

---

## 1. Problem and goal

The Factory renderer shipped from the archived 2026-09-14 plan. Acceptance failed:

1. **Wrong project folder.** Output is `.factory/agents/<name>.md`. Factory loads custom
   droids from `.factory/droids/<name>.md`.
2. **`--global` does not work.** `SquadGlobalRoots` throws for Factory;
   `squad doctor --global` skips it; `FactoryRenderer` ignores `SquadDeploymentScope`.

When this work is done:

- Project: `.factory/droids/<name>.md` and `.factory/skills/<name>/SKILL.md`. Never
  `.factory/agents/`.
- Global: `droids/<name>.md` and `skills/<name>/SKILL.md` under `~/.factory`.
- Droid YAML matches Factory's documented schema, with Copilot/Claude non-broadening
  emission (never omit `tools`).
- `squad doctor --global` scans Factory.
- Architecture, onboarding, requirements, and coverage stop teaching `.factory/agents/`.

Standards: **<csharp-coding-standard>** and **<test-coding-standard>** in the repository
root `AGENTS.md`.

---

## 2. Approved decisions

Provenance: user 2026-09-16 (Q1-A, Q2-A, Q3 schema) where live docs agree; live docs win
on conflict (none found). Factory pages fetched 2026-09-16:
https://docs.factory.ai/harness/subagents and https://docs.factory.ai/harness/skills.
Kyber-Weave emission rules cite `CopilotRenderer` and `ClaudeRenderer` remarks plus
KS-002 (`docs/kyber-squad/requirements.md`).

### Paths (Factory docs)

| Id | Decision | Source |
|---|---|---|
| D-path-project-droid | Project custom droids are Markdown files `.factory/droids/<name>.md` (top-level `.md` in `droids/`, not a subdirectory). Never `.factory/agents/`. | subagents "Where they live"; example `.factory/droids/deep-analyzer.md` |
| D-path-personal-droid | Personal droids are `~/.factory/droids/<name>.md`. | subagents: "Personal droids live in `~/.factory/droids/`"; example `~/.factory/droids/security-sweeper.md` |
| D-path-project-skill | Project skills are `.factory/skills/<name>/SKILL.md` (`SKILL.md` entry point, not `skill.mdx`). | skills "Create your first skill" and anatomy |
| D-path-personal-skill | Personal skills are `~/.factory/skills/<name>/SKILL.md`. | skills table: Personal `~/.factory/skills/` |
| D-override | Project `.factory/` wins over personal `~/.factory/` on the same droid or skill name. | subagents: "When a project droid and a personal droid share the same name, the project definition wins." Skills: project/folder-specific precede personal. |
| D-compat | Compatibility skill trees `~/.agents/skills/` and `~/.agent/skills/` (and repo `.agents/skills/`, `.agent/skills/`) exist in Factory docs. Squad does not write them. | skills "Where skills live"; not a Squad output path |
| D-global-root | `--global` physical root is `~/.factory` with no environment override. No machine-wide (all-users) path is documented. | personal paths above; sibling `ResolveWithOverride(null, …)` used by Antigravity |
| D-prefix-strip | Global relative paths strip the `.factory/` prefix (`droids/…`, `skills/…`), matching Claude/Kilo/Pi `ResolvePrefixedDirectory`. | Factory is silent on Squad `--global`; sibling renderer policy |
| D-no-settings | Do not write `~/.factory/settings.json`, `.factory/settings.json`, `.factory/mcp.json`, or `~/.factory/mcp.json`. | Factory documents those files; Squad install does not own them (sibling Pi/Claude do not write harness settings) |

The fetch rendered some project paths as ` /.factory/droids/` (leading space). Same-page
examples are `.factory/droids/<name>.md`. Q1-A uses the examples. No third path.

### Droid YAML (Factory docs — the Q3 contract)

Each droid is Markdown with YAML frontmatter and a non-empty system-prompt body.

| Field | Factory rule (fetched 2026-09-16) |
|---|---|
| `name` | Required. `^[a-z0-9-_]+$`. |
| `description` | Optional but recommended. >500 characters warns. |
| `model` | `inherit` (default) or omit; or a public model ID; or `custom:<key>`. Blocked/unconfigured pins fall back to the parent session (Factory, not Squad). |
| `reasoningEffort` | Optional `low` \| `medium` \| `high`. Ignored when `model` is `inherit`. |
| `tools` | Omit = allow **all** tools. Category string (e.g. `read-only`) **or** YAML array of IDs. `tools: all` is a validation error. IDs are case-sensitive. |
| `mcpServers` | Omit = inherit parent MCP. `[]` = none. Whitelist names from `.factory/mcp.json` or `~/.factory/mcp.json`. MCP tool IDs may also appear in `tools`. |

Auto-injected (do not list): `TodoWrite`, `Skill`. Forbidden (validation error):
`ExitSpecMode`, `GenerateDroid`.

Documented categories: `read-only` (`Read`, `LS`, `Grep`, `Glob`); `edit` (`Create`,
`Edit`, `ApplyPatch`); `execute` (`Execute`); `web` (`WebSearch`, `FetchUrl`); `mcp`
(dynamically populated).

Subagents are non-interactive: `AskUser` is disabled; a subagent cannot spawn subagents
(`Task` is not available to it).

### Skill YAML (Factory docs)

Required: `name`, `description`. Optional: `allowed-tools` (metadata, **not** a runtime
sandbox), `enabled`, `user-invocable`, `disable-model-invocation`, `license`,
`compatibility`, `metadata`, `version`. Older skill `tools` is deprecated. Squad does not
rely on `allowed-tools` as a security boundary (Factory: "Do not rely on `allowed-tools`
as a security boundary").

### Squad emission (repo policy, valid Factory YAML)

Factory is silent on how a *canonical Squad capability profile* becomes `tools`. This
repository already decided that question for Copilot and Claude: omitting `tools` is
silent widening (CopilotRenderer remarks: omitting the key means all tools;
ClaudeRenderer remarks: always emit an explicit list). KS-002: unsupported `ask` narrows
to `deny`; unenforceable constraints must not broaden.

| Id | Decision |
|---|---|
| D-emit-tools | **Never omit `tools`.** Factory documents omit = allow all. Always emit an explicit YAML array of documented IDs (or a category string that is exactly the granted set). Never emit `tools: all`. |
| D-allow-only | Only `allow` grants a Factory tool. `deny` withholds. `ask` withholds and records `safety-narrowed` (Factory disables `AskUser` on subagents; same as Copilot/Claude). |
| D-capability-map | Allow-only map onto **documented** Factory IDs, in this fixed order: `Read`, `LS`, `Grep`, `Glob`, `Create`, `Edit`, `ApplyPatch`, `Execute`, `WebSearch`, `FetchUrl`. `filesystem.read` → `Read`. `filesystem.search` → `LS`, `Grep`, `Glob` (Squad's read/search split; Claude maps search to Grep/Glob; Factory documents `LS` in the same exploration set). `filesystem.write` → `Create`, `Edit`, `ApplyPatch` (Factory `edit`; docs add `ApplyPatch` whenever `Edit` is enabled under `inherit`). `process.execute` → `Execute`. `network.read` → `WebSearch`, `FetchUrl`. |
| D-unmapped | `network.publish`: no documented Factory tool → withhold web-publish, record `permission-not-expressible`. `delegate`: Factory withholds `Task` from subagents → do not emit `Task`, record `permission-not-expressible`. |
| D-no-list | Do not list `TodoWrite` or `Skill` (Factory auto-injects). Do not list `ExitSpecMode` or `GenerateDroid`. |
| D-mcp | Squad does not own Factory MCP server names. Omitting `mcpServers` inherits parent MCP (widening). Emit `mcpServers: []` (documented "excludes every MCP server") and record `permission-not-expressible` that parent MCP was not inherited. Do not invent server names or MCP tool IDs. |
| D-model | Resolve `models.yml` harness key `factory`; omit the field when missing or `inherit` (Factory default). Do not invent public IDs. |
| D-reasoning | Omit `reasoningEffort` (optional; ignored on inherit; no verified per-agent value in `models.yml`). |
| D-no-invented-keys | Do not emit `permission`, `permissions`, or any key not in Factory's droid table. |
| D-skill-fm | Skills emit `name` and single-line `description` only. Do not emit `license` (optional in Factory; current Factory renderer omits it; Kilo's `license: MIT` is Kilo's schema, not Factory's requirement). Do not emit `allowed-tools` (not a sandbox). Do not emit deprecated skill `tools`. |
| D-single-projection | Profile-declared shared identities suppress skill projections (native registry rule; unchanged). |
| D-resources | Linked resources via `SquadResourceProjection.Append` beside the principal (Squad, not Factory). |
| D-doctor | Keep `ArgumentOutOfRangeException` skip as a future-target guard. After D-global-root, Factory is scanned; tests must not expect `no verified global root` for factory. |
| D-stale-agents | Update deletes receipt-owned `.factory/agents/` paths the new render no longer emits. No sweeper for unmanaged leftovers (Pi U9). |

Archived 2026-09-14 D1 (`.factory/agents/`) and D3 (omit `tools` because unverified) are
replaced. D2 (name/description/model) is subsumed by the Factory schema. D5–D7 stand.

Process: plan not spec; `development-mode: test-first`.

---

## 3. Draft decision ledger

All items ANSWERED. No OPEN questions. Factory-specified items were not re-asked.

| Id | Status | Answer | Provenance |
|---|---|---|---|
| Q1 | ANSWERED | A — `.factory/droids/<name>.md`; never `.factory/agents/` | User 2026-09-16; live subagents docs agree |
| Q2 | ANSWERED | A — `~/.factory`, no env override; prefix-strip `droids/` and `skills/` | User 2026-09-16; live docs agree; prefix-strip from sibling renderers |
| Q3 | ANSWERED | Factory YAML contract in section 2; Squad emission D-emit-tools…D-no-invented-keys | Live subagents docs; Copilot/Claude/KS-002 for non-broadening |

Conflict check: user answers vs live docs — **none**. Live docs win clause unused.

---

## 4. Investigation findings

MCP `docs_explore` missed this worktree corpus (0 documents considered, twice). No
`.codegraph/` here. Factory facts are from the 2026-09-16 page fetches, not memory.

### 4.1 Fetched quotes (2026-09-16)

**https://docs.factory.ai/harness/subagents**

> Personal droids live in `~/.factory/droids/` and follow you across workspaces.
> When a project droid and a personal droid share the same name, the project definition wins.

> `tools` — Omit to allow all tools, use a category string (for example `read-only`), or
> pass an array of tool IDs. Tool IDs are case-sensitive.

> The literal value `tools: all` is rejected. Omit the `tools` field entirely to allow
> every tool.

> `TodoWrite` and `Skill` are always included for every droid … You do not list them.
> `ExitSpecMode` and `GenerateDroid` cannot be enabled by a custom droid.
> Subagents run non-interactively. The `AskUser` tool is disabled … a subagent cannot
> spawn its own subagents (the `Task` tool is not available to it).

> Omitting `mcpServers` keeps the parent session's MCP tool availability.
> Setting `mcpServers: []` excludes every MCP server, even globally configured ones.

**https://docs.factory.ai/harness/skills**

> Put team-shared skills under `.factory/skills/` … The skill entry point must be named
> `SKILL.md`.
> Personal: `~/.factory/skills/`
> Personal compatibility: `~/.agents/skills/**/SKILL.md`, `~/.agent/skills/**/SKILL.md`

No `.factory/agents/` custom-droid path appears on either page. No env-override for
`~/.factory`. No all-users path.

### 4.2 Current code (feat/factory-renderer)

- `FactoryRenderer`: `AgentsDirectory = ".factory/agents"`; no `ResolvePrefixedDirectory`;
  ignores `request.Scope`; omits `tools` (archived D3 — **contradicts** Factory omit=all
  plus Copilot/Claude non-broadening).
- Registry `AgentOutputPath` Factory: `.factory/agents/{name}.md`.
- `SquadGlobalRoots`: Factory throws.
- Doctor `--global`: skips Factory on that throw.
- Tests pin `.factory/agents/`, absent `tools`, and doctor `no verified global root`.

### 4.3 Sibling policy cited

- CopilotRenderer remarks: omitting `tools` is a silent grant of everything.
- ClaudeRenderer remarks: always emit an explicit list; only `allow` grants; `ask` →
  `safety-narrowed`; capability table `filesystem.read`→Read, `filesystem.search`→Grep/Glob.
- Claude/Kilo/Pi: `ResolvePrefixedDirectory` for Global.

### 4.4 Docs still teaching `.factory/agents/`

`docs/kyber-squad/architecture.md` §8, onboarding, requirements (Factory
"unverified mapping omitted"), `docs/todo/kyber-squad-renderer-coverage.md`,
`docs/todo/factory.md`.

---

## 5. Rendering contract

| Scope | Droid | Skill | Physical root |
|---|---|---|---|
| Project | `.factory/droids/<name>.md` | `.factory/skills/<name>/SKILL.md` | install target |
| Global | `droids/<name>.md` | `skills/<name>/SKILL.md` | `~/.factory` |

Droid frontmatter keys ⊆ {`name`, `description`, `model`, `reasoningEffort`, `tools`,
`mcpServers`}. Always `name`, `description`, `tools` (array), `mcpServers: []`. `model`
only when resolved and not inherit. Never `reasoningEffort` today. Body =
`agent.InstructionBody`, LF, trailing newline, `SquadMarkdownDocument.Compose`.

Independent expected `tools` array = union of D-capability-map IDs whose capability is
`allow`, in the fixed order in D-capability-map. Empty grant → `tools: []` (fail-closed;
never omit).

Degradations: `safety-narrowed` per `ask`; `permission-not-expressible` for
`network.publish` (non-deny), `delegate` (non-deny), and MCP-not-inherited.
`InstructionDigest == agent.BodyDigest`.

---

## 6. Test contract

After `dotnet build KyberWeave.sln -c Release`:

- **C1:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~FactoryRendererContractTests"`
- **C2:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadGlobalRootTests"`
- **C3:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadCliCommandTests"`
- **CF:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`

Load `products/kyber-squad` via `SquadSourceLoader.Load`. No hardcoded rosters. No writes
to the real home or the repo tree.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 → T2 | `tests/KyberWeave.Tests/FactoryRendererContractTests.cs` | C1 | (1) Project: every agent at `.factory/droids/<name>.md`; no `.factory/agents/`; skills `.factory/skills/<name>/SKILL.md`; shared identities droid-only; resources beside principals. (2) Global: paths `droids/` or `skills/` only; no leading `.` and no `.factory/`. (3) Each droid: `name`/`description` match canonical; `tools` **present**; value is the independently computed allow-only ID list (section 5) in fixed order; never scalar `all`; never `TodoWrite`/`Skill`/`ExitSpecMode`/`GenerateDroid`; `mcpServers` is `[]`; no `permission`/`permissions`; `model` follows inherit-omit; no `reasoningEffort`; no `license` on droids. (4) Each `ask` → one `safety-narrowed`; each non-deny `network.publish` and `delegate` → `permission-not-expressible`; MCP-not-inherited `permission-not-expressible`; digests match `BodyDigest`; Details never claim a guessed Factory field. (5) Determinism; resource probe under `.factory/droids/`. (6) SupportedTargets / non-Factory guard unchanged. | C1 fails: `.factory/agents/` still emitted; `tools` absent (today's D3); Global still `.factory/…`. Capture before T2. | C1 unchanged assertions pass. Sibling renderer suites unmodified. CF passes. |
| T3 → T4 | `tests/KyberWeave.Tests/SquadGlobalRootTests.cs` | C2 | (1) Factory root with env always set is still `tempHome/.factory` (`FACTORY_HOME` ignored). (2) Default-root InlineData Factory `.factory`. (3) Throw test removed/inverted. (4) Factory-specific global dry-run: `droids/` or `skills/` only — **do not** add Factory to the `agents/`-only theory. (5) Project dry-run: `.factory/droids/` or `.factory/skills/`; never `.factory/agents/`. No writes to `~/.agents` or `~/.agent`. | C2 fails on throw / missing `.factory` row. Capture before T4. | C2 passes; sibling InlineData rows unmodified. CF passes. |
| T5 → T6 | `tests/KyberWeave.Tests/SquadCliCommandTests.cs` | C3 | Doctor `--global` still warns on Pi `agents/architect.md` and does **not** contain `no verified global root`. Optional Factory unmanaged `droids/architect.md` under temp `~/.factory`. | Fails: today's output still has factory + `no verified global root`. | C3 passes. Existing assertions unmodified. CF passes. |

No-test: T7/T10 `docs validate` (+ drift if `.codegraph/` exists); T8 gates; T9 council.

---

## 7. Dispatchable tasks

**T1 RED** (skills: `test-dev`) — `FactoryRendererContractTests.cs` only. Section 6 T1 RED.

**T2 GREEN** (skills: `csharp-dev`) — `FactoryRenderer.cs` (`.factory/droids`,
`ResolvePrefixedDirectory`, Q3 YAML + D-emit-tools mapping, remarks cite both Factory
URLs and Copilot/Claude omit=all); `SquadRendererRegistry.cs` `AgentOutputPath` →
`.factory/droids/{name}.md`. Depends on T1.

**T3 RED** (skills: `test-dev`) — `SquadGlobalRootTests.cs` only. Do not weaken the
sibling `agents/` theory. Section 6 T3 RED.

**T4 GREEN** (skills: `csharp-dev`) — `SquadGlobalRoots.cs`:
`Factory => ResolveWithOverride(null, ".factory")`. Depends on T3.

**T5 RED** (skills: `test-dev`) — `SquadCliCommandTests.cs` doctor `--global`. Disjoint
from T1/T3.

**T6 GREEN** (skills: `csharp-dev`) — `SquadDoctorCommand.cs` skip comment only unless
T5 still fails after T2+T4. Keep the catch. Depends on T5, T4, T2.

**T7** (skills: `docs-dev`) — architecture §8, onboarding (override + `/droids`/`/skills`
inspect), requirements Factory row (explicit tools array + degradations, not "omit
unverified"), coverage checklist, `todo/factory.md` paths; code-review architecture only
if it still says Factory is unimplemented. Depends on T2, T4.

**T8** (skills: `csharp-dev`, `test-dev`) — C1–C3, CF, format, Release build, factory
dry-run shows `.factory/droids/` not `agents/`, `docs validate .`. Depends on T2, T4, T6, T7.

**T9** (skills: `code-review`) — depends on T8.

**T10** (skills: `docs-dev`) — superseded-by sentence on the archived 2026-09-14 plan
(D1 and D3). No `supersedes:` id pointing at `docs/archive/`. Depends on T9.

---

## 8. Dependency graph and MAX_CONCURRENCY

```text
T1 RED ─┐
T3 RED ─┼─ concurrent (three test files)
T5 RED ─┘
   ├─► T2 GREEN (renderer + registry)
   └─► T4 GREEN (SquadGlobalRoots)
          └─► T6 GREEN ─► T7 docs ─► T8 ─► T9 ─► T10
```

**MAX_CONCURRENCY: 3** (T1, T3, T5). Then 2 (T2 ∥ T4). T6 waits for T2+T4+T5. T8–T10 serial.

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| Sibling global theory requires `agents/` | Factory-specific `droids/` test; do not weaken siblings |
| Empty `tools: []` not shown in Factory examples | Fail-closed vs omit-all; corpus today always has at least `filesystem.read: allow` |
| Fetch path ` /.factory/droids/` | Examples `.factory/droids/<name>.md` are authority |
| Receipt-owned `.factory/agents/` | Standard update deletes them; no unmanaged sweeper |
| MCP `[]` disables parent MCP the user wanted | Documented Factory form; KS-002 forbids inherit-all widening; degradation records it |

---

## 10. Out of scope

- Warp renderer / Warp global root.
- Writing settings.json or mcp.json.
- Writing `~/.agents/skills/` or `~/.agent/skills/`.
- Sweeping unmanaged `.factory/agents/`.
- Adding `factory:` model tokens or `reasoningEffort` values.
- Inventing Factory env overrides or all-users paths.
- Live Factory TUI.
- Skill `allowed-tools` as a sandbox.

---

## 11. Verification gates

C1, C2, C3, CF; `dotnet format` verify; Release build `TreatWarningsAsErrors`; project
dry-run `.factory/droids/` not `agents/`; `docs validate .`; docs drift iff `.codegraph/`
exists.

---

## 12. Review

`code-review` once (T9). Weakening a Test-contract assertion returns this plan to Draft.

---

## 13. docs-dev closeout

Confirm architecture/onboarding/requirements/coverage match section 2. Note on the
archived 2026-09-14 plan that D1 and D3 are replaced by this plan. Keep `todo/factory.md`
superseded with corrected paths.

---

## 14. GAPS

- **G1:** MCP docs_explore miss on this worktree; Factory facts from page fetch.
- **G2:** No `.codegraph/` in `/Users/dave/git/personal/kyber-weave-factory`.
- **G3:** Hidden `FACTORY_*` env override unverified; Q2 follows published docs.
- **G4:** Empty `tools: []` not illustrated on the Factory page (corpus likely never hits it).
- **G5:** Fetch leading-space project path; examples used for Q1.
