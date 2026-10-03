---
id: plans/2026-10-02-issue-189-sessions-dashboard
title: Restore missing harness sessions behind issue #189 (KiloCode #227, Codex #196)
doc-type: plan
status: draft
owner: dpalfery
last-reviewed: 2026-10-02
component: KyberDash
---

# Sessions missing from the dashboard — #189

Fixes [issue #189](https://github.com/dpalfery/kyber-weave/issues/189) ("KyberDash:
most harness sessions never reach the dashboard"). Development mode:
**test-first**. Every task names its failing test first.

## Status of earlier work

PR #230 (plan
[`2026-09-30-issues-189-198-199.md`](../archive/plans/2026-09-30-issues-189-198-199.md))
shipped the ingest-coverage honesty work for #189: the refresh window is
persisted, collector activity is recorded, checkpoint `partial` state is
surfaced, and the harness matrix carries no-data reasons. Per the owner's
comment on the issue, #189 stays open for the two per-harness parser gaps
tracked as [#227](https://github.com/dpalfery/kyber-weave/issues/227) (KiloCode
yields zero records) and [#196](https://github.com/dpalfery/kyber-weave/issues/196)
(Codex ingests 647 folder sessions but shows 5; no `codex-cli` or collector
data). This plan is the remaining work that lets #189 close.

## Open questions recorded before implementation

These were put to the owner (question ids below); the run was non-interactive,
so the recommended option was taken. Answers can amend this plan before the
implementation PR opens.

- **Q1 — scope.** Options: (a) fix both parser gaps (#227, #196) so sessions
  reach the dashboard, then close #189 [recommended, assumed]; (b) KiloCode
  only; (c) Codex only; (d) close #189 as delivered by #230.
  **Assumed: (a).**
- **Q2 — plan artifact.** Options: (a) this durable `docs/plans` entry
  [recommended, used]; (b) ephemeral conversation plan.
  **Assumed: (a).**

## Root causes (from the audit evidence, not yet re-verified)

1. **KiloCode (#227).** Refresh sees 70 `kilo-shared-runtime` checkpoint
   units against a live in-window `~/.local/share/kilo/kilo.db`, yet zero
   KiloCode rows exist in `records`; one nonzero checkpoint even claims 3
   records that are absent. Provider discovery reads both the legacy
   `kilocode.kilo-code` task tree and the SQLite store
   (`dash/src/providers/kilo-code.ts`,
   `dash/src/providers/sqlite-session-parser.ts`); the failure is in why 69/70
   units produce 0 records and where the 3 claimed records went.
2. **Codex (#196).** `source_checkpoint` shows 647 `codex-desktop` units but
   only 85 records, all under stored source `codeburn/codex-desktop`; no
   `codex-cli` rows and no collector-sourced Codex data. Discovery/parsing
   lives in `dash/src/providers/codex.ts`; the gap is why folder-discovered
   sessions almost never yield records and why neither the CLI harness id nor
   the OTLP collector path contributes.

## Smallest change per gap

- Reproduce each gap with a failing, fixture-backed test against a synthetic
  store shaped like the live one (never the live `~/.kyberdash/canon.db`).
- Fix the discovery/parser/checkpoint path so sessions land in `records`
  under the correct stored source id, with the checkpoint unit claim
  consistent with what was actually ingested.
- Where a gap is genuinely a source-side absence (nothing collectable), say
  so honestly via the existing no-data/checkpoint reasons — never fabricate
  counts (rule: `docs/rules/honest-unobservability.md`).

## Test-first task list

- **T1 KiloCode RED:** `dash/src/providers/kilo-code.test.ts` /
  `sqlite-session-parser` test — a fixture `kilo.db` with the live schema
  shape yields non-zero parsed sessions; a unit claiming N records but
  producing 0 is a named, reproducible failure first.
- **T2 KiloCode GREEN:** provider/sqlite-session-parser fix; checkpoint
  `recordCount` agrees with `records` rows.
- **T3 Codex RED:** `dash/src/providers/codex.test.ts` — fixture session
  folders for both the desktop and CLI layouts produce records under the
  expected stored source; the folder-count → record-count drop is a failing
  assertion first.
- **T4 Codex GREEN:** discovery/parser fix in `dash/src/providers/codex.ts`
  (and the registry binding if the `codex-cli` identity was never wired).
- **T5 Gates and docs:** `npm --prefix dash run typecheck lint test
  check:reachable`; `docs validate` + `docs drift` zero findings; close
  [#227](https://github.com/dpalfery/kyber-weave/issues/227),
  [#196](https://github.com/dpalfery/kyber-weave/issues/196), and
  [#189](https://github.com/dpalfery/kyber-weave/issues/189) with evidence.

## Non-goals

- No default change to `--history-weeks`, no tray refresh-arg change (owner
  backfill already decided in #230's D1).
- No new harnesses, no OTLP attribution changes, no UI redesign.

## Approval gates for the owner

- **G1 (Q1/Q2 check):** confirm the assumed scope and plan location.
- **G2:** T1/T3 red tests reviewed before any fix code is written.
- **G3:** PR opened as draft, title `[issue #189] [hal.hermes.opencode]
  Sessions missing from dashboard`, body starting `Fixes #189`.
