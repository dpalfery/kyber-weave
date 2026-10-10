---
id: adr/0029-kyberdash-per-bucket-otel-first-content-precedence
title: Per-Bucket, OTel-First Content Precedence; Amend ADR 0009 D4
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-07
---

# ADR 0029: Per-Bucket, OTel-First Content Precedence; Amend ADR 0009 D4

## Status

Accepted, 2026-10-07. Records decision D10 of the KyberDash context-capture plan.

This record amends the content half of [ADR 0009](0009-multi-signal-ingestion-span-shaped-record.md)
decision 4: "content comes from the file row when the OTel row carries no parts". The counter
half of that decision — counters come from the OTel row, and values are never summed across
sources for the same turn — stands unchanged, as does everything else in ADR 0009. ADR 0009 is
not edited; this is the same shape as [ADR 0008](0008-kyberdash-single-canonical-store.md)
superseding one decision of ADR 0007.

## Context

A turn can be described twice: once by an OTel record the embedded receiver built from spans and
logs, and once by a dot-folder file such as `~/.claude/projects/**/*.jsonl`. ADR 0009 decision 4
chose between them for content at the level of the whole record. If the OTel record carried any
parts at all, the file row contributed none.

ADR 0009 chose the whole-record rule on the ground that the file path holds what was actually
sent. KyberDash now asks every harness to export all content it can, so an OTel record routinely
carries some content buckets and not others. Claude Code's `claude_code.api_request_body` log,
for example, supplies the system prompt and the tool definitions, while the conversation history
may only be on disk. Under a whole-record rule, one OTel part is enough to hide every bucket the
file holds and the OTel record does not, so under that rule the turn reports less context than
the harness actually sent. D10 changes the rule per bucket: OTel now wins whenever it has parts
for that bucket, and file parts fill the rest.

Content is addressed through the canonical content keys in `dash/src/canon/types.ts`
(`system_prompt`, `tool_definitions`, `instruction_context`, `conversation_history`,
`tool_result_content`). Those keys are the natural unit at which to choose a source.

## Decision

1. **Content precedence is decided per canonical content bucket, OTel first.** For each bucket,
   the OTel record's parts win when it has any for that bucket.
2. **File parts fill only the buckets the OTel record lacks.** A bucket the OTel record carries
   takes nothing from the file row.
3. **No bucket is ever filled from two sources.** Parts for one bucket come wholly from the OTel
   record or wholly from the file row, never from both.
4. **Counters are unchanged.** They still come from the OTel record and are never summed across
   sources, exactly as ADR 0009 decision 4 states.
5. **Scope is OTel-and-file pairings only.** The rule applies to every OTel-and-file pairing,
   whatever the harness. File-and-file joins (#231, #232) keep their existing behaviour.

## Alternatives Considered

- **Keep the whole-record rule.** Rejected. Once OTel export is enabled for content, the rule
  discards file content for exactly the turns where both sources exist. Those are the
  best-instrumented turns.
- **File first, per bucket.** Rejected. The OTel record is the counter source, and precedence
  per bucket needs one rule that never mixes sources within a bucket, so OTel stays first.
- **Merge both sources within a bucket.** Rejected. The two sources describe the same call, so
  concatenating them double-counts the bucket's tokens. Deduplicating text across two encodings
  of one prompt is not reliable enough to rank findings on.

## Consequences

- One turn's content can now come from two sources, split by bucket. Nothing in this decision
  records per-bucket provenance.
- Twin deduplication (`dash/src/canon/twin-dedupe.ts`), for OTel-and-file pairings only,
  changes from choosing a donor record to
  choosing a donor per bucket. Its existing comment, which restates ADR 0009 decision 4, has to
  change with it.
- Turns that were previously under-reported gain buckets once this ships. Context totals for
  those turns rise, and a comparison across the change is not like-for-like.
- A harness whose OTel export carries a partial bucket, such as truncated history, still wins
  that bucket over a complete file. Precedence is by presence, not by completeness.

## Related

- [ADR 0009: Multi-Signal Ingestion into a Span-Shaped Canonical Record](0009-multi-signal-ingestion-span-shaped-record.md)
  — decision 4 amended here
- [ADR 0008: Single Canonical Store; Supersede ADR 0007 D4](0008-kyberdash-single-canonical-store.md)
- [ADR 0031: The KyberDash Capture Command Owns Harness Telemetry Keys](0031-kyberdash-capture-command-owns-harness-telemetry-keys.md)
  — how OTel content export gets enabled
- [KyberDash architecture](../dash/architecture.md)
