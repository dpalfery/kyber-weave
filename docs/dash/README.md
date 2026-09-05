---
id: dash-index
title: KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-09-04
---

# KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows

> **Observe, analyze, and tune agentic context windows with local OpenTelemetry visualization powered by .NET Aspire and multi-surface UI delivery.**

Modern AI coding agents operate through complex, multi-turn execution loops: invoking local tools, delegating tasks across specialized subagents, and ingesting repository context. Without dedicated observability, agent workflows remain opaque black boxes: developers cannot see where token budgets are wasted, which tool calls introduce latency bottlenecks, or when context windows approach saturation thresholds.

**KyberDash** delivers observability across four local execution surfaces (Terminal TUI, Web Dashboard, Electron Desktop, and Windows Menubar/Tauri). Its canonical `canon.db` store accepts OTLP traces and logs plus supported local session sources. Session APIs read that store only; the retired Python pipeline's `sessions.db` is not a production path.

---

## Why KyberDash?

Optimizing AI agent performance and context quality requires moving from intuition to empirical telemetry. Teams face key observability hurdles:

### 1. The Token "Black Box" & Context Window Bloat
Agents frequently consume hundreds of thousands of tokens per session without clear visibility into what proportion is spent on system prompts, skill instructions, tool parameters, or conversation history. KyberDash provides granular, turn-by-turn token breakdowns to identify bloat immediately.

### 2. Unseen Latency Bottlenecks & Multi-Agent Handoff Failures
In complex workflows involving orchestrators, subagents, and MCP servers, a slow tool execution or a circular subagent delegation loop can stall development. KyberDash visualizes execution timelines and distributed call trees to pinpoint bottlenecks and failed handoffs.

### 3. Tuning Instructions Without Empirical Data
Refining agent instructions and skill definitions has historically been guesswork. With KyberDash, developers can measure the direct impact of prompt changes on token efficiency, tool call accuracy, and execution latency across iterative runs.

### 4. Flexible UI Delivery Across 4 Local Surfaces
Observability belongs where the developer already works:
- **Terminal TUI (`dash/src/dashboard.tsx`)**: Instant terminal dashboard with keyboard navigation for command-line workflows.
- **Web Dashboard (`dash/dash/`)**: Standalone browser application for deep session exploration and team sharing.
- **Electron Desktop (`dash/app/`)**: Dedicated desktop window with persistent views and local IPC.
- **Windows Menubar / Tray (`dash/windows/`)**: Lightweight background tray companion for continuous spend monitoring.

---

## Key Capabilities

| Capability | How It Solves the Problem |
|---|---|
| **Multi-Surface UI Delivery** | Provides dedicated Terminal TUI, 5-tab browser Web Dashboard (`[Usage]`, `[Context]`, `[Compare]`, `[Quarantine]`, `[Problems]`), Electron Desktop, and Windows Menubar Tray surfaces. |
| **ASAD Context Dashboard** | The `Context` page renders the Agent Session Analysis Dashboard from canonical session payloads: overview, per-turn spend, context composition, tool/schema cost, execution timeline, cost/token accounting, caveats, and a content-inspector drawer. |
| **Canonical-store projection** | `~/.kyberdash/canon.db` stores rebuildable derived session payloads over canonical records. `KyberBridge` queries that store only. |
| **Multi-signal enrichment** | OTLP logs enrich their correlated span-shaped record rather than creating a second record. When a turn has both OTLP and file evidence, OTLP counters win and file content is used only if OTLP has no parts. |
| **Interactive Context Heatmap** | Visualizes context window headroom, token accumulation rates, and context pressure across conversation turns. |
| **Tool & Subagent Trace Visualizer** | Displays deep hierarchical call graphs of MCP tool executions, subagent invocations, and return payloads. |
| **Context Tuning Feedback Loop** | Provides empirical metrics to benchmark prompt reductions and skill routing optimizations against historical sessions. |

---

## Documentation Roadmap

The following technical documentation pages are published in this directory:

* **Operational Runbook ([`runbook.md`](runbook.md))** — Local development, execution runners, demo bridge, and test suites across all 4 surfaces.
* **Architecture ([`architecture.md`](architecture.md))** — Canonical-store ingest, ASAD session projection, multi-signal enrichment, and REST API contract.
* **Telemetry inventory ([`telemetry-inventory.md`](telemetry-inventory.md))** — Verified per-harness collection and measurability outcomes, including owner-controlled runtime gates.
* **ADR 0006: Soft Fork & Merge Zone ([`../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md`](../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md))** — Core architectural decision for vendored subtree and embedded OTLP receiver.
* **ADR 0007: Agent Session Analysis Integration ([`../adr/0007-kyberdash-agent-session-analysis-integration.md`](../adr/0007-kyberdash-agent-session-analysis-integration.md))** — Single coherent session view in Context, 5-tab topology, and dual-database bridge.
* **ADR 0008: Single Canonical Store ([`../adr/0008-kyberdash-single-canonical-store.md`](../adr/0008-kyberdash-single-canonical-store.md))** — Retires the Python-store fallback.
* **ADR 0009: Multi-Signal Ingestion ([`../adr/0009-multi-signal-ingestion-span-shaped-record.md`](../adr/0009-multi-signal-ingestion-span-shaped-record.md))** — Establishes log enrichment, non-model quarantine, and source precedence.
