---
id: todo/kyberdash-tray-endpoint-full-payload-sweep
title: Tray endpoint surface enumeration for full-payload consumers
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# Tray endpoint surface enumeration for full-payload consumers

The sessions-list work swept `dash/src` for multi-row payload reads and dispositioned
every hit — except the tray surface (`dash/tray/src-tauri/src/api.rs`), which was never
exhaustively enumerated. Only session-detail was ruled out there.

Confirm no other full-payload consumer exists on the tray's endpoint surface. If one
is found, file it as its own task rather than folding it in here.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 3).

## Acceptance criteria

- Every tray endpoint is enumerated with the payload shape it serves, or the sweep
  records that no full-payload consumer remains outside the sanctioned
  single-row-per-id detail reads.
