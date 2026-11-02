---
id: todo/factory-native-span-claim-withdrawn
title: The native Factory span claim was withdrawn; this holds its measurement and what would restore it
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The native Factory span claim was withdrawn; this holds its measurement and what would restore it

The Factory capture plan's decision **D50** already names this as a follow-up, in its own words:
"The withdrawn path's measurement, the arithmetic and the conditions for restoring it are filed as
a todo under the todo index, so the evidence is kept and the claim is not." This todo is that
record. It does not restate D50, and it adds nothing to the decision; it preserves the evidence so
that a later session does not have to rediscover it.

## What was measured

Factory's native OpenTelemetry export has two shapes, and the live probe captured real spans from
both.

**The `legacy` format** carries four content-shaped spans (`droid.message.*` and `droid.tool.*`),
produced only when Factory's message-content logging is on. Their **span names are the only
Factory-specific evidence** they carry; no attribute fingerprint an adapter may claim. Claiming
them would also import content through a route the consent rule does not govern.

**The `genai` format** carries an `invoke_agent droid` root plus per-turn `chat` and per-tool
`execute_tool` children, all children of the root. The `droid` adapter recognised a native span
**solely** by `gen_ai.agent.name === 'droid'`, and Factory puts that attribute on the **root
only**. The registry's group score is the winning adapter's total over the group's **span count**
(`dash/src/canon/adapters/registry.ts:104-128`), against a threshold of **0.6**
(`registry.ts:34`). A trace of one root and *k* children therefore scores at most

    1 / (1 + k)

A real turn's trace has *k ≥ 1*, so it scores at most **0.5** — below the threshold. The measured
probe reported:

    droid   scores=[1,0]  total=1  confidence=0.500
    ingest outcome: {"accepted":0,"quarantined":2,"rejected":0}

Both spans were quarantined as `unclaimed`.

**After the withdrawal** (the plan's T41), the same real captured spans through the branch's
production ingest path score against **no adapter at all**, and are still quarantined as
`unclaimed`:

    per-adapter detect scores:      (none)
    ingest outcome: {"accepted":0,"quarantined":2,"rejected":0}

The plugin envelope is unaffected and is the control: `droid scores=[1] total=1 confidence=1.000`,
accepted with 1 span and 0 quarantined.

The metrics path is a separate matter and is **not** withdrawn: Factory's customer-side export is
metrics-only by default, the branch's receiver serves it, and the points are persisted in
`metric_point` (D31, D38, D42).

## What would have to be true to restore it

1. **The claim has to be group-scoped, not span-scoped.** A trace whose root declares
   `gen_ai.agent.name = 'droid'` is Factory's own trace, so the root may claim the whole group.
   That is a change to the **shared** attribution contract (`dash/src/canon/adapters/base.ts`,
   `registry.ts`) whose only beneficiary would be this one harness. The rejected alternative was
   exactly this, and the rejection is recorded in D50.
2. **The model chain has to be amended.** Under D23's precedence a native span for a session that
   already has plugin rows is excluded from the session's turns, tools, duration **and model**,
   so the native `gen_ai.request.model` would never reach the dashboard without a further
   amendment. (The plugin names no model — D39 — and the file source names a session-level one.)
3. **The batch dependence has to be removed.** The registry's second pass lets an undecided group
   inherit an attribution from a confidently-mapped group of the same source, so one childless root
   trace could pull the source's other groups into `droid`, and a batch without one would not.
   A claim that depends on what else is in the batch is not a claim.
4. **The content question has to be answered separately.** The `legacy` format's spans are
   content-shaped, and the content route is the one the consent file does not govern (the separate
   todo on the file source's ungated content route). A restoration that includes the `legacy`
   format inherits that problem.

## The one thing the native path would add

Its unique contribution is the **per-turn model** (`gen_ai.request.model` on the `chat` span).
The plugin names no model and the file source names a session-level one, so a per-turn model is
reachable nowhere else. That is the whole of the case for restoring the claim, and it is a
narrower case than a second capture path.

**Deliberately not done here.** The claim stays withdrawn. The native span path is not
re-enabled, no adapter was changed, and the shared attribution contract was not touched. The
evidence above is preserved so that a decision to restore can start from the measurement rather
than from a rediscovery.
