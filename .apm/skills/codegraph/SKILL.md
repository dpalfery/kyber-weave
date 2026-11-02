---
name: codegraph
description: "Use when the CodeGraph index is missing or stale, when codegraph tools report a project is not indexed, or when docs drift fails KW-DOC-DRIFT-001. Do NOT use for ordinary code search or exploration — call codegraph_explore directly."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Making CodeGraph usable in this repository

CodeGraph answers structural questions about the codebase, and `docs drift` resolves
documented code references against its index. Both stop working the moment `.codegraph/`
is absent, stale, or unreadable, and the failure arrives as an error rather than as a
question — which is what this skill exists to turn around.

`kyber-weave doctor` is the check. This skill calls it; it never runs `codegraph` itself,
so every harness gets the same behaviour and the rules stay in one place.

## Do this

1. Run the read-only check:

   ```bash
   kyber-weave doctor --check
   ```

2. **Exit 0** — nothing is wrong. Stop here and carry on with the original task.

3. **Exit 3** — the `codegraph` executable is not on `PATH`. Stop, and ask the user to
   install it. Do not install it yourself, do not fetch an installer, and do not work
   around it by guessing at an index. Say plainly that CodeGraph is missing, quote the
   `fix:` line the command printed, and continue the original task without CodeGraph if
   the user prefers.

4. **Exit 1 with `codegraph.index`** and the binary present — the index is missing, stale,
   or failed to build. Provision it once:

   ```bash
   kyber-weave doctor --provision
   ```

   This is repository-local: it runs `codegraph init .` when `.codegraph/` is absent and
   `codegraph index .` otherwise, always at the git root, and it never installs anything.
   If it fails, report the check id and the `fix:` line and stop.

5. **Any other exit** — report the `FAIL <check-id> <reason>` and `fix:` lines verbatim.
   That covers `mcp.start`, `mcp.handshake`, `mcp.docs_explore` and `mcp.root`: the
   Kyber-Weave MCP server is the other half of the check. **Each check prints its own
   remedy, and it is not the same one every time**: `kyber-weave update` when the server
   will not start or answers in a shape that is not a tool result, a larger `--mcp-timeout`
   when it did not answer inside the budget, and a restart of the stale server when
   `mcp.root` names another checkout. Quote the printed line; do not substitute `update`
   for a timeout bump.

## Rules this skill keeps

- **Never install CodeGraph.** Installing is the user's decision. Exit 3 is a request for
  that decision, not a failure to route around.
- **Never delete `.codegraph/`.** If the directory is corrupt or owned by another user,
  doctor says so and suggests the removal; a human does it.
- **Repository-local only.** Provisioning runs at the git root of the repository being
  worked in, never at `$HOME` and never for another checkout.
- **Prefer `--check`.** It never writes. `--provision` indexes, which on a large
  repository costs minutes.

## When not to use this

If CodeGraph is working and you simply want to know how something is implemented, call
`codegraph_explore` (or `codegraph explore "<question>"`) directly. This skill is for the
index being unusable, not for searching.
