---
id: adr/0032-kyberdash-user-initiated-clean
title: User-Initiated Clean of the KyberDash Database, with Receiver Pause, Checkpoint Reset, and Opt-In Re-Ingest
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-10
supersedes:
  - adr/0016-kyberdash-harness-source-refresh
  - adr/0018-kyberdash-content-retention-purge
---

# ADR 0032: User-Initiated Clean of the KyberDash Database, with Receiver Pause, Checkpoint Reset, and Opt-In Re-Ingest

## Status

Accepted, 2026-10-09. Records decisions D1–D10 of the
[clean plan](../plans/2026-10-09-issue-312-clean-kyberdash-db.md).

This record narrowly supersedes one sentence of
[ADR 0016](0016-kyberdash-harness-source-refresh.md) decision 6 ("They do not
rewrite or delete `records.raw`. ... A source disappearing from discovery
never auto-deletes history.") and one sentence of
[ADR 0018](0018-kyberdash-content-retention-purge.md) decision 3 ("Rows stay.
... Compressed `records.raw` is untouched."). The supersession covers only the
explicit, user-confirmed clean defined below: a deliberate wipe of records —
including `records.raw` — by harness scope or for all harnesses, with no
backup, followed by an explicitly requested re-ingestion. Everything else in ADR 0016 and
ADR 0018 stands, and neither record is edited — the same shape as
[ADR 0008](0008-kyberdash-single-canonical-store.md) superseding one decision
of ADR 0007, and [ADR 0023](0023-kyberdash-report-model-and-tray-ownership.md)
superseding one sentence of ADR 0016.

Amended 2026-10-10 ([issue #319](https://github.com/dpalfery/kyber-weave/issues/319)):
the automatic re-ingest of decision 6 is withdrawn, and the surfaces no longer reach the
clean by spawning a CLI child. A wipe no longer imports folder history at all unless the
caller asks for it with `dash clean --reingest-weeks <n>` (or the clean flow's **Import
folder history** control), `--no-reingest` is now the default and is kept only as an
accepted no-op, and nothing re-imports afterwards because scheduled folder import is off by
default behind the shared setting `settings.folder_import.scheduled`. The tray asks the
engine over `POST /api/kyber/clean` and holds no clean logic. The decision these changes
follow from is [ADR 0033](0033-kyberdash-surfaces-are-display-layers.md); the always-on
boundary they serve is [`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md).
Decisions 2, 3, 4, 5, 7 and 8 are unchanged.

## Context

KyberDash data is ephemeral point-in-time telemetry
([issue #312](https://github.com/dpalfery/kyber-weave/issues/312)). Bad or
stale data — double-counting, mis-attribution, test noise — previously had no
remedy short of manual database surgery, because the only deletion paths were
the 14-day content purge (which empties content columns but never deletes rows)
and per-span quarantine. The retention and checkpoint contracts therefore
promised that rows, and specifically `records.raw`, are never deleted.

A user-initiated clean needs exactly what those contracts forbid: deleting
records rows, their provenance, and their checkpoints, so a re-ingest
window applies to genuinely re-parsed source logs rather than to
rows the store already holds. The decision below confines that deletion to one
explicit, confirmed code path and keeps every automatic path under the old
contracts.

## Decision

1. **One central clean module, asked for over the API.** `dash/src/clean/clean.ts`
   (`cleanDatabase`) holds all wipe and re-ingest logic. `kyberdash dash clean` and
   `POST /api/kyber/clean` are thin callers, and every surface — the web dashboard and the
   tray alike — reaches that one implementation through the route rather than by spawning a
   CLI child; the tray holds no clean logic at all
   ([ADR 0033](0033-kyberdash-surfaces-are-display-layers.md),
   [`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md)). ADR 0023 design
   D3's tray spawns a clean child no longer holds, and its decision 3 is superseded by ADR
   0033.
2. **Explicit confirmation on every surface.** The CLI requires `--yes` (exit 2
   without it, before the store opens); the web dialog and the tray two-step
   confirm carry fixed disclosures: no backup is taken, the wipe cannot be
   undone, and OTLP-collected records will not come back. No backup exists, by
   design: the data is ephemeral and re-derivable from source logs.
3. **Receiver pause with lease.** The OTLP receiver serves loopback admin
   routes to pause and resume ingestion (`dash/src/otel/receiver.ts`). While
   paused, export paths shed load with 503 + Retry-After (exporters retry, so
   nothing is dropped); `/healthz` stays 200 with a `paused` flag so the tray
   still recognizes the port as its own. A lease TTL auto-resumes a cleaner
   that crashes mid-wipe. A pause refusal fails the clean closed, before any
   row is wiped.
4. **Same lock as refresh.** A clean holds the store refresh lock
   (`dash/src/refresh/lock.ts`); a held lock exits 3, which the tray already
   reads as busy ([ADR 0023](0023-kyberdash-report-model-and-tray-ownership.md) D4).
5. **Checkpoint deletion, not invalidation.** The wipe deletes
   `source_checkpoint` and `record_provenance` rows for the scope in the same
   transaction as the records — the inverse of `commitSourceUnit` — so the
   re-ingest window applies to re-parsed units.
6. **Re-ingest is opt-in; the default is none.** A clean wipes and stops, and the caller asks
   for the history back with `--reingest-weeks <n>` (a whole number of weeks, 1 to 52) or the
   clean flow's **Import folder history** control, which asks for the same window through
   `POST /api/kyber/clean`. `--no-reingest` is still accepted and is now simply the stated
   default, so a script written against it keeps working. Nothing re-imports afterwards:
   the next scheduled refresh does not read folder sources unless the shared setting
   `settings.folder_import.scheduled` is `on` ([ADR 0033](0033-kyberdash-surfaces-are-display-layers.md)
   decision 6). A one-off import that a clean does not carry — at any window, for any
   harness — is the separate `dash import-history` command. OTLP-collected records have no
   source logs and do not return; both surfaces say so.
7. **Fold-aware scope.** A harness wipe expands the canonical id to its folded
   raw record names (`claude-desktop` under `claude-code`, `cursor-agent`
   under `cursor`), matching how derived surfaces stamp identity
   ([ADR 0012](0012-progressive-disclosure-6-level-diagnostic-spine.md) twin fold).
8. **Audit survives.** `ingest_log`, `refresh_run`, `metadata` (stamped with
   `last_clean_at` and scope), `token_cache`, and the model-window catalog are
   never wiped. The post-wipe projection rebuilds the derived caches,
   including the harness-less `finding` table.

## Alternatives Considered

- **Backup-before-clean with restore.** Rejected: the data is point-in-time
  and re-derivable from source logs; a sidecar invites restores from unknown
  schemas.
- **SIGTERM the receiver instead of an in-band pause.** Rejected: it kills a
  collector the user may run standalone and drops the resume guarantee.
- **Rely on SQLite busy_timeout without pausing.** Rejected: the ingest
  writer's failure path can crash the receiver on a busy-timeout collision
  with the wipe transaction.
- **Checkpoint invalidation instead of deletion.** Rejected: it preserves
  stale coverage floors, so the 7-day window would not apply.

## Consequences

- `docs/dash/architecture.md` and `docs/dash/runbook.md` document the clean
  command, route, pause protocol, and wipe table map; the plan's closeout
  mapping records the harvest.
- The tray's seventh command (`clean_database`) asks the engine over
  `POST /api/kyber/clean` with the scope and an explicit confirmation; it passes
  `--reingest-weeks` only when the user asked for the history back. The next
  scheduled refresh does not re-ingest: folder import on a schedule is off until
  someone enables it.
- Automatic paths (refresh, retention purge) remain under the ADR 0016/ADR 0018
  no-delete contracts; only the confirmed clean deletes rows, and only an
  explicitly requested re-ingest repopulates from source logs.

## Related

- [ADR 0016: Harness-Source Refresh](0016-kyberdash-harness-source-refresh.md) — decision 6 narrowed here for the confirmed clean only
- [ADR 0018: Stored Content with a 14-Day Automatic Purge](0018-kyberdash-content-retention-purge.md) — decision 3 narrowed here for the confirmed clean only
- [ADR 0023: Report Model and Tray Ownership](0023-kyberdash-report-model-and-tray-ownership.md) — tray-holds-no-logic and exit-3 precedent
- [ADR 0033: The KyberDash Tray and Web Dashboard Are Display Layers](0033-kyberdash-surfaces-are-display-layers.md) — the surfaces are display layers, the engine hosts the jobs, and folder import is opt-in
- [KyberDash display-layer rule](../rules/kyberdash-display-layer.md) — the always-on boundary the amended decisions serve
- [KyberDash architecture](../dash/architecture.md) — clean section
- [KyberDash runbook](../dash/runbook.md) — clean usage
