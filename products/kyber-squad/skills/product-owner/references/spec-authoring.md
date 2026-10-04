# Spec Authoring

Specification phase files are durable state. A live agent instance is disposable.

## Start or resume

1. Resolve the specification directory and **<specification-index>** through Config Reg, then read the index.
2. Use a prompt-supplied artifact path only when its canonical path remains beneath the specification directory. If the destination phase file does not exist, create it before writing phase content. On harnesses where file editing requires an existing destination file (such as Devin), initialize the file via shell (for example, `touch <file>`) before editing.
3. Then load only the reference for the assigned phase.
4. Reconcile body status, frontmatter lifecycle, and index status in the same save.
5. Save after every write and before every status handoff.

Open only active Draft or Ready specifications. Completed, Superseded, and archived artifacts are not authoring authority.

A failed write or documentation check returns `STATUS: SPEC_WRITE_ERROR` with the intended path and exact error. Never report a later lifecycle from unsaved state.
