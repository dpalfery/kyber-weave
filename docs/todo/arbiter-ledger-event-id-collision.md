---
id: todo/arbiter-ledger-event-id-collision
title: The Arbiter ledger builds event ids from the clock, so two events in one instant collide
doc-type: todo
component: KyberArbiter
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
---

# The Arbiter ledger builds event ids from the clock

## The finding

The Arbiter's in-flight ledger and decision log derive an event id from the current instant. Two
events recorded in the same clock tick therefore get the same id, and a reader that keys by id sees
one event where there were two. The product already has `IIdGenerator` for exactly this: a GUID, a
random token or a sortable record id for an instant, all of which a test can control.

Found while converting the ledger's tests onto fakes (phase 4 of the interface-driven unit tests
work). The conversion did not change the id scheme; it only made the tests able to see it.

## What is and is not established

The collision is established by reading the id construction: the clock is the only input. How often
it happens in practice is not measured. A ledger write is one line under an exclusive lock, so two
writes in one tick need two events close enough together; the risk is real on a fast machine and
certain in a test that writes two events without advancing the fake clock.

## Why this was not fixed here

Closeout records decisions and retires the specification; it does not change the ledger's on-disk
identity. An id scheme change needs a migration story for journals that already hold clock ids, and
a test that reproduces the collision first.

## Options

1. **Take `IIdGenerator` and use a sortable record id for an instant (recommended).** The ledger
   stays ordered, a test can make two ids distinct, and nothing about the append lock changes.
   Decide whether an existing clock id is read as-is or migrated.
2. **Append a per-process counter to the clock id.** Cheap and monotonic, but it leaks process state
   into a durable id and does not survive a restart cleanly.
3. **Keep the clock id and make readers tolerate duplicates.** Rejected: the id is the identity a
   reader keys by, so tolerance means silently dropping an event.

## Related

- [Kyber Arbiter architecture](../kyber-arbiter/architecture.md) — the ledger and decision log
- [Seam conversion leftovers](seam-conversion-leftovers.md) — the other product-side residue
