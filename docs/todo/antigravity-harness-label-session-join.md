---
id: todo/antigravity-harness-label-session-join
title: Verify how the session view joins antigravity and antigravity-cli on one session
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Verify how the session view joins antigravity and antigravity-cli on one session

OTLP records of an Antigravity session carry harness `antigravity`, by design (GitHub issue #195 and
`dash/src/canon/measurability.ts`). File records of the CLI root carry harness `antigravity-cli`. If the two
land on one `session_id`, the session view must treat them as one session.

The T13 smoke run did not establish this. The plan records it as unverified. Confirm it on a real
session that has both kinds of record, and fix the view only if it splits them. The first evidence is
the count of OTLP sessions that gain file records once `antigravity-cli` is imported (see
[antigravity-cli-backfill-import-picker](antigravity-cli-backfill-import-picker.md)).
