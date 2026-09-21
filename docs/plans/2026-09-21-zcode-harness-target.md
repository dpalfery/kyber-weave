---
id: plans/2026-09-21-zcode-harness-target
title: Add ZCode as a Kyber-Squad harness target
doc-type: plan
status: current
owner: dpalfery
last-reviewed: 2026-09-21
component: KyberSquad
---

# Add ZCode as a Kyber-Squad harness target

**Status:** Ready
**Date:** 2026-09-21
**Goal:** Declare `zcode` as a Kyber-Squad target and register a `ZCodeRenderer` so
`kyber-weave squad install --target zcode` and `--global --target zcode` both produce a
working ZCode deployment — native subagents, native skills, and the conductor lowered to a
native slash command.

---

## 1. Problem / Motivation

ZCode is Z.ai's coding agent harness: an Electron desktop application with an embedded
headless CLI, and the official harness for GLM-5.3. It is not on the Kyber-Squad roster, so
`squad install --target zcode` fails closed in `SquadRendererRegistry`'s coverage gate before
any asset is downloaded.

ZCode is a strong fit for the canonical Squad model because, unlike most targets on the
roster, it has three distinct primitives that map cleanly onto what Squad already expresses:
user- **and** project-scoped subagents, user- and project-scoped skills, and user- and
project-scoped slash commands.

---

## 2. Source of truth

**Every fact in this plan was verified against the ZCode source, not its published
documentation**, because the two disagree on the single most important point.

- Source: [`zai-org/ZCode`](https://github.com/zai-org/ZCode), Apache-2.0, **v3.14.0**
  (`package.json`), read 2026-09-21.
- The published [subagents page](https://zcode.z.ai/en/docs/subagents) states *"The current
  Beta manages global / user-level subagents stored under `~/.zcode/agents/`. Creating or
  editing workspace / project-level subagents from Settings is not available yet."* **That
  statement is stale.** `apps/zcode-cli/packages/bootstrap/src/subagents.ts` loads a
  `project` root, and `packages/services/src/subagents/subagentStorage.ts` exports
  `resolveWorkspaceSubagentRoot`, which `subagentsService.ts` uses to list, create, and edit
  workspace-scope subagents in the desktop settings UI.

Where this plan cites a behaviour, it cites the file that implements it. Re-verify against a
newer ZCode before treating any of it as current.

### 2.1 Discovery roots

| Primitive | Global | Project | Implementation |
|---|---|---|---|
| Subagents | `$ZCODE_STORAGE_DIR/agents/<name>.md` (default `~/.zcode`) | `<repo>/.zcode/agents/<name>.md` | `bootstrap/src/subagents.ts` |
| Skills | `~/.zcode/skills/<name>/SKILL.md` | `<repo>/.zcode/skills/<name>/SKILL.md` | `adapters/src/skills/roots.ts` |
| Commands | `~/.zcode/commands/<name>.md` | `<repo>/.zcode/commands/<name>.md` | `adapters/src/commands/roots.ts` |

`ZCODE_STORAGE_DIR` reaches the agent root through `config.storage.dir`
(`adapters/src/config/env-config.adapter.ts` → `bootstrap/src/app/create-app.ts:202`). The
same value can also be set in `~/.zcode/cli/config.json`, which no environment read can see —
recorded as a known limitation, not solved here.

`.agents/skills/` and `.agents/commands/` are **also** ZCode roots at both scopes
(`skillRootsForBase`, `commandRootsForBase`). A repository that already carries an
Antigravity deployment therefore exposes those skills to ZCode too; within one scope
`.zcode` is registered first.

### 2.2 Precedence, and why it differs per primitive

- **Subagents: project wins.** Roots load user-then-project, and
  `normalizeAgentProfiles` collapses them with `active.set(profile.name, profile)` —
  last write wins.
- **Skills: user wins.** `discoverSkills` dedupes by **path**, not name, so a same-named
  user and project skill both load; `loadSkill` then takes the first by name from a
  stable sort, which is the lower-priority (user) root.

This asymmetry is upstream behaviour. Squad does not attempt to correct it; the onboarding
note tells an operator to pick one scope per repository.

### 2.3 Agent frontmatter

`parseAgentProfileFromMarkdown` (`core/src/subagent/profile.ts`) accepts `name`,
`description`, `model`, `thoughtLevel`, `color`, `tools`, `disallowedTools`, `skills`,
`permissionMode`, `maxTurns`, `background`, `injectAgentsMd`, `mcpServers`. `name` and
`description` are required; every other key is optional and unknown keys are ignored.

- Reserved names: `general-purpose`, `Explore` (`RESERVED_AGENT_NAMES`).
- **`permissionMode` is stripped from project-scope profiles** by
  `sanitizeProjectAgentProfile`: repository content may not raise a child runtime to
  bypass/yolo. Squad never emits the key, so this is alignment rather than a constraint.
- Omitting `tools` grants every built-in tool. An explicitly empty list (`tools: []`)
  grants none. The renderer therefore always emits `tools`, for the same reason
  `ClaudeRenderer` and `PiRenderer` always emit theirs.

### 2.4 Two parser hazards the renderer must respect

**Frontmatter is not YAML.** `profile-frontmatter.ts` is a hand-rolled line parser:
`^([A-Za-z][A-Za-z0-9_-]*)\s*:\s*(.*)$` per line, with an empty value opening a `- item`
list. Block scalars, folded scalars, and wrapped lines are silently dropped — a
`description: >-` yields the literal string `>-`. Every emitted value must be a single
physical line.

`unquoteScalar` slices the outer quote pair without unescaping, so this renderer matches
ZCode's own writer (`serializeSubagentMarkdown` → `formatYamlScalar`/`escapeYamlString`):
plain scalar when safe, otherwise double-quoted with `\\`, `\"`, `\n`, `\r` escaped. A
description containing a literal `"` round-trips through ZCode with the backslash visible.
That is upstream's own behaviour for its own output; this renderer reproduces it rather than
emitting a cleverer encoding that only ZCode's reader would accept.

**`.zcode/agents/` is scanned recursively.** `listMarkdownFiles` walks subdirectories and
collects every `*.md`/`*.markdown`. `.zcode/commands/` is scanned recursively too
(`scanMarkdownFiles`, `MAX_SCAN_DEPTH = 12`), and a command's name is derived from its
relative path with separators mapped to `:`. This is what D3 below turns on.

### 2.5 Tool vocabulary and the delegation ceiling

The built-in tool surface is Claude-shaped: `Read`, `Grep`, `Glob`, `Bash`, `Edit`, `Write`,
`WebFetch`, `WebSearch`, `TodoWrite`, `Agent`, `Skill` and others
(`core/src/tool/provider-visible-order.ts`). MCP tools use `mcp__<server>__<tool>`.

`toolNameFromSpec` **truncates every specifier at `(`**. `Agent(architect, research-agent)`
is stored as bare `Agent`; `Bash(git:*)` as bare `Bash`. ZCode has no runtime-enforced
delegation roster — no `allowed_subagents` equivalent — so a canonical `delegates-to` roster
cannot be expressed as a permission, only as instruction text in the body.

---

## 3. Approved decisions

- **D1 (Target, tokens, marker, global root).**
  - Target `SquadTarget.ZCode`, canonical token `zcode`.
  - Strong detection marker: `.zcode/`.
  - Global root: `ZCODE_STORAGE_DIR`, default `~/.zcode` — a two-tier
    `ResolveWithOverride` chain like Claude's and Codex's.
  - Project agent path `.zcode/agents/<name>.md`; skill path
    `.zcode/skills/<name>/SKILL.md`; command path `.zcode/commands/<name>.md`. Under
    `--global` the `.zcode/` prefix strips and the same three subtrees hang off the
    resolved global root.

- **D2 (Conductor lowers to a slash command, not a skill).** ZCode has no primary-agent
  primitive — `ZCODE_AGENT_MODE_OPTIONS` is a closed set of permission modes
  (`build`, `edit`, `plan`, `yolo`), not a selectable agent. Unlike every other target on
  the roster, ZCode does have project-scoped slash commands, so a
  `SquadInvocation.Primary` agent whose fallback profile declares
  `no-primary-agent: skill` renders at `.zcode/commands/<name>.md` and is invoked as
  `/<name>`. It records `role-skill-fallback` (the fallback profile's declared outcome,
  reached through a command rather than a skill) and `permission-not-expressible` (a
  command's `allowed-tools` is not the canonical capability lattice, and its
  `delegates-to` roster is instruction-only). `no-primary-agent: omit` emits nothing and
  records `omitted`.

  Command frontmatter uses **kebab-case** keys, unlike agents' camelCase:
  `allowed-tools`, `argument-hint`, `description`, `disable-noninteractive`, `model`,
  `skills` (`adapters/src/commands/index.ts`). An unknown key is a warning diagnostic, so
  the renderer emits only keys from that set.

- **D3 (Agent and command resources are rewritten into the skills tree).** Because both
  `.zcode/agents/` and `.zcode/commands/` are scanned recursively, projecting an agent's
  resource closure beside its principal is actively harmful, not merely noisy:
  `.zcode/commands/conductor/references/plan-path.md` registers as a real command named
  `conductor:references:plan-path`, since a command with no frontmatter falls back to its
  first body line for a description. The `.zcode/agents/` equivalent is a
  `agent_missing_frontmatter` diagnostic per resource on every start.

  So for **agents and the lowered command only**, the resource closure is projected under
  `.zcode/skills/<owner>/…` and the owner's authored links are rewritten to reach it.
  `.zcode/skills/<owner>/` without a `SKILL.md` is not a skill: `scanSkillFilesUnderRoot`
  only admits a directory that contains one.

  The rewrite is driven by the declared resource closure, never by a regex over prose: for
  each resource whose path relative to the owner's source directory is `<p>`, occurrences
  of `<p>` as a Markdown link target become `../skills/<p>`. Three canonical agents carry
  resources today — `architect`, `conductor`, `task-reviewer` — and all three link as
  `<agent-name>/references/<file>.md`, so `architect/references/plan-authoring.md` becomes
  `../skills/architect/references/plan-authoring.md`. The relative form is identical under
  both scopes.

  **Skill** resources keep the ordinary `SquadResourceProjection` treatment beside their
  `SKILL.md`; the skill scanner is one level deep and never mistakes them for skills.

  This is a deliberate, target-local exception to the architecture's "authored relative
  links resolve verbatim in the deployed tree" rule. ZCode is the only target that rewrites
  a body, and it does so because the alternative is emitting phantom commands. Recorded as
  a `resource-links-rewritten` degradation per owner so the deviation is visible in the
  receipt rather than implicit.

- **D4 (Capability lowering).** Only `allow` grants a tool; `ask` and `deny` both withhold,
  because ZCode has no per-capability prompt and `tools` is a binary allow-list. `ask`
  records `safety-narrowed`.

  | Capability | ZCode tools |
  |---|---|
  | `filesystem.read` | `Read` |
  | `filesystem.search` | `Grep`, `Glob` |
  | `filesystem.write` | `Edit`, `Write` |
  | `process.execute` | `Bash` |
  | `network.read` | `WebFetch`, `WebSearch` |
  | `network.publish` | *(none)* |
  | `delegate` | `Agent` |

  `network.publish` has no built-in tool: an `allow` records
  `permission-not-expressible`. `delegate: allow` emits bare `Agent` and records
  `permission-not-expressible` naming the roster, because §2.5's truncation means the
  roster cannot be enforced — the same shape as `PiRenderer`'s empty-roster case, where an
  unenforceable grant is recorded rather than silently claimed. `TodoWrite` is granted
  unconditionally; it writes no file and executes nothing.

- **D5 (Model profiles).** Add a `zcode` key to `models.yml` and its schema:
  `deep-planning: glm-5.3`, and `glm-5.3-flash` for `fast`, `general`, `orchestration`,
  and `reviewer`. Both ids are canonical `modelId` values in ZCode's
  `config/provider/zcode-builtin.json`. Note that `deep-planning` also carries
  `bug-crusher-investigator` and `sql-database-architect`, not only `architect`.

- **D6 (Skill frontmatter).** Emit `name`, `description`, `license` only.
  `SAFE_FRONTMATTER_KEYS` for skills is `name`, `description`, `when_to_use`, `license`,
  `metadata`, and **any key outside that set clears `safeToAutoLoad`**, disabling implicit
  invocation. Descriptions are capped at 1024 characters (a longer one is dropped whole,
  not truncated) and a body over 100 KB is truncated on load — both asserted in tests
  against the canonical corpus.

---

## 4. Scope

**In:** target declaration, marker, global root, `ZCodeRenderer`, registry output-path and
validation wiring, CLI composition, packer exclusion, doctor coverage, `models.yml` and its
schema, contract/lifecycle/global-root/CLI tests, and documentation alignment including
ADR 0020.

**Out:** ZCode plugin packaging (`.zcode-plugin/plugin.json`), hooks, MCP server projection,
and reading `storage.dir` from `~/.zcode/cli/config.json`. Each becomes a todo rather than
scope creep.

---

## 5. Verification

The declared gate suite (`kyber-weave review gates .`) plus:

- a `ZCodeRendererContractTests` suite covering agent/skill/command layout at both scopes,
  the tools lowering table, every degradation code, single-line frontmatter values, the
  1024-character description cap, the resource rewrite, and the fail-closed paths;
- `SquadGlobalRootTests` for `ZCODE_STORAGE_DIR` and its `~/.zcode` default;
- `SquadTargetResolutionTests` for the `.zcode/` marker;
- `docs validate` and `docs drift` at zero findings.

---

## Related

- [Kyber-Squad architecture](../kyber-squad/architecture.md) — §8 rendering
- [ADR 0019](../adr/0019-pi-native-subagents-and-primary-lowering.md) — the primary-agent
  lowering precedent this plan follows and departs from
- [Renderer coverage](../todo/kyber-squad-renderer-coverage.md)
