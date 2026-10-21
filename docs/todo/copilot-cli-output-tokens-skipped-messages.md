---
id: todo/copilot-cli-output-tokens-skipped-messages
title: Ingest Copilot CLI assistant messages that omit outputTokens
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Ingest Copilot CLI assistant messages that omit outputTokens

From Copilot CLI 1.0.81 onward, `assistant.message` events in `events.jsonl` no longer carry
`outputTokens`. The parser defaults the value to 0 and then skips every message whose output is 0
(`dash/src/providers/copilot.ts`, around lines 1028 and 1035). On the owner's store that accounts for
3,821 of 5,605 journal messages, so those messages never become records.

The per-message record set is therefore empty for these sessions, and the Copilot CLI tool and skill
attribution is session-level as a result. Journal calls are not ingested (plan D20), so any change
must weigh the session's cost and token totals against the rollups.

Decide on an accounting-safe way to ingest these messages. It interacts with the session-store
placeholder rows and the shutdown rollups (see [copilot-cli-placeholder-session-rows](copilot-cli-placeholder-session-rows.md)).
If the answer is to ingest them, record the decision as an ADR, because it changes which records a
session owns.
