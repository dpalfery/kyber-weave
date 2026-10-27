# TypeScript review checklist

Review TypeScript changes against the path declared as **<typescript-coding-standard>** in the root
`AGENTS.md` registry (in this repository, `docs/standards/typescript/README.md`). That document is
the checklist. This file states no rule of its own, because a rule stated here is one a host
repository cannot reverse. Its Review traps are non-authoritative detection prompts, not rules.

When **<typescript-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no TypeScript standard declared" or "TypeScript
standard is a draft" — so the reviewer surfaces it to the human. Do not substitute a
built-in checklist, and do not report the absence as clean.

Where the standard is silent, the traps below stay detection prompts only, and the lens's own
concern still applies.

## Review traps

Non-authoritative detection prompts for the correctness and security lenses when they read a
changed TypeScript file. They name where defects recur in Node code. They are not rules and
not a substitute for the declared standard: whether a pattern is allowed is the standard's call,
and a finding raised from a trap needs quoted evidence like any other.

- **Absent and malformed input.** `null`, `undefined`, or a malformed JSON payload reaching a
  property access or a typed cast. Check the failure path of every parse step.
- **Numeric edges.** `NaN`, `Infinity`, and numeric strings where a number is expected.
  `Number("")` is `0`, `parseInt` truncates trailing text, and every comparison with `NaN` is false.
- **Unbounded reads and writes.** `JSON.stringify` of, or a body or file read of, input with no
  size limit when an outside party controls the size.
- **Swallowed errors.** An empty or log-only `catch` that lets execution continue in a state the
  code never established.
- **Split-brain parsing.** Two modules parsing the same external format with different rules, so
  one accepts input the other rejects.
