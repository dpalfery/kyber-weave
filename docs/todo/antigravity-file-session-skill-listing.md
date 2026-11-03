---
id: todo/antigravity-file-session-skill-listing
title: Skill listing for file-only Antigravity sessions
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Skill listing for file-only Antigravity sessions

The `Active Skills` listing exists only on OTLP spans, as `gen_ai.skills` on `llm.invoke` records.
File records of Antigravity conversations carry no listing. A session that exists only as file
records, which is every session the transcript reader reaches, therefore shows its Skills panel's
listing as not measurable, with no listing source. Invocations are measured from the transcript
where coverage exists.

Decide between two routes. Surface the listing from the OTLP side when the labels join (see
[antigravity-harness-label-session-join](antigravity-harness-label-session-join.md)), or document the
panel as listing-free for file-only sessions. Choose one and state it in the Skills view contract.
