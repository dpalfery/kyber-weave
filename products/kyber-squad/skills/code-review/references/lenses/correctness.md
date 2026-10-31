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

**Operational lifecycle state-machine walk.** Walk the complete lifecycle of any concurrency primitive, worktree allocation, mutex/lock, or temp-path: from `create` → `execute` → `integrate` → `cleanup`/`remove`/`prune`, and trace failure paths including `conflict` and `rollback`. Resources allocated during execution or integration must not be leaked or orphaned when operations abort or encounter conflict/rollback; cleanup must be deterministic and guaranteed across all exit branches.

**Literal prompt parsing.** When reviewing agent prompts and markdown instructions, mandate literal parsing of instructions. Prohibit charitable reading of prompts: agents interpret text literally. Flag any gate deadlock, instruction ambiguity, self-contradiction, or impossible precondition that traps an executing model.

**Inverted or dropped conditions.** Negation errors, a short-circuit that skips a necessary
side effect, an `else` attached to the wrong branch, a condition that can never be true.

**Malformed and null input.** For every input the change parses, reads, or accepts from another
component, trace what happens when it is null, empty, truncated, of the wrong type, or shaped
differently from the happy path. Name the input and the wrong result it produces; a parser that
throws on a missing field where the caller expects an absent value is a finding.

**Non-finite numbers.** Where arithmetic or comparison touches a number that can arrive as
`NaN`, `Infinity`, `-Infinity`, or a negative zero, check what each comparison, sum, and
division does with it. A `NaN` that compares false to everything passes a range check it should
fail.

**Boundaries across line endings.** Where the change reads, splits, or counts lines or
offsets, test the boundary on both `LF` and `CRLF` input: a split on `\n` that leaves a trailing
`\r`, an offset that counts two characters where the code counts one, or a last line with no
terminator are the defects this catches.

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
