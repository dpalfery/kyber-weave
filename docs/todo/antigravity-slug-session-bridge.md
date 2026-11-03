---
id: todo/antigravity-slug-session-bridge
title: Bridge OTLP Antigravity sessions with a slug session id to their conversation
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Bridge OTLP Antigravity sessions with a slug session id to their conversation

Four of the 13 OTLP-only Antigravity sessions carry a slug `gen_ai.session.id`. The slug equals
`copilot_chat.chat_session_id`. They have no conversation id, and there is no brain directory for
them, so the transcript reader has no way to reach them.

Decide whether a bridge from the slug to a conversation is possible from the data, and whether it is
worth building. If the answer is no, record that in the inventory as a permanent limit rather than a
pending fix.
