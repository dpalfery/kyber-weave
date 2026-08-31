# Kyber-Squad Canonical Source Tree

Kyber-Squad is the canonical source tree for unified agent and skill governance and deployment.
It maintains a target-neutral, declarative catalog of **21 canonical agents** and **24 canonical skills**,
governed by explicit schemas, model profiles, semantic capability profiles, and fallback lowering rules.

`products/kyber-squad/` is the canonical and package authority from which target-native agent and
skill deployments and release packages are built. The target catalog declares nine harnesses.
Five renderers are implemented and registered today: `copilot`, `cursor`, `claude`, `codex`, and
`antigravity`. The other four declared targets—`opencode`, `kilo`, `warp`, and `factory`—fail
renderer-coverage preflight.

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
├── agents/                             # 21 canonical agent definition files plus owned references
│   ├── architect.md
│   ├── architect/                      # Progressive-disclosure references (Markdown)
│   ├── azure-reader.md
│   ├── bug-crusher-investigator.md
│   ├── code-reviewer.md
│   ├── conductor.md
│   ├── conductor/                      # Progressive-disclosure references (Markdown)
│   ├── csharp-dev.md
│   ├── dal-dev.md
│   ├── docs-dev.md
│   ├── github-devops.md
│   ├── maui-dev.md
│   ├── product-owner.md
│   ├── pulumi-dev.md
│   ├── python-dev.md
│   ├── react-dev.md
│   ├── research-agent.md
│   ├── review-lens.md
│   ├── review-triage.md
│   ├── sql-database-architect.md
│   ├── task-reviewer.md
│   ├── task-reviewer/                  # Progressive-disclosure references (Markdown)
│   ├── tauri-dev.md
│   └── test-dev.md
├── skills/                             # 24 canonical skill directories; 88 recursive files
│   ├── app-docs-standard/
│   ├── architecture-decision-record/
│   ├── azure-cli/
│   ├── azure-naming/
│   ├── bug-crusher/
│   ├── code-review/
│   ├── create-pull-request/
│   ├── create-pull-request-github/
│   ├── csharp-dev/
│   ├── csp-security/
│   ├── dal-dev/
│   ├── dp-code-reviewer/
│   ├── github-cli/
│   ├── github-devops/
│   ├── lm-studio-cli/
│   ├── maui-dev/
│   ├── pr-review-fix-comments/
│   ├── product-owner/
│   ├── python-dev/
│   ├── resharper-clt/
│   ├── second-brain/
│   ├── security-review/
│   ├── setup-dev-environment/
│   └── test-dev/
└── migration/                          # 19 Hotshot import and baseline verification logs
    └── <agent-name>.md
```

---

## Canonical Components

### 1. Agents (21 Canonical Roles)

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
copilot-tools: [vscode, execute, read, agent, edit, search, web, todo]
delegates-to: [azure-reader, research-agent]
fallback: role-skill
aliases: []
---
```

The normalized Markdown body following the second `---` delimiter is the authoritative instruction body.
An agent body may link local Markdown resources beneath the agent's own directory (for example
`conductor/references/*.md`); those links stay authored verbatim and resolve at the deployment
target (see [Resource Closures](#6-resource-closures-and-progressive-disclosure)).

### 2. Skills (24 Canonical Skills)

The 24 canonical skill directories under `skills/` adhere to the Agent Skills open standard
(`SKILL.md`, optional `scripts/`, `references/`, and asset files). Every raw `SKILL.md` except the
two explicitly evolved skills (`product-owner`, `bug-crusher`) is byte-identical to the designated
Hotshot golden copy. The canonical tree also retains 64 supplemental resources, for 88 skill-tree
files in total; recursive APM and Agent Plugins packages preserve those resources and their local
references.

Renderers project every validated resource beside its principal output with authored relative
links preserved, so the former dangling-reference defect is closed at every target: a fresh
Copilot render now emits the 45 principal files plus each owner's resources (113 files total).
The tracked root `.github/` self-deployment predates resource delivery and remains a stale
snapshot until a human refreshes it after a release candidate; packages and fresh renders carry
resources today. Surplus skill content remains packaged until the
[content-preserving migration todo](../../docs/todo/migrate-skill-resources-into-standards.md)
meets its acceptance criteria.

`kyber-weave-docs` is intentionally managed separately under
`.apm/skills/kyber-weave-docs/` for Kyber-Docs distribution and is not part of Kyber-Squad.

### 3. Profiles

- **Model Profiles (`profiles/models.yml`)**: Defines the abstract `deep-planning`, `fast`, `general`, `mai-code-flash`, and `orchestration` tiers and maps them to target model identifiers where an override is required.
- **Capability Profiles (`profiles/capabilities.yml`)**: Declares a closed capability lattice and assigns permissions (`deny`, `ask`, `allow`) to each agent role. A target-scoped internal profile may validate an exact Copilot tool allow-list without replacing or widening the agent's shared capability profile.
- **Fallback Profiles (`profiles/fallbacks.yml`)**: Governs role-skill lowering on harnesses lacking native agent support.

### 4. Namespace Collision and Fallback Lowering

Agent and skill namespaces intersect at exactly seven names, and all seven are distinct-body
collisions: `csharp-dev`, `dal-dev`, `github-devops`, `maui-dev`, `product-owner`, `python-dev`,
and `test-dev`. On fallback harnesses, the canonical skill retains `<name>` and the agent
projects to `role-<name>`. `role-` is reserved exclusively for generated projections.

There are no shared product identities. `conductor` has no canonical skill, so fallback targets
lower it through the unoccupied-identity rule to a same-name role skill. `conductor` is the single
orchestrator: it executes test-first by default with an explicit user opt-out to standard mode,
and it routes plans, specifications, todos, and open requests through one intake.

### 5. Migration Logs (`migration/`)

The `migration/` directory contains 19 per-agent Hotshot import reports recording baseline
sources, SHA-256 hashes, retained additions, excluded harness mechanics, resolved permission
policies, and final normalized instruction digests. `review-lens` and `review-triage` are
canonical-only roles and therefore have no Hotshot migration record. The retired test-first
duplicates (`conductor-v3`, `architect-v3`, `task-reviewer-v3`) have no separate reports: each
was folded into its canonical counterpart's report, which retains the retired source hash and a
refreshed final-body digest for the consolidated instruction body.

### 6. Resource Closures and Progressive Disclosure

Every agent and skill owns an immutable, validated resource closure. Local links in the
instruction body and in referenced Markdown are resolved recursively under the owning artifact's
directory: URL, mail, and fragment-only links are ignored; Markdown resources recurse; other
files are retained as UTF-8 leaf content. A missing target, active recursion cycle, root or
symlink escape, invalid UTF-8, or portable path alias collision fails source loading with an
actionable path and hint. Closures are de-duplicated by normalized ordinal path and rendered in
ordinal order beside the principal output, so progressively disclosed workflow detail survives
deployment without link rewriting.

---

## Source Governance Rules

- **Generated-output boundary**: Generated APM trees, Agent Plugins packages (`plugin.json`),
  and target-rendered harness trees are deployment/build output, not canonical or package source.
  The repository root `.github/agents/` and `.github/skills/` trees are an intentional tracked,
  stale Copilot self-deployment; `.kyber-weave/squad.lock.yml` and
  `.kyber-weave/squad.receipt.json` are its stale tracked state. This synchronization leaves all
  four paths untouched. A human will refresh them after a fresh Kyber-Weave release candidate.
- **Immutable Role Digests**: Instruction digests are computed deterministically after UTF-8 and LF normalization; they are never hand-edited.
- **Strict Validation**: All manifests and profiles are validated against their respective JSON schemas during build and packaging.
