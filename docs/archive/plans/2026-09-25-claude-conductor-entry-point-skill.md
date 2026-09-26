---
id: archive/plans/2026-09-25-claude-conductor-entry-point-skill
title: Expose the Claude conductor as a /conductor entry-point skill
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-25
development-mode: test-first
---

# Expose the Claude conductor as a /conductor entry-point skill

**Status:** Archived (implementation delivered; live Claude verification deferred)
**Date:** 2026-09-25
**Development mode:** test-first (the default; the user confirmed it through the conductor, 2026-09-25)
**Goal:** Under `squad install --target claude`, make the primary-invocation agent `conductor`
invocable as `/conductor` in Claude Code. It runs in the main conversation, where the Agent
tool is available. The existing `.claude/agents/conductor.md` subagent stays alongside it.
**Approval:** The approve-and-execute gate passed on 2026-09-25, relayed by the parent session.
The user approved execution in test-first mode with Q1-retain=A, Q2-mechanism=A,
Q3-invocation=B, Q4-skill-keys=A and Q5-adr=B. The approval includes the RED waiver for T1 row
(f) and the parent-session live verification (T6) that temporarily updates `~/.claude`. The Draft
was committed at `76077f9`, with `docs validate` and `docs drift` reporting 0 findings.

---

## 1. Problem / Motivation

On Claude, `ClaudeRenderer` renders every canonical agent, the primary `conductor` included, as
a subagent at `.claude/agents/<name>.md`. A subagent is not a slash command. There are three
ways to start one, and none of them is right for the conductor:

- **Automatic delegation or `@agent-conductor`.** Both run the conductor nested, as a subagent.
  In a nested subagent, Claude Code ignores the `Agent(roster)` parentheses.
- **Cloud sessions.** Here `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1`, so a nested subagent has no
  Agent tool at all. A subagent conductor cannot delegate, and delegation is its whole job.
- **`claude --agent conductor`.** This runs it in the main thread, with enforcement. It is a CLI
  launch flag, not something a user reaches from inside a running session.

The conductor has to run in the main thread and be reachable as `/conductor`.

---

## 2. Investigation findings

All facts were verified on branch `claude/epic-hamilton-4ttau7` on 2026-09-25. That branch is
level with `main` and includes #118 (`e46398d`), which set `models.yml` orchestration to
`claude: sonnet` and added `RenderAsync_Claude_ConductorRunsOnSonnet`.

How the facts were gathered:

- Code: CodeGraph and direct reads.
- Governed docs: the Kyber-Weave docs MCP.
- Vendor facts: `code.claude.com/docs/en/skills` and `code.claude.com/docs/en/sub-agents`,
  fetched on 2026-09-25.

Delegation was unavailable (spawn depth 1), so all discovery was done by the architect itself.
Every citation from the intake digest was re-checked against the current tree.

### 2.1 Claude Code primitives (vendor docs, 2026-09-25)

**Skill versus command.**
- "Custom commands have been merged into skills." Both `.claude/commands/deploy.md` and
  `.claude/skills/deploy/SKILL.md` create `/deploy`, and the skill wins when both exist.
- A skill directory takes supporting files, and only `SKILL.md` defines a skill.
- A command file under `.claude/commands/<subdir>/` registers as `<subdir>:<name>`, so
  resources placed beside a command would become phantom commands. This is the ZCode problem
  that [ADR 0021](../../adr/0021-zcode-command-lowering-and-resource-relocation.md) solved by
  relocating resources.
- **Chosen primitive:** a skill at `.claude/skills/conductor/SKILL.md`, run inline (no
  `context: fork`).

**Arguments.** "If you invoke a skill with arguments but no placeholder in the skill's content
receives one, Claude Code appends `ARGUMENTS: <your input>`." `/conductor <path or request>`
therefore works with no change to the body.

**Invocation control.** With `disable-model-invocation: true`, "Only you can invoke the skill".
Per the docs table: "Description not in context, full skill loads when you invoke".

**Turn-scoped keys.** Three keys last only for the turn that invokes the skill:

| Key | Behaviour | Why it cannot hold the profile |
|---|---|---|
| `allowed-tools` | Pre-approves tools; "The grant clears when you send your next message." | It does not restrict which tools exist. |
| `disallowed-tools` | Removes tools from the pool; "The restriction clears when you send your next message." | The conductor is multi-turn, so the restriction lapses at its first decision gate. |
| `model` | "The override applies for the rest of the current turn"; the session model resumes on the next prompt. | It cannot hold the orchestration profile's Claude model. |

`hooks` stay registered "for the rest of the session", which outlives the conductor's use.

**Persistence.** The rendered `SKILL.md` "enters the conversation as a single message and stays
there across later turns". After auto-compaction, Claude Code re-attaches the most recent
invocation of each skill, keeping its first 5,000 tokens. The conductor body is far below that.
Reference files the conductor opened earlier are not re-attached; its input router re-reads the
one it needs.

**Live reload.** Claude Code watches `~/.claude/skills/` and `.claude/skills/` and picks up
added skills without a restart. A top-level skills directory created after the session started
needs a restart. In this environment `/root/.claude/skills/` already exists.

**Scope precedence differs between the two primitives.**
- Skills: "personal over project".
- Subagents: project `.claude/agents/` (priority 3) over user `~/.claude/agents/` (priority 4).

With both a global and a project Squad install, `/conductor` therefore resolves to the global
copy, while `@agent-conductor` and `--agent conductor` resolve to the project copy.

**Main-thread agent.**
- `claude --agent <name>`, or the `agent` setting, makes the main thread take the subagent's
  system prompt, tool restrictions and model.
- The `Agent(agent_type)` allowlist applies only in that mode. In a nested subagent,
  "any type list inside parentheses is ignored".

**Spawn depth.**
- By default a subagent can spawn subagents up to 3 layers below the main conversation.
- `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1` disables nesting entirely.

**Agent directory scanning.**
- `.claude/agents/` is scanned recursively.
- A file with no frontmatter, or with no `name`, "is treated as documentation".
- The conductor's four reference files have neither frontmatter nor links.

**Delegation control.**
- No frontmatter field prevents automatic delegation to a subagent.
- The docs do document an operator setting, `"permissions": {"deny": ["Agent(my-custom-agent)"]}`.

**Not in the docs.**
- How relative links in `SKILL.md` resolve.
- A skill and a subagent sharing a name.

T6 checks both live.

### 2.2 Canonical source

- `products/kyber-squad/agents/conductor.md` declares:
  - `invocation: primary` (`:5`);
  - `model-profile: orchestration` (`:6`);
  - `capability-profile: orchestrator` (`:7`);
  - an 18-agent `delegates-to` roster (`:9`);
  - `fallback: role-skill` (`:10`).
- Its body links `conductor/references/{plan-path,spec-path,intake-path,execution-and-review}.md`
  (`:33-35`, `:49`).
- It is the only `invocation: primary` agent, and no canonical skill is named `conductor`.
- `profiles/fallbacks.yml:3-4` sets `role-skill` → `no-primary-agent: skill`.
- `profiles/capabilities.yml:73-86` sets the `orchestrator` profile:
  - read: allow;
  - search, write, execute, network.read, network.publish: deny;
  - delegate: allow.
  - It has no `ask`, so there is no `safety-narrowed` record, and execute is denied, so there is
    no `capability-not-isolable` record.
- `profiles/models.yml:36-39` sets orchestration to `claude: sonnet`.
- **`no-primary-agent` is closed to `skill|omit` at load time.** Both
  `SquadSourceLoader.RequireFallbackDecision` (`SquadSourceLoader.cs:1188-1201`) and
  `schemas/fallback-profiles.schema.json:16` reject any other value. The load fails with a
  `SquadSourceValidationException` whose message is
  `Squad source validation failed: Fallback field 'no-primary-agent' uses unsupported decision '<v>'.`
- Consequence: a renderer's "other value" branch is unreachable through a real load. Pi
  (`PiRenderer.cs:255-261`) and ZCode (`ZCodeRenderer.cs:409-414`) keep that branch as defence
  in depth, and Pi's contract test never exercises it (`PiRendererContractTests.cs:463-468`).

### 2.3 Renderer and registry seams

**`ClaudeRenderer.cs`**

| Lines | What is there |
|---|---|
| `:11-51` | Class remarks; docs last verified 2026-08-23. |
| `:125-139` | `ResolvePrefixedDirectory` strips `.claude/` under global scope, so the skill lands at `skills/conductor/SKILL.md`. |
| `:169-180` | The agent loop renders every agent uniformly. |
| `:234-258` | `RenderSkill` emits `name`, a single-line `description` and `license: MIT`. |
| `:320-333` | MCP is withheld from the pure orchestrator. |
| `:358-443` | `BuildDegradationRecords`. The nested-roster `permission-not-expressible` text is at `:398-409` and is emitted once per agent at `:411-420`. |

**`SquadRendererRegistry.cs`**

| Lines | What is there |
|---|---|
| `:175-181` | Native targets may not emit any `role-` path, so there is no rename escape. |
| `:184-197` | The "Native Both" check covers declared shared identities only. |
| `:255-260` | Any degradation `Details` containing `widening` (case-insensitive) fails the whole render. |
| `:264-279` | `ValidatePortableOutputIdentities` catches physical duplicate paths. |
| `:281-328` | `ValidateResourcePrincipalCollisions` keys `principalOutputs` by the owner's source path (`:305`); the stored output path only appears in the error message. |
| `:356-364` | Remark: "the other four native targets render every agent uniformly". It is stale today and becomes false. |
| `:365-385` | `AgentOutputPath`, with Claude at `:374`. |

**Pi and ZCode precedents**

| Location | What it does |
|---|---|
| `PiRenderer.cs:196-199` | Reads the capability vocabulary from source. |
| `PiRenderer.cs:220-261` | Branches on the fallback profile and fails closed on a canonical-skill collision. |
| `PiRenderer.cs:571-605` | Emits `role-skill-fallback` plus `permission-not-expressible`. |
| `ZCodeRenderer.cs:382-459` | The ZCode equivalent of the branch above. |
| `ZCodeRenderer.cs:903-942` | The ZCode equivalent of the records above. |

**Duplication already present.**
- `DescribeCapabilityDecisions` appears twice: `PiRenderer.cs:613-630` and
  `ZCodeRenderer.cs:949-966`.
- `DescribeDecision` appears three times: `PiRenderer.cs:632`, `ZCodeRenderer.cs:1011` and
  `CapabilityDegradations.cs:84`.
- Claude needs the same function. Adding a third copy would feed the council's
  `duplicate-implementation` lens, so T4 extracts it instead.

**No change needed** in these places:

| Location | Why |
|---|---|
| `SquadGlobalRoots.cs:91` | The `$CLAUDE_CONFIG_DIR` → `~/.claude` resolution is unchanged. |
| `SquadDeploymentPlan.IdentityFromRelativePath` (`SquadDeploymentPlan.cs:531-556`) | Already maps `SKILL.md` to its parent directory name. |
| `SquadPathPolicy.GetPortableIdentity` | Path-based, so `agents/conductor.md` and `skills/conductor/SKILL.md` do not collide. |

**The receipt keys degradations on target, subject and code.**
`SquadLifecycleService.cs:164-166` and `:303-305` map each record to
`SquadDegradation(Target, Subject, Code)` (`SquadDeploymentModels.cs:47-50`). Two
`permission-not-expressible` records for `claude/conductor` would be indistinguishable in the
receipt, so the entry-point gaps must merge into the agent's existing record.

### 2.4 Tests affected

`ClaudeRendererContractTests.cs`:

- The corpus test is at `:122-413`:
  - count at `:137-146`;
  - canonical-skills loop at `:289-326`;
  - `permission-not-expressible` set and "Roster:" checks at `:341-395`;
  - allowed codes at `:402-404`.
  - It asserts exactly one `permission-not-expressible` per agent (`:366-371`), which the merge
    preserves.
- These tests are unaffected:
  - `RenderAsync_Claude_ConductorRunsOnSonnet` (`:487-512`): it asserts on
    `.claude/agents/conductor.md` and stays green because the agent file is kept (Q1).
  - `RenderAsync_IsDeterministic` (`:514-539`).
  - The resource-projection test (`:541-550`).

**New finding (the intake missed it):** `SquadGlobalRootTests.ExpectedRenderedFileCount()`
(`:814-833`) is documented as target-agnostic. Its remark says "a lowered primary agent still
contributes exactly one principal", requirement "R17" from the archived Pi plan. The Claude rows
of two theories will fail once Claude emits five more files:
- `:369-472`, count at `:414`;
- `:765-812`, count at `:793`.

T2 owns this file.

These are unaffected:
- `PiSquadLifecycleTests` (Pi only).
- `SquadRenderingContractTests`: its collision test (`:193-208`) is Copilot, and the
  native-projection helper (`:325-354`) uses a subagent.
- `SquadLifecycleTests` (fake renderer).
- Every other renderer suite; none instantiates `ClaudeRenderer`.

Fixtures to reuse:
- `PiPrimaryIdentityCollisionFixture` (`PiRendererContractTests.cs:1115-1150`), which adds a
  canonical skill named after the primary agent.
- `PiCorpusFixtureHelpers.CopyDirectory` (`:1202-1214`).

Both are `internal` in the test assembly.

### 2.5 Documentation state and drift (intake items re-checked)

Drift still present:

- `requirements.md:50` lists the code `lowered`. No renderer emits it, and every renderer emits
  `role-skill-fallback` instead (Warp `WarpRenderer.cs:154,168,180`; Pi; ZCode).
- `requirements.md` taxonomy (`:44-55`) has no `resource-links-rewritten` row, though ZCode
  emits it (`ZCodeRenderer.cs:592`).
- `requirements.md:65` (Claude matrix row) reads "Not lowered / Native execution". It omits the
  `safety-narrowed`, `capability-not-isolable` and `permission-not-expressible` records Claude
  already emits.
- `SquadRendererRegistry.cs:362-363` says "four native targets" (a code comment).

Docs this change touches:

- `architecture.md`:
  - §3 (`:123-170`; the Pi subsection is at `:160-170`);
  - §8 dispatch line (`:280`);
  - table row (`:294`);
  - MCP bullet (`:331-333`);
  - coverage line (`:376`).
- `onboarding.md`:
  - target row (`:60`);
  - coverage prose (`:72-73`);
  - global-scope row (`:171`), which already covers `skills/<name>/SKILL.md` generically;
  - Pi notes (`:86-129`) and Factory notes (`:131-144`) set the pattern for a new "Claude notes"
    section.
- Formal `code-refs` ownership of `ClaudeRenderer` and `SquadRendererRegistry` is
  `architecture.md` only (`docs_for_symbol`).

### 2.6 ADR numbering

`docs/adr/0023-kyberdash-report-model-and-tray-ownership.md` already exists. The next free
number is 0024, and nothing in the tree reserves it. It goes unused: under Q5-B there is no ADR
(§3).

### 2.7 Live-environment facts, for T6

- This cloud environment's global Squad Claude deployment is owned by the `$HOME` root:
  `scripts/cloud-session-setup.sh:47` runs `cd "$HOME"`, and `:124-125` runs
  `squad update --global --target claude || squad install --global --target claude`.
- A `--global` install from any other root fails on sibling ownership
  (`SquadDeploymentPlan.cs:696-701`).
- `squad install` and `squad update` fetch only the release that matches the running CLI's own
  version (see [the version-flag issue](https://github.com/dpalfery/kyber-weave/issues/127)). A local build is
  therefore deployed through the
  [local release loop](../../distribution.md#verifying-a-release-locally):
  1. `scripts/release-local.sh --version <v>`;
  2. `scripts/local-release-server.py`;
  3. a loopback `KYBER_WEAVE_RELEASE_ORIGIN`;
  4. the published single-file binary, never `dotnet run`.
- `/root/.claude/agents/conductor.md` exists and `/root/.claude/skills/conductor/` does not.

---

## 3. Approved decisions

Provenance: user decisions relayed by the conductor on 2026-09-25, recorded here as relayed.
They are not reopened.

- **Path: PLAN.** The user chose a plan over a spec.
- **Development mode: `test-first`** (the default, confirmed).
- **Q1-retain = A.** Keep `.claude/agents/conductor.md` alongside the new `/conductor` skill.
  `claude --agent conductor` stays the enforced option.
- **Q2-mechanism = A.** The fallback profile's `no-primary-agent` drives it:
  - `skill` emits the entry-point skill and keeps the agent;
  - `omit` gives today's output (agent only);
  - any other value fails closed.
- **Q3-invocation = B** (2026-09-25, at the gate; not the architect's recommendation). Omit
  `disable-model-invocation`. Claude may auto-load the skill when a request matches its
  description, and the description stays in Claude's context.
- **Q4-skill-keys = A** (2026-09-25, at the gate). The skill carries no `model`,
  `allowed-tools` or `disallowed-tools`. The session model and permissions govern, and the
  degradation records state the gaps.
- **Q5-adr = B** (2026-09-25, at the gate; not the architect's recommendation). No ADR. The
  rationale and rejected alternatives live in this section, in a new `architecture.md` §3
  subsection, and in the `requirements.md` matrix.
- **Execution approved** (2026-09-25): test-first. The approval includes the RED waiver for T1
  row (f) and the parent-session live verification T6, which temporarily updates `~/.claude`.

§4 records the options and the architect's original recommendations for Q3 to Q5.

### Rationale and rejected alternatives (recorded here because Q5-B chose no ADR)

**What `no-primary-agent` means on Claude.** Claude has a real primary-agent primitive,
`claude --agent <name>`, and only that mode enforces the tool list, `Agent(roster)` and the
model. On Pi and ZCode, `no-primary-agent: skill` means "render as a skill or command instead
of an agent". On Claude it means "*also* expose an entry point": the subagent file stays the
enforced form, and the skill is the main-thread way to start the conductor from inside a
session. `omit` means "no entry point", which is today's output.

This makes Claude the first native target to emit two principal outputs for one agent. The
render accepts, and states in `permission-not-expressible`, that the main-thread entry point
does not enforce the orchestrator profile.

**Rejected alternatives**

| Alternative | Why rejected |
|---|---|
| A legacy command at `.claude/commands/conductor.md` | Claude Code registers files under `.claude/commands/<subdir>/` as `<subdir>:<name>` commands. The conductor's resources would become phantom commands, or would need ZCode-style relocation and link rewriting ([ADR 0021](../../adr/0021-zcode-command-lowering-and-resource-relocation.md) §2). A skill directory takes supporting files natively, and a skill wins over a same-named command. |
| A skill with `context: fork` and `agent: conductor` | This runs an isolated subagent that does not see the conversation history and runs in the background by default. It recreates the nested-subagent failure (no Agent tool at depth 1) and cannot relay decision gates to the user. |
| Setting `"agent": "conductor"` in `.claude/settings.json` | It makes every session a conductor session, and Squad does not own settings files. |
| Replacing the subagent with the skill (Q1-B) | It drops the only enforced form (`claude --agent conductor`), breaks existing `@agent-conductor` use, and `squad update` would delete the file. |
| An unconditional renderer rule for every primary agent (Q2-B) | Claude would become the only target that ignores the declared `no-primary-agent` value, and there would be no off switch in source. |
| A new dedicated profile key (Q2-C) | It is a schema change, when the existing key already declares the intent. |
| Emitting `model`, `allowed-tools` or `disallowed-tools` on the skill (Q4-B/C) | All three last only for the invoking turn and so cannot hold the orchestration profile. `allowed-tools` pre-approves tools rather than restricting them. |
| Hook-based enforcement from the skill | Skill hooks stay registered for the rest of the session, which outlives the conductor's use. Whether a hook can tell main-thread calls from subagent calls is unverified. |

### Architect-settled technical design (under Q1 to Q4 and the technical-design mandate)

Approval of this plan approves these.

- **N1 (render branch).**
  - Every agent, the primary one included, renders exactly as today: `RenderAgent` plus the
    resources beside it. The `.claude/agents/<name>.md` bytes do not change.
  - For an agent with `Invocation == Primary`, the renderer reads
    `source.FallbackProfiles.Profiles[agent.Fallback].NoPrimaryAgent` and branches:
    - `skill`: additionally render the entry-point skill (N2).
    - `omit`: emit nothing more and record nothing more, because the agent still renders.
      This deliberately differs from Pi and ZCode, whose `omit` records `omitted`: here nothing
      is omitted.
    - Any other value: throw `SquadRenderValidationException` naming the profile, the value
      and the agent, worded as in Pi. This is defence in depth that the loader makes
      unreachable (§2.2).
- **N2 (entry-point skill).**
  - Path: `{ResolvePrefixedDirectory(".claude/skills", scope)}/<name>/SKILL.md`. That is
    `.claude/skills/conductor/SKILL.md` in project scope and `skills/conductor/SKILL.md` in
    global scope.
  - Frontmatter is exactly three keys (Q3-B, Q4-A):
    - `name`;
    - `description`: the agent description collapsed to one line, as `RenderSkill` does;
    - `license: MIT`.
  - Never emitted: `disable-model-invocation`, `model`, `allowed-tools`, `disallowed-tools`,
    `context`, `agent`, `hooks`, `user-invocable`, `argument-hint`, `when_to_use`, `effort`.
  - Body: the canonical `agent.InstructionBody`, verbatim. It is byte-identical to the agent
    file's body and no link is rewritten.
- **N3 (resources).**
  - `SquadResourceProjection.Append` places the agent's resource closure beside the skill as
    well, at `.claude/skills/conductor/conductor/references/*.md`.
  - The copy beside the agent (`.claude/agents/conductor/references/*.md`) is kept.
  - The body's `conductor/references/*.md` links resolve unchanged from both principals, in
    both scopes, so no `resource-links-rewritten` record is emitted.
  - Net Claude render change: five more files (one `SKILL.md` and four references).
- **N4 (fail closed on a canonical skill).**
  - Under `skill`, if a canonical skill has the agent's name, `ClaudeRenderer` throws
    `SquadRenderValidationException` naming it, before emitting.
  - This is checked in the renderer itself, as in Pi (`PiRenderer.cs:223-231`). It does not
    rely on the registry's incidental duplicate-path check.
  - There is no rename escape, because native targets forbid `role-`.
- **N5 (degradations; conductor, `skill` mode).**
  - **`role-skill-fallback`**, one record.
    - Target `claude`; canonical and output identity `conductor`; digest `BodyDigest`.
    - `Details` states:
      - Claude selects a primary agent only through `claude --agent <name>` or the `agent`
        setting;
      - fallback profile `<id>` declares `no-primary-agent: <value>`;
      - the agent therefore also renders as an entry-point skill invoked as `/<name>`, running
        in the main conversation;
      - the subagent file is kept.
    - On Claude this code means "also exposed as an entry point", not "instead of".
  - **`permission-not-expressible`**, one merged record.
    - It keeps the existing nested-roster sentence unchanged, including `Roster: …`.
    - It appends an entry-point sentence:
      - every vocabulary capability's decision, as `capability: decision` pairs from
        `source.CapabilityProfiles.Capabilities`;
      - that the session's tools, permission mode, MCP servers and model apply to `/<name>`;
      - that the pure-orchestrator MCP withholding does not apply (only when the profile is
        `orchestrator`);
      - that the `delegates-to` roster is instruction-only there;
      - that `claude --agent <name>` is the enforced alternative.
  - No `Details` may contain the substring `widening` in any case (registry `:255-260`). Avoid
    `widen` in any form.
  - There must be at most one record per `(Target, CanonicalIdentity, Code)`.
  - Under `omit`, conductor's records are exactly today's.
- **N6 (registry).**
  - `AgentOutputPath` stays single-valued, and its Claude branch is unchanged.
    `ValidateResourcePrincipalCollisions` keys on the agent's *source* path, so a second output
    path would only change error text. `ValidatePortableOutputIdentities` already catches a
    physical duplicate of the skill path.
  - Only the stale remark at `:356-364` is rewritten, to describe:
    - Pi and ZCode choosing a primary agent's single output;
    - Claude adding an entry-point skill while its agent file remains the representative
      principal;
    - the other native targets rendering uniformly, without a hard-coded count.
- **N7 (shared helper).** T4 moves `DescribeCapabilityDecisions` into `CapabilityDegradations`
  as `internal static`, reusing its existing `DescribeDecision`, and routes Pi, ZCode and Claude
  through it. It is behaviour-preserving: Pi and ZCode `Details` stay byte-identical.
- **N8 (class remarks).** Add a "Primary-agent entry point" paragraph to `ClaudeRenderer`'s
  remarks with the §2.1 facts, re-verified 2026-09-25 against `/docs/en/skills` and
  `/docs/en/sub-agents`.

---

## 4. Decision record (resolved 2026-09-25)

Every decision is answered; nothing is open. The user answered at the approve-and-execute
gate, and the parent session relayed the answers. The options and the architect's original
recommendations are kept for provenance.

| Id | Question | Options | Architect recommendation | User decision |
|---|---|---|---|---|
| **Q3-invocation** | Should Claude be able to load the `/conductor` skill on its own? | **A:** emit `disable-model-invocation: true`. Only the user's `/conductor` starts it, and its description stays out of Claude's context. **B:** omit the key, so Claude may auto-load the skill when its description matches. | **A.** The description ("Primary orchestrator and default entry point: accepts a plan, specification, todo, or open request…") matches almost any request, so under B Claude could turn the main thread into the conductor unasked. A also keeps the description out of context, and matches ADR 0021's "operator starts on purpose". | **B** (the recommendation was not taken). The accepted consequence is in §10. Onboarding tells operators who want the entry point opt-in to use `permissions.deny: ["Skill(conductor)"]` or `claude --agent conductor`. |
| **Q4-skill-keys** | Which model and tool keys does the skill carry? | **A:** none of `model`, `allowed-tools` or `disallowed-tools`. The session model and permissions govern, and the degradation records state the gaps. **B:** emit the orchestration profile's Claude `model` (`sonnet` today). It applies only to the turn that invokes the skill, and the session model resumes at the next prompt. **C:** B plus `disallowed-tools` listing the built-in tools the profile withholds (`Grep, Glob, Edit, Write, NotebookEdit, Bash, PowerShell, WebFetch, WebSearch`). This narrows only the first turn, and its effect on subagents spawned in that turn is unverified. | **A.** B switches models between turn 1 and turn 2 of one workflow, which looks like enforcement but is not. C adds the risk that first-turn delegates are stripped of tools. The enforced model and tools are what `claude --agent conductor` provides. | **A** (as recommended). |
| **Q5-adr** | Record this as an ADR? | **A: ADR 0024** (next free; 0023 is taken), plus an index row, `decided-by` on the three Squad docs, and Related-only back-links in ADRs 0019 and 0021. **B: no ADR.** The rationale and alternatives go in this plan (§3), a new `architecture.md` §3 subsection, and the `requirements.md` matrix, matching the "no ADR" choice made on 2026-09-23 for the squad path-safety plan. | **A.** Every earlier primary-agent classification has its own ADR (0019 Pi, 0021 ZCode, 0022 Antigravity). This one changes what a profile key means per target and constrains every future primary agent on Claude. | **B** (the recommendation was not taken). §3 holds the rationale and rejected alternatives. T5 writes no ADR files and adds no `decided-by` or ADR back-links. |

---

## 5. Scope

**In:**

- `ClaudeRenderer`: the primary-agent branch, the entry-point skill, resources beside it, the
  fail-closed check, the merged degradations, and the remarks.
- The `SquadRendererRegistry` remark.
- Extracting `DescribeCapabilityDecisions` into `CapabilityDegradations`, with the Pi and ZCode
  call sites (N7).
- `ClaudeRendererContractTests` and `SquadGlobalRootTests`.
- `docs/kyber-squad/architecture.md` (a new §3 subsection carrying the rationale and rejected
  alternatives), `requirements.md` (including the §2.5 drift fixes) and `onboarding.md` (a new
  "Claude notes" section).
- A live verification by the parent session.
- `docs-dev` closeout.

**Out:**

- Any change to canonical content under `products/kyber-squad/`: `conductor.md`,
  `fallbacks.yml`, `models.yml`, the schemas.
- Writing `.claude/settings.json`, including `agent` or `permissions` keys. Squad does not own
  settings files.
- `context: fork`, legacy `.claude/commands/`, and hook-based enforcement. The related todo
  `claude-renderer-ask-narrowing` is already archived.
- Other targets' primary-agent behaviour, beyond the byte-preserving N7 refactor.
- The root `.github/` self-deployment.
- Any ADR, and any `decided-by` or ADR back-link edits (Q5-B).
- Unrelated drift, reported to the user on 2026-09-25 and not fixed here:
  `products/kyber-squad/README.md:8-11`
  ("ten harnesses / eight renderers") and `onboarding.md:35` (the `squad update` synopsis omits
  `--target/--exclude`).

---

## 6. Test contract (development-mode: test-first)

Runner commands:

- Suite runs: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~<Class>"`.
- GREEN always ends with the full suite: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`.

Conventions for the rows:

- `<name>` is the single primary agent, read from source (`conductor` today). No test hard-codes
  the name, the roster or the capability list.
- **Key set** means the entry-point skill's exact frontmatter key set, fixed by Q3-B and Q4-A:
  exactly `{name, description, license}`. In particular, `disable-model-invocation`, `model`,
  `allowed-tools` and `disallowed-tools` are all absent.

**T1** — all rows in `ClaudeRendererContractTests.cs`

| Test | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|
| (a) `RenderAsync_Claude_RendersTheRealCanonicalCorpus` (updated) | File count adds `1 + agent.Resources.Count` for each primary agent whose profile says `no-primary-agent: skill`. The allowed code set adds `role-skill-fallback`. Still exactly one `permission-not-expressible` per agent. | The count assertion fails: expected base + 5, actual base. | Passes unmodified after T3. |
| (b) `RenderAsync_Claude_ExposesThePrimaryAgentAsAnEntryPointSkillBesideItsSubagent` (new) | See "Row (b) assertions" below. | `Assert.Single` fails: no `.claude/skills/<name>/SKILL.md`. | Passes after T3. |
| (c) `RenderAsync_Claude_GlobalScopePlacesTheEntryPointSkillUnderSkills` (new) | Global scope through the registry emits `skills/<name>/SKILL.md`, `skills/<name>/<resource>` for every resource, and `agents/<name>.md`. No path starts with `.claude/`. | No `skills/<name>/SKILL.md` exists. | Passes after T3. |
| (d) `RenderAsync_Claude_NoPrimaryAgentValueSwitchesOnlyTheEntryPointSkill` (new) | Renders the real corpus (`skill`) and `ClaudeNoPrimaryAgentFixture.Create("omit")`. See "Row (d) assertions" below. | The set difference is empty. | Passes after T3. |
| (e) `RenderAsync_Claude_ThrowsWhenACanonicalSkillOccupiesTheEntryPointIdentity` (new) | Uses `PiPrimaryIdentityCollisionFixture.Create(<name>)`. A **direct** `new ClaudeRenderer().RenderAsync(...)`, not through the registry, throws `SquadRenderValidationException` whose message contains `<name>`. | No exception: the render succeeds today. | Passes after T3. |
| (f) `RenderAsync_Claude_FailsClosedOnAnUnsupportedNoPrimaryAgentValue` (new) | Uses `ClaudeNoPrimaryAgentFixture.Create("rename")`. `new ClaudeRenderer().RenderAsync(...)` throws `SquadSourceValidationException` whose message names `no-primary-agent`, and no file is produced. | **RED waived: a regression guard.** The loader already rejects the value (§2.2), so this passes before implementation. Evidence is the captured pre-implementation pass. Approving the plan approves this waiver. | Still passes after T3. |

**Row (b) assertions.** Project scope, rendered through the registry:

- `.claude/agents/<name>.md` is present exactly once (Q1).
- `.claude/skills/<name>/SKILL.md` is present exactly once, with:
  - frontmatter keys equal to exactly `{name, description, license}` (the **Key set**; no
    `disable-model-invocation`, `model`, `allowed-tools` or `disallowed-tools`);
  - `name` = `<name>`;
  - `description` = the one-line collapse of the agent description;
  - `license: MIT`;
  - a body equal to the normalized canonical body.
- For every agent resource:
  - `.claude/skills/<name>/<resource>` exists once, with canonical bytes;
  - `.claude/agents/<name>/<resource>` still exists.
- Every relative Markdown link in the body resolves to an emitted file under
  `.claude/skills/<name>/`.
- Exactly one `role-skill-fallback` record for `<name>`:
  - `OutputIdentity` = `<name>`, digest = `BodyDigest`;
  - `Details` contains `/<name>`, the fallback profile id, and
    `no-primary-agent: <value>`.
- Exactly one `permission-not-expressible` record for `<name>`. Its `Details` contains:
  - every `<capability>: <decision>` pair for the whole vocabulary;
  - `Roster:`;
  - `/<name>`;
  - `MCP`;
  - `claude --agent <name>`.
- No other codes for `<name>`.
- Across every Claude record:
  - at most one record per (Target, CanonicalIdentity, Code);
  - no `Details` contains `widening` (case-insensitive).

**Row (d) assertions:**

- In omit mode, no path starts with `.claude/skills/<name>/`.
- Every omit-mode path also appears in skill mode.
- Skill-mode paths minus omit-mode paths equal exactly `SKILL.md` plus its resources.
- `.claude/agents/<name>.md` has identical bytes in both modes.
- In omit mode, `<name>` has exactly one record, a `permission-not-expressible` whose `Details`
  contains `Roster:` and not `/<name>`. There is no `role-skill-fallback` and no `omitted`.

**T2** — `SquadGlobalRootTests.cs`

| Test | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|
| `ExpectedRenderedFileCount(SquadTarget)` (made target-aware) and its three callers (`:414`, `:626`, `:793`) | The Claude rows of `InstallAsync_GlobalScopeDryRun_PlansEveryFileUnderTheResolvedTargetRootWithBareRelativePaths` and `InstallAsync_RealInstallGlobalScope_WritesOnlyUnderTheResolvedHomeSubtree` expect base + `Σ(1 + resources)` over primary agents in `skill` mode. The Claude receipt contains `skills/<name>/SKILL.md` and `agents/<name>.md`. Other targets keep the base count. The remark is updated: Claude is the one target where a primary agent adds a principal. | The Claude rows fail on the count; every other row stays green. | Passes after T3. |

**Tasks with no new test**

| Task | Replacement verification | Acceptance |
|---|---|---|
| T3 | None; it is the GREEN task. | Every T1 and T2 row green (f still green). `RenderAsync_Claude_ConductorRunsOnSonnet` green and unmodified. Full suite green. |
| T4 | Guarded by the existing Pi, ZCode, Claude and `CapabilityDegradationsTests` suites, run unmodified. | Full suite green with no assertion edits. `review duplicates` reports no cluster for `DescribeCapabilityDecisions`. |
| T5 | `docs validate .` and `docs drift .`, run by the parent session. | Zero findings. |
| T6 | Manual live check in Claude Code (§7, T6). | Checks (i) and (ii) recorded as pass or fail with evidence. Checks (iii) to (v) are observations, recorded as seen. |
| T7 | The declared gate suite (§9). | All gates pass. The council verdict is recorded. |
| T8 | `docs validate . --merge-ready` and `docs drift .`. | Zero findings. The plan is archived. |

Sequencing: T1 and T2 are the RED wave, on disjoint files. T3 is the single GREEN scope. T4 is
REFACTOR, and T3's GREEN must hold through it. If an approved row is weakened to reach green,
that is a scope change: the plan returns to Draft.

---

## 7. Tasks

### T1: RED, Claude renderer contract

**Objective:** Author §6 rows (a) to (f) and the class `<remarks>` update, which adds the
entry-point skill and `role-skill-fallback`.

**File scope:** `tests/KyberWeave.Tests/ClaudeRendererContractTests.cs` only.

**Fixture:** a new `internal sealed class ClaudeNoPrimaryAgentFixture : IDisposable` in the same
file. It:
- uses `TempDirectory`;
- copies the corpus with `PiCorpusFixtureHelpers.CopyDirectory`;
- replaces `no-primary-agent: skill` in `profiles/fallbacks.yml`, and throws if nothing changed.

Reuse `PiPrimaryIdentityCollisionFixture` for row (e). Do not add another corpus-copy helper,
and do not edit `PiRendererContractTests.cs`.

**Acceptance:** RED run captured, with the failing assertions for (a) to (e) and the guard
pass for (f).

**Depends on:** none. The Q3 and Q4 gate was satisfied on 2026-09-25.

**Required skills:** `test-dev`.

### T2: RED, lifecycle counts

**Objective:** Make `SquadGlobalRootTests.ExpectedRenderedFileCount` target-aware and add the
Claude receipt-path assertions, per §6 T2.

**File scope:** `tests/KyberWeave.Tests/SquadGlobalRootTests.cs` only.

**Acceptance:** RED run captured: only the Claude rows fail.

**Depends on:** none. It can run alongside T1.

**Required skills:** `test-dev`.

### T3: GREEN, render the entry-point skill

**Objective:** Implement N1 to N6 and N8.

**File scope:**
- `src/KyberWeave.Core/Squad/Rendering/ClaudeRenderer.cs`:
  - `RenderAsync` reads the canonical skill identities and the capability vocabulary up front;
  - the primary-agent branch in the agent loop;
  - a new entry-point skill render beside `RenderSkill`;
  - `BuildDegradationRecords` merges the entry-point clause into the single
    `permission-not-expressible` record and yields `role-skill-fallback`;
  - the class remarks.
- `src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs`: the remark at `:356-364`
  only, with no logic change.

**Constraints:**
- `Details` never contains `widening` (avoid `widen` entirely).
- `TreatWarningsAsErrors` applies.
- For the capability-decision text, T3 may temporarily call a private copy. T4 removes it.

**Acceptance:** §6 T3 row.

**Depends on:** T1, T2.

**Required skills:** `csharp-dev`.

### T4: REFACTOR, shared capability-decision text

**Objective:** Implement N7. Move `DescribeCapabilityDecisions` into
`CapabilityDegradations` (`internal static`), reusing its `DescribeDecision`, and delete the
Pi, ZCode and Claude private copies, together with Pi's and ZCode's private `DescribeDecision`.

**File scope:**
- `CapabilityDegradations.cs`
- `PiRenderer.cs`
- `ZCodeRenderer.cs`
- `ClaudeRenderer.cs`

**Acceptance:** §6 T4 row; Pi and ZCode `Details` byte-identical.

**Depends on:** T3, which overlaps on `ClaudeRenderer.cs`.

**Required skills:** `csharp-dev`.

### T5: Documentation

**Objective:** Align the governed docs with the shipped behaviour and the approved Q3-B, Q4-A
and Q5-B. There is no ADR: write no ADR files, and make no `decided-by` or ADR back-link edits.

**`docs/kyber-squad/architecture.md`:**
- Add a §3 subsection, "Native Branch: Claude with a Primary-Agent Entry-Point Skill", after
  the Pi subsection at `:160-170`. Under Q5-B this subsection is the durable home of the
  decision, so it carries the §3 rationale and the rejected-alternatives table in substance. It
  covers:
  - why Claude keeps the agent (`claude --agent` enforces);
  - what `no-primary-agent` means on Claude ("also an entry point", not "instead of");
  - the `omit` outcome;
  - the fail-closed collision;
  - resources beside both principals;
  - that Claude is the first native target with two outputs for one agent;
  - that the skill carries only `name`, `description` and `license`, and can auto-load from
    its description;
  - the rejected alternatives: legacy command, `context: fork`, the settings `agent` key,
    replacing the agent, an unconditional rule, a new profile key, turn-scoped
    `model`/`allowed-tools`/`disallowed-tools`, and hooks.
- Update the §8 dispatch line (`:280`), table row (`:294`), MCP bullet (`:331-333`, the
  orchestrator withholding applies to the subagent file only) and coverage line (`:376`).
- Bump `last-reviewed`. `decided-by` is unchanged.

**`docs/kyber-squad/requirements.md`:**
- Taxonomy (`:44-55`):
  - replace `lowered` with `role-skill-fallback`; its meaning now includes Claude's
    "additional entry point", with examples for Warp, Pi, ZCode and Claude;
  - add a `resource-links-rewritten` row (ZCode).
- Claude matrix row (`:65`):
  - projection: `.claude/agents` plus the conductor entry-point skill;
  - delegation: supported, with the roster enforced only under `claude --agent`;
  - lowering: "Not lowered; primary agent additionally exposed as `/conductor`
    (`role-skill-fallback`)";
  - permission model: an explicit tool allow-list; `safety-narrowed` on ask;
    `capability-not-isolable` for shell-implies-write; `permission-not-expressible` for
    `network.publish`, the nested roster and the unenforced entry point, which can auto-load
    from its description.
- Bump `last-reviewed`. `decided-by` is unchanged.

**`docs/kyber-squad/onboarding.md`:**
- Update the `:60` row and the `:72-73` prose.
- Add a new `### Claude notes` section before `### Pi notes`, covering:
  - (1) Three ways to run the conductor:
    - `/conductor <path or request>`: main thread; the profile is not enforced; the session's
      tools, permission mode, MCP and model apply.
    - `claude --agent conductor`: enforced tools, `Agent(roster)` and the `sonnet` model; CLI
      launch.
    - `@agent-conductor`: nested; the roster is ignored; discouraged.
  - (2) Automatic loading (Q3-B). The skill's description is in Claude's context, so Claude can
    load the conductor skill into the main conversation by itself when a request matches it,
    without the user typing `/conductor`. For operators who want the entry point opt-in:
    - add `"permissions": {"deny": ["Skill(conductor)"]}` to their own Claude settings. The
      docs' exact-match rule stops Claude invoking the skill; whether it also blocks a
      user-typed `/conductor` is undocumented, so state what T6 (v) observed.
    - or start the enforced form with `claude --agent conductor`.

    Either way it is a Claude Code setting, not a file Squad writes. Report what T6 (iii)
    observed about auto-loading.
  - (2b) Automatic delegation to the subagent copy cannot be switched off from frontmatter. Cite
    the documented operator setting `"permissions": {"deny": ["Agent(conductor)"]}` as Claude
    Code behaviour, not a Squad-written file, and state whether T6 (iv) verified it.
  - (3) The scope-precedence asymmetry (skills: personal over project; agents: project over
    user) and its consequence for mixed global and project installs.
  - (4) Cloud sessions. `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1` was observed on 2026-09-25.
    Under it, a subagent conductor has no Agent tool and cannot delegate, so use `/conductor`.
    Specialists that delegate further (`architect`, `product-owner`, `code-reviewer`) cannot
    nest.
  - (5) The global paths `~/.claude/skills/conductor/SKILL.md` and
    `~/.claude/agents/conductor.md`, with references beside each; live reload; and that
    `/conductor` appears only after `squad update` with a release that carries it.
- Bump `last-reviewed`. `decided-by` is unchanged.

**File scope:** the three `docs/kyber-squad/` files above only. No files under `docs/adr/`.

**Acceptance:** zero `docs validate` and `docs drift` findings, run by the parent session. The
wording matches the T3 `Details` and the three-key frontmatter.

**Depends on:** T3, whose emitted keys and `Details` it consumes. It can run alongside T4 and T6.

**Required skills:** `kyber-weave-docs`, `app-docs-standard`.

### T6: Live verification (run by the parent session)

**Objective:** Prove the deployed behaviour in Claude Code.

**Steps:**

1. Build a local release from the finished tree:
   `scripts/release-local.sh --version <v> --no-kyberdash`.
2. Serve it on loopback: `scripts/local-release-server.py`.
3. Extract the published single-file `kyber-weave` binary.
4. `cd "$HOME"`, which is outside the repo root and is the root that owns this environment's
   global receipt (§2.7).
5. Run `KYBER_WEAVE_RELEASE_ORIGIN=<loopback> <binary> squad update --global --target claude`.
   If there is no global deployment, use `squad install --global --target claude`.

**Checks:**

- (i) On disk:
  - `~/.claude/skills/conductor/SKILL.md` and its four references under
    `~/.claude/skills/conductor/conductor/references/`;
  - `~/.claude/agents/conductor.md` and its references, still present;
  - the global receipt lists `claude/conductor` with `role-skill-fallback` and
    `permission-not-expressible` once each.
- (ii) In Claude Code:
  - `/skills` lists `conductor`;
  - `/conductor <a plan path>` runs in the main conversation, with no subagent spawn for the
    conductor itself;
  - its first reference `Read` resolves under `~/.claude/skills/conductor/conductor/references/`;
  - `@agent-conductor` still offers and starts the subagent;
  - so the skill and the subagent coexist.
- (iii) Observation (Q3-B). Send a request that matches the skill's description without typing
  `/conductor`, for example "here is a plan, coordinate it". Record whether Claude auto-loads
  the conductor skill into the main conversation, and what it did. Either outcome is recorded;
  neither fails the task.
- (iv) Optional observation: with `Agent(conductor)` in a scratch settings deny, record whether
  `@agent-conductor` is blocked.
- (v) Optional observation: with `Skill(conductor)` in a scratch settings deny, record whether
  Claude stops auto-loading the skill and whether a user-typed `/conductor` still runs.

**Restore:** the next session start's `squad update --global` returns `~/.claude` to the
published release. Until a release carries the skill, `/conductor` disappears again.

**Acceptance:** an evidence note for each check, relayed to T8.

**Depends on:** T4, the final code.

**Required skills:** none. This is a manual check in the parent session.

### T7: Gates and review

**Objective:** Run the §9 gate suite and the `code-review` council over the branch diff, with
`static-analysis-triage` attributing InspectCode findings. Also gather `task-reviewer` passes
per task.

**Depends on:** T4, T5, T6.

**Required skills:** `code-review`, `resharper-clt`.

### T8: `docs-dev` closeout

**Objective:**
- Record the T6 evidence in a §2 addendum.
- Reconcile any onboarding claim that T6 changed: auto-loading (iii), the `Agent(conductor)`
  deny (iv), the `Skill(conductor)` deny (v), and link resolution.
- Harvest into canonical docs. There is no ADR: `architecture.md` §3 carries the rationale and
  rejected alternatives.
- Move this plan to `docs/archive/plans/` and move its index row to Archived. List the
  canonical docs and note "no ADR — the decision is recorded in plan §3 and `architecture.md`
  §3".

**Acceptance:** `docs validate . --merge-ready` and `docs drift .` report zero findings.

**Depends on:** T7.

**Required skills:** `kyber-weave-docs`.

---

## 8. Dependency graph and concurrency audit

```text
T1 ─┐
    ├─► T3 ─► T4 ─► T6 ─┐
T2 ─┘      └─► T5 ──────┼─► T7 ─► T8
                 T4 ────┘
```

MAX_CONCURRENCY: **2**. It is reached three times:

- T1 ∥ T2: disjoint test files.
- T4 ∥ T5: source versus docs.
- T5 ∥ T6: docs versus a live check with no repository writes.

The dependencies are real:

- T3 consumes both RED suites.
- T4 overlaps T3 on `ClaudeRenderer.cs`.
- T5 documents T3's emitted key set and `Details`.
- T6 needs final code.
- T7 reviews everything.
- T8 archives after review.

---

## 9. Verification gates

These follow the repository `AGENTS.md` and run on this branch:

- `dotnet restore KyberWeave.sln`
- `dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal`
- `dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal`
- `dotnet build KyberWeave.sln -c Release --no-restore`
- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready`
  (at closeout; plain `docs validate .` before)
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`
- `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`

The local release loop (`scripts/update-loop.sh`) is **not** required. This change touches
neither the self-updater, `install.sh`, the Squad release path, nor `kyberdash`. T6 uses
`release-local.sh` only to obtain a deployable build.

---

## 10. Risks

- **Automatic delegation to the kept subagent continues.** This is the failure mode seen in
  practice, and no frontmatter prevents it. Mitigations: onboarding guidance, and the documented
  operator deny `Agent(conductor)` (T6 iv).
- **The main thread is unenforced.** `/conductor` holds every session tool. The body's
  "delegation and task tracking only" is instruction-only. This is accepted under Q1 and Q2 and
  stated in `permission-not-expressible`. `claude --agent conductor` is the enforced path.
- **Scope asymmetry.** With both a global and a project install, `/conductor` and
  `@agent-conductor` can resolve to different versions. Onboarding documents it; Squad does not
  correct upstream precedence.
- **Auto-load (accepted under Q3-B).**
  - The description ("…accepts a plan, specification, todo, or open request…") is always in
    Claude's context and matches many requests. Claude may load the conductor into the main
    conversation unprompted and start routing work to specialists.
  - Every Squad Claude agent carries the `Skill` tool (`ClaudeRenderer.cs:57`). It is
    unverified whether a subagent can also auto-load the conductor skill.
  - Mitigations: the onboarding opt-in route (`permissions.deny: ["Skill(conductor)"]`, or
    `claude --agent conductor`) and the T6 (iii)/(v) observations.
  - The description also costs its length in context on every session.
- **Session model governs `/conductor` (Q4-A).** The orchestration profile's `sonnet` pin
  applies only to the subagent and `--agent` forms. `/conductor` runs on whatever model the
  session uses.
- **Depth 1 in cloud sessions.** `architect`, `product-owner` and `code-reviewer` (whose
  review-lens council depends on nesting) cannot delegate. This change does not fix that; it
  only moves the conductor to depth 0.
- **Coexistence is undocumented.** The docs are silent on an agent and a skill with the same
  name. Precedent: seven agent/skill pairs already deploy on Claude (`architecture.md:151`).
  T6 verifies coexistence.
- **T6 mutates the parent session's real `~/.claude`.** It self-heals at the next session start.
  Version lockstep with an installed MCP server of another version was not verified.
- **A future canonical skill named `conductor`** now fails the Claude render closed, as it does
  on Pi. This is intended.

---

## 11. Review

- Each implementation task (T1 to T5) gets up to three `task-reviewer` passes.
- `code-review` runs once, as the end-of-run council in T7.
- Gate evidence: `artifacts/gates.json`.

---

## 12. Docs-dev closeout

- This plan is registered in `docs/plans/README.md` (Active Plans), with development mode
  `test-first`.
- **Finalized 2026-09-25.** The conductor relayed the approval.
  - The answers and approval provenance are recorded in the header and §3.
  - The Draft ledger became the resolved decision record in §4, which keeps the options and
    recommendations for provenance.
  - Body Status Draft → Ready; frontmatter `status: draft` → `current`; index Status → Ready.
  - The parent session re-runs `docs validate .` and `docs drift .` on this edit.
- **On completion (T8):**
  - record the T6 evidence;
  - harvest into `docs/kyber-squad/`, with no ADR;
  - archive the plan and its index row.
  - No todo is created for the out-of-scope drift in §5 unless the user accepts one.

## 13. PR #136 closeout (2026-09-25)

The PR contains the T1–T5 implementation and canonical documentation. At head `b75a3cd`,
GitHub Actions run `36205234996` passed Build and test, the platform contracts, security
checks, and the release loops. The Skill and docs gate failed on the single
`KW-DOC-LIFECYCLE-003` finding for this plan in the active plans directory; CI Summary failed
because that required gate failed.

The user directed this closeout toward fixing the PR rather than changing the local Claude
installation. T6 checks (i)–(v) were **not exercised in a live Claude Code session**. Renderer
tests and package output are not presented as live evidence. The remaining check is recorded in
[the Claude conductor live-verification todo](../../todo/claude-conductor-live-verification.md),
and the corresponding caveat remains in [onboarding](../../kyber-squad/onboarding.md).

The implementation rationale and rejected alternatives are harvested into
[Kyber-Squad architecture](../../kyber-squad/architecture.md), with the behaviour and rollout
guidance in [requirements](../../kyber-squad/requirements.md) and
[onboarding](../../kyber-squad/onboarding.md). No ADR was added: §3 of this plan and §3 of the
architecture record the decision. The plan was archived on 2026-09-25 so the PR can pass the
merge-ready documentation lifecycle gate without treating the deferred live check as completed.

## Related

- [Kyber-Squad architecture](../../kyber-squad/architecture.md)
- [Kyber-Squad requirements](../../kyber-squad/requirements.md)
- [Kyber-Squad onboarding](../../kyber-squad/onboarding.md)
- [ADR 0019: Pi primary-agent skill lowering](../../adr/0019-pi-native-subagents-and-primary-lowering.md)
- [ADR 0021: ZCode command lowering](../../adr/0021-zcode-command-lowering-and-resource-relocation.md)
- [ADR 0022: Antigravity native agents](../../adr/0022-antigravity-native-agents.md)
- [Verifying a release locally](../../distribution.md#verifying-a-release-locally)
