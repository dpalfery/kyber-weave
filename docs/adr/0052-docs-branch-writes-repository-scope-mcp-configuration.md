---
id: adr/0052-docs-branch-writes-repository-scope-mcp-configuration
title: The Docs Branch Writes Repository-Scope Harness MCP Configuration, Never User Scope
doc-type: adr
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-31
decided-by: []
supersedes: []
---

# ADR 0052: The Docs Branch Writes Repository-Scope Harness MCP Configuration, Never User Scope

## Status

Accepted, 2026-10-31, with the MCP configuration wiring specification. It is deliberately a
**new record rather than an amendment**: the rule ADR 0026 states stays true, word for word,
for the component it governs.

## Context

Two standing decisions keep deployment tools out of harness configuration files.
**ADR 0026** limits Kyber Utilities to files it owns: it "never creates, edits, merges, backs
up, restores, or reads a shared harness settings file — for any harness and any command,
including `--dry-run`", and activation is manual. Squad drew the sibling boundary earlier and
holds it without exception ("Squad does not own settings files", D-no-settings,
[Kyber-Squad architecture](../kyber-squad/architecture.md) §3).

Neither decision covers the case that produced this record. A host's agent instructions
declare the MCP servers they rely on — in this repository, the kyber-weave documentation
server — and each harness reads its own file to learn about them. Nothing writes those files:
the [DocGraph MCP runbook](../docgraph/mcp-runbook.md) documents each client's recipe by
hand, one file and one key path at a time, and detects nothing. When the entry is missing, an
agent that needs the server fails at the point of use: the observed symptom is `architect`
returning `STATUS: BLOCKED` because the documentation tools it must call are not wired, with
nothing in the run output naming the file to edit.

ADR 0026 never draws the distinction this decision turns on, because it did not need to. Its
subject is a **status-line settings key at user scope**: a file under the developer's home
directory, in a schema that drifts between harness versions, auto-loaded by some harnesses and
invisible to the tool afterwards. The files this decision concerns are a different class:

- They live **inside the repository tree**, are committed, and are reviewed in a pull request.
  A wrong edit is visible in a diff and undone with `git revert`, not with a support request.
- They hold **one discrete, named server entry** per server, not a settings key whose shape
  changes under the tool.
- They are **team-shared**: a checkout that wires them wires them for every teammate, which is
  the point — the failure being repaired is a property of the repository, not of one machine.

That difference is what makes a write defensible here and not there, and it is what this
record adds to the corpus.

## Decision

**The docs branch's repair may write repository-scope harness MCP configuration, and never
user-scope configuration.**

1. **The writers are closed, and so is the file set.** `docs mcp-wire --target <harness>`
   writes the entry for the named harnesses; `docs init`'s confirmed prompt calls that same
   write path after the check finds a fixable gap; `docs mcp-unwire` restores only what a
   receipt records. No other command writes these files, and no harness outside the catalog is
   ever written.

   | Harness | Command | File written (repository scope) | Server key |
   |---|---|---|---|
   | Factory | `docs mcp-wire --target factory` | `.factory/mcp.json` | `mcpServers` |
   | Claude Code | `docs mcp-wire --target claude-code` | `.mcp.json` | `mcpServers` |
   | Cursor | `docs mcp-wire --target cursor` | `.cursor/mcp.json` | `mcpServers` |
   | VS Code | `docs mcp-wire --target vscode` | `.vscode/mcp.json` | `servers` |
   | opencode | `docs mcp-wire --target opencode` | `opencode.json` | `mcp` |

   Every path is relative to the repository root the command was pointed at. `docs mcp-unwire`
   writes only the file a receipt records, and only while that file's digest still matches what
   the wire command left.

2. **User-scope configuration is never written and never read.** These commands never write and
   never read a user-scope configuration file: `~/.factory/mcp.json`, `~/.cursor/mcp.json`,
   `~/.codex/config.toml`, `~/.config/opencode/opencode.json`, and ZCode's user storage tier
   (`<storage>/cli/config.json`) stay the developer's own, and are reported with the manual
   snippet instead. The exclusion holds for the check, the repair, and the dry run alike. A
   server configured only in a user-scope file is therefore reported as not wired for the
   repository, which is the honest report for a committed configuration.

3. **The write is bounded, and the bounds are part of the decision.** Each bound is a refusal to
   do something the repository cannot undo for a teammate:

   - **Per-target opt-in.** The repair lists the harnesses in play and the gaps found, and
     writes only harnesses the operator selected. Invoked with no target, it writes nothing.
     Running it is the consent; there is no ambient repair.
   - **Insertion-only.** Where the file does not exist, it is created holding only the declared
     servers' entries. Where it exists, the entries are inserted textually and no other content
     is touched.
   - **Never reformat, reorder, or re-serialize any byte it did not write.** Existing
     formatting, comments, and key order survive byte-for-byte outside the inserted text. A
     TOML target is handled by textual table insertion, never by parsing and re-serializing the
     whole document — which serves the byte-preservation bound and keeps the no-new-dependency
     rule.
   - **Refusal.** An entry with the same server name already present is refused for that file
     with the difference reported; it is never modified, merged, or overwritten. A file that is
     malformed, unparseable as its harness's format, or structurally foreign (the wrong
     top-level shape) is refused with a diagnostic naming it, and nothing is written to it.
   - **A receipt, with drift-refusing removal.** Every write records the target file, the
     inserted entry, and the pre-write and post-write SHA-256 digests in
     `.kyber-weave/receipts/mcp-wiring.json`, which is gitignored. `docs mcp-unwire` restores the
     pre-write state recorded there, and refuses when the file's current digest differs from the
     receipt's post-write digest rather than restoring over someone else's edit.
   - **Dry run.** `--dry-run` prints the exact per-file change and writes nothing — not even the
     receipt.

4. **An absent binary is never installed, and its entry is never written.** Kyber-Weave installs
   no external binary as a side effect of checking or repairing. When a declared third-party
   server's binary is not resolvable on `PATH`, the check reports the gap with the declaration's
   manual install text (or a manual pointer), and the repair refuses that server's entry for
   every harness: an entry whose launch command cannot work converts a diagnosable absence into
   a harness-side startup failure that blames the configuration file. When the first-party
   `kyber-weave-mcp` binary is absent, the finding says the Kyber-Weave installation itself is
   broken and points at `kyber-weave update` or the install script, not at harness configuration.

5. **ADR 0026 stands, and D-no-settings stands.** Neither is amended. ADR 0026's rule remains
   true without exception for Kyber Utilities, and Squad continues to write no harness
   configuration for any harness. This record is the docs branch's own boundary, stated for its
   own component; it borrows ADR 0026's reasoning and widens nothing that record decided.

6. **The v1 write set is narrower than the catalog, deliberately.** Codex CLI and ZCode are
   checked and reported but written by nothing: Codex's repository-scope relative-path semantics
   are unverified, and the runbook's verified form (an absolute path) cannot go into a committed
   file without breaking every other teammate; ZCode's entry body inside `mcp.servers` is pinned
   nowhere in this repository, and inventing it risks a configuration ZCode cannot load. The
   Copilot coding agent's configuration is repository settings, not a file, so no command can
   write it. Widening any of the three is a verified fact plus a catalog edit — not a change to
   this boundary. A new harness, a new key beyond the server entry, or any widening to user
   scope is a new decision.

## Alternatives Considered

- **A snippet-only remedy: print the configuration and let the operator paste it.** Rejected as
  the whole remedy. It is the state that produced the failure being repaired — the runbook's
  per-client recipes are exactly this, and the `architect` block is what an unperformed manual
  step looks like. A gap that every teammate must close by hand is closed inconsistently, and
  the printed snippet drifts from the file it describes because nothing re-checks it. The
  snippet is retained where it is the only safe answer — the non-interactive path, harnesses
  whose entry cannot be written, and user scope — but it is the degradation path, not the
  policy.
- **Extend Kyber Utilities or Kyber-Squad to write it.** Rejected. Kyber Utilities deploys owned
  files to per-user staging and activates by hand; writing inside a host repository is outside
  its scope, and widening it would make ADR 0026's own rule ambiguous. Squad owns agents and
  skill files and its architecture rejects settings ownership; droid-level MCP visibility is a
  different question from session-level wiring, and teaching Squad to write harness files would
  reverse a boundary held since before ADR 0026. A narrow writer in the component that owns the
  declaration and runs the check is cheaper than widening either.
- **A general harness-settings writer.** Rejected. Any key, in any harness's settings file, at
  either scope, is precisely what ADR 0026 forbids, and the bounds in this decision cannot be
  stated for it: there is no closed set of files to name, no per-harness verified entry shape,
  and no way to bound an insertion when the tool is allowed to write arbitrary keys. The write
  stays one catalog, one server entry per harness, repository scope only.
- **Amend ADR 0026 in place instead of writing a new record.** Rejected. ADR 0026 decides what
  Kyber Utilities may do, and that decision is unchanged; carving an exception into it would
  make its rule read as conditional for the component it still governs. A separate record on a
  different subject, cross-referencing it, leaves both statements unambiguous — and a reader of
  Kyber Utilities never has to learn about MCP wiring to understand the rule that binds it.

## Consequences

- **A committed harness configuration can now change under a repair**, so a host that keeps an
  always-human policy over its configuration path will see the change escalate for review. That
  is the intended cost: the file is team-shared, and the diff is the review.
- **A write is auditable and removable locally, and revertible for everyone else.** The receipt
  covers the operator who ran the command; a teammate who never had the receipt uses `git
  revert`, because the file is committed.
- **The user-scope gap stays manual.** The check cannot repair a configuration it will not read,
  and it cannot detect a server wired only in a user-scope file. That limit is the price of
  keeping ADR 0026's boundary intact, and it is reported rather than papered over.
- **Three harnesses are reported but never written.** A Codex or ZCode user gets the finding and
  the snippet and no repair until the missing fact is verified; the Copilot coding agent is
  configured in repository settings, where no command can reach.
- **The bounds are the contract.** Weakening one — writing without a receipt, merging an
  existing entry, re-serializing a document, writing on a dry run, writing at user scope — is a
  new decision, not an implementation detail.
- **The ownership map now has three statements, not two**, and the split is readable: Kyber
  Utilities owns per-user staged utilities, Squad owns agent and skill files, and the docs branch
  owns repository-scope harness MCP wiring under this record's bounds.

## Related

- [ADR 0026](0026-kyber-utilities-owned-files-not-settings.md) — the Kyber Utilities boundary
  this record cross-references and leaves intact
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — droid-level visibility versus
  harness-level wiring, and Squad's refusal to own settings files
- [DocGraph MCP runbook](../docgraph/mcp-runbook.md) — the per-client wiring recipes and key
  paths every written entry matches
- [Component and owner catalog](../catalog.md) — the components the three ownership statements
  belong to
