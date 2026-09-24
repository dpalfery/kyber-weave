# Lens: correctness

## Applicability

Applies whenever the diff changes executable logic in any language.

Skip when the change is confined to documentation, comments, formatting, or declarative
data with no behavioural effect.

## What this lens owns

Whether the code does the right thing on the inputs it will actually receive: control flow,
boundaries, state, concurrency, and the error paths nobody exercises until production does.

## What to look for

**Boundaries.** Empty collection, single element, exactly-at-the-limit, one past it, zero,
negative, maximum value, first iteration, last iteration. Read the comparison operators
carefully — `<` where `<=` was meant is invisible at a glance and wrong at exactly one input.

**Absent values.** Every dereference of something that can be absent, and every place a
default silently substitutes for a real value. Distinguish "absent" from "empty" from
"zero": code that conflates them is code that will one day treat a legitimately empty
result as a failure, or a failure as an empty result.

**Error paths.** Follow every failure branch to its end. A caught exception that is logged
and then allowed to continue as though nothing happened is a silent failure — the calling
code proceeds on state that was never established. A retry with no bound is a hang. A
fallback that returns plausible-looking wrong data is worse than a thrown error.

**State and lifetime.** Mutation of something shared, ordering assumptions between
operations that are not actually ordered, a cache that outlives the validity of what it
caches, an object used after it was disposed or released.

**Concurrency, only where it is real.** Interleavings that the code genuinely permits — a
check-then-act on shared state, an await between a read and the write that depends on it,
an assumption of single-threaded access in something the framework calls concurrently. Do
not report theoretical races you cannot construct.

**Inverted or dropped conditions.** Negation errors, a short-circuit that skips a necessary
side effect, an `else` attached to the wrong branch, a condition that can never be true.

**Silent truncation and conversion.** Narrowing casts, precision loss in money or time
arithmetic, string operations that assume single-byte characters, time-zone-naive handling
of instants.

## What this lens must not report

- Style, naming, or formatting.
- Performance, unless the code is outright wrong rather than merely slow — the performance
  lens owns cost.
- Security consequences — the security and authz lenses own those, and duplicate reporting
  costs the reviewer real reconciliation work.
- Missing tests. The test-adequacy lens owns coverage; you own whether the code is right.
- Hypothetical misuse by a caller that does not exist and could not exist given the
  visibility of the code.
