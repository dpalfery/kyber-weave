# Lens: performance

## Applicability

Applies when the diff touches a loop over unbounded input, a database or network call, a
request-handling path, a background job, a cache, or serialization of anything that can grow.

Skip when the change is confined to code that runs once, over fixed small input, outside any
hot path. Say what you checked; a confident skip is more useful than a speculative finding.

## What this lens owns

Cost that shows up at real scale, not micro-optimization.

The bar: report a finding only when you can name **what grows** — rows, users, tenants,
items, concurrent requests, retained bytes — and what happens to the code when it does.
Performance findings that cannot state the growth dimension are guesses, and they crowd out
the ones that are not.

## What to look for

**Queries inside loops.** The most common and most expensive defect in this lens. One query
per item, one HTTP call per item, one file read per item. Look for the loop and the call
inside it, including where the call is hidden behind a property access, a lazy collection, or
a repository method that does not look like a query.

**Work that repeats identically.** The same value computed, fetched, or deserialized on every
iteration when it does not change. Loop-invariant work is cheap to spot and cheap to fix.

**Fetching more than is used.** Whole rows for one column, whole collections for a count,
whole documents for one field, unbounded result sets with no paging. Reading everything to
filter in memory what the store could have filtered.

**Blocking in an asynchronous path.** Synchronous waits on asynchronous work, blocking file
or network calls on a request thread, a lock held across an await or an I/O call. These
consume the resource that determines concurrency, so their cost is superlinear in load.

**Retained memory.** Accumulating into a collection that is never bounded or cleared. A cache
with no eviction and no size limit. Event handlers or subscriptions attached and never
detached. Large objects captured by a closure that outlives their usefulness.

**Missing indexes for new access paths.** A new query filtering or sorting on a column with
no supporting index is a table scan that grows with the table. Check the schema, not just
the query.

**Algorithmic shape.** Nested iteration over the same growing collection. Repeated linear
search where a lookup structure is already available. Sorting inside a loop.

## What this lens must not report

- Micro-optimizations with no measurable effect: allocation counts on cold paths, string
  concatenation outside a loop, choice of equivalent collection types.
- Findings without a named growth dimension.
- Speculative caching, or any suggestion whose benefit you cannot state concretely.
- Correctness defects — the correctness lens owns those, including in code that also happens
  to be slow.
- Denial-of-service framing. Cost is a performance concern here, not a security one.
