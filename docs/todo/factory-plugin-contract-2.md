---
id: todo/factory-plugin-contract-2
title: Factory plugin contract 2 would fix the tool-span collapse, the session timings and the quarantined spans
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-09
---

# Factory plugin contract 2 would fix the tool-span collapse, the session timings and the quarantined spans

This is the third of three follow-ups from the Droid data-completeness work. The first slice
(2026-11-09) fixed what the transcript can supply. This todo holds what only the Kyber-owned Factory
plugin can fix, and that needs a new wire contract, contract 2 in place of contract 1.

A contract change has a cost: the receiver and the `droid` adapter must accept the new version, and
the change helps only sessions recorded after the fix. The transcript is retroactive, which is why
the first slice came first.

## What contract 2 would change

1. **Tool-span anchor collapse.** Factory's hook payloads carry no `tool_call_id`, so every tool span
   anchors on the session id and every tool call of a session collapses onto one span. The identity
   question is in [factory-tool-call-identity-ambiguous.md](factory-tool-call-identity-ambiguous.md).
   The first slice counts a session's tools from the transcript instead, so this matters for the
   plugin's own tool spans only.
2. **Stop hook timings.** Factory's hooks reference, as read on 2026-11-09, lists `elapsed_time` and
   `tool_execution_count` on the `Stop` payload. The plugin records neither. Re-check the reference
   before implementing.
3. **Session duration.** The `SessionEnd` payload lists `session_duration_ms`, which the plugin does
   not record. Re-check the reference before implementing.
4. **Quarantined plugin spans.** 21 plugin spans in the owner's store are quarantined and have not been
   diagnosed.
5. **Capture status.** `kyberdash kyber capture status --harness factory` reports native export as "not
   configured" while native metrics are arriving. It should report what actually arrives.

## Related

- [ADR 0055: Factory (Droid) capture is a hooks-plugin pipeline](../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md),
  whose amendment of 2026-11-09 records the measured tool-span collapse.
- [factory-native-metrics-projection.md](factory-native-metrics-projection.md), the other follow-up.
