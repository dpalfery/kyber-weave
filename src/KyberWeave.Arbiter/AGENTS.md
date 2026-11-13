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

## Fail open (ADR 0072)

One top-level catch in `HookCommand` turns any exception into an allow: it logs
`KW-ARB-HOOK-001` with the reason to stderr and writes the harness's allow document.
Malformed stdin, missing or malformed configuration on a classified event, an engine
crash and a guard fault all allow and log. Only a machine fact or a confident model
answer blocks. `RenderFailClosed` and `LastResortBlock` are no longer called by the host.

## The MCP fallback (`serve`)

`serve` is this binary's second surface: a stdio MCP server carrying the decision-gate
fallback for the harnesses that render no hooks. Two names describe it, and they are not
interchangeable.

- **Server key `kw-arbiter`.** It is the value of `McpServerNames.Arbiter` in
  `src/KyberWeave.Core/Configuration/`, never a literal in this project. Every rendered
  grant and every client configuration is built from that key, so a literal spelled here
  is how a grant stops resolving.
- **Executable and argv are unchanged.** `kyber-weave-arbiter serve --repo-root <root>`
  is what a client launches; `kw-arbiter` is what it declares the server as. A change to
  one is not a change to the other.

The three decision tools are `arbiter_evaluate_agent_dispatch_or_return`,
`arbiter_list_effective_rules_by_trigger`, and
`arbiter_check_configuration_and_key_readiness`. Two more ask the provider and answer
fail-safe: `arbiter_select_relevant_document_sections` and
`arbiter_classify_bot_review_comment`. Their names are client-visible contract —
a client registers them by exact name — so a rename has to carry the rendered grants and
the docs with it, or the fallback stops resolving silently.

The stdout rule above is the hook's. Under `serve`, stdout carries the MCP protocol and
nothing else, for the same reason: one stray line corrupts the stream.
