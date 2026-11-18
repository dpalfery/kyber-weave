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

This project only parses arguments and wires services; the decisions live in the layers.
`Composition.cs` dispatches `--version`, `hook`, `guard` and `worker-guard`, and builds a plain
`ServiceCollection` (`ArbiterContainer`) inside each path's own fault handler. It is never a Host
builder, and an event that passes through untouched builds no container at all
(`HookFastPath.IsPassThrough`). The pieces:

- `src/KyberWeave.Application/Arbiter/Hooks/`: the harness adapters, `HarnessAdapterRegistry`,
  `ReadGuard`, `ArchitectShellGuard`, `PluginEnvelope`, the guards' pure rules, `HookCommand` (the hook
  host), the decision engine, `ConductorGuard`, `WorkerGuard` and `HookGitFacts`, behind the
  `Contracts/Services` interfaces.
- `src/KyberWeave.Domain/Arbiter/Hooks/`: the outcome records and hook context the adapters and the host
  agree on.
- `src/KyberWeave.Contracts/Repositories/`: the `IFactoryTranscriptSource` port the Factory adapter reads a
  session transcript through; `Data`'s `FileFactoryTranscriptSource` implements it.
- `src/KyberWeave.Data/Hooks/Arbiter/`: the claim, snapshot and budget files, the Factory transcript reader.

Tests build `HookCommand` through `ArbiterTestServices` with scripted engines and counting config
loaders: that is how the fast path proves it never loads `.kyber-weave/kyber-weave.yml`.

## Fail open (ADR 0072)

One top-level catch in `HookCommand` turns any exception into an allow: it logs
`KW-ARB-HOOK-001` with the reason to stderr and writes the harness's allow document.
Malformed stdin, missing or malformed configuration on a classified event, an engine
crash and a guard fault all allow and log. Only a machine fact or a confident model
answer blocks.

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
- **The serve container is `ArbiterContainer.AddArbiterServe(args, log)`.** It registers the
  hook host's services plus the `ArbiterServeContext` built from this process's argv. The SDK
  constructs `ArbiterTools` from that container on each call, so every constructor parameter
  must be a registered service; `McpCompositionRootTests` constructs every tool type from the
  same container for both servers.

The three decision tools are `arbiter_evaluate_agent_dispatch_or_return`,
`arbiter_list_effective_rules_by_trigger`, and
`arbiter_check_configuration_and_key_readiness`. Two more ask the provider and answer
fail-safe: `arbiter_select_relevant_document_sections` and
`arbiter_classify_bot_review_comment`. Their names are client-visible contract —
a client registers them by exact name — so a rename has to carry the rendered grants and
the docs with it, or the fallback stops resolving silently.

The stdout rule above is the hook's. Under `serve`, stdout carries the MCP protocol and
nothing else, for the same reason: one stray line corrupts the stream.
