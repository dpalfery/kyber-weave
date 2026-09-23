---
id: adr/0022-antigravity-native-agents
title: Native per-agent Antigravity rendering and cross-target capability-not-isolable degradation
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-23
component: KyberSquad
---

# ADR 0022: Native per-agent Antigravity rendering and cross-target capability-not-isolable degradation

## Status

Accepted, 2026-09-23. Records the fallback-to-native reclassification of the Antigravity harness
target, the directory-per-agent projection shape, and the cross-target capability degradation
for shell-mediated filesystem writes.

## Context

Antigravity is Google's agentic harness for Gemini models (`agy` CLI and Antigravity IDE).
In the initial Kyber-Squad target classification, Antigravity was catalogued as lacking an agent
primitive and assigned to fallback lowering: every canonical agent became a skill under
`.agents/skills/` (project) or `~/.gemini/config/skills/` (global), with colliding identities
prefixed `role-` (`AntigravityRenderer.cs`). A delegated Antigravity session could only be told to
follow a skill; it could not load a specialist agent or invoke subagents natively.

Live verification against installed `agy` **1.2.7** (Mach-O arm64) on 2026-09-21 established that
Antigravity supports first-class native custom agents:
- Agents are discovered at `<workspace>/.agents/agents/<name>/agent.md` (project scope) and
  `~/.gemini/config/agents/<name>/agent.md` (global scope), with project configurations taking
  precedence over global configurations.
- Custom agents are listed by `agy agent` / `agy agents` and selected for a session via
  `agy --agent <name>`.
- The conductor runs natively as an orchestrator: Antigravity recognizes `mainAgent: true`,
  `subagent: false`, and `enable_subagent_tools: true`.
- YAML frontmatter supports tool allowlists (`tools:`), execution switches (`enable_write_tools`,
  `enable_subagent_tools`, `enable_mcp_tools`), closed model tier enums (`inherit`, `flash`, `pro`),
  and reasoning effort tiers (`minimal`, `low`, `medium`, `high`).

However, live verification also surfaced critical security and governance findings:
1. **The shell-implies-write gap:** Live experiment C0 against `agy` 1.2.7 proved that granting
   `process.execute` (`run_command`) while denying or asking `filesystem.write` (withholding
   `write_to_file`, `replace_file_content`, and `multi_replace_file_content`) prevents direct tool calls
   to those names, but completely fails to isolate the underlying write capability. An agent with shell
   access executes arbitrary file creation and modification via standard shell redirection
   (`printf "..." > file`). `process.execute` is a strict superset of `filesystem.write`.
2. **Structural presence across multiple targets:** Code inspection revealed that this same
   structural gap—a named shell-class tool held distinct from named write-class tools, both derived
   from capability profiles—exists in Claude (`Bash`/`PowerShell` vs. `Edit`/`Write`/`NotebookEdit`),
   Pi (`bash` vs. `edit`/`write`), ZCode (`Bash` vs. `Edit`/`Write`), Factory (`Execute` vs.
   `Create`/`Edit`/`ApplyPatch`), and OpenCode (`bash` vs. `edit`).
3. **Unenforceable delegation rosters:** While Antigravity equips subagent invocation via
   `enable_subagent_tools: true` and `invoke_subagent`, its frontmatter provides no mechanism to
   restrict *which* subagents an agent may spawn.
4. **Invocation-mode workspace resolution risk:** Under `agy --mode plan`, project-scoped agents
   can fail to resolve, causing `agy` to fall back to same-named global skills. Proper resolution
   requires `--mode accept-edits --add-dir <workspace>`.

## Decision

1. **Antigravity is reclassified from fallback role-skill lowering to a native target.**
   `SquadRendererRegistry` registers `SquadTarget.Antigravity` as native. Agents render to
   `.agents/agents/<name>/agent.md` (or `~/.gemini/config/agents/<name>/agent.md`), and canonical
   skills render to `.agents/skills/<name>/SKILL.md` (or `~/.gemini/config/skills/<name>/SKILL.md`).
   Because the agent and skill trees reside in disjoint namespaces (`agents/` vs. `skills/`), the
   "Native Both" pattern applies: canonical agents and skills with identical names (such as the seven
   distinct-body collisions: `csharp-dev`, `dal-dev`, `github-devops`, `maui-dev`, `product-owner`,
   `python-dev`, `test-dev`) render to both namespaces without collision and without requiring a
   `role-` prefix. Shared conductor identities emit only the canonical skill (suppressing the redundant
   skill projection), while the agent renders natively.

2. **The directory-per-agent shape (`.agents/agents/<name>/agent.md`) is adopted.**
   Antigravity requires each agent to reside in its own named directory containing `agent.md`. No
   other target currently uses this layout (others emit flat files such as `<name>.md` or
   `<name>.agent.md`). `SquadRendererRegistry.AgentOutputPath` is extended with a dedicated
   Antigravity branch, and `SquadDeploymentPlan.IdentityFromRelativePath` resolves a bare `agent.md`
   to its parent directory name to ensure accurate doctor collision detection and deployment tracking.

3. **Conductor renders as a native primary agent.**
   Unlike Pi (which lowers `conductor` to a skill to preserve subagent depth cap) and ZCode (which
   lowers `conductor` to a `/conductor` slash command), Antigravity natively supports primary agent
   execution. Conductor renders at `.agents/agents/conductor/agent.md` with `mainAgent: true`,
   `subagent: false`, and `enable_subagent_tools: true`. Live verification confirmed that non-mainAgent
   custom agents are directly selectable via `agy --agent <name>`.

4. **Residual write capability through shell execution is degraded as `capability-not-isolable`.**
   When a capability profile grants `process.execute: allow` but restricts `filesystem.write` to
   `ask` or `deny` (concretely `investigator` and `reviewer` profiles), `enable_write_tools: true` is
   emitted because Antigravity's switch gates both execution and write tools. The write tools
   (`write_to_file`, `replace_file_content`, `multi_replace_file_content`) are withheld from the
   frontmatter `tools:` list, but this narrowing is explicitly recognized as incomplete.
   A shared helper (`CapabilityDegradations.BuildCapabilityNotIsolable`) emits a structured degradation
   record with code `capability-not-isolable`, naming the granted shell tool (`run_command`) and the
   withheld write tools reachable via redirection.

5. **Cross-target capability degradation is implemented immediately (Track D).**
   Rather than deferring the shell-implies-write gap on other harnesses to future work, the owner
   directed an immediate cross-target rollout. The shared `CapabilityDegradations` helper is wired into
   all five other targets sharing this structural capability lattice mapping:
   - **Claude:** grants `Bash`, `PowerShell`; withholds `Edit`, `Write`, `NotebookEdit`.
   - **Pi:** grants `bash`; withholds `edit`, `write`.
   - **ZCode:** grants `Bash`; withholds `Edit`, `Write`.
   - **Factory:** grants `Execute`; withholds `Create`, `Edit`, `ApplyPatch`.
   - **OpenCode:** grants `bash`; withholds `edit`.

6. **Four harness targets are deliberately excluded from `capability-not-isolable`.**
   - **Copilot:** Excluded because its frontmatter `tools:` membership derives from a hand-authored,
     per-agent catalog (`agent.CopilotTools`), not from the canonical capability lattice mapping.
   - **Codex & Kilo:** Excluded because neither harness supports a frontmatter tool allowlist; both
     already record all non-`deny` capability decisions as `permission-not-expressible`.
   - **Cursor:** Excluded because write and execute are bundled behind a single `readonly` boolean;
     Cursor already emits `permission-not-expressible` ("Canonical denies not enforced without readonly")
     when permissions cannot be isolated.
   - **Warp:** Excluded because Warp remains an instruction-only fallback skill target that cannot
     express permissions and already degrades all non-`deny` capabilities to `permission-not-expressible`.

7. **Delegation rosters emit tools with `permission-not-expressible` degradation.**
   Agents with a non-empty canonical `delegates-to` roster receive `enable_subagent_tools: true` and
   `invoke_subagent` in `tools:` (with `manage_subagents` added for `orchestrator` and `conductor`).
   Because Antigravity cannot enforce restrictions on which agents may be invoked, the renderer records
   a `permission-not-expressible` degradation noting that the roster constraint is unenforced, matching
   the established Claude precedent.

8. **Inconclusive tool mappings are withheld pending further live validation.**
   Per D12, tools whose capability mappings could not be fully exercised live during C0
   (`grep_search`, `replace_file_content`, `multi_replace_file_content`, `search_web`, `read_url_content`)
   are withheld from emitted `tools:` lists in this pass. `model` emits only confirmed tiers (`inherit`,
   `flash`, `pro`); the unconfirmed `FLASH_LITE` tier is omitted.

9. **Existing deployment receipts migrate automatically.**
   `SquadDeploymentPlan.CreateUpdate` diffs previous receipt files against fresh native renders. When
   migrating an existing installation from fallback skills to native agents, former skill files
   (`.agents/skills/<name>/SKILL.md` or `.agents/skills/role-<name>/SKILL.md`) matching their receipt
   SHA-256 digests are automatically planned for deletion (`SquadFileMutation.Delete`). Locally modified
   files are preserved untouched. This behavior is regression-tested (C3) without requiring custom
   migration scripts.

## Consequences

- **Native specialist execution:** Delegated Antigravity sessions can spawn and select specialist roles
  as real agents via `agy --agent <name>`, significantly improving orchestration fidelity for
  the conductor.
- **Unique directory layout:** Antigravity introduces the repository's first directory-per-agent
  projection (`.agents/agents/<name>/agent.md`). Registry path resolvers and doctor identity extractors
  explicitly account for this layout.
- **Auditable permission boundaries:** The `capability-not-isolable` degradation record provides
  transparent reporting in deployment receipts and `squad doctor` checks across six targets (Antigravity,
  Claude, Pi, ZCode, Factory, OpenCode), documenting where shell execution bypasses nominal write
  restrictions.
- **Documented invocation risk:** Running `agy --mode plan` can bypass project-scoped agents and load
  global skills of the same name. To reliably invoke native project agents, operators must use
  `agy --mode accept-edits --add-dir <workspace>`.
- **Clean fallback retirement:** Deploying the native renderer cleanly retires old fallback artifacts
  on existing installations during standard `squad update` runs without manual cleanup.

## Related

- [ADR 0019](0019-pi-native-subagents-and-primary-lowering.md) — Native Pi agents via pi-subagents, with primary-agent skill lowering
- [ADR 0021](0021-zcode-command-lowering-and-resource-relocation.md) — ZCode command lowering, resource relocation, and the inverted empty tool list
- [Pi thinking and Antigravity native agents plan](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md) — The delivery plan defining tracks A, C, and D
- [Antigravity capability verification evidence](../todo/antigravity-capability-verification-evidence.md) — Live verification against `agy` 1.2.7 (C0) establishing the shell-implies-write proof
- [Antigravity native agents spec](../archive/todo/antigravity-native-agents.md) — Verified technical specification for Antigravity native agents
- [Black Hawk Hotel handover](../archive/todo/black-hawk-hotel-todo.md) — Initial handover documenting harness fidelity tasks
