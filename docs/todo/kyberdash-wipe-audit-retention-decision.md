---
id: todo/kyberdash-wipe-audit-retention-decision
title: Decide whether a wipe-all should also clear the operational audit
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-16
---

# Decide whether a wipe-all should also clear the operational audit

A wipe-all empties the corpus, but four things deliberately survive it, and the
request that prompted this note was for a wipe that clears *every* row of every
table. Clearing them is a decision, not a bug fix, because each is load-bearing:

| Survivor | Why it is kept |
|---|---|
| `refresh_run` (pre-wipe rows) | [ADR 0032](../adr/0032-kyberdash-user-initiated-clean.md) decision 8 keeps them as audit history. `server/coverage-after-clean.test.ts` and `jobs/wipe-acceptance.integration.test.ts` read coverage through that contract: `bridge.getRefreshState()` must report no window *without* the rows being deleted. |
| `metadata` | It is not corpus. `settings/shared-settings.ts` stores the dashboard's shared settings there (folder-import flag, jobs-paused, cadence, receiver-hosted, port) and `shared-settings.test.ts` requires every one to survive `wipeAll()`. The `schema_version` migration ledger and the `detector_version` stamp live there too — clearing the table makes an existing store look brand new and skips every migration. |
| `ingest_log`, `token_cache` | Operational audit, and the token/pricing cache. Both rebuild; neither is corpus. |
| `model_context_window_catalog` | A read-only vendor catalog, repopulated on demand. |

The reclamation half is already done: `wipeAll` now checkpoints and vacuums, so a wiped
store returns to the size its contents need instead of keeping every page it ever
allocated — a live store reported 442 MB of file for ~13 records before that.

## What to decide

Either supersede [ADR 0032](../adr/0032-kyberdash-user-initiated-clean.md) decision 8
and widen `wipeAll`, or add an explicit opt-in hard reset (for example
`dash clean --all --everything`) whose help text states that it clears the audit, the
caches, and the window catalog. The second leaves the existing contract and its tests
untouched, which is what makes it the smaller change.

Whichever is chosen, `metadata` needs care rather than a blanket `DELETE`: the shared
settings and the schema stamps must survive, or the next open either loses the owner's
configuration or re-runs migrations against a populated store.
