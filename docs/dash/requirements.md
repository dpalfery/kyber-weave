---
id: dash/requirements
title: KyberDash requirements and invariants contract
doc-type: requirements
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-16
status: current
decided-by:
  - adr/0008-kyberdash-single-canonical-store
  - adr/0009-multi-signal-ingestion-span-shaped-record
  - adr/0011-asad-only-context-view-and-payload-contract
  - adr/0012-progressive-disclosure-6-level-diagnostic-spine
  - adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking
  - adr/0015-opt-in-llm-context-review-seam
  - adr/0018-kyberdash-content-retention-purge
  - adr/0033-kyberdash-surfaces-are-display-layers
  - adr/0038-post-run-transcript-sync-subagent-hierarchy
  - adr/0039-single-instance-engine
---

# KyberDash requirements and invariants contract

This document states the formal requirements (**KD-001** through **KD-018**) that KyberDash
must hold, and the invariants that follow from them. Each requirement is a current,
enforced property of the product, not an aspiration; the ADR or rule that decided each one
is named so a reader can find the reasoning and the rejected alternatives. Where a
requirement cites a measurement, the measurement is a failure that has already occurred, so
dropping the requirement would reproduce it.

---

## Requirements specifications

| ID | Requirement specification |
|---|---|
| **KD-001** | **One canonical store.** `~/.kyberdash/canon.db` is the only production store. Sessions, runs, executions, findings, and rollups are derived projections over canonical records and are rebuildable without re-ingesting; no legacy `sessions.db` path is a production fallback ([ADR 0008](../adr/0008-kyberdash-single-canonical-store.md)). |
| **KD-002** | **One record per model call, from any signal.** The canonical record is span-shaped. OTLP logs enrich the record they belong to and never insert a parallel one, correlated on `(trace_id, span_id)` and then on session plus a bounded timestamp window ([ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md)). |
| **KD-003** | **Quarantine is counted, never silent.** A non-model span or an unmatched log record is quarantined with the attributes observed, and the quarantine surface counts it. Content that vanishes without a trace is indistinguishable from content never sent ([ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md)). |
| **KD-004** | **Disjoint token classes.** `freshInput + cacheRead + cacheCreation === reportedInput`, with `reasoning` a subset of `output`. Each adapter converts its harness's convention on the way in. A decomposition that is negative or does not reconcile to the reported total rejects the record and writes a problem rather than storing it ([architecture: TokenUsage](architecture.md#tokenusage-and-the-disjoint-class-invariant)). |
| **KD-005** | **Cost carries its basis.** A cost figure travels with a basis — a published table or the harness's own arithmetic — and figures of different bases are never blended into one total silently. A genuine harness-reported figure is never repriced; `no_rate`, `not_billed`, and `out_of_scope` are distinct states ([architecture: CostBlock](architecture.md#costblock-and-cost-basis), [`rules/secondary-cost-display`](../rules/secondary-cost-display.md)). |
| **KD-006** | **Honest unobservability.** A dimension a source does not export is `not_measurable` with a named reason, rendered as `—`, never coerced to `0`. A genuine measured zero is still `0` ([`rules/honest-unobservability`](../rules/honest-unobservability.md), [ADR 0011](../adr/0011-asad-only-context-view-and-payload-contract.md)). |
| **KD-007** | **No composite efficiency score.** Performance is evaluated across six independent dimension vectors; no single scalar, grade, or index is computed or rendered ([`rules/composite-efficiency-ban`](../rules/composite-efficiency-ban.md), [ADR 0012](../adr/0012-progressive-disclosure-6-level-diagnostic-spine.md)). |
| **KD-008** | **Relocation over deletion.** A diagnostic that finds misplaced context recommends moving and progressively disclosing it before recommending removal ([`rules/relocation-over-deletion`](../rules/relocation-over-deletion.md), [ADR 0013](../adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking.md)). |
| **KD-009** | **Grounded findings with evidence.** A context finding carries at least two linked evidence rows and an outcome-risk caveat, and ranks by `waste × risk × confidence` rather than by a raw count ([ADR 0013](../adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking.md)). |
| **KD-010** | **Bounded content, durable raw records.** Full plaintext is retained for a 14-day rolling window and emptied after refresh by `purgeExpiredContent`; `records.raw` is never deleted by the purge ([ADR 0018](../adr/0018-kyberdash-content-retention-purge.md), [ADR 0014](../adr/0014-unclipped-turn-inspection-and-copy-out-protocol.md)). |
| **KD-011** | **Surfaces are display layers.** The tray and web dashboard hold no isolated feature logic, scheduler, or job. Refresh scheduling, clean, import, pause, the shared settings, and the OTLP receiver live in the shared engine (the CLI and the `kyberdash web` server); both surfaces use one `canon.db` and one `/api/kyber/*` layer ([`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md), [ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md)). |
| **KD-012** | **One report model.** Every reporting surface renders one versioned `ContextReport` from one builder, so CLI, REST, and tray agree by construction rather than by a test comparing three implementations ([ADR 0023](../adr/0023-kyberdash-report-model-and-tray-ownership.md)). |
| **KD-013** | **Record-timestamped, checkpointed refresh.** Local-history inclusion is a UTC window `[commandStartedAt − N×7 days, commandStartedAt]` over record timestamps; reruns are checkpointed, idempotent, and additive, never rewriting or deleting `records.raw`. The shipped change-detection store is `source_checkpoint` plus `record_provenance` ([ADR 0016](../adr/0016-kyberdash-harness-source-refresh.md)). |
| **KD-014** | **Declared windows are their own provenance.** A context window a harness declares in its own config is stored with its own provenance; pressure from it is `derived`, never `measured`; window precedence is reported, then declared, then default; a default window is never used for pressure ([ADR 0030](../adr/0030-kyberdash-declared-context-window-provenance.md)). |
| **KD-015** | **Opt-in LLM review, isolated.** Model-based context review runs only on a discrete per-invocation user action, respects relocation and risk constraints, and never writes model text into the canonical finding tables ([ADR 0015](../adr/0015-opt-in-llm-context-review-seam.md)). |
| **KD-016** | **Raw-packet evidence is quarantined, not scrubbed.** A quarantined span keeps the sender's decoded packet, served one row at a time over `GET /api/kyber/quarantine/:spanId`; a per-harness wipe scopes only that harness's quarantine and pending rows ([ADR 0035](../adr/0035-kyberdash-quarantine-raw-packet-evidence.md)). |
| **KD-017** | **Post-run transcript sync is engine-hosted and settled.** The engine reconciles on-disk transcripts with live telemetry after a 30-minute inactivity settlement, times a missing transcript out after 24 hours, and yields immediately when the store refresh lock is held ([ADR 0038](../adr/0038-post-run-transcript-sync-subagent-hierarchy.md)). |
| **KD-018** | **One engine per user, local-only.** One engine runs per user via an OS-bound endpoint, and it attaches, starts, or replaces a different-version engine rather than running two. The OTLP receiver binds loopback; telemetry does not leave the machine ([ADR 0039](../adr/0039-single-instance-engine.md)). |

---

## Invariants the requirements impose

- **Disjointness is what makes the token check possible.** Storing token classes separately is
  what lets `validateTokens` detect the pi/Copilot convention inversion — the same
  `gen_ai.usage.input_tokens` key with opposite meanings across harnesses. A store that kept
  "input" as one number could not.
- **A display layer cannot grow a job.** KD-011 is enforced by
  `dash/src/architecture/display-layer.test.ts`, which runs in `npm --prefix dash run test`.
  Adding a scheduler or job to a surface fails the architecture test, not merely a review.
- **Quarantine is a first-class, counted outcome.** KD-003 and KD-016 together mean an
  uncorrelated or unmatchable input is preserved and surfaced, never dropped to make a number
  look clean.
- **Derived data is a cache, with one named exception.** Every derived table is rebuildable
  from `records`; the single exception is the projection-time cost write-back through
  `CanonStore.setCosts` in one transaction, which is itself re-derivable on the next pass.

---

## Related

- [KyberDash architecture](architecture.md) — the ingest, normalization, analysis, and surface layers these requirements govern
- [KyberDash adoption guide](onboarding.md) — install, update, and configure
- [Telemetry inventory](telemetry-inventory.md) — per-harness collection outcomes and the remaining runtime gates
- [KyberDash runbook](runbook.md) — operating the store and running the test suites
- [Architecture decision records](../adr/README.md) — the decisions cited above
