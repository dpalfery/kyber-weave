---
id: todo/harness-specific-instruction-inserts
title: Support harness-specific conditional inserts in canonical agent instructions
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-25
status: draft
---

# Support harness-specific conditional inserts in canonical agent instructions

This is **context for planning the work, not a plan** — what's known, what needs deciding, and
where the precedent already is.

## Why this exists

Canonical agent definitions under `products/kyber-squad/agents/` are written once and projected
by `Kyber-Squad` renderers into different target harness formats (such as GitHub Copilot, Cursor,
Claude Code, Antigravity, OpenCode, and Kilo).

However, harnesses have differing communication models, execution constraints, and UI capabilities:
- **GitHub Copilot**: Subagents execute in isolated turns without a direct channel to prompt the user
  interactively (e.g. no interactive question tools or modals). They must record open questions in a
  plan file and hand them back to the orchestrator via a question hand-back protocol.
- **Cursor / Antigravity / CLI tools**: Agents and subagents can often prompt the user directly or
  invoke interactive questioning tools (e.g. interactive multi-choice question tools, modals, or CLI prompts).

Currently, instructions like those in [`architect.md`](../../products/kyber-squad/agents/architect.md)
hardcode Copilot-specific constraints into the canonical agent body (for example, lines 36–39 and
58–59 instructing the agent that it cannot prompt the user and must hand questions back to the
orchestrator). When rendered for harnesses that do support user interaction, these instructions
unnecessarily restrict the agent.

Kyber-Squad needs a mechanism to conditionally include, exclude, or swap instruction blocks based on
the target harness (or harness capabilities) during rendering.

## What is known

- **Canonical agent representation**: Agents are defined as Markdown files with YAML frontmatter
  (`schema: kyber-squad.agent/v1`) in `products/kyber-squad/agents/`.
- **Instruction body handling**: `SquadSourceLoader` reads the Markdown instruction body into
  `SquadAgent.InstructionBody` and computes `SquadAgent.BodyDigest`.
- **Renderer pipeline**: `ISquadRenderer` implementations (such as `CopilotRenderer`) take a
  `SquadSource` and emit target-specific deployment files (`SquadDeploymentFile`).
- **Existing target differentiation**: Target differentiation currently exists in frontmatter
  lowering (e.g. `SquadModelProfile.HarnessModels` mapping logical model profiles to target-specific
  models, and `SquadCapabilityProfile` lowering permissions onto target tool allow-lists). However,
  the instruction body text itself is currently treated as static and unconditioned.

## What needs deciding

1. **Directive / Templating syntax in Markdown**:
   - What authoring syntax should canonical agents use to mark conditional blocks?
   - *Option A (HTML comment preprocessor directives)*:
     ```markdown
     <!-- if harness:copilot -->
     - Interview relentlessly via the question hand-back protocol, never by prompting the user directly.
     <!-- else -->
     - Interview relentlessly about every important aspect of the plan using interactive question tools.
     <!-- endif -->
     ```
     *Benefit*: Standard Markdown renderers and linters ignore HTML comments, keeping the raw document readable.
   - *Option B (Slot / Macro replacement)*:
     Define placeholders in the body (e.g. `{{harness_question_protocol}}` or `<harness-question-protocol>`)
     and resolve them from a shared template registry or harness profile.
   - *Option C (Section/Block level metadata)*:
     Specify inserts in agent YAML frontmatter or separate partial files and stitch them into named anchor points.

2. **Target naming vs Feature/Capability traits**:
   - Should conditional logic key directly on concrete harness names (e.g. `copilot`, `cursor`, `claude-code`, `antigravity`), or on semantic harness capability traits (e.g. `feature:interactive-prompts`, `feature:subagent-delegation`), or allow both?
   - Semantic traits allow new harnesses to opt into shared behavior profiles without editing every agent definition.

3. **Where insert content is defined**:
   - Are harness-specific blocks authored directly inline within the canonical agent Markdown files, or stored in a centralized directory / manifest (e.g. `products/kyber-squad/inserts/` or `harness-profiles.yml`)?

4. **Default fallback behavior**:
   - When rendering for a harness with no explicit block or matching condition, does the engine emit a default block, omit the section, or raise a validation error?

5. **Instruction body digest and degradation tracking**:
   - How does conditional rendering affect `BodyDigest` and degradation tracking?
   - Should `SquadDegradationRecord` track when harness-specific conditional adaptations occur, or should `BodyDigest` be computed per-target post-render?

6. **Scope**:
   - Does conditional insertion apply only to canonical agents (`SquadAgent`), or also to canonical skills (`SquadSkill` / `SKILL.md`)?

## How to verify

- Rendering `architect.md` for Copilot includes the Copilot question hand-back protocol and user-prompting restrictions.
- Rendering `architect.md` for harnesses supporting direct user interaction includes interactive prompting instructions or omits the Copilot-specific restrictions.
- Unrelated harnesses do not receive Copilot-specific instruction blocks.
- `squad pack`, `squad render`, and `squad validate` execute deterministically across all supported targets.
- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj` passes.
- `kyber-weave docs validate .` and `kyber-weave docs drift .` report zero findings.
