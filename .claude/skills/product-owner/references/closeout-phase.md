# Closeout Phase

Retire a specification once its tasks are delivered and their tests pass.

Closeout is not paperwork. A specification left in the active directory after its work is done reads as current guidance and is not — it describes intent, and intent diverges from the system the moment the first task lands differently than planned. Retiring it is what stops a future agent quoting a proposal as behaviour.

This phase mirrors plan closeout exactly, because a specification and a plan have the same shelf life and the same failure mode.

## Who runs it

The `docs-dev` documentation specialist, assigned by the orchestrator as a distinct task **before** the work is reported complete. The implementing agent does not close its own specification: verifying that documentation survived the change is a different job from making the change, and the same agent doing both is how "done" gets claimed without the durable content ever being written down.

## Preconditions — verify, do not assume

Do not begin closeout until all of these hold. If any fails, the specification returns to `In progress` or `Blocked` with the reason stated in the specification, and closeout stops.

1. Every task in `tasks.md` is checked off.
2. The tests those tasks specified exist and pass. Run them; a green summary from an earlier session is not evidence.
3. Every requirement ID in `requirements.md` is traceable to delivered work. A requirement no task covered is a gap, not an oversight to wave through.
4. Code review is approved, per the repository's review workflow.

## Steps

1. **Verify requirements against implementation evidence.** Read `requirements.md` and confirm each requirement against what was actually built — the code, the tests, the review record. Where the implementation diverged from the design, the implementation is the truth; note the divergence, because it is usually the most valuable thing the specification produced.

2. **Migrate the durable content.** This is the step that matters and the one most often skipped. Everything in the specification that describes *how the system now works* belongs in canonical documentation before the specification leaves. Typically:
   - architecture and component behaviour → the component's detailed documentation
   - operational procedures → a runbook
   - configuration and interfaces → the component reference
   - a decision that constrains future work → an ADR (see the repository documentation standard)
   - the component catalog entry, if the public surface, source root, or owner changed

   Ask what a reader six months from now needs. If the only place an answer exists is the specification, it has not been migrated.

3. **Update the specification index.** Move the entry from the active inventory to the archive register, recording the archive date, the outcome, and — specifically — which canonical documents now carry its content. "Verified complete" alone is not an outcome; name the documentation.

4. **Archive the specification.** Move the whole `{feature-name}/` directory to the archive specifications path, and set the status in all three documents to `Archived`.

5. **Verify the corpus is still clean.** Run the repository's documentation validation and drift checks. Migrating content moves code references between documents, and a reference that no longer resolves is a broken join, not a formatting nit.

## Ordering is the whole point

Migrate first, archive second. Archiving a specification before its durable content has been moved silently deletes that content from the governed corpus — the archive is excluded from retrieval and is never cited as current guidance, so the knowledge does not become stale, it becomes invisible. This is the specific failure the gate exists to prevent.

## Completion digest — return this; do not ask the user anything

```text
STATUS: ARCHIVED
SPECIFICATION: {feature-name}
REQUIREMENTS VERIFIED: <count verified / total, and any that could not be>
DOCUMENTATION UPDATED: <each canonical document changed, and what moved into it>
INDEX: <confirm the archive register entry names the replacing documentation>
VALIDATION: <result of the documentation validation and drift checks>
```

or, when a precondition fails:

```text
STATUS: NOT_READY
SPECIFICATION: {feature-name}
BLOCKER: <which precondition failed and the evidence>
RETURNED TO: <In progress | Blocked | Review required>
```
