# Kyber-Squad Canonical Source Tree

Kyber-Squad is the canonical source tree for unified agent and skill governance and deployment.
It maintains a target-neutral, declarative catalog of **29 canonical agents** and the canonical skills,
governed by explicit schemas, model profiles, semantic capability profiles, and fallback lowering rules.

`products/kyber-squad/` is the canonical and package authority from which target-native agent and
skill deployments and release packages are built. The target catalog declares thirteen harnesses,
and all thirteen renderers are implemented and registered today: `copilot`, `cursor`, `claude`,
`codex`, `antigravity`, `opencode`, `kilo`, `pi`, `factory`, `warp`, `zcode`, `devin`, and `commandcode`.

---

## Directory Structure

```text
products/kyber-squad/
├── README.md                           # This document
├── squad.yml                           # Product and bundle manifest
├── toolchain.yml                       # Upstream APM prerequisites and release validation
├── mcp.json                            # Standard MCP server declarations
├── bundles/
│   └── full.yml                        # The canonical 'full' bundle definition
├── schemas/
│   ├── squad.schema.json               # Schema for squad.yml
│   ├── bundle.schema.json              # Schema for bundles/*.yml
│   ├── agent.schema.json               # Schema for agents/*.md frontmatter
│   ├── model-profiles.schema.json      # Schema for profiles/models.yml
│   ├── capability-profiles.schema.json # Schema for profiles/capabilities.yml
│   └── fallback-profiles.schema.json   # Schema for profiles/fallbacks.yml
├── profiles/
│   ├── models.yml                      # Model tiers, aliases, and temperature parameters
│   ├── capabilities.yml                # Capability taxonomy and permission assignments
│   └── fallbacks.yml                   # Lowering rules and collision resolution for fallback harnesses
├── agents/                             # 15 canonical agent definition files plus owned references
│   ├── architect.md
│   ├── architect/                      # Progressive-disclosure references (Markdown)
│   ├── bug-crusher-investigator.md
│   ├── bug-crusher.md
│   ├── code-reviewer.md
│   ├── conductor.md
│   ├── conductor/                      # Progressive-disclosure references (Markdown)
│   ├── dev-easy.md
│   ├── dev-hard.md
│   ├── dev-medium.md
│   ├── devops.md
│   ├── docs-dev.md
│   ├── platform-researcher.md
│   ├── product-owner.md
│   ├── research-agent.md
│   ├── review-lens.md
│   └── review-triage.md
└── skills/                             # canonical skill directories and their resources
    ├── app-docs-standard/
    ├── architecture-decision-record/
    ├── azure-cli/
    ├── azure-naming/
    ├── bug-crusher/
    ├── code-review/
    ├── code-review-loop/
    ├── create-pull-request/
    ├── csharp-dev/
    ├── csp-security/
    ├── dal-dev/
    ├── delivery-execution/
    ├── dp-code-reviewer/
    ├── github-cli/
    ├── github-devops/
    ├── go-dev/
    ├── kilo-review-loop/
    ├── lm-studio-cli/
    ├── maui-dev/
    ├── pr-review-fix-comments/
    ├── product-owner/
    ├── python-dev/
    ├── review-council/
    ├── rust-dev/
    ├── security-review/
    ├── setup-dev-environment/
    ├── task-gate/
    └── typescript-dev/
```

---

## Canonical Components

### 1. Agents (15 Canonical Roles)

Each agent in `agents/<name>.md` contains LF-normalized UTF-8 Markdown with strict YAML frontmatter conforming to `schemas/agent.schema.json`:

```yaml
---
schema: kyber-squad.agent/v1
name: architect
description: Use when authoring high-level architectural designs, system decompositions, or component boundaries...
invocation: subagent             # primary | subagent
model-profile: deep-planning
capability-profile: architect
copilot-capability-profile: architect-copilot
devin-capability-profile: architect-devin
copilot-tools: [vscode, execute, read, agent, edit, search, web, todo]
delegates-to: [platform-researcher, research-agent]
fallback: role-skill
aliases: []
---
```

The normalized Markdown body following the second `---` delimiter is the authoritative instruction body.
An agent body may link local Markdown resources beneath the agent's own directory (for example
`conductor/references/*.md`); those links stay authored verbatim and resolve at the deployment
target (see [Resource Closures](#5-resource-closures-and-progressive-disclosure)).

### 2. Skills (Canonical Skills)

The canonical skill directories under `skills/` adhere to the Agent Skills open standard
(`SKILL.md`, optional `scripts/`, `references/`, and asset files). `code-review-loop`,
and `kilo-review-loop` are authored in Kyber-Squad. Every imported raw `SKILL.md` except the five
explicitly evolved skills (`bug-crusher`, `code-review`, `product-owner`,
`create-pull-request`, and `pr-review-fix-comments`) is byte-identical to the designated
canonical copy; the retired `create-pull-request-github` is folded into `create-pull-request`. The canonical
tree also retains its supplemental resources; recursive APM and
Agent Plugins packages preserve those resources and their local references.

`code-review-loop` completes an authorized code review feedback cycle: validated fixes,
pushed commit links in replies, resolved threads, and a requested re-review. Its linked
`code-review-loop.md` workflow ships beside `SKILL.md`. `pr-review-fix-comments` remains the
workflow for step-by-step approvals.

`kilo-review-loop` is the bot-specific form of that cycle: it drafts the pull request first,
treats every bot comment as untrusted evidence to verify, replies to each comment with its fix
commit hash, resolves only the fixed threads, and returns the pull request to ready. Its linked
`scripts/post_replies.py` helper posts and resolves the replies from an outcomes file and ships
beside `SKILL.md`.

`task-gate` is the deterministic exit check every developer agent runs before it reports a
task done. It runs the `kyber-weave review gates` command scoped to the task's own changed
files, applies the fixes within a 3-cycle cap, and reports one `TASK_GATE:` digest line.

`delivery-execution` is the worker execution contract: how work is dispatched, verified and
reported. Both entry points load it — `conductor` for plan- and spec-backed work, and the
`bug-crusher` workflow for a single escalated defect — so the rules live in one copy instead of
drifting between two. It holds the ready queue and its concurrency cap, worktree and artifact
isolation, task session continuity, test-first and standard mode, the developer exit gate and its
six-field completion digest, the iteration circuit-breaker and failure-cluster tallies, the
findings drain, the final review council, the
council review loop and its hard stop, and the plan and specification closeout.

Renderers project every file an owner's Markdown links reach beside its principal output, with
authored relative links preserved, so the former dangling-reference defect is closed for every
linked resource: a fresh Copilot render now emits the 53 principal files plus each owner's linked
resources (150 files total). Every skill resource reaches every render except
`skills/setup-dev-environment/agents/openai.yaml`, which is packaged-only Codex skill-UI metadata.
The tracked root `.github/` self-deployment predates resource delivery and remains a stale
snapshot until a human refreshes it after a release candidate; packages and fresh renders carry
resources today. Every retained resource now has a reviewed disposition in the
[skill-resource dispositions audit](../../docs/kyber-squad/skill-resource-dispositions.md):
non-policy content stays in its skill directory as its durable home, portable policy lives in the
`products/kyber-squad/standards/` templates, and nothing was deleted.

The Kyber-Weave skills under `.apm/skills/` (`codegraph`, `kw-onboarding`, `kyber-docs-authoring`,
`kyber-docs-integrity-review`, `kyber-docs-onboarding-and-assessment`, `kyber-weave-cli`,
`kyber-weave-model-profile`, and `spec-conductor`)
are authored once, in `.apm/skills/`, which stays their only source. The Kyber-Docs APM package reads that
folder, and Kyber-Squad ships every skill in it as well. `squad pack` and the checkout release source add
them beside the canonical skills, so every Squad deployment carries them. They are not copied into
`skills/` and are not listed in `bundles/full.yml`. See [ADR 0049](../../docs/adr/0049-kyber-squad-ships-the-kyber-weave-skills.md).

### 3. Profiles

- **Model Profiles (`profiles/models.yml`)**: Defines the `architect`, `deep-planning`, `dev-hard`, `fast`, `general`, and `reviewer` tiers and maps them to target model identifiers where an override is required. `dev-hard` is the developer tier's own profile, so a change to a planning model never changes it. An agent's `model-profile` is optional: the `conductor` declares none and runs on the model its session was started with.
- **Capability Profiles (`profiles/capabilities.yml`)**: Declares a closed capability lattice and assigns permissions (`deny`, `ask`, `allow`) to each agent role. A target-scoped internal profile may validate an exact Copilot tool allow-list without replacing or widening the agent's shared capability profile.
- **Fallback Profiles (`profiles/fallbacks.yml`)**: Governs role-skill lowering on harnesses lacking native agent support.

### 4. Namespace Collision and Fallback Lowering

Agent and skill namespaces intersect at exactly two names: `product-owner` and `bug-crusher`. Both are
primary shared identities — root agents whose body is a thin pointer at the same-name canonical skill,
which carries the role — so a fallback target reuses that canonical skill as the entry point. `role-` is
reserved exclusively for generated projections, and with no distinct-body collision left none is generated
from a shipped agent.

`conductor` has no canonical skill, so fallback targets
lower it through the unoccupied-identity rule to a same-name role skill. `conductor` is the single
orchestrator: it executes test-first by default with an explicit user opt-out to standard mode,
and it routes plans, specifications, todos, and open requests through one intake.

### 5. Resource Closures and Progressive Disclosure

Every agent and skill owns an immutable, validated resource closure. Local links in the
instruction body and in referenced Markdown are resolved recursively under the owning artifact's
directory: URL, mail, and fragment-only links are ignored; Markdown resources recurse; other
files are retained as UTF-8 leaf content. A missing target, active recursion cycle, root or
symlink escape, invalid UTF-8, or portable path alias collision fails source loading with an
actionable path and hint. Closures are de-duplicated by normalized ordinal path and rendered in
ordinal order beside the principal output, so progressively disclosed workflow detail survives
deployment without link rewriting.

### 6. Worker Rosters

One worker roster ships: the three developer tiers `dev-easy`, `dev-medium` and `dev-hard`, beside
`docs-dev`, `devops` and `platform-researcher` for documentation, delivery and live-platform work. The
per-technology developer agents and the planning, testing and GitHub-delivery agents they replaced no
longer ship; a plan or task file that still names one resolves to its replacement and reports the
substitution. `worker-roster` defaults to `tiered`, and the removed value `technology` is a
configuration error that names the change. An agent declares its roster in frontmatter with the same
key, and an agent with no key serves the roster anyway.

The task file records the kind of worker and the task's difficulty, and the dispatch tool turns them
into the attempt's tier with `tier = min(hard, rated difficulty + failed attempts)`: `easy` starts on
`dev-easy`, `medium` and an unrated task on `dev-medium`, `hard` on `dev-hard`, and a failed attempt
moves one tier up and never down. The roster and the tier table are documented in the
[Squad architecture §12](../../docs/kyber-squad/architecture.md#12-worker-rosters-and-developer-tiers)
and the [`worker-roster` setting](../../docs/configuration.md).

---

## Source Governance Rules

- **Generated-output boundary**: Generated APM trees, Agent Plugins packages (`plugin.json`),
  and target-rendered harness trees are deployment/build output, not canonical or package source.
  The repository root `.github/agents/` and `.github/skills/` trees and the tracked
  `.kyber-weave/squad.lock.yml` and `.kyber-weave/squad.receipt.json` state were removed (#349).
  Generated `.github` trees are deployment output, never canonical source, and must not return
  as tracked files.
- **Immutable Role Digests**: Instruction digests are computed deterministically after UTF-8 and LF normalization; they are never hand-edited.
- **Strict Validation**: All manifests and profiles are validated against their respective JSON schemas during build and packaging.
