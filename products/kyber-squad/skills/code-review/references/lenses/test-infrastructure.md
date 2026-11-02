# Lens: test-infrastructure

## Applicability

Applies when the diff adds or changes **test support code**: fakes, stubs, fixtures, builders,
helpers, harnesses, shared caches, temp-directory or repository templates, console or
environment capture, test-runner parallelism settings, or any non-assertion code that lives in
a test project or test directory.

Skip when the change touches no test project and no test directory, or when it only adds or
edits individual test methods whose helpers are unchanged. (A test method's *assertions* belong
to `test-adequacy`; the code that makes many tests possible belongs here.)

## What this lens owns

Whether the shared machinery tests stand on is itself correct: isolated, deterministic,
bounded, and honest about what it provides. A test suite is production code for the people
who maintain it, and a defect in a shared fixture fails hundreds of tests at once or, worse,
lets them pass for the wrong reason.

## What to look for

**Shared mutable state across parallel tests.** A cached, static, or lazily built object handed
to more than one test. Ask who can mutate it (`Add`, `Clear`, `Sort`, in-place edits, a preset
changed after retrieval) and what the next test sees. A comment saying "treat as immutable" is a
wish, not an enforcement; the finding is the absence of a copy, a frozen type, or a read-only
view.

**Caches that remember failure.** A cache storing a `Task`, `Lazy`, or computed result must
decide what a failure does. A faulted or cancelled task stored under a key fails every later
test for that key until the process ends. Check the exception path of each `GetOrAdd`,
`Lazy`, and memoised call.

**Cache keys and directory names.** Keys derived by sanitising or joining strings: can two
distinct inputs collapse to one key (`a:b` and `a_b`)? Is every path separator and reserved
character handled on every platform the suite runs on, including backslash and drive
letters? A collision means one fixture initialised on top of another.

**Process-global state.** Environment variables, current directory, `Console` streams, static
settings, culture, time zone, and registered handlers. Any code that sets one must restore it,
and any assertion that **reads** one as a precondition (`Assert.Null(GetEnvironmentVariable)`)
races with every other test that writes it. Check *both* `Console.Out` and `Console.Error`,
and every async continuation that may outlive the scope that captured the output.

**Scope and lifetime of captured resources.** A writer, stream, process, or directory disposed
by a scope while a task started inside it can still write to it. Fire-and-forget work inside a
synchronous capture scope has no fence.

**Growth.** Caches, template directories, and temp roots that gain one entry per distinct key
for the life of the process. Name the growth dimension (the size of the test matrix) and whether
it is bounded or cleaned up. A small, fixed matrix is a note; an input-driven one is a finding.

**Fixture builders that rewrite text.** Regex or string replacement used to derive a fixture
from a canonical file must prove it fired. A pattern that matches only one formatting (inline
vs block list, LF vs CRLF, single-line vs multi-line) silently leaves the original content in
place, and the fixture then claims to be valid or minimal while it is neither. Require a
match-count assertion, or parse and re-emit the structure.

**Isolation claims versus enforcement.** A fixture or collection that documents "isolated",
"read-only", "lock-free", or "safe to run in parallel" is making a claim. Find the mechanism
that enforces it; if the claim depends on every future caller behaving, name that.

**Parallelism settings.** Removing a serialising collection or `DisableParallelization` is only
safe if every remaining writer of the state it guarded is gone. Search for the remaining
writers of the process-global state; do not trust the PR's statement that none remain.

## What this lens must not report

- Whether the tests assert the right behaviour: `test-adequacy` owns that.
- Naming, framework taste, or a preference for a different fixture library.
- Slowness of the suite as such, unless a specific construct makes it unbounded: `performance`
  owns cost at scale in production code.
- Defects in production code that a fixture happens to exercise: `correctness` owns those.
- Documentation counts and descriptions of fixtures: `intent-alignment` owns agreement between
  documents and code. Report a fixture's *documented guarantee* only when the code does not
  provide it.
