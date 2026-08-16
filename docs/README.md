---
id: docs-index
title: Kyber-Weave documentation
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-15
---

# Kyber-Weave documentation

This corpus is governed by the tool it documents. Every page carries conformant
frontmatter, every `code-refs` entry resolves against a live code index, and
`kyber-weave docs validate .` and `docs drift .` both run clean — see
[the ontology](documentation-ontology.md) for what that means and
[governance](docgraph/governance.md) for how it is enforced.

An agent should reach this corpus through [`docs_explore`](docgraph/mcp-runbook.md) rather
than by reading these files.

## Start here

- [Installing Kyber-Weave](install.md) — one command
- [Adopting DocGraph](docgraph/onboarding.md) — `kyber-weave docs init .`, then retrofit
- [Deploying Kyber-Squad](kyber-squad/onboarding.md) — `kyber-weave squad install`, multi-harness deployments
- [The documentation ontology](documentation-ontology.md) — the opinion everything rests on
- [Component and owner catalog](catalog.md) — the authoritative vocabulary
- [Configuration](configuration.md) — adapting the defaults to your repository

## Feature 1 — DocGraph

The opinionated documentation structure, its conformance gates, graph-first claim analysis,
and the in-memory retrieval graph served to agents over MCP.

| Page | Covers |
|---|---|
| [Adoption](docgraph/onboarding.md) | `docs init`, the authoring skill, retrofitting an existing tree |
| [Architecture](docgraph/architecture.md) | The pipeline, the two-clock reload, the code-graph join |
| [Retrieval and ranking](docgraph/retrieval.md) | Scoring, authority weighting, budgeted excerpts |
| [Analysis and review](docgraph/analysis.md) | Graph-first duplicate/conflict/terminology detection, agent verdicts, managed glossary |
| [Governance gates](docgraph/governance.md) | `docs validate`, `docs drift`, `docs catalog` |
| [MCP server runbook](docgraph/mcp-runbook.md) | Serving the graph to an agent |

## Feature 2 — ContextHygiene

Governance for the artifacts that shape an agent's context: Agent Skills and harness agent
definitions.

| Page | Covers |
|---|---|
| [Skill governance](context-hygiene/skills.md) | Spec conformance, routing readiness, simulation |
| [Agent harness governance](context-hygiene/agents.md) | Parity and drift across six harnesses |
| [Instruction-surface scanning](context-hygiene/security-scanning.md) | The shared security engine |
| [ALM governance playbook](alm-governance-playbook.md) | Operating skills at enterprise scale |

## Feature 3 — Kyber-Squad

Unified multi-harness deployment and lifecycle control plane for 20 canonical agents and
25 skills across 10 coding harnesses with transactional rollback.

| Page | Covers |
|---|---|
| [Adoption & usage guide](kyber-squad/onboarding.md) | `squad install`, `update`, `uninstall`, `status`, `doctor`, `pack`, scopes, target resolution |
| [Architecture](kyber-squad/architecture.md) | AgentIR, permission lattice, role-skill lowering, state store, mutex lease, transaction engine |
| [Requirements & degradation](kyber-squad/requirements.md) | KS-001–KS-008 specifications, structured degradation taxonomy, capability matrix |

## Feature 4 — CI Pipelines

The diagnostic engine every gate reports through, and the workflows that consume it.

| Page | Covers |
|---|---|
| [Architecture](ci-pipelines/architecture.md) | Rule ids, severities, output formats, exit codes |
| [Rule reference](ci-pipelines/rule-reference.md) | Every `KW-*` id in one table |
| [Workflow runbook](ci-pipelines/workflows-runbook.md) | Copy-ready GitHub Actions gates |

## Supporting

- [Installing Kyber-Weave](install.md)
- [Configuration](configuration.md)
- [Distribution and release flow](distribution.md) — maintainer-facing
