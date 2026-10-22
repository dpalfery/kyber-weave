---
id: todo/copilot-cli-placeholder-session-rows
title: Copilot CLI session-store placeholder rows and the undiscovered data.db
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Copilot CLI session-store placeholder rows and the undiscovered data.db

The `sessions` table of `~/.copilot/session-store.db` has no model or token columns. Its rows become
canon records with model `unknown`, $0 cost and the key `copilot:<id>:<id>`, and they sit outside
window slicing. Under a narrow refresh window the store unit yields one such row per session. Under a
wider window it yields per-request rows instead (plan D23).

Two questions follow:

- Should these placeholder rows exist at all, or should they carry real figures? The Sessions view
  shows them as sessions with no cost.
- `data.db` (3 rows on the owner's store) is never discovered, so its data is not read.

The inventory states the current behaviour plainly. This todo decides whether that behaviour is the
intended one.
