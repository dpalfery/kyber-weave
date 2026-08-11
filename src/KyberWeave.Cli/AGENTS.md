# KyberWeave.Cli

The `kyber-weave` binary. Spectre.Console.Cli, three symmetric branches — `skill`,
`agent`, `docs` — registered in `Program.cs`.

Read [`/AGENTS.md`](../../AGENTS.md) first for repository-wide rules.

## Adding a command

Three edits, in this order:

1. A `*Settings` class. Inherit `AnalysisSettings` if the command reports diagnostics — it
   supplies `--format`, `--no-info`, `--config`, and the path argument. Inherit
   `DocsSettings` for docs commands to also get `--docs-root`.
2. A `Command<TSettings>` whose `Execute` does the work and returns the exit code.
3. Registration in `Program.cs` with `.WithDescription()` and at least one `.WithExample()`.

Keep the branches symmetric. If a verb exists for one artifact class and makes sense for
another, that is a gap worth naming rather than an asymmetry to preserve.

## This is a composition root

The CLI decides which implementations Core uses. `DocsCommandComposition` builds the
`DocumentLoader` and the `ICodeGraphResolver`; commands take what it returns. Do not
construct adapters inside Core — see [`../KyberWeave.Core/AGENTS.md`](../KyberWeave.Core/AGENTS.md).

## Reporting and exit codes

Route diagnostics through `CommandHelpers.Finish`, which applies `--no-info`, renders in
the requested format, and prints the summary. Do not hand-roll output for findings.

Exit codes are deliberately not uniform, because the branches carry different risk:

| Command kind | Non-zero on |
|---|---|
| `validate`, `lint`, `sync-check`, `drift` | any error |
| `scan` | any critical, raised by `--fail-on warning\|error` |
| `route` | accuracy below `--min-accuracy` |

Scanning gates on `Critical` alone by default so that adopting it does not immediately
break every host build. Do not tighten that default.

Configuration failures surface as `KW-CONFIG-001` through `CommandHelpers.TryLoadConfig`,
never as an unhandled exception.

## stdout belongs to Spectre

Human output goes through `AnsiConsole`; machine output happens only via `--format
json|sarif`. Escape interpolated values with `Markup.Escape` — a path or a message
containing `[` will otherwise be parsed as markup and throw.

This is also why the MCP server is a separate binary: it cannot share a process with a
library that writes to stdout. See [`../KyberWeave.Mcp/AGENTS.md`](../KyberWeave.Mcp/AGENTS.md).

## Shelling out

`DocsInitCommand` invokes `apm`. Two rules held there and worth holding again: pass
arguments through `ArgumentList` and never a shell string, and treat an absent tool as a
degraded path with a printed manual command rather than a failure. **Kyber-Weave never
installs software as a side effect** — external tools are expected dependencies.

## Documented behaviour

The command surface is described in [`docs/`](../../docs/README.md), by feature. Adding or
changing a verb, a flag, or an exit-code rule means updating the matching page in the same
change.
