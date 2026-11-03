---
id: archive/todo/kyberdash-tray-endpoint-full-payload-sweep
title: Tray endpoint surface enumeration for full-payload consumers
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# Tray endpoint surface enumeration for full-payload consumers

> [!NOTE]
> **Closed 2026-10-20.** The sweep is done. The tray reads **no** session
> payload, and that is structural rather than a grep artefact:
> `dash/tray/src-tauri/src/api.rs` declares a closed 7-path allow-list
> (`report`, `jobs`, `settings`, `coverage`, `refresh`, `clean`,
> `import-history`), enforced in `endpoint_url()` and `api_request_url()`; the
> tray UI makes zero HTTP calls; no other tray Rust file opens a socket. But
> the sweep also found **four full-payload consumers elsewhere** that the
> earlier grep-based ruling had missed, and they are the follow-up plan's work,
> not this todo's: `fetchKyberSessionTurns` (downloads up to 10,933 turn rows
> to read one), `GET /api/kyber/timeline` (reads 15.3 MB to serve 13.8 KB), the
> report builder's `buildLatestSession` (one full payload read per report
> request — and `/api/kyber/report` is on the tray's own endpoint list), and
> the two legacy routes `GET /api/kyber/context` and `GET /api/kyber/schema`
> (no live caller — **now deleted**, see
> [ADR 0041](../../adr/0041-kyberdash-retire-legacy-context-schema-routes.md)).

The sessions-list work swept `dash/src` for multi-row payload reads and dispositioned
every hit — except the tray surface (`dash/tray/src-tauri/src/api.rs`), which had never been
exhaustively enumerated. It now has been: every tray endpoint is enumerated by the
allow-list it serves, and no full-payload consumer remains on the tray surface. The
wider-surface consumers the sweep surfaced were filed as their own tasks in the
follow-up plan rather than folded in here.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 3).

## Acceptance criteria

- [x] Every tray endpoint is enumerated with the payload shape it serves, and the sweep
  records that no full-payload consumer remains on the tray surface — with the four
  wider-surface consumers dispositioned as follow-up-plan tasks.
