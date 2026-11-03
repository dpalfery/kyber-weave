---
id: todo/sibling-harness-capture-coverage-gap
title: The sibling harnesses are not covered by the Factory capture path
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The sibling harnesses are not covered by the Factory capture path

The Factory capture plan's decision **D21** already names this as a follow-up, in its own words:
the sibling coverage gap named in the issue — Cursor, Warp, ZCode, Devin and Command Code — is
"captured as a todo under the todo index, not implemented here". This todo is the reminder the
plan's harvest requires; it does not restate D21, and it adds the question D21 leaves open.

Those harnesses are not left with nothing. Cursor and Warp have file sources, and the KyberDash
capture registry holds writers for the harnesses that can export OpenTelemetry. What they do not
have is the thing the Factory plugin gives: a turn boundary, a tool call and a timing observed
where they happen, rather than reconstructed afterwards from a transcript.

**What a decision must settle.** Whether the same plugin shape would serve the sibling harnesses,
and what would have to differ.

- Whether a plugin is the right shape for them at all. The Factory plugin is a hooks plugin
  installed from a marketplace: it needs a harness that has hooks, a plugin cache, a marketplace
  concept and a plugin identity. A harness with any of those missing needs another shape, and
  the shape is the harness's to name.
- What the shared parts would be. The transport, the consent file and the hook runner are already
  shared with the OpenCode plugin, so a second harness that can use them gets the consent
  semantics and the bounded delivery for free; what stays harness-specific is the wire contract,
  the hook names and the attribute namespace.
- What the ordering and the accounting would be. A session observed through two sources has to
  say which source owns which figure — the Factory source-precedence rule
  ([architecture](../dash/architecture.md#the-factory-source-precedence-rule)) was decided for one
  harness, and a second one would need its own decision or an explicit extension of that rule.
- Whether the sibling coverage gap is one todo or several. Each harness is a separate piece of
  work with a separate harness-side investigation, and this todo is the reminder of the gap, not
  a plan for any of them.

**Deliberately not done here.** No sibling harness was given a capture path. The work is one
harness-shaped investigation per harness, and this delivery was for Factory. The todo carries the
gap D21 names, so it is not lost behind the Factory delivery's own scope statement.
