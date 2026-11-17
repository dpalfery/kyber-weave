---
id: adr/0086-the-task-file-is-yaml
title: The task file is YAML
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-17
component: KyberSquad
decided-by: []
supersedes: []
code-refs:
  - PlanTaskFile
  - PlanTaskFileGenerator
  - PlanTaskFileValidator
  - PlanDocumentParser
  - DocsTasksCommand
---

# ADR 0086: The task file is YAML

## Status

Accepted, 2026-11-17. Decided by the owner. It amends the stored form in
[ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md). The file stays
tracked beside its plan, the plan stays authoritative, and the execution digest and lifecycle are
unchanged.

## Context

[ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) wrote the task file
as JSON, `<plan>.tasks.json`, and a specification's as `tasks.json`. Every other data file this
work adds is YAML: the host configuration, the Arbiter rules, model and capability profiles, and
the gate catalogue. Two syntaxes for data an author may inspect is one more format to learn, and
the JSON form is the only data file in the repository written with braces.

The task file is a machine format: the generator writes it, the validator reads it, and no agent
edits it by hand. Changing its stored form changes who reads which spelling, not what it means.

## Decision

1. **A plan's task file is `<plan>.tasks.yml`, and a specification's is `<spec-folder>/tasks.yml`.**
   Both sit where the JSON form sat, beside the plan or the specification's `tasks.md`, and both
   are committed with the artifact.

2. **Schema version 2 is the YAML shape.** The field names are unchanged and stay kebab-case
   (`schema-version`, `execution-digest`, `depends-on`, `difficulty-source`, `worker-roster`), so
   the YAML is the same graph the JSON form held, converted in one place by
   `PlanTaskFileFormat`. The file is written as LF-normalised YAML with one final newline, and the
   numeric fields are read back from YAML scalars before the model's strict reader sees them.

3. **The old JSON file is read for two minor releases, with a warning.** A `.tasks.json` beside a
   plan, or a `tasks.json` beside a specification's `tasks.md`, is read as schema version 1 and
   reported as `KW-DOC-TASKS-014` with a hint naming the conversion command. The warning is not
   an error, so an existing plan keeps working while its author converts it.

4. **`docs tasks <plan> --convert` rewrites the file as YAML.** It writes the YAML form and removes
   the JSON file once the YAML is on disk; when there is no JSON file to convert it says so and
   changes nothing. `--write` writes the YAML file in place and removes a legacy JSON beside it
   once the entries are the same. A version-1 YAML file is read and reported as needing `--write`,
   not as malformed.

5. **The digest and lifecycle are unchanged.** The execution digest is still over the parsed plan,
   not over the file's bytes, so converting the format does not make a current file stale.
   `KW-DOC-TASKS-001` to `-007` keep their meanings, `docs-dev` still removes the file with
   `git rm` when it archives the plan, and `KW-DOC-LIFECYCLE-005` still fails a file left behind.
   The new task fields of
   [ADR 0085](0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md)
   (`difficulty`, `difficulty-source`, `technology`) are carried in this shape, and the top-level
   `worker-roster` records the roster the file was written under.

### What this changes in ADR 0063

- **The path and the schema version change.** `<plan>.tasks.json` becomes `<plan>.tasks.yml` at
  schema version 2; a specification's `tasks.json` becomes `tasks.yml`.
- **The format changes, the contract does not.** The architect still writes the file at
  finalization and commits it with the plan; the conductor still dispatches by task id; the plan is
  still the authority checked by one execution digest; and the file is still removed in the commit
  that archives the plan.
- **The fetch step is no longer how the prompt reaches a worker.** ADR 0063's `docs packet` fetch
  is replaced by the dispatch hook and its file fallback in
  [ADR 0085](0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md).
  `docs packet` remains the repair and fallback path when no hook ran.

## Consequences

- One stored-data syntax across the repository: YAML. An author who reads a task file sees the
  same spelling as the configuration.
- A legacy file costs one warning and one command, not a break. The two-minor-release window is
  the same one used for the standards file rename, so a host has one conversion story to learn.
- A YAML file has no braces and no trailing-comma trap, but it is whitespace-sensitive. The
  generator owns the layout and `--check` reports a file whose content matches but whose form does
  not, so a hand edit is caught rather than silently accepted.
- The conversion command deletes the JSON file in the same step that writes the YAML, so the two
  forms never sit beside one another as competing authorities.

## Related

- [ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the tracked task file, its digest, its lifecycle and the plan's authority
- [ADR 0085](0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md) — the fields the file now carries and how its prompt reaches a worker
- [Kyber-Squad architecture](../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) — the file, the digest and the dispatch
- [Documentation governance](../docgraph/governance.md) — the task-file rules, including the legacy warning
- [Configuration](../configuration.md) — every stored data file this work adds is YAML
