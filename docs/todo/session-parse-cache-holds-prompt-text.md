---
id: todo/session-parse-cache-holds-prompt-text
title: The session parse cache holds the user's prompt text on disk, outside the database
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The session parse cache holds the user's prompt text on disk, outside the database

The session parse cache stores a cached turn's user message. The parser writes it into the cached
turn (`dash/src/ingest/parser.ts`, the `parsedTurnToCachedTurn` mapping, where the user message is
flattened and capped at 2000 characters), the cached turn carries it as a field
(`dash/src/ingest/session-cache.ts`, `CachedTurn.userMessage`), and the cache is persisted as plain
JSON files under the state directory's cache tree (`dash/src/ingest/cache-dir.ts`).

That cache is not the canon store. The canon store's retention rules and its wipes reach
`canon.db` and its tables; the cache is a separate set of files on disk, so a wipe of the store
does not reach them and the content-retention pass does not empty them. A user's prompt text
therefore sits on disk in a form that none of the store's data-governance rules govern.

This is pre-existing behaviour for every harness the parser reads, not something the Factory
capture introduced. It became visible in this delivery because the Factory file source reads a
session's first user message out of the transcript and carries it as the call's `userMessage`
(`dash/src/providers/droid.ts`), so a Factory session's prompt text lands in the cache through
that path too.

**What a decision must settle.** Whether the cache should carry prompt text at all, and what
should govern it if it does.

- Stop caching the user message. The cache exists to avoid re-parsing; the prompt is one field of
  a cached turn, and dropping it costs a re-parse of the message on a cold read. Whether that
  cost is acceptable is a judgement about the cache's purpose, not about privacy.
- Keep the text and give the cache its own governance: a retention window, a wipe that reaches it,
  and a statement in the governed documents of what the cache holds. Today the cache is described
  as a cache; nothing says it is a content store.
- Keep the text and treat the cache as what it is — a derived cache of the owner's own
  transcripts, which the owner already has on disk — and say so, while making sure a store wipe
  does not leave a stale copy of a prompt the owner has since deleted.

Whichever is chosen, the honest-unobservability rule applies to the documentation: the cache's
contents are a fact about the owner's disk, and the governed documents should say what they are
rather than leave a reader to assume the cache holds no content.

**Deliberately not done here.** The cache format and the retention rules were not changed. Both
are shared with every other harness, and a change to them is a store-wide decision rather than a
Factory one. The finding is recorded so that the cache is not mistaken for a place the store's
governance already covers.
