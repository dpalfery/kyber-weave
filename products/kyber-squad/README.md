# Kyber-Squad Canonical Source Tree

Kyber-Squad is the canonical source tree for unified agent and skill governance and deployment.
It maintains a target-neutral, declarative catalog of **24 canonical agents** and **24 canonical skills**,
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
├── agents/                             # 24 canonical agent definition files
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
│   ├── task-reviewer-v3.md
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
└── migration/                          # 22 Hotshot import and baseline verification logs
    └── <agent-name>.md
```

---

## Canonical Components

### 1. Agents (24 Canonical Roles)

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

### 2. Skills (24 Canonical Skills)

The 24 canonical skill directories under `skills/` adhere to the Agent Skills open standard
(`SKILL.md`, optional `scripts/`, `references/`, and asset files). Every raw `SKILL.md` is
byte-identical to the designated Hotshot golden copy. The canonical tree also retains 64
supplemental resources, for 88 skill-tree files in total; recursive APM and Agent Plugins
packages preserve those resources and their local references.

Copilot deliberately renders exactly 24 golden skill paths at
`.github/skills/<name>/SKILL.md`, alongside 24 `.github/agents/<name>.agent.md` paths for an exact
48-file tree. It does not render the 64 resources, because the golden tree omits them even though
61 are referenced by its skills. That known dangling-reference defect is contained to the exact
golden render rather than copied into canonical source or packages. The resources remain packaged
until the
[content-preserving migration todo](../../docs/todo/migrate-skill-resources-into-standards.md)
meets its acceptance criteria.

`kyber-weave-docs` is intentionally managed separately under
`.apm/skills/kyber-weave-docs/` for Kyber-Docs distribution and is not part of Kyber-Squad.

### 3. Profiles

- **Model Profiles (`profiles/models.yml`)**: Defines the abstract `deep-planning`, `fast`, `general`, `mai-code-flash`, `orchestration`, and `test-first-orchestration` tiers and maps them to target model identifiers where an override is required.
- **Capability Profiles (`profiles/capabilities.yml`)**: Declares a closed capability lattice and assigns permissions (`deny`, `ask`, `allow`) to each agent role. A target-scoped internal profile may validate an exact Copilot tool allow-list without replacing or widening the agent's shared capability profile.
- **Fallback Profiles (`profiles/fallbacks.yml`)**: Governs role-skill lowering on harnesses lacking native agent support.

### 4. Namespace Collision and Fallback Lowering

Agent and skill namespaces intersect at exactly seven names, and all seven are distinct-body
collisions: `csharp-dev`, `dal-dev`, `github-devops`, `maui-dev`, `product-owner`, `python-dev`,
and `test-dev`. On fallback harnesses, the canonical skill retains `<name>` and the agent
projects to `role-<name>`. `role-` is reserved exclusively for generated projections.

There are no shared product identities. `conductor` and `conductor-v3` exist only as canonical
agents, so fallback targets lower them through the unoccupied-identity rule to same-name role
skills. `conductor` is the default orchestrator; `conductor-v3` is the explicit test-first
orchestrator.

### 5. Migration Logs (`migration/`)

The `migration/` directory contains 22 per-agent Hotshot import reports recording baseline
sources, SHA-256 hashes, retained additions, excluded harness mechanics, resolved permission
policies, and final normalized instruction digests. `review-lens` and `review-triage` are
canonical-only roles and therefore have no Hotshot migration record.

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
