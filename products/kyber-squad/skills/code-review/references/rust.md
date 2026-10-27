# Rust review checklist

Review Rust changes against the path declared as **<rust-coding-standard>** in the root
`AGENTS.md` registry (in this repository, `docs/standards/rust/README.md`). That document is
the checklist. This file states no rule of its own, because a rule stated here is one a host
repository cannot reverse. Its Review traps are non-authoritative detection prompts, not rules.

When **<rust-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no Rust standard declared" or "Rust
standard is a draft" — so the reviewer surfaces it to the human. When no standard is declared,
the lens may use the Review traps below only as detection prompts; it does not substitute a
built-in checklist. Do not report the absence as clean.

Where the standard is silent, the traps stay detection prompts only, and the lens's own concern
still applies.

## Review traps

Non-authoritative detection prompts for the correctness and security lenses when they read a
changed Rust file. They name where defects recur in practice. They are not rules and not a
substitute for the declared standard: whether a pattern is allowed is the standard's call, and
a finding raised from a trap needs quoted evidence like any other.

- **Panics on external input.** `unwrap()` or `expect()` on a value from a file, network
  socket, IPC call, or environment variable. Check whether a `Result` the caller can handle
  was the right shape.
- **Lossy integer casts.** An `as` cast between integer widths or signedness truncates or
  wraps without error. Check every cast whose source value came from outside the process.
- **Unbounded reads.** `read_to_string`, `read_to_end`, or an unlimited line read over untrusted
  input with no size cap.
