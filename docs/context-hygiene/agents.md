---
id: context-hygiene/agents
title: Agent harness governance
doc-type: architecture
status: current
component: ContextHygiene
source-root: src/KyberWeave.Core/Agents
owner: dpalfery
last-reviewed: 2026-08-01
code-refs:
  - AgentLoader
  - AgentSpecValidator
  - AgentPromptScanner
---

# Agent harness governance

Teams run more than one coding harness, and each keeps its own copy of the same agent
roles. Nothing keeps those copies honest. A reviewer role fixed in `.claude` stays broken
in `.cursor`, and the only symptom is that one tool behaves worse than another for reasons
nobody can reproduce.

Agent governance answers to an unusual source of truth: **the sibling copies themselves**.
There is no external spec to conform to, so the invariant is parity.

## The six harnesses

Agent definitions are discovered as `<harness>/agents` beneath the project root:

| Folder | Harness |
|---|---|
| `.codex` | Codex |
| `.cursor` | Cursor |
| `.claude` | Claude |
| `.github` | GitHub Copilot |
| `.opencode` | OpenCode |
| `.kilo` | Kilo |

Formats differ — Markdown with YAML frontmatter, TOML — so `AgentLoader` normalises each
into one `AgentModel` before anything compares them. Every command accepts `--harness` to
narrow to one.

## Commands

| Command | What it answers | Gate |
|---|---|---|
| `agent validate` | Are manifests well-formed? | fails on **error** |
| `agent sync-check` | Are roles synchronized across harnesses? | fails on **error** |
| `agent scan` | Are the prompts a safe trust surface? | fails on **critical** (configurable) |
| `agent catalog` | Role × harness parity matrix | — |

## Manifest conformance — `KW-AGENT-SPEC-001`…`-004`

| Rule | Fires when |
|---|---|
| `KW-AGENT-SPEC-001` | The agent has no name |
| `KW-AGENT-SPEC-002` | The agent has no description |
| `KW-AGENT-SPEC-003` | The agent has no instructions |
| `KW-AGENT-SPEC-004` | A referenced file does not resolve |

## Parity and drift — `KW-AGENT-SYNC-*`, `KW-AGENT-LINT-*`

| Rule | Fires when |
|---|---|
| `KW-AGENT-SYNC-001` | A role exists in some harnesses but not others |
| `KW-AGENT-SYNC-002` | The same role carries materially different instructions across harnesses |
| `KW-AGENT-LINT-001` | An agent's description scores too low to route reliably |

`-002` is the one that pays for itself. Two copies of a role that diverged through
independent edits still both look fine in isolation; only comparing them surfaces it.

## Capability profiles

Harnesses are not equivalent — one may support tool restrictions another lacks — so
parity cannot mean byte equality. `HarnessCapabilityProfile` describes what each harness
can express, and hosts override the profiles in
[`.kyber-weave/kyber-weave.yml`](../configuration.md) so a legitimate capability
difference is not reported as drift.

## Known gaps

`agent route`, `agent lint`, and `agent new` exist in Core without CLI verbs. The skill
branch has all three; the agent branch is deliberately behind, not accidentally.

## Related

- [Skill governance](skills.md) — the other half of ContextHygiene
- [Instruction-surface scanning](security-scanning.md) — what `agent scan` runs
- [Configuration](../configuration.md) — overriding harness capability profiles
