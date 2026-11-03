# KyberWeave.Arbiter

The `kyber-weave-arbiter` binary. The separate process harness hooks call to gate
dispatches and guard planning paths.

Read [`/AGENTS.md`](../../AGENTS.md) first for repository-wide rules.

## Never write to stdout

**stdout is the harness's decision document.** The host writes the decision there and
nothing else: one stray line corrupts it, and the harness reports a protocol error
with no useful detail.

- No `Console.WriteLine`, no `AnsiConsole`, no `Console.Out` — anywhere in this project or
  in a code path it reaches.
- Logging is pinned to stderr: `HookCommand` takes its log writer as an argument, and
  `Program.cs` passes `Console.Error`. Do not relax it.
- This is why the host is a **separate executable** rather than a `kyber-weave hook`
  subcommand: the CLI is built on Spectre.Console, which writes to stdout. Separate
  entry points make the corruption structurally impossible instead of a matter of
  discipline. Do not merge them.

## Composition root

`Composition.cs` builds the production `HookCommand`: the default
`HarnessAdapterRegistry` (command hooks plus plugin hooks), host configuration loading,
and decision ids. Tests construct `HookCommand` directly with scripted engines and
counting config loaders — that is how the fast path proves it never loads
`.kyber-weave/kyber-weave.yml`.

## Fail closed

One top-level catch in `HookCommand` turns any exception into that harness's block,
carrying `KW-ARB-HOOK-001`. On conductor dispatches the reason is an escalation
envelope with `ANSWER: error`. Malformed stdin, missing or malformed configuration on
a classified event, and an engine crash all block, never pass.
