---
id: adr/0094-squad-inserts-the-kw-docs-entry-into-antigravitys-user-mcp-config
title: Squad inserts the kw-docs entry into Antigravity's user MCP config
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-25
component: KyberSquad
decided-by: []
code-refs:
  - AntigravityMcpConfiguration
  - SquadDocsServerCommand
  - AntigravityRenderer
  - McpServeMode
  - DocsRootRegistry
  - JsonServerSplice
---

# ADR 0094: Squad inserts the kw-docs entry into Antigravity's user MCP config

## Status

Accepted, 2026-11-25. Records the design of the plan [kw-docs any-repo mode, and Squad wiring it for
Antigravity](../archive/plans/2026-11-25-kw-docs-any-repo-mode-for-antigravity.md) (decisions D1 to D17 and D21),
approved by the owner in chat the same day. It narrows decision 2 of
[ADR 0052](0052-docs-branch-writes-repository-scope-mcp-configuration.md) for this one write and changes
nothing else in it.

## Context

Antigravity starts every MCP server once, from its hub process, with the working directory `/` (verified on the
owner's Mac, 2026-11-25). The `kw-docs` entry Squad rendered into agent files, `kyber-weave-mcp --repo-root .`,
therefore resolved to `/`, exited with `KW-MCP-ROOT-001`, and Antigravity sessions had no documentation tools. A
pinned absolute root works for one repository only, and Antigravity reads a user-scope file
(`~/.gemini/config/mcp_config.json`) that no Squad or docs-branch writer touched, so the operator had to hand-write
a working entry.

## Decision

1. **`kyber-weave-mcp --any-repo` is a server mode with no bound root.** Every tool requires `projectPath`, the
   path of a directory inside an initialized repository (git top level with a host configuration). Combined with
   `--repo-root` or `--expect-root` it refuses to start with `KW-MCP-ROOT-003`. The mode is opt-in; bound mode and
   its `KW-MCP-ROOT-001` and `-002` rules are unchanged.
2. **Squad renders that entry into Antigravity agent files.** Global renders carry the absolute path PATH names for
   `kyber-weave-mcp` (Antigravity does not expand `~`); project renders, whose files may be committed, carry the
   bare name. Both pass `--any-repo`.
3. **A global `squad install` and `squad update` insert one `kw-docs` entry into Antigravity's user MCP config.**
   This is the single exception to "Squad writes no harness MCP configuration". The write is a byte splice
   (`JsonServerSplice`, the splice `docs mcp-wire` uses) that inserts one member and keeps every other byte, is
   atomic and keeps the file's Unix mode and symbolic link, never replaces an existing `kw-docs` entry (it warns when
   that entry lacks `--any-repo`), refuses a file that is malformed, not an object, or over 1 MiB, writes nothing on
   a dry run, is idempotent, and never puts file contents in a warning, because that file can hold other servers' keys.
4. **ADR 0052's list of writers is unchanged.** The docs branch still never writes user scope; this write is Squad's
   and is limited to Antigravity's global file. Instance and project scope write nothing.
5. **`squad uninstall` leaves the entry.** The entry points at a machine-level executable Squad does not own.

## Consequences

- An Antigravity session gets `kw-docs` tools in any initialized repository without per-repository wiring; the agent
  passes `projectPath` per call.
- A hand-pinned entry is never overwritten; the operator switches it to `--any-repo` by hand after the warning.
- An absolute command goes stale if the executable moves off its PATH directory; a re-render fixes the agent files,
  and the config entry is fixed by hand.
- The seam `ISquadFileAccess` grew `WriteAllBytesAtomic` and `GetLength`, and `AtomicFile` an opt-in mode-preserving
  overload ([seam register](../reference/seams.md)).

## Alternatives rejected

- **Pin `--repo-root` to an absolute path**: serves one repository.
- **Set the working directory in the entry (`cwd`)**: one entry per repository, again.
- **Leave the file to the operator**: the documented recipe was the broken one, and the operator's file is where
  Antigravity looks first.
- **Overwrite an existing entry**: could silently destroy a deliberate pin.
