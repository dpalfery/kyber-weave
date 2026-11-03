---
id: todo/antigravity-cli-backfill-import-picker
title: Backfill antigravity-cli and let the import picker offer it
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Backfill antigravity-cli and let the import picker offer it

Eight of the 13 OTLP-only Antigravity sessions on the owner's store have a transcript-bearing
conversation database under `~/.gemini/antigravity-cli/conversations/`. Canon has no `antigravity-cli`
or `antigravity-ide` records for them. Only one folder pass has run since the clean, and it used
`history_weeks=1` with the `antigravity` descriptor only.

Two parts:

- **Backfill.** Run `dash import-history --weeks 2 --harness antigravity-cli` (or a longer window
  when the conversation is older). The eight sessions then gain file records and their transcript
  activity. This is an operational step for the owner's store.
- **Picker.** The web import picker offers only harnesses that already hold samples
  (`dash/web/src/components/maintenance/MaintenancePanel.tsx`, around lines 185 and 311 to 329), so
  it cannot offer `antigravity-cli` until it has samples. Decide whether the picker should list every
  known harness, with the samples count shown beside each.
