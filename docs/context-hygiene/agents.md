---
id: context-hygiene/agents
title: Agent harness governance
doc-type: architecture
status: current
component: ContextHygiene
source-root: src/KyberWeave.Core/Agents
owner: dpalfery
last-reviewed: 2026-09-24
code-refs:
  - AgentLoader
  - AgentSpecValidator
  - AgentPromptScanner
  - AgentSyncLinter
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

The table is the six named `AgentLoader` mappings. `DiscoverHarnessAgentDirs` still scans
every dot-prefixed `*/agents` directory; unmapped folders, including `.factory` and `.pi`,
load as `HarnessKind.Custom`. Kyber-Squad renderer output is a deployment surface, not this
inventory.

Formats differ — Markdown with YAML frontmatter, TOML — so `AgentLoader` normalises each
into one `AgentModel` before anything compares them. Every command accepts `--harness` to
narrow to one.

## Deployment control plane with Kyber-Squad

While `agent validate` and `agent sync-check` audit and lint existing on-disk agent definitions
across individual harnesses, **[Kyber-Squad](../kyber-squad/architecture.md)** provides the
authoritative, end-to-end deployment control plane. Kyber-Squad maintains 21 canonical agent
definitions in `products/kyber-squad/` and compiles them into target-native configurations
for all ten currently implemented and registered renderers: `copilot`, `cursor`, `claude`, `codex`,
`antigravity`, `opencode`, `kilo`, `pi`, `factory`, and `warp`.

The current agent namespace intersects the 24-skill namespace at seven names, all distinct-body
collisions. There are no shared product identities. Fallback targets preserve each colliding skill
and lower its agent to `role-<name>`; unoccupied agents, including `conductor`, lower to
same-name role skills.

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

### Instruction-body file reference validation — `KW-AGENT-SPEC-004`

`AgentSpecValidator` scans both the agent's `description` and `instructions` fields for file references and raises `KW-AGENT-SPEC-004` (severity: Error) when a path does not resolve.

**Reference patterns**:
- **Markdown links**: `[text](path/to/file)` — extracted from `LinkInline` AST nodes via Markdig parser
- **Inline backtick paths**: `` `relative/path/to/file` `` — the entire parsed inline-code span must match `@"\A(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)\z"`, scoped to conventional subdirectories (`scripts/`, `references/`, `assets/`)

**Excluded patterns** (not treated as file references):
- HTTP/HTTPS URLs: `http://`, `https://`
- Anchor-only links: starting with `#`
- Mailto links: `mailto:`
- Path traversal attempts: containing `..`
- Config Reg tokens: `<property-name>` (e.g., `<docs-root>`, `<plan-index>`)
- Absolute filesystem paths: `/absolute/path`, Windows drive paths, and UNC paths

**Resolution**: Relative to the agent's `DirectoryPath` (the folder containing the agent definition file). Paths beginning with `./` are normalized before resolution. If the agent has no directory path, the check is skipped.

Reference deduplication follows the agent directory's filesystem case rules, so differently cased paths remain separate when the filesystem distinguishes them.

**Hint text**: When a reference does not resolve, the check attempts to find the nearest existing file or directory using Levenshtein distance (threshold ≤ 3 edits), searching only the referenced subdirectory, so suggestions never point outside the agent directory. Candidate suggestions are relative to the agent directory. When the subdirectory is missing, empty, or cannot be enumerated, fallback text directs the author to check spelling relative to the agent definition directory.

## Parity and drift — `KW-AGENT-SYNC-*`, `KW-AGENT-LINT-*`

| Rule | Fires when |
|---|---|
| `KW-AGENT-SYNC-001` | A role exists in some harnesses but not others |
| `KW-AGENT-SYNC-002` | The same role carries materially different instructions across harnesses |
| `KW-AGENT-LINT-001` | An agent's description routing score is below threshold (< 50/100) |
| `KW-AGENT-LINT-002` | An agent's description is an action summary or lacks trigger phrasing |

`KW-AGENT-SYNC-002` is the one that pays for itself. Two copies of a role that diverged through
independent edits still both look fine in isolation; only comparing them surfaces it.

### Agent description trigger quality

Multi-agent orchestrators use agent manifest descriptions as delegation triggers. When
deciding whether to delegate a task to a specialized subagent, orchestrators match prompt
intent against the trigger conditions defined in the description.

Descriptions that only explain what the agent does (e.g. `"Builds C# projects and runs unit
tests"`) fail to specify *when the orchestrator should delegate to it*. Manifest descriptions
should lead with explicit trigger framing and negative boundaries:

- **Trigger clause**: `"Use when authoring or running .NET unit tests. Do NOT use for editing application source code."`
- **Lint rule `KW-AGENT-LINT-002`**: Emits a Warning when an agent description lacks an
  explicit trigger condition (`"Use when..."`, `"Invoke when..."`, `"Trigger when..."`).
- **Lint rule `KW-AGENT-LINT-001`**: Emits an Info finding when an agent description scores
  below 50 on the [DescriptionScorer rubric](skills.md#descriptionscorer-rubric).
- **Review exchange**: Agent descriptions can be exported alongside skills through the
  `SkillReviewExchange` Core seam for LLM/agent-assisted semantic trigger review — a CLI
  verb for it does not exist yet (`docs review export` exports documentation candidates
  only). Candidate ids are `{Harness}:{RoleName}` so a role present in multiple harnesses is
  not collapsed.

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
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — multi-harness deployment control plane
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — deploying canonical agent squads
- [Instruction-surface scanning](security-scanning.md) — what `agent scan` runs
- [Configuration](../configuration.md) — overriding harness capability profiles
