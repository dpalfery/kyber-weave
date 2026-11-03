---
id: todo/factory-file-source-even-token-split
title: The Factory file source splits a session's tokens evenly across its assistant calls
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The Factory file source splits a session's tokens evenly across its assistant calls

The Factory capture plan's decisions **D21** and **D29** already name this as a follow-up, in
their own words: "a Factory *file* import rewrite (the even token split in `providers/droid.ts`
stays as it is, with a follow-up todo filed)", and "the even split of the session total across
assistant calls (`dash/src/providers/droid.ts:196-232`) is a **known lossy approximation** that
better per-call data exists for — not an accident". This todo is the reminder the plan's harvest
requires; it does not restate D21 or D29, and it adds the question those rows leave open.

## What the parser does

Droid's own session record carries token usage only at the **session** level. The parser reads
`settings.tokenUsage` and divides every figure by the number of assistant calls it emitted,
giving the remainder to the last one:

    const inputPerCall = Math.floor(totalInput / numCalls)
    const outputPerCall = Math.floor(totalOutput / numCalls)
    // …cache creation, cache read, thinking, the same way
    // Assign remainder to the last call

The code says so itself, in a comment that names the consequence: "Droid records token usage only
at session level (`settings.tokenUsage`), not per-message. We split evenly across the emitted
assistant calls and price all of them at `settings.model` (the latest model the session used). For
sessions where the user switched models mid-stream, costs are approximate — we have no ground-truth
breakdown to attribute tokens per model."

Two consequences follow, and both are losses rather than rounding noise:

- **Which call consumed which tokens is unknown.** A session with one cheap turn and one
  expensive turn reports the same per-call figures for both. Every per-turn figure derived from
  this split is an assumption, and the sum is the only part that is measured.
- **Which model consumed the tokens is unknown.** Every call is priced at the session's LATEST
  model, so a session that switched models mid-stream is priced as though the whole session ran on
  the last one.

## The data that would remove the loss

The companion `<session-id>.settings.json` file carries **two** further fields beside
`tokenUsage`:

- **`lastCallTokenUsage`** — the per-call usage of the most recent call. This is the better
  per-call data: it is measured, not apportioned.
- **`childInclusiveTokenUsageBySessionId`** — the parent's **aggregate** of its children, keyed by
  child session id. It is not a per-call breakdown, so it cannot replace `lastCallTokenUsage` for
  this purpose; it answers a different question (how much a child session cost its parent).

`lastCallTokenUsage` is therefore the field a fix would use. Its limitation is equally plain: it
describes the LAST call only, so on its own it does not yield every call's usage either. The
companion file's exact shape — whether it holds a per-call list, whether the earlier calls are
recoverable from it, and whether a session's file carries more than the last call — is what
decides whether the split can be replaced or only improved.

**What a decision must settle.**

- Whether the per-call split is replaced by measured data where it exists, kept where it does not,
  or dropped in favour of a session-level figure that is honest about being session-level.
- Whether a turn's figures may carry an *apportioned* value at all, or must be absent when no
  measurement supports them. The honest-unobservability rule is the reason this is a question
  rather than a formatting choice: an apportioned figure that looks measured is worse than an
  absent one.
- Whether the model attribution follows the same rule, given that the pricing model is currently
  the session's latest rather than the call's own.
- Whether the fix belongs to the Factory parser alone or to a shared rule about apportioned usage
  that other harnesses' parsers also apply. This is the reason the plan's projection carve-out
  (D28) was kept narrow: one harness's approximation should not become every harness's rule.

**Deliberately not done here.** The parser was not changed and the split was not removed. The
Factory capture work does not depend on it — the plugin path measures its own token usage — so
this is later work, and this todo is the seed for it. The scope of the change must be settled
before any code moves, because the parser's figures feed the session's cost, and a change to
apportioned usage may not be a change one harness may make alone.
