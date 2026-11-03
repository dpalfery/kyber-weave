---
id: todo/factory-tool-call-identity-ambiguous
title: Two Factory tool calls in one session can land on one row
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# Two Factory tool calls in one session can land on one row

Factory's documented tool hook payload carries `tool_name`, `tool_input` and `tool_response`, and
no per-call identifier. The mapper anchors a tool span on the call id when the payload carries
one and on the session id when it does not
(`dash/src/capture/harnesses/factory-plugin/mapper.ts`), and the span id is derived from that
anchor. Two calls of the same tool in one session therefore share a span id when the payload
carries no call id, and land on the same record row.

The consequence is that the two calls cannot be told apart by the record: one row carries one
tool's name, one status and one pair of sizes, and the second call's figure either overwrites the
first or is lost, depending on which write lands last. The `PreToolUse` and `PostToolUse` pair of
a single call is unaffected — those two share a span id by design, so the call's before and after
join — but two distinct calls are not distinguishable.

**What a decision must settle.** Whether a per-call identifier can be obtained, or whether the
record must be accepted as ambiguous.

- Find the identifier in the payload under another name. The documented payload names three tool
  fields and no id, so this needs a live check of what the harness actually sends; the capture
  plan's live procedure records the real payload per event, which is where the answer would come
  from. If the harness sends one, the contract gains a key and the anchor stops being the session
  id.
- Derive an identity from the call itself. The tool name, the argument bytes and the event order
  are what the payload has; hashing them would make the two calls distinguishable in most cases
  and identical in the case where a tool is called twice with the same arguments — which is a real
  case, and it would make the collision a stated limitation rather than a bug.
- Accept the ambiguity and say so. The record would state that a tool row may represent more than
  one call, and the derived tool counts would be declared a lower bound rather than a total. This
  is the honest-unobservability answer: the counts are measured, but not exactly.

Whichever is chosen, the contract's version has to move with it if the wire changes: the contract
is versioned (`factory.contract_version`), and a consumer's claim must not change when a span's
fields change.

**Deliberately not done here.** The mapper was not changed and the contract was not bumped. The
capture plan's live check was to settle what the payload carries; that answer is not in, and
guessing an identifier that the harness does not send would put a fabricated field on the wire.
