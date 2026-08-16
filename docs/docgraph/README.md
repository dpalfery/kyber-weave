---
id: docgraph-index
title: DocGraph — Graph-First Governed Knowledge for AI Agents
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-15
---

# DocGraph — Graph-First Governed Knowledge for AI Agents

> **Transform markdown repositories into a typed, verified in-memory knowledge graph joined to live code.**

AI coding assistants are notoriously vulnerable to stale documentation, unverified architectural claims, and hallucinated API routes. When an agent searches raw Markdown via naive vector similarity (RAG) or text grep, it retrieves outdated snippets, superseded designs, and broken symbol references—polluting its reasoning context.

**DocGraph** replaces unguided text search with a strongly typed, strictly governed in-memory documentation graph joined dynamically at query time to a live CodeGraph index.

---

## Why DocGraph?

Adopting DocGraph solves fundamental challenges engineering teams face when using AI agents on large codebases:

### 1. Zero Stale Documentation (Enforceable Code-to-Doc Joins)
Documentation that cannot be verified drifts within weeks. In DocGraph, every document declares its component ownership, source root, and the exact code symbols (`code-refs`) or API endpoints (`api-endpoints`) it claims to describe. When a symbol is renamed or moved in source code, `kyber-weave docs drift` catches it in CI.

### 2. High-Precision MCP Retrieval without Vector Database Overhead
Traditional RAG requires vector databases, embeddings, and complex synchronization pipelines. DocGraph compiles your Markdown corpus into a typed in-memory graph with **sub-millisecond retrieval** served directly to agents over the Model Context Protocol (MCP via `docs_explore`). Ranking is governed by declared document authority, ontology types, and dynamic CodeGraph hops—not raw keyword matches.

### 3. Graph-First Claim Analysis & Managed Terminology
As systems evolve, conflicting assertions and terminology drift proliferate across team docs. DocGraph's analysis engine (`kyber-weave docs integrity-check`) extracts and compares claims across related components, detecting contradictions, duplicate assertions, and unapproved jargon before they confuse human developers and agents alike.

---

## Core Capabilities

| Capability | How It Solves the Problem | Command / MCP Tool |
|---|---|---|
| **Ontology & Schema Gates** | Validates frontmatter, required keys, closed vocabularies, and catalog components across all documentation. | `kyber-weave docs validate` |
| **Code Join Drift Detection** | Reconciles declared `code-refs` and `source-root` against live CodeGraph SQLite indices. | `kyber-weave docs drift` |
| **Sub-Millisecond Agent Search** | Serves token-budgeted, rank-boosted document excerpts and live code joins to AI agents via MCP. | `docs_explore` · `docs_for_symbol` |
| **Managed Glossary & Terminology** | Enforces canonical sense rows, extracts domain concepts, and flags ambiguous aliases across docs. | `kyber-weave docs glossary` |
| **Claim Conflict & Review** | Employs graph-first extraction to identify duplicates, contradictions, and outdated claims for review. | `kyber-weave docs integrity-check` |

---

## Jump In

Explore the comprehensive DocGraph documentation suite:

* **[Adopting DocGraph](onboarding.md)** — Step-by-step guide to initializing, cataloging, and gating documentation in existing repositories.
* **[Architecture](architecture.md)** — The two-clock reload mechanism, in-memory graph index, and dynamic CodeGraph symbol joins.
* **[Retrieval & Ranking](retrieval.md)** — Scoring algorithms, authority weighting, doc-type boosts, and token budgeting.
* **[Analysis & Claim Review](analysis.md)** — Graph-first duplicate/conflict detection, agent-assisted verdicts, and managed glossary.
* **[Governance Gates](governance.md)** — CI/CD enforcement with `docs validate`, `docs drift`, and SARIF reporting.
* **[MCP Server Runbook](mcp-runbook.md)** — Configuring and connecting the Kyber-Weave MCP server to Claude, Cursor, Antigravity, and other clients.
