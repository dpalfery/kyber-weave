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

**Malformed and null input: the input-site table.** This is required output, not a reading
habit, because a seat that traces only the inputs it happens to think of misses the rest. For every changed code file, write "no input site" or one row per site where a value from outside the process is parsed, converted, or passed to a file or collection API. That covers `Parse` and
`TryParse`, a regex capture turned into a number, `Deserialize`, a path passed to `File.*`, and a
number used as an index or a line. Write the table in Markdown, before your findings list, with every row starting `|` at column 0 so the verdict command skips it:

| Site | Source | Absent or null | Null element in a list | Empty | Wrong type | Zero or negative | Largest value | Characters the API rejects | Repeated or contradictory markers |
|---|---|---|---|---|---|---|---|---|---|
| `path/File.cs:120` | seat file | refused | handled | refused | THROWS | handled | THROWS | handled | accepted wrong |

Each cell says what the code does today for that input: `refused`, `handled`, `THROWS` or
`accepted wrong`. Trace each one through the code; a cell you did not trace is not `handled`.
Every THROWS or accepted wrong cell is a finding. A file with no such site gets one line,
`path/File.cs: no input site`, so the table shows that every changed file was looked at.

Close a correctness seat with Input sites: N traced, M files with none, directly above the Discovery line. N counts the table's rows and M the files you marked "no input site". Write the line
even when nothing was traced, as `Input sites: 0 traced, M files with none`: the verdict command
does not know which files changed, so it warns (`KW-REVIEW-047`) whenever a correctness seat that
reported findings or `NO FINDINGS` wrote no readable `Input sites:` line.

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
- Missing tests. The test-adequacy lens owns coverage; you own whether the code is right,
  including every malformed input test-adequacy hands you as `test-adequacy/out-of-scope`.
- Hypothetical misuse by a caller that does not exist and could not exist given the
  visibility of the code.
