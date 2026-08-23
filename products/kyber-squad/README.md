# Kyber-Squad Canonical Source Tree

Kyber-Squad is the canonical source tree for unified agent and skill governance and deployment.
It maintains a target-neutral, declarative catalog of **23 canonical agents** and **26 canonical skills**,
governed by explicit schemas, model profiles, semantic capability profiles, and fallback lowering rules.

This canonical tree is the single source of truth from which target-native agent and skill deployments
are rendered across 10 supported coding harnesses: Codex, Cursor, Claude, GitHub Copilot, OpenCode,
Kilo, Gemini CLI, Antigravity, Warp, and Factory Droids.

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
├── agents/                             # 23 canonical agent definition files
│   ├── architect.md
│   ├── architect-v3.md
│   ├── azure-reader.md
│   ├── bug-crusher-investigator.md
│   ├── code-reviewer.md
│   ├── conductor.md
│   ├── conductor-v3.md
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
│   ├── tauri-dev.md
│   └── test-dev.md
├── skills/                             # 26 canonical skill directories
│   ├── app-docs-standard/
│   ├── architecture-decision-record/
│   ├── azure-cli/
│   ├── azure-naming/
│   ├── bug-crusher/
│   ├── code-review/
│   ├── conductor/
│   ├── conductor-v3/
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
│   ├── second-brain/
│   ├── security-review/
│   ├── setup-dev-environment/
│   └── test-dev/
└── migration/                          # 20 import and baseline verification logs
    └── <agent-name>.md
```

---

## Canonical Components

### 1. Agents (23 Canonical Roles)

Each agent in `agents/<name>.md` contains LF-normalized UTF-8 Markdown with strict YAML frontmatter conforming to `schemas/agent.schema.json`:

```yaml
---
schema: kyber-squad.agent/v1
name: architect
description: Use when authoring high-level architectural designs, system decompositions, or component boundaries...
invocation: subagent             # primary | subagent
model-profile: deep-planning
capability-profile: architect
delegates-to: []
fallback: role-skill
aliases: []
---
```

The normalized Markdown body following the second `---` delimiter is the authoritative instruction body.

### 2. Skills (25 Canonical Skills)

The 26 canonical skill directories under `skills/` adhere to the Agent Skills open standard (`SKILL.md`, optional `scripts/`, `references/`, and asset files). Note that `kyber-weave-docs` is intentionally managed separately under `.apm/skills/kyber-weave-docs/` for Kyber-Docs distribution and is not part of Kyber-Squad.

### 3. Profiles

- **Model Profiles (`profiles/models.yml`)**: Defines abstract model tiers (`deep-planning`, `orchestrator`, `deep-analysis`, `code-generation`, `code-review`, `fast-assist`, `fast-read`) mapped to recommended models, reasoning effort, and temperature parameters.
- **Capability Profiles (`profiles/capabilities.yml`)**: Declares a closed capability lattice (`file-read`, `file-write`, `terminal-execute`, `terminal-subagent`, `docs-read`, `browser-read`, `database-read`, etc.) and assigns permissions (`deny`, `ask`, `allow`) to each agent role.
- **Fallback Profiles (`profiles/fallbacks.yml`)**: Governs role-skill lowering on harnesses lacking native agent support.

### 4. Shared Identities and Collision Lowering

Agent and skill namespaces intersect at exactly 9 names:
1. **Shared Identities (`conductor`, `conductor-v3`)**: Canonical agent and skill have byte-identical instruction bodies. Emitted as a native agent on native harnesses, or as a single same-name skill on fallback harnesses. `conductor` is the default orchestrator; `conductor-v3` is explicit.
2. **Distinct-Body Collisions (`csharp-dev`, `dal-dev`, `github-devops`, `maui-dev`, `product-owner`, `python-dev`, `test-dev`)**: The canonical skill and canonical agent serve distinct purposes. On fallback harnesses, the skill retains `<name>` and the agent projects to `role-<name>`. `role-` is reserved exclusively for generated projections.

### 5. Migration Logs (`migration/`)

The `migration/` directory contains per-agent audit reports recording baseline sources, SHA-256 hashes, retained additions, excluded harness mechanics, resolved permission policies, and final normalized instruction digests.

---

## Source Governance Rules

- **No Tracked Generated Artifacts**: Generated APM trees, Agent Plugins packages (`plugin.json`), and target-rendered files are never committed to this source tree.
- **Immutable Role Digests**: Instruction digests are computed deterministically after UTF-8 and LF normalization; they are never hand-edited.
- **Strict Validation**: All manifests and profiles are validated against their respective JSON schemas during build and packaging.
