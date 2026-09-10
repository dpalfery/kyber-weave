---
id: reference/kyberdash-rationale
title: KyberDash measurable rationale
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-08-29
---

# KyberDash measurable rationale

Several KyberDash requirements cite a **measurement** rather than a preference. Each measurement
is a failure that already occurred in the Python pipeline (`agent-session-analysis-dashboard`)
that KyberDash supersedes, and a reimplementation that drops the requirement reproduces the
failure. This document preserves those measurements so a later reader cannot mistake a
correctness constraint for a style choice — and so they survive the retirement of the Python
project (KyberDash requirement 15.4). The source of each measurement is the record of the
Python pipeline's analysis over real span corpora; each is mirrored by a regression test in
the KyberDash test suite that reproduces the failure when the guard is removed.

## Token accounting (Requirement 4)

### R4.2 — The same attribute key with opposite meanings

`gen_ai.usage.input_tokens` is not one thing across harnesses, and no reading of the
OpenTelemetry specification distinguishes its meanings:

- **Copilot's** `gen_ai.usage.input_tokens` *includes* cache read and cache creation.
- **pi's** *excludes* both; its input figure is fresh input only.

Applying Copilot's convention to pi produced **negative fresh input on 293 of 307 measured
spans** (the input figure counted as inclusive while the cache classes were added separately,
so the decomposition no longer summed). Applying pi's convention to Copilot **double-counts
input by up to 2×** (inclusive input already contains the cache classes; counting them again
adds them a second time).

The consequence is architectural: adapters must convert each harness's convention **on the way
in**, and `TokenUsage` must store `freshInput + cacheRead + cacheCreation` disjointly so the
identity `freshInput + cacheRead + cacheCreation === reportedInput` is checkable on every
record — orphans included (R4.3, R4.4). A model that stored "input" as one number could not
detect the inversion. Regression coverage: `dash/kyber/canon/adapters/{copilot,pi}.test.ts`,
`dash/kyber/canon/types.test.ts`.

### R4.5 — Per-turn sum vs harness-reported total

When a harness reports its own total, the per-turn sum is reconciled against it and the result
exposed as a per-request match indicator. The hint is that the inversion surfaces per request
rather than as a gross total, where two wrongs can cancel.

### R4.6 — Derived counts are a lower bound

Counts produced by tokenizing content rather than read from a counter carry the tokenizer's
identity and are presented as a lower bound. On the `o200k_base` proxy tokenizer the
unattributed residual measured **2.8–4.4% on one model against 35–41% on another** — the
difference is the tokenizer, not missing content, and the figure must not read as a true
count. Regression coverage: `dash/kyber/canon/tokens.test.ts`.

## Cost attribution (Requirement 5)

### R5.3 — Rate-table scoping failure

A published rate table must not price a harness it does not name. Unguarded, the applicability
scoping failure would have priced **143 pi turns** at GitHub's credit rate (same model, same
tokens, entirely plausible-looking) and totalled **$0.27 against the $1.57 actually charged —
wrong by 5.8×, in the understating direction**. The plausibility is the danger: an
understated, plausible total is accepted where a suspicious one would be questioned.

The consequence is that a cost figure travels with its **basis** — a published table or the
harness's own arithmetic (R5.1) — and a harness the table does not name is priced as
`out_of_scope`, never from that table. Regression coverage:
`dash/kyber/canon/cost.scoping.test.ts` (the regression test reproduces the $0.27 fabrication
when the guard is removed).

### R5.4, R5.5 — No published rate is not zero

A model with no published rate renders as "no published rate", never `$0.00` — an unpriced
total looks plausible and is wrong. "Explicitly not billed" is a distinct status from "absent
rate", so a model the provider does not charge for is not reported as either zero or unknown.

## Harness attribution and quarantine (Requirement 6)

### R6.2 — Attribute the fingerprint, never the source name

Harness attribution is a per-source-and-trace fingerprint vote, never a read of the telemetry
source name. The source name carries per-instance suffixes, does not track content, and is not
stable across reconfiguration. The two-pass design (fingerprint vote, then source inheritance
for undecided spans of a confidently mapped source) exists because **15 tool-execution spans in
the measured corpus carried GenAI attributes with no vendor namespace and sat alone in their
traces** — too little signal to attribute by content, but each sitting under a source that
other, confident groups had already mapped.

### R6.1, R6.3 — Quarantine instead of guessing

A span that matches no adapter is quarantined with its observed attribute namespaces and never
assigned a harness by guess. Quarantined spans and validation problems are exposed in a view
with the counts and namespaces needed to write the missing adapter — unclassifiable telemetry
is a diagnosable gap, not a silent absence. Regression coverage:
`dash/kyber/canon/adapters/{quarantine,registry}.test.ts`.

## Other measured constraints

| Requirement | Measurement | Consequence |
|---|---|---|
| R2.7 | **25 of 1,009 spans** had already lost their parent to ring-buffer eviction in the Aspire-mediated pipeline; **17 sessions held 27 run identifiers against only 20 surviving run spans**. | The embedded OTLP receiver owns the buffer instead of depending on Aspire's; where records arrive with a missing parent they are still grouped, by attribute rather than ancestry. |
| R7/R10 (design) | pi **invoked 14 distinct tools across 368 calls while reporting zero tools offered**, because it exports no definitions. | Absent is not zero: each source declares measurability per metric, and a view a source cannot fill renders "not measurable", never a zero that reads as a result. |
| R12.4 | The existing store held **37,623 spans in 2.9 GB — roughly 78 KB per span** — because raw content attributes were persisted uncompressed. | The raw column is compressed, and stored bytes per record are asserted under a budget in `dash/kyber/canon/store.test.ts`. |
| R12.3 | The Python pipeline's tracked-artifact check **caught three real leaks** of captured content into version control. | The KyberDash equivalent is specified rather than assumed: seeded local-only markers must never appear in a git-tracked file, because one commit survives every later cleanup. |
| R15.1 | The ported pipeline must reproduce the Python pipeline's **content-free parity digest** exactly. | `dash/kyber/tools/parity.ts` emits the same digest shape; the digest test fails with a section report when the two diverge, and the Python pipeline stays authoritative until they agree. |

## How the rationale is enforced

Each measurement above has a mirror in the KyberDash test suite that asserts both the correct
behaviour **and** that the validation catches the convention being applied wrongly — a test
that only proves the happy path would not have caught the pi/Copilot inversion. The guard
removed, the test reproduces the measured failure. The measurements themselves originated in
the Python pipeline's analysis and are preserved here because the pipeline is retired once the
parity gate of Requirement 15 authorizes it.

## Related

- [KyberDash architecture](../dash/architecture.md) — how the constraints shape the system.
- [ADR 0006](../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) — the
  foundational decisions.
- [KyberDash product story](../dash/README.md)