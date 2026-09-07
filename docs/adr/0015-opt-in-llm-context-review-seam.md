---
id: adr/0015-opt-in-llm-context-review-seam
title: Opt-In LLM Context Review Seam and Finding Isolation
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-05
---

# ADR 0015: Opt-In LLM Context Review Seam and Finding Isolation

## Status

Accepted, 2026-09-05. Extends [ADR 0012](0012-progressive-disclosure-6-level-diagnostic-spine.md), [ADR 0013](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md), and [ADR 0014](0014-unclipped-turn-inspection-and-copy-out-protocol.md).

## Context

While deterministic signal detection identifies concrete structural defects (duplicate schemas, post-cache churn, uninvoked tools), developers tuning complex prompts frequently seek subjective evaluation: identifying subtle prompt redundancies, suggesting instruction refactorings, or evaluating conversational flow.

However, integrating LLM-based analysis presents serious privacy and architectural hazards:
1. **Unintended egress**: Automatically transmitting assembled codebase prompts and conversation logs to external cloud APIs violates local-first privacy guarantees.
2. **Hallucinated findings corrupting telemetry**: LLM output is non-deterministic and can easily confuse absence of telemetry with efficiency, invent arbitrary composite grades, or advise deleting essential edge-case instructions.
3. **Fragile external dependencies**: Coupling core diagnostic workflows to external API availability or hard-coded provider keys causes crashes when offline or unconfigured.

## Decision

1. **Opt-In, Explicit User Action Required (D10)**:
   - No context or prompt text leaves the developer's machine automatically or on background schedules.
   - Every review invocation requires a discrete, intentional user click in the Context Review Panel (`ContextReviewPanel.tsx`).
   - The panel displays the exact payload size and target destination before transmission.
2. **Configurable Provider Architecture**:
   - Supported providers include Anthropic, OpenAI-compatible endpoints (including local Ollama / vLLM runners), and a fallback `NullProvider`.
   - The default configuration is "no provider configured". When unconfigured, the UI clearly presents setup instructions rather than failing or throwing exceptions.
   - Provider credentials are read from environment variables or local configuration files and are never persisted to telemetry tables or logs.
3. **Rigorous Review System Prompt Constraints**:
   - The review prompt strictly enforces Kyber-Weave documentation and diagnostic principles:
     - Must clearly separate verified measurements from subjective inferences.
     - Must never describe unobserved context as "waste" or "unnecessary".
     - Must never treat missing telemetry as zero.
     - Must prefer relocation (caching breakpoints, progressive disclosure) over deletion (D8).
     - Must state potential outcome risks for every recommendation.
     - Must never generate composite efficiency scores or letter grades.
4. **Finding Table Isolation**:
   - Review responses are labelled as external model opinions.
   - Review outputs are **never** written into the canonical `finding` database table in `canon.db`. The canonical findings table remains strictly reserved for deterministic, telemetry-grounded findings (D5).
   - Review text is copyable and kept ephemeral within the review UI session.

## Alternatives Considered

- **Automatic background LLM analysis on session ingest**: Rejected. Violates privacy guarantees, incurs unbudgeted cloud API costs, and fails in offline environments.
- **Blending LLM suggestions into canonical findings**: Rejected. Mixing speculative LLM prose with deterministic findings destroys the calibrated confidence model (D6) and erodes developer trust.
- **Requiring cloud credentials to use KyberDash**: Rejected. The entire KyberDash product operates hermetically offline with zero external cloud dependencies.

## Consequences

- The backend implements `/api/kyber/review` and `/api/kyber/review/status` in `routes.ts` via `runContextReview`.
- The review system prompt is unit-tested (`review.test.ts`) to ensure prompt compliance with relocation discipline and risk communication.
- Developers can safely obtain LLM second opinions on specific prompt blocks or turns without compromising telemetry purity or local-first privacy.

## Related

- [ADR 0012: Progressive Disclosure Six-Level Diagnostic Spine](0012-progressive-disclosure-6-level-diagnostic-spine.md)
- [ADR 0013: Telemetry-Grounded Finding Contracts and Waste Ranking](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md)
- [ADR 0014: Unclipped Turn Inspection and Copy-Out Protocol](0014-unclipped-turn-inspection-and-copy-out-protocol.md)
- [KyberDash architecture](../dash/architecture.md)
- [KyberDash runbook](../dash/runbook.md)
- [Plan: 2026-09-05 Diagnostic Hierarchy and Context Inspector](../plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md)
