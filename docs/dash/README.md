---
id: dash-index
title: KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-09-03
---

# KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows

> **Observe, analyze, and tune agentic context windows with local OpenTelemetry visualization powered by .NET Aspire and multi-surface UI delivery.**

Modern AI coding agents operate through complex, multi-turn execution loops: invoking local tools, delegating tasks across specialized subagents, and ingesting repository context. Without dedicated observability, agent workflows remain opaque black boxes: developers cannot see where token budgets are wasted, which tool calls introduce latency bottlenecks, or when context windows approach saturation thresholds.

**KyberDash** delivers comprehensive observability across four local execution surfaces (Terminal TUI, Web Dashboard, Electron Desktop, and Windows Menubar/Tauri). It ingests OpenTelemetry (OTEL) traces, metrics, and logs streamed from the .NET Aspire dashboard as well as raw session files from 41 agent harnesses, translating raw telemetry into actionable context tuning insights for agentic workflows.

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
| **Multi-Surface UI Delivery** | Provides dedicated Terminal TUI, browser Web Dashboard, Electron Desktop, and Windows Menubar Tray surfaces for flexible developer workflows. |
| **.NET Aspire & Ingest Pipeline** | Ingests OTLP traces streamed from .NET Aspire alongside local session files across 41 agent harnesses. |
| **Interactive Context Heatmap** | Visualizes context window headroom, token accumulation rates, and context pressure across conversation turns. |
| **Tool & Subagent Trace Visualizer** | Displays deep hierarchical call graphs of MCP tool executions, subagent invocations, and return payloads. |
| **Context Tuning Feedback Loop** | Provides empirical metrics to benchmark prompt reductions and skill routing optimizations against historical sessions. |

---

## Documentation Roadmap

KyberDash is under active development. The following technical documentation pages are published or planned in this directory:

* **Operational Runbook ([`runbook.md`](runbook.md))** — Local development, execution runners, and testing procedures across all 4 surfaces (Electron, Tauri, Web, TUI).
* **Architecture ([`architecture.md`](architecture.md))** — Deep dive into the telemetry ingest pipeline, local web architecture, and Aspire OTEL integration.
* **Onboarding & Setup (`onboarding.md`)** — Quickstart guide to configuring .NET Aspire and launching the local KyberDash UI.
* **Context Tuning Playbook (`tuning-playbook.md`)** — Practical guidelines for diagnosing context bloat and optimizing agent workflows using telemetry data.
