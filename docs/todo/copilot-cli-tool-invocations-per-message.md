---
id: todo/copilot-cli-tool-invocations-per-message
title: Ingest Copilot CLI tool invocations per message
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Ingest Copilot CLI tool invocations per message

Copilot CLI's reader yields one turn per journal `assistant.message`, keyed by its `messageId`, and
each call of that message takes the message's own system prompt. The `skill` requests on a message
already reach the call as its skills. The message's other tool requests do not: Copilot CLI tool
invocations are not ingested, so a Copilot CLI session has no tool records.

Follow up by ingesting the journal's `toolRequests` as child `tool.invoke` records on the per-message
turns. Join each request to its `tool.execution_complete` event on `toolCallId` for the result, status
and duration. A child attaches to the record of the message that requested it, following the same
per-message pairing as the system prompt, rather than to a session-level carrier.

A session whose messages omit `outputTokens` (Copilot CLI 1.0.81 onward, see
[copilot-cli-output-tokens-skipped-messages](copilot-cli-output-tokens-skipped-messages.md)) produces
no per-message calls at all. Its tool invocations stay unrecorded until that todo is decided.

Child records change the session's record set, so weigh their effect on cost and token totals against
the rollups before shipping, as the output-tokens todo requires.
