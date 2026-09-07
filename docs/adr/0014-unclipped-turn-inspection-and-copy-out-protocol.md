---
id: adr/0014-unclipped-turn-inspection-and-copy-out-protocol
title: Unclipped Turn Inspection, Content Retention Window, and Copy-Out Protocol
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-05
---

# ADR 0014: Unclipped Turn Inspection, Content Retention Window, and Copy-Out Protocol

## Status

Accepted, 2026-09-05. Extends [ADR 0008](0008-kyberdash-single-canonical-store.md) and [ADR 0011](0011-asad-only-context-view-and-payload-contract.md).

## Context

Prior context analysis tools visualized token consumption in abstract charts (e.g. "Turn 4: 42,000 tokens in system prompt and tools") but provided no mechanism to read the actual text that comprised those tokens. Developers trying to tune agent prompts, debug tool result truncation, or eliminate redundant instruction headers had no path from the chart to reading the text, and no mechanism to copy the assembled prompt out into an editor.

At the same time, storing the complete plaintext of every prompt, tool parameter, MCP response, and repository file seen by an agent introduces significant data liability. An unbounded local SQLite database retaining uncompressed, unmanaged plaintext prompts across months of development poses credential exposure and privacy risks.

## Decision

1. **Unclipped Turn Context Inspection (D4)**:
   - Every semantic context block (system prompt, conversation history, tool definitions, tool results, instructions) is readable in full within the progressive-disclosure Context Inspector (`ContextInspector.tsx`).
   - Clicking any composition band loads the full assembled block subdivided into its constituent parts via `GET /api/kyber/session/:id/turn/:index/content` and `GET /api/kyber/session/:id/content`.
   - When server-side content budgeting applies, the UI explicitly labels shown versus total character length; clipped text is never presented as complete.
   - Blocks that are not measurable declare their unavailability reason explicitly and render no content area (preventing placeholder confusion).
2. **Whole-Turn and Per-Block Copy-Out Protocol**:
   - The inspector provides single-click copying for individual parts, entire semantic blocks, and whole-turn assembled context.
   - Whole-turn copy formats content into clean, headed plain text with clearly demarcated sections, ready for inspection in a text editor or test harness.
   - Clipboard operations support robust fallbacks where browser async clipboard permissions are restricted.
3. **Rolling Content Retention Window (D14)**:
   - Full plaintext context parts stored in `canon.db` are governed by a default **14-day rolling retention window**.
   - After 14 days, raw content blocks and `parts_json` are pruned while preserving token usage metrics, span timestamps, session summaries, and diagnostic findings.
4. **Explicit CLI Content Purge Operation**:
   - An explicit CLI command (`kyber purge-content`) and database maintenance routine allows users to immediately zero out stored plaintext content across all sessions or specific runs without losing statistical history or metric accounting.

## Alternatives Considered

- **Hash-only storage without plaintext content**: Rejected. Hashing avoids local plaintext storage but completely destroys prompt inspection, forcing developers to blindly guess why a prompt or tool result was oversized.
- **Unbounded permanent plaintext storage**: Rejected. Accumulating months of sensitive codebase context and LLM conversational history on local workstations creates unacceptable operational and security liabilities.
- **Client-only truncation without server budgets**: Rejected. Transporting multi-megabyte raw prompt bodies over HTTP without server-side bounds risks freezing browser tabs and Electron renderers.

## Consequences

- The backend bridge implements `assembleTurnContent` and `getSessionContent` with explicit budget parameters and compression handling.
- `CanonStore` manages compressed `parts_json` columns and exposes content purge routines.
- Developers gain immediate end-to-end visibility into exact prompt composition with seamless export into local editing environments.

## Related

- [ADR 0008: Single Canonical Store](0008-kyberdash-single-canonical-store.md)
- [ADR 0011: ASAD as the Only Context View and as the Canonical Session Contract](0011-asad-only-context-view-and-payload-contract.md)
- [ADR 0013: Telemetry-Grounded Finding Contracts and Waste Ranking](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md)
- [KyberDash architecture](../dash/architecture.md)
- [KyberDash runbook](../dash/runbook.md)
- [Plan: 2026-09-05 Diagnostic Hierarchy and Context Inspector](../plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md)
