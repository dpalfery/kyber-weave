# Go review checklist

Review Go changes against the path declared as **<go-coding-standard>** in the root
`AGENTS.md` registry. That document is the checklist. This file states no rule of its own,
because a rule stated here is one a host repository cannot reverse. Its Review traps are
non-authoritative detection prompts, not rules.

When **<go-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no Go standard declared" or "Go standard is a
draft" — so the reviewer surfaces it to the human. When no standard is declared, the lens may
use the Review traps below only as detection prompts; it does not substitute a built-in
checklist. Do not report the absence as clean.

Where the standard is silent, the traps stay detection prompts only, and the lens's own concern
still applies.

## Review traps

Non-authoritative detection prompts for the correctness and security lenses when they read
a changed Go file. They name where defects recur in practice. They are not rules and not a
substitute for the declared standard: whether a pattern is allowed is the standard's call, and
a finding raised from a trap needs quoted evidence like any other.

- **Unchecked errors.** A returned `error` discarded with `_` or ignored in a bare call
  statement, especially on write, close, parse, or network paths.
- **Context propagation.** A function that accepts a `context.Context` but calls a blocking
  operation without passing it on, or that creates `context.Background()` inside request-scoped
  code, so cancellation and deadlines stop at that point.
- **Goroutine leaks.** A goroutine with no exit path, such as a send or receive with no
  cancellation, or a channel that nothing closes or drains.
