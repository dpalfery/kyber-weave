---
id: adr/0037-squad-state-in-user-home
title: Squad Lock and Receipt State Moves to the User Home, with a Registry and a Global Anchor
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-14
component: KyberSquad
code-refs:
  - SquadStateStore
  - SquadStateMigration
---

# ADR 0037: Squad Lock and Receipt State Moves to the User Home, with a Registry and a Global Anchor

## Status

Accepted, 2026-10-14. Amends [ADR 0024](0024-squad-global-receipt-layout-marker.md): project
receipts are no longer byte-identical to receipts written before, because `targetRoot` now
records the canonical physical root instead of `"."`. Every other decision of ADR 0024 stands,
including the v2 global layout marker and legacy single-root recovery.

## Context

Squad's lock (`squad.lock.yml`) and receipt (`squad.receipt.json`) were kept in two places that
did not agree:

- **Project installs** wrote both files into the repository at `.kyber-weave/`. Their
  `targetRoot` was `"."`, so a receipt named no physical root and nothing outside the
  repository could list the deployments a machine held.
- **Global installs** wrote both files into the per-user application-data tree under
  `KyberWeave/squad/roots/<root-key>/`, one directory per binding. Global scope was anchored to
  the root that the invoking directory resolved to, so the same `--global` command could act on
  different state depending on where it ran, and several sibling global receipts could exist.

Without one location for lock and receipt state, a global update could not find the projects
it should carry, and `status` and `doctor` could not report stale or unmigrated state. Install
state that describes the files deployed on one machine was also written into the repository,
where it is version-controlled alongside the project it describes.

## Decision

1. **Lock and receipt state lives under the user home.** The state root is
   `<home>/.kyber-weave/squad/`, where `<home>` is `ISquadUserPaths.HomeDirectory`, the user
   profile in production. Global state is `global/squad.lock.yml` and
   `global/squad.receipt.json`. Project state is `projects/<root-key>/squad.lock.yml` and
   `projects/<root-key>/squad.receipt.json`, where `<root-key>` is the lowercase SHA-256 of the
   canonical physical root path (`SquadPhysicalRootIdentity.Key`). Lexical and symbolic-link
   spellings of one directory therefore share one entry. The application-data directory is no
   longer a state location. It is read only as the legacy migration source.

2. **Global scope has one anchor, the home directory, with one exception.** Under `--global`, the
   positional path, `--path`, and the invoking directory are ignored. A path supplied with
   `--global` produces a warning and the command continues; the warning is not printed when the
   supplied path is `.`. The exception is a legacy pre-existing single-root global receipt. Its
   relative paths are not portable, so after migration (decision 6) the anchor is the absolute
   `targetRoot` that migration recorded, not the home and not the invoking directory. Migration runs
   first and the anchor is chosen second, so every later run of that deployment anchors on the same
   root. For that case the warning still prints when a path is supplied, and it names the recorded
   root in place of the home. A legacy receipt that records no absolute root keeps the operator's
   path, or the process directory when none is given, as its anchor, and prints no such warning. The
   global mutex is keyed by the physical home, and the global transaction work directory and journal
   are `<home>/.kyber-weave/.squad-transaction`.

3. **Receipts record the physical root.** A project receipt's `targetRoot`, previously `"."`,
   stores the canonical absolute physical root. A global receipt stores the home anchor, or, for
   a migrated legacy single-root receipt, the binding root resolved during migration. Receipts
   that still carry `"."` remain readable. Schema versions do not change: project receipts stay on
   `kyber-squad.receipt/v1` and global receipts on `kyber-squad.receipt/v2`. This is the amendment
   to ADR 0024 recorded in the Status above.

4. **A registry indexes project roots, and is not an authority.**
   `<home>/.kyber-weave/squad/registry.json`, schema `kyber-squad.registry/v1`, maps each root
   key to its physical path, the last update time in UTC, and its targets. Project install,
   update, and import upsert an entry; project uninstall removes it. The file is written
   atomically through a temporary file and a rename. `ListProjects` marks an entry stale with a
   reason (path missing, physical key mismatch, or receipt missing) and rebuilds an entry that
   has a receipt under `projects/<root-key>/` but no registry row. A failed registry write after
   a committed transaction produces a warning that names the remedy, rerunning `squad status`, and
   does not fail the transaction. Global state is not registered.

   The registry is reconciled when `squad status --global`, `squad update --global`, or
   `squad doctor` lists it, and when `squad update --global --projects` resolves a named path. No
   other verb rebuilds it, so a rebuilt entry can lag a project install until one of those runs. A
   failed write after a committed transaction warns, "Run `squad status --global` to rebuild the
   registry". There is no command that unregisters a stale entry: an entry whose directory no longer
   exists stays flagged until that path returns. Project uninstall removes the entry it names, but only
   while that directory exists, because uninstall requires an existing project root.

5. **One global receipt.** Sibling global receipts are retired. `global/squad.receipt.json` is
   the only global receipt. Legacy global roots that have not been migrated are reported by
   `squad doctor`.

6. **Migration is automatic, non-destructive, and idempotent.** One shared helper runs before
   every squad verb (install, update, status, uninstall, doctor). It applies these rules:
   - Empty legacy roots are ignored.
   - With exactly one non-empty legacy global receipt and no home global receipt, the receipt and
     lock are copied into `global/`, with `targetRoot` set per decision 3. The legacy files are
     left untouched.
   - A legacy receipt in per-target-roots layout needs no root. A single-root receipt needs its
     binding root resolved, in this order: an absolute `targetRoot`, the registry, then a key
     match against the home directory or the invoking directory. A receipt whose root cannot be
     resolved is not migrated and is reported.
   - With more than one non-empty legacy global receipt, none is migrated automatically. Each is
     reported with guidance. Squad does not guess which one is meant.
   - When a home global receipt already exists, migration does nothing.
   - When a project-scope verb or a picker-driven update touches root R, `projects/<root-key(R)>/`
     has no receipt, and `R/.kyber-weave/squad.receipt.json` exists, the receipt and lock are
     imported, `targetRoot` is set, and R is registered. The in-repository files are untouched.
     Home state wins from then on.
   - Under `--dry-run` nothing is written, and the ledger is not written either. The run reports that
     the legacy state at each path will be migrated on the next invocation without `--dry-run`.
     Install, update, and uninstall stop after that report, so they never plan against state the
     real run has not yet moved.
   - Every copy or import runs under the same transaction lease an install takes for the root. A busy
     lease writes nothing and warns; the legacy files stay in place and the next run retries.
   - A global copy writes the lock first and the receipt last, and the receipt is the completion
     marker. A crash that leaves a home lock with no receipt is completed on the next run when that
     lock is byte-identical to the source lock. A home lock that differs blocks the copy and is
     reported.
   - `squad doctor` runs this migration as a verb, so it writes as well as reports.

7. **Legacy in-repository files are retained.** Migration never deletes a file. The legacy
   `.kyber-weave/squad.lock.yml` and `squad.receipt.json` in a host repository remain on disk for
   rollback, and `squad doctor` warns that they were retained and should be removed from version
   control.

8. **Mutex and transaction work directory stay per physical root.** Only the receipt and lock
   move. A project installed at the home directory resolves to the same physical root as the
   global anchor, so it contends for the same mutex and uses the same work directory.

9. **A migration ledger makes each migration one-shot per source.** The ledger is
   `<home>/.kyber-weave/squad/migration-ledger.json`, schema `kyber-squad.migration-ledger/v1`, with
   two arrays of root keys:

   ```json
   { "globalMigratedFrom": ["<legacy-root-key>"], "projectsImported": ["<root-key>"] }
   ```

   A global copy adds its legacy root key to `globalMigratedFrom`, after the receipt is written. An
   import adds the project's root key to `projectsImported`, in the same lease as its writes. Once a
   key is recorded, the legacy files are not copied or imported again. That is the point of the
   ledger: an uninstall of migrated state is not undone by the legacy copy that decision 7 keeps.
   Only migration reads the ledger, so a fresh install after an uninstall is not blocked by it. A
   dry run never writes the ledger. A ledger that does not parse, or does not match the schema, is
   read as empty and reported as a warning; the next successful write replaces it. No command edits or
   removes ledger entries.

## Alternatives Considered

- **Move only global state and keep project state in the repository.** Rejected: a registry and a
  single `doctor` report need one location that lists every deployment on the machine, and two
  stores would each need their own reconciliation.
- **Keep global scope anchored to the invoking directory's root.** Rejected: the same `--global`
  command would keep acting on different state depending on where it was run, which is the
  defect decision 2 removes.
- **Make the registry the authority for project state.** Rejected: an index that is wrong must
  not change what a deployment is taken to own. Receipts under `projects/<root-key>/` stay
  authoritative, and the registry is rebuilt from them.
- **Keep project receipts byte-identical and record the root somewhere else.** Rejected: the
  `"."` target root names no physical root, and import and registry both need one. Giving up
  ADR 0024's byte-identical rule for project receipts is the cost, and this record accepts it.
- **Migrate a single legacy global root by guessing among several.** Rejected: migration fails
  closed. The owner resolves the ambiguity by hand.
- **Keep sibling global receipts.** Rejected: one global anchor needs one global receipt.
- **Delete legacy files once they are migrated.** Rejected: older CLIs and rollback still read
  them.

## Consequences

- An older CLI keeps reading the legacy locations and files, which are retained for rollback.
  Every machine that runs Squad should be upgraded. Once a root has been imported, its home state
  wins, so changes an older CLI makes to the legacy files afterwards are not seen by the newer CLI.
- A project whose repository was moved or deleted appears in the registry as stale (path
  missing) rather than being forgotten.
- A project receipt from before this record still holds `"."` and remains readable. A newly
  written project receipt holds the absolute physical root.
- Receipt schemas do not change. Migration only changes where a receipt is read from and what
  its `targetRoot` records; the layout rules of ADR 0024 apply to migrated receipts as they do to
  any other.
- Tests inject the home directory and never touch the real home directory.
- **Placement trade-off of decision 1 and 8.** The global transaction work directory, journal,
  staging, and backups live under `<home>/.kyber-weave/.squad-transaction`. A project's lock and
  receipt are published into the home store, while the project's journal stays beside the project
  root. When the destination is on a different volume from the home, placement is not an atomic
  rename: files are copied and then deleted, and directory moves fail closed and roll back. Operators
  who relocate a tool directory with `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, or a similar override should
  keep that directory on the home volume.
- **Downgrade limit.** The intent journal gains an additive `home` value in its created-directory
  `area` field. An older CLI that recovers a journal containing that value fails closed with
  `InvalidDataException` rather than recovering it wrongly. Older CLIs still read the legacy
  locations and files, as decision 7 requires. Only a journal that records a home-area directory
  triggers the fail-closed refusal; an interrupted transaction from the new CLI that has none is
  recovered as before.
- **Registry updates are not locked across roots.** The registry's read-modify-write is not held
  under a lock that spans different roots' leases, so two projects updated at the same moment can
  lose one registry update. The receipt under `projects/<root-key>/` is the authority, so a lost
  entry is recovered by `squad status --global` or `squad doctor`, which rebuild the registry from
  those receipts.

## Related

- [ADR 0024: An Explicit Layout Marker on Global Squad Receipts, with Legacy Single-Root Recovery](0024-squad-global-receipt-layout-marker.md), amended here
- [Kyber-Squad architecture: state, identity, and locking](../kyber-squad/architecture.md#4-state-identity-and-locking-model)
- [Kyber-Squad adoption and usage guide: deployment scopes](../kyber-squad/onboarding.md#deployment-scopes)
