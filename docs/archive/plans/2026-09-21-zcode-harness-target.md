---
id: archive/plans/2026-09-21-zcode-harness-target
title: Add ZCode as a Kyber-Squad harness target
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-09-24
component: KyberSquad
---

# Add ZCode as a Kyber-Squad harness target

**Status:** Archived
**Archive Date:** 2026-09-24
**Closeout:** Delivered by #100 (`ZCodeRenderer`); archived 2026-09-24 when the open-work gate found it still marked Ready.
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
same value can also be set in `~/.zcode/cli/config.json`, and `createConfig` layers the two —
system defaults, user config file, project config files, then `ZCODE_*` environment
variables — so the environment outranks the file. The resolver honours both tiers; see D1.

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
- Omitting `tools` grants every built-in tool — **and so does `tools: []`**. Both reduce to
  an empty `request.allowedTools`, which `resolveSubagentToolAllowlist` reads as
  `inheritsAvailableTools`. The renderer therefore always emits a **non-empty** `tools`, for
  a sharper version of the reason `ClaudeRenderer` and `PiRenderer` always emit theirs: here
  the restrictive-looking value is the permissive one. See D4a.

### 2.4 Two parser hazards the renderer must respect

**Frontmatter is not YAML.** `profile-frontmatter.ts` is a hand-rolled line parser:
`^([A-Za-z][A-Za-z0-9_-]*)\s*:\s*(.*)$` per line, with an empty value opening a `- item`
list. Block scalars, folded scalars, and wrapped lines are silently dropped — a
`description: >-` yields the literal string `>-`. Every emitted value must be a single
physical line.

`unquoteScalar` slices the outer quote pair without unescaping, so for agents and commands
this renderer matches ZCode's own writer (`serializeSubagentMarkdown` →
`formatYamlScalar`/`escapeYamlString`): plain scalar when safe, otherwise double-quoted with
`\\`, `\"`, `\n`, `\r` escaped. A description containing a literal `"` would round-trip with
the backslash visible — upstream's own behaviour for its own output, and unavoidable on those
two readers.

**Skills escape the hazard entirely.** Their adapter *does* support block scalars, and the
desktop skill service parses skill frontmatter with a real YAML parser, so a skill description
is emitted as a folded block scalar and round-trips losslessly. See D6. The only canonical
description containing a `"` is a skill, so the artifact does not arise in practice — pinned
by a test.

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
  - Global root: `ZCODE_STORAGE_DIR`, then `storage.dir` from `~/.zcode/cli/config.json`,
    then `~/.zcode`. This is the only target whose root can come from a file, because it is
    the only one whose harness resolves the value through a layered runtime config:
    `createConfig` layers system defaults, the user config file, project config files, then
    `ZCODE_*` environment variables, so the environment outranks the file. Project config
    files sit between the two and are deliberately not read — honouring them would make a
    `--global` root depend on the working directory, which is the one thing `--global` exists
    not to do. `SquadGlobalRoots` takes a file-reading port for this; the composition root
    supplies it, per Core's rule that Core does not construct its own collaborators.
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
  unenforceable grant is recorded rather than silently claimed.

  **`TodoWrite` and `Skill` are granted unconditionally**, matching `ClaudeRenderer`'s own
  ungoverned base. Neither can broaden a canonical decision: `TodoWrite` writes no file and
  executes nothing, and `Skill` only opens the skill tree this renderer itself deploys —
  without it the 24 deployed skills would be unreachable. The `skills` frontmatter key is
  not emitted, because `resolveSubagentSkillPort` reads an absent list as "every discovered
  skill" and a present one as a hard filter, and the canonical model declares no per-agent
  skill roster.

- **D4a (An empty tool list is not an empty grant).** `resolveAllowedTools` reduces both a
  missing `tools` key and an explicit `tools: []` to an empty `request.allowedTools`, and
  `resolveSubagentToolAllowlist` treats that as `inheritsAvailableTools` — the child then
  receives every tool the parent has, plus parent MCP via `shouldBorrowParentMcp`. Emitting
  `[]` would therefore be maximal widening dressed as maximal restriction. The key is always
  emitted carrying at least the ungoverned base, and a resolved grant of nothing is a
  fail-closed render error rather than an empty list.

- **D4b (MCP is granted by fully qualified tool name).** `registerMcpTools` admits a tool
  only on an exact set membership test against the allow-list, with no wildcard expansion, so
  `ClaudeRenderer`'s `mcp__<server>__*` selector registers nothing here — and worse, the
  selector is still collected into `requiredServerNames`, imposing a connected-server
  requirement while granting no tool. The renderer therefore emits the fully qualified
  `mcp__<server>__<tool>` names declared by `toolchain.yml`'s `required-mcp-tools`, which
  `registerMcpTools` matches exactly.

  That makes the grant real and makes it a hard requirement:
  `validateSubagentMcpRequirements` raises a configuration error when a required tool's server
  is not connected or the tool is absent from the parent startup snapshot. **This is the
  intended behaviour** — `squad doctor` fails against the same declared roster, so the
  breakage surfaces at diagnosis time rather than mid-run.

  The roster lives in canonical source rather than in the renderer because it is an external
  contract that drifts — context7 renamed `get-library-docs` to `query-docs` — and because a
  harness matching by exact name breaks on a rename instead of degrading. One declared roster
  also means the renderer and the doctor check cannot disagree. `mcpServers` is still never
  emitted: it would scope the borrowed connection set, but the tool allow-list already decides
  what the model can see, and naming a server there only adds a second failure mode.

  A pure orchestrator gets no MCP, matching `ClaudeRenderer`'s carve-out, and records
  `permission-not-expressible` naming the withheld servers. The declared roster, verified
  2026-09-21 by listing each server's tools over MCP:

  | Server | Tools |
  |---|---|
  | `codegraph` | `codegraph_explore` |
  | `context7` | `resolve-library-id`, `query-docs` |
  | `kyber-weave` | `docs_analysis_candidates`, `docs_explore`, `docs_for_symbol`, `docs_glossary` |

- **D4c (`squad doctor` fails on a ZCode install missing those servers).** Reading the same
  declared roster, doctor inspects ZCode's config tiers — the user file at
  `<storage>/cli/config.json`, then `<dir>/zcode.json` and `<dir>/.zcode/config.json` for each
  directory from the working directory up to the git worktree root — and errors naming any
  required server no tier declares under `mcp.servers`. It fails only where ZCode is in play,
  keyed on the same `.zcode/` marker target resolution uses, so a repository that never
  deploys to ZCode is reported as skipped rather than failed.

- **D5 (Model profiles).** Add a `zcode` key to `models.yml` and its schema:
  `deep-planning: glm-5.3`, and `glm-5.3-flash` for `fast`, `general`, `orchestration`,
  and `reviewer`. Both ids are canonical `modelId` values in ZCode's
  `config/provider/zcode-builtin.json`. Note that `deep-planning` also carries
  `bug-crusher-investigator` and `sql-database-architect`, not only `architect`.

- **D6 (Skill frontmatter, and the one place a block scalar is safe).** Emit `name`,
  `description`, `license` only. `SAFE_FRONTMATTER_KEYS` for skills is `name`,
  `description`, `when_to_use`, `license`, `metadata`, and **any key outside that set clears
  `safeToAutoLoad`**, disabling implicit invocation. Descriptions are capped at 1024
  characters (a longer one is dropped whole, not truncated) and a body over 100 KB is
  truncated on load — both asserted in tests against the canonical corpus.

  A skill's `description` is emitted as a **folded block scalar** (`>-` plus one indented
  line) rather than a quoted one. The skill adapter's `parseFlatYaml` supports block scalars
  (`parseBlockScalarStyle` / `readBlockScalar`) and the desktop skill service parses skill
  frontmatter with a real YAML parser, so the form needs no quoting and no escaping and an
  embedded `"` survives intact on both readers. This removes the §2.4 escape artifact
  wherever it could actually bite: the only canonical description containing a `"` is a
  skill. Agents and commands cannot use it — a source comment in ZCode's own skill adapter
  records that the agent side reads only the top-level `description: >` and skips the
  indented continuation — so they keep the single-line quoted form. No canonical agent or
  command description contains a character that form would escape, which a test pins.

---

## 4. Scope

**In:** target declaration, marker, global root, `ZCodeRenderer`, registry output-path and
validation wiring, CLI composition, packer exclusion, doctor coverage, `models.yml` and its
schema, contract/lifecycle/global-root/CLI tests, and documentation alignment including
ADR 0021.

**Out:** ZCode plugin packaging (`.zcode-plugin/plugin.json`) only, and only because it is a
different deployment model rather than remaining renderer work: a plugin is enabled as one
unit instead of per-file, which is what Squad's receipt, drift detection, and transactional
rollback are built on, and `loadPluginAgentProfiles` would namespace every agent
`<plugin>:<agent>`. Recorded in [the todo](../../todo/zcode-plugin-packaging.md) as a decision
for the owner.

Two things that first looked like scope are not. **Hooks** have no canonical source —
`products/kyber-squad/` declares no hook artifact and `SquadSource` models none — so there is
nothing to render. **MCP** is granted, under D4b, and gated by doctor under D4c.

---

## 5. Verification

The declared gate suite (`kyber-weave review gates .`) plus:

- a `ZCodeRendererContractTests` suite covering agent/skill/command layout at both scopes,
  the tools lowering table, the ungoverned base, the never-empty tool list, the qualified MCP
  grant and the pure-orchestrator withholding, every degradation code, single-line agent/command frontmatter
  values, the lossless skill block scalar, the 1024-character description cap, the resource
  rewrite, and the fail-closed paths;
- `ZCodeMcpConfigurationTests` for the doctor check's config tiers, worktree-root walk,
  partial configuration, and tolerance of unparseable or JSONC configuration;
- `SquadSourceTests` for the `required-mcp-tools` roster: ordering, empty-server rejection,
  duplicate rejection, and rejection of names a harness would rewrite;
- `SquadGlobalRootTests` for `ZCODE_STORAGE_DIR`, the `storage.dir` config tier and its
  precedence, `~/`-expansion, malformed-config fallback, relative-path rejection, and the
  `~/.zcode` default;
- `SquadTargetResolutionTests` for the `.zcode/` marker;
- `docs validate` and `docs drift` at zero findings.

---

## Related

- [Kyber-Squad architecture](../../kyber-squad/architecture.md) — §8 rendering
- [ADR 0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) — the primary-agent
  lowering precedent this plan follows and departs from
- [Renderer coverage](../todo/kyber-squad-renderer-coverage.md)
