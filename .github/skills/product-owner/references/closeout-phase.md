# Closeout Phase

Retire a specification after delivery. `docs-dev`, not the product owner or implementing worker, performs this phase as a distinct conductor-assigned task.

Resolve the active and archive locations from **<specification-index>**. Never assume a fixed documentation root.

## Preconditions

Stop and return `STATUS: NOT_READY` unless:

1. Every implementation task and task-review pass is complete.
2. The selected Test contract or verification contract has current passing evidence.
3. Every requirement id traces to delivered code and tests.
4. The end-of-run `code-reviewer` verdict is `APPROVE`.

## Closeout

1. Verify each requirement against implementation, tests, and review evidence; implementation is authoritative where it diverged from intent.
2. Migrate durable architecture, behavior, operations, configuration, interfaces, and consequential decisions into the canonical documentation located through Config Reg. Create an ADR only when the repository's ADR criteria are met.
3. Move the specification from the active inventory to the archive register, naming the canonical documents that replace it.
4. Archive the whole feature specification and mark its artifacts archived according to the index's lifecycle rules.
5. Run the repository's documentation validation and drift checks.

Migrate first and archive second. Archived specifications are not current guidance.

## Digest

```text
STATUS: ARCHIVED
SPECIFICATION: <feature name>
REQUIREMENTS_VERIFIED: <count / total>
DOCUMENTATION_UPDATED: <canonical documents and migrated facts>
INDEX: <archive entry and replacing documentation>
VALIDATION: <documentation validation and drift results>
```

or:

```text
STATUS: NOT_READY
SPECIFICATION: <feature name>
BLOCKER: <failed precondition and evidence>
RETURNED_TO: In progress | Blocked | Review required
```

Never ask the user anything from this phase.
