# Tauri review checklist

Review Tauri changes against the path declared as **<tauri-coding-standard>** in the root
`AGENTS.md` registry (in this repository, `docs/standards/tauri/README.md`). That document is
the checklist. This file states no rule of its own, because a rule stated here is one a host
repository cannot reverse. Its Review traps are non-authoritative detection prompts, not rules.

When **<tauri-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no Tauri standard declared" or "Tauri
standard is a draft" — so the reviewer surfaces it to the human. When no standard is declared,
the lens may use the Review traps below only as detection prompts; it does not substitute a
built-in checklist. Do not report the absence as clean.

Where the standard is silent, the traps stay detection prompts only, and the lens's own concern
still applies.

## Review traps

Non-authoritative detection prompts for the correctness and security lenses when they read a
changed Tauri file, including the Rust side of its IPC boundary. They name where defects recur
in practice. They are not rules and not a substitute for the declared standard: whether a
pattern is allowed is the standard's call, and a finding raised from a trap needs quoted
evidence like any other.

- **IPC command input.** A `#[tauri::command]` argument comes from the webview, so treat it as
  outside input. Check that type, range, length, and path shape are validated before use, and
  that the command does not trust a frontend-supplied path or identifier.
- **Capability and permission scope.** A capability file grants a window, command, filesystem,
  or shell scope wider than the feature that uses it needs. Check each grant against its
  caller.
