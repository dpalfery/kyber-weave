---
id: docgraph/mcp-runbook
title: Running the DocGraph MCP server
doc-type: runbook
status: current
component: DocGraph
source-root: src/KyberWeave.Mcp
owner: dpalfery
last-reviewed: 2026-11-01
code-refs:
  - DocsTools
  - DrainingStreamServerTransport
  - HarnessMcpCatalog
  - McpWiringInspector
  - McpWiringRepairer
---

# Running the DocGraph MCP server

`kyber-weave-mcp` serves [the documentation graph](architecture.md) to an agent over stdio
MCP. It is the delivery surface for the whole feature: everything else produces the corpus,
this hands it to a model.

## Why it is a separate binary

JSON-RPC owns stdout, and the CLI is built on Spectre.Console, which also writes there. A
separate executable makes stream corruption **structurally impossible** rather than a
matter of discipline. All logging is pinned to stderr for the same reason — one stray line
on stdout breaks the transport.

## Closing stdin

A client may write its requests and close stdin without waiting, as a scripted health check
or `kyber-weave-mcp < requests.jsonl` does. Every request the server read before stdin
closed is still answered on stdout, and the process exits once the last answer is written.
Only replies are sent after stdin closes. Notifications, and any request the server would
start itself, are dropped, because nothing can answer them any more.

The SDK's own stdio transport drops every message once stdin reaches end of stream, even
replies to requests it has already read. `DrainingStreamServerTransport` replaces it for
that reason; its remarks have the detail. Releases up to and including `v0.1.7-rc.13` ship
the SDK transport, and there piped requests produce zero bytes on stdout
([#95](https://github.com/dpalfery/kyber-weave/issues/95)).

## Wiring it up

Every client launches the same process the same way — `kyber-weave-mcp` over stdio, with
`--repo-root` pointing at the repository. Only the file it goes in and the names of the
keys differ, and they differ more than you would expect:

| Client | File | Server key | Notes |
|---|---|---|---|
| [Claude Code](#claude-code) | `.mcp.json` (repo root) | `mcpServers` | Committed servers need approval on first run |
| [Factory](#factory) | `.factory/mcp.json` (repo root) or `~/.factory/mcp.json` | `mcpServers` | Same shape as Claude Code, but a Task-dispatched droid sees only the servers its own droid file whitelists |
| [Cursor](#cursor) | `.cursor/mcp.json` | `mcpServers` | Same shape as Claude Code, and subagents inherit the session's servers rather than holding a grant of their own |
| [VS Code / Copilot in the editor](#vs-code-and-github-copilot-in-the-editor) | `.vscode/mcp.json` | `servers` | Not `.mcp.json`, and not `mcpServers` |
| [Copilot coding agent](#github-copilot-coding-agent) | Repository settings, not a file | `mcpServers` | `tools` is required; the binary must be installed first |
| [Codex CLI](#codex-cli) | `~/.codex/config.toml` | `[mcp_servers.<name>]` | TOML, and the table name is snake_case; custom agents inherit the session's servers and cannot override them |
| [opencode](#opencode) | `opencode.json` | `mcp` | Command and arguments are one array |
| [ZCode](#zcode) | `zcode.json`, or `.zcode/config.json` | `mcp.servers` | A nested key path, and the files are found by walking up to the worktree root |
| [Command Code](#command-code) | `.mcp.json` (repo root) | `mcpServers` | Same shape as Claude Code; a subagent reaches a tool only when its own `tools` list names it |
| [Kilo](#kilo) | `kilo.jsonc` | `mcp` | Per-agent reach is the agent frontmatter `permission` map entry `kyber-weave_*: allow` — an approval grant, not isolation |
| [Antigravity](#antigravity) | `.agents/mcp_config.json` (workspace) | `mcpServers` | The agent file carries its own `mcpServers` entry; MCP tools default to Ask until a policy pattern allows them |
| [Pi](#pi) | `.mcp.json` (repo root), or `~/.config/mcp/mcp.json` | — | Read by the `pi-mcp-adapter` extension (6.0.0 or later) in the adapter's own schema; a project-scoped entry needs a trust approval on first use |
| [Warp](#warp) | Warp's own MCP settings | — | Squad's Warp surface is role skills, which carry no tool scoping |

Two rows are worth reading before the rest, because they decide whether a per-agent grant exists
at all. **Cursor** and **Codex** reach MCP *structurally through the session*: a Cursor subagent
inherits every parent tool including MCP tools, and a Codex custom agent inherits the parent's MCP
servers with no way to override them. Nothing Squad renders on those two targets changes what an
agent can call, which is why neither has a per-agent grant — wire the server and every agent,
`conductor` included, already sees it.

Use an **absolute path** to the binary in any client launched from a desktop icon rather
than a shell — GUI processes inherit a minimal PATH that usually excludes `~/.local/bin`,
and the failure looks like a server that will not start rather than a missing binary.

### Claude Code

`.mcp.json` in the repository root, so the whole team gets the server from a checkout:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "type": "stdio",
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

Or write it from the CLI, which is equivalent:

```bash
claude mcp add --scope project kyber-weave -- kyber-weave-mcp --repo-root .
```

Servers from a committed `.mcp.json` are **not trusted automatically** — a checkout cannot
approve its own servers. Each user approves once on first run, and the server sits at
`⏸ Pending approval` in `/mcp` until then.

### Factory

`.factory/mcp.json` in the repository root, or `~/.factory/mcp.json` to get it in every
project. The entry is the same shape as the Claude Code row:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

**This file configures the session, and a Task-dispatched droid is not the session.** Factory
gives every droid its own `mcpServers` key in its droid file under `.factory/droids/`, and that
key — not the app-level file — decides which servers the droid may call. The two directions are
documented and opposite: an **omitted** key inherits the parent session's servers, and an
explicit `[]` excludes every one of them, unconditionally. A droid whose own list is empty is
MCP-blind however well the rest of the client is configured.

So there are two grants, made by two owners, and only one of them is your configuration file:

- **The droid-level whitelist** is written by Kyber-Squad's `FactoryRenderer`, into the droid
  file it owns. It is derived from the server names the squad itself declares in the `mcp.json`
  beside its canonical source — today exactly `kyber-weave`, launched as
  `kyber-weave-mcp --repo-root .` — and it is granted to every droid whose capability profile
  allows `filesystem.read`, the pure orchestrator `conductor` included. One of that server's tools
  is `agent_coding_standards`, and the orchestrator is asked to hold specialists to standards it
  could not otherwise read, so the orchestration reasoning that used to exclude it does not reach
  this server ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). `mcpServers: []`
  remains the fail-closed value for a droid whose profile grants no read entitlement and for a
  squad that declares no servers, and no droid ever omits the key: omission would inherit the
  session's servers, which is silent widening. The contract is stated in
  [Kyber-Squad architecture §4](../kyber-squad/architecture.md#4-deployment-scopes-and-state-topology)
  and pinned under the Factory row of the
  [capability matrix](../kyber-squad/requirements.md#target-capability-and-degradation-matrix).
- **The app-level file above** decides only whether the server the whitelist names actually
  exists. It is yours; Squad writes no Factory settings file and does not invent server names.

Both have to hold for a dispatched droid to reach the corpus. Wire the file first, then confirm
the droid names the server — and compare the provenance line rather than trusting the answer, as
with every other client: call `docs_status`, which takes no arguments, and check that its
`root=` equals `git rev-parse --show-toplevel`.

**CodeGraph, and the boundary that follows from it.** The same file can launch the CodeGraph
server:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    },
    "codegraph": {
      "command": "codegraph",
      "args": ["serve", "--mcp"]
    }
  }
}
```

Wiring it is not the same as reaching it from a droid, and this is the boundary worth stating
plainly rather than implying: a dispatched droid sees **only** the servers its rendered whitelist
names, and the squad declares only `kyber-weave`. `codegraph_explore` is therefore session-level
on Factory today — available to you in the main conversation and not claimed as droid-visible.
A role that needs it, such as `architect` or `research-agent`, gets it by dispatch from a
session that has it, not by holding the tool itself. Leave the entry in place for your own use
and do not read a green droid file as evidence that a droid can resolve symbols.

`docs drift` reads a `.codegraph/` index and reports a missing or unreadable one as critical
rather than passing, so `docs_drift` called from any client needs that index to exist. In this
repository, `codegraph init .` at the repository root is pre-authorized by the root
[`AGENTS.md`](../../AGENTS.md); record `git status --short` before and after, because
initialization can rewrite the marked CodeGraph block there.

### Cursor

`.cursor/mcp.json` in the repository, or `~/.cursor/mcp.json` to get it in every project.
The shape matches Claude Code:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

Cursor subagents inherit every tool from the parent session, including MCP tools, and a subagent
file carries no `tools` allow-list key (cursor.com/docs/subagents.md, read 2026-10-31). That is why
Squad renders no per-agent MCP grant here: the wiring above is the whole grant, and every Squad
agent — `conductor` included — already reaches the server once the file exists.

### Command Code

`.mcp.json` in the repository root, the same `mcpServers` shape as Claude Code
(commandcode.ai/docs/mcp, read 2026-10-31):

```json
{
  "mcpServers": {
    "kyber-weave": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

Command Code reaches an MCP tool only when the agent's own `tools` list names it, so configuring
the server is half the grant and the other half is the rendered list. Squad enumerates the
qualified `mcp__<server>__<tool>` names in a granted agent's `tools`, which makes an unwired server
an inert grant rather than an error: no name matches and nothing is attempted. Configuration stays
the operator's — `squad install` writes no `.mcp.json`.

### Kilo

Kilo declares MCP servers under an `mcp` key in `kilo.jsonc`
(kilo.ai/docs/automate/mcp/using-in-kilo-code, read 2026-10-31), and per-agent reach is a
`permission` map on the agent frontmatter keyed `{server}_{tool}`, with wildcards supported
(kilo.ai/docs/customize/custom-subagents, read 2026-10-31).

**What Squad emits.** For every agent entitled to `kyber-weave` — `conductor` included —
`KiloRenderer` writes one frontmatter key ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)):

```yaml
permission:
  kyber-weave_*: allow
```

An agent outside the entitlement renders no `permission` key at all. That absence is the
withholding, and it is deliberate: a `deny` would assert a decision the capability lattice cannot
derive, since it declares no MCP capability.

**An approval grant, not isolation.** Kilo's `permission` values are `ask`, `allow`, `deny`, or
null, and `allow` means the call runs without the ask prompt. It hides nothing: only a `deny` whose
pattern is `*` removes a tool from the model. The entry above is what keeps the eight `kyber-weave`
tools callable from a subagent nobody is watching, because an ask that no one answers fails
closed. It restricts nothing, and deleting it from a rendered file would not hide the tools — it
would put the prompt back.

**The hyphen belongs in the key.** Kilo sanitizes the server name only for characters outside
`[a-zA-Z0-9_-]`, so `kyber-weave` survives verbatim and the server wildcard is `kyber-weave_*`. A
`kyber_weave_*` spelling parses cleanly and matches nothing. Verified against `@kilocode/cli`
7.8.18 (`Kilo-Org/kilocode` tag `v7.8.18`) by running Kilo's own matcher:
`kyber-weave_docs_status` matches `kyber-weave_*`; `kyber_weave_docs_status` does not.

**Your half:** the server entry in `kilo.jsonc`'s `mcp` key, launched as
`kyber-weave-mcp --repo-root .`. An agent file cannot add a server and `squad install` writes no
harness settings file, so a rendered grant with no `mcp` entry reaches nothing. Then check the root
rather than assuming it — call `docs_status` (it takes no arguments) and confirm its `root=` equals
`git rev-parse --show-toplevel` in the repository you meant.

Do not hand-add a permission key to a rendered agent file: install and update own those bytes.

### Antigravity

Workspace-scoped, at `.agents/mcp_config.json` in the repository
(antigravity.google/docs/mcp, read 2026-10-31):

```json
{
  "mcpServers": {
    "kyber-weave": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

MCP tools default to **Ask** on Antigravity, so approval goes through that client's
`mcp(server/*)` policy patterns rather than through an agent file.

**What Squad emits.** For every agent entitled to `kyber-weave` — `conductor` included — the
renderer adds one array entry to the agent file under `.agents/agents/<name>/agent.md`
([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)):

```yaml
mcpServers:
  - name: kyber-weave
    command: kyber-weave-mcp
    args: [--repo-root, .]
```

`command` and `args` are read from the squad's own `mcp.json`, so the rendered line is the line
your MCP configuration launches. A squad that declares no such server, or declares it malformed,
yields no entry rather than a guessed one. An agent whose profile is outside the entitlement also
gets no entry, and its degradation record states that the server was withheld, for which profile,
and which capability the entitlement rests on.

**The entry shape is the probed one.** `{name, command, args}` was called successfully against
`agy` 1.4.12, the live-validation discipline
[ADR 0022](../adr/0022-antigravity-native-agents.md) established for every Antigravity capability.
The single-key map spelling (`- kyber-weave: {command, args}`) listed and ran but was never
actually invoked, so it is not emitted.

**Three moves not to make, and why the shape must match exactly.** An MCP tool name must never be
added to `tools:` — Antigravity
resolves that list against its executor registry and fails fast on an unmapped name
(`unknown component: tool "..." not found in registry`, surfaced as `AGY_ERROR` on stderr while the
process still exits 0, so a caller reading only the exit code sees success). `enable_mcp_tools:
false` does not block a server declared in frontmatter, so no such key is emitted to switch one on.
Unknown frontmatter keys are ignored, which is why the entry has to be the probed shape rather than
merely a plausible one. `--repo-root .` is workspace-relative on purpose: a probe with an absolute
path returned the global server's root, so a same-named global definition can take precedence over
the per-agent entry — keep the workspace file pointing at the same root.

**Your half, beyond the agents:** the workspace `.agents/mcp_config.json` above has to name the
server, and that client's own policy patterns decide whether a call is approved. Then compare the
provenance rather than trusting the answer: call `docs_status` and check that its `root=` equals
`git rev-parse --show-toplevel`.

### Pi

Pi reaches MCP through the `pi-mcp-adapter` extension, which reads its servers from a project
`.mcp.json`, a user-global `~/.config/mcp/mcp.json` or `~/.agents/mcp.json`, or Pi's own
agent-directory `mcp.json`. A project-scoped server needs a trust approval the first time the
project uses it. Declare `kyber-weave` there launched as `kyber-weave-mcp --repo-root .`; as on
every other client, `squad install` writes none of those files, and the schema of the entry is the
adapter's own — read the package's documentation for the key rather than copying a rendered agent
file, which carries no server definition at all.

**What Squad emits.** Every entitled agent file gets a derived overlay in its `.pi/agents/<name>.md`
frontmatter, merged with whatever the agent declares itself
([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). `extensions` gains
`pi-mcp-adapter`, and `tools` gains two kinds of entry: the proxy tool
`ext:pi-mcp-adapter/mcp`, then one entry per tool the roster declares for the server, in ordinal
order:

```text
ext:pi-mcp-adapter/mcp
ext:pi-mcp-adapter/mcp__kyber_weave__agent_coding_standards
ext:pi-mcp-adapter/mcp__kyber_weave__docs_analysis_candidates
ext:pi-mcp-adapter/mcp__kyber_weave__docs_drift
ext:pi-mcp-adapter/mcp__kyber_weave__docs_explore
ext:pi-mcp-adapter/mcp__kyber_weave__docs_for_symbol
ext:pi-mcp-adapter/mcp__kyber_weave__docs_glossary
ext:pi-mcp-adapter/mcp__kyber_weave__docs_status
ext:pi-mcp-adapter/mcp__kyber_weave__docs_validate
```

The emitted `tools` value is one CSV line: the capability-derived built-in names first, then the
agent's own declared `pi-tools`, then the derived entries above. An agent that already declares one
of them renders it once, and `research-agent` keeps its hand-authored `context7` pair beside the
derived `kyber-weave` entries.

**The namespace maps the hyphen to an underscore.** The adapter names a tool
`mcp__<namespace>__<tool>` and maps every character outside `[A-Za-z0-9_]` to `_` in the server
namespace first, so `kyber-weave` is reached as `kyber_weave`. An entry spelled
`mcp__kyber-weave__<tool>` names no registered Pi tool. This differs from Claude, ZCode, Devin, and
Command Code on purpose: those pass the server name through verbatim, and Pi does not.

**Version floor: `pi-mcp-adapter` 6.0.0, current release 7.0.0.** The deferred-tool surface this
naming belongs to arrived in 6.0.0, whose changelog first records tools becoming a deferred tool
named `mcp__<server>__<tool>`; 7.0.0 is the supported target. On 5.x there is no per-tool deferred
registration at all — the tools would be named `kyber-weave_agent_coding_standards` — so the proxy
entry `ext:pi-mcp-adapter/mcp` still works and only the enumerable per-tool names resolve to
nothing. Upgrade the adapter rather than editing the names.

**The lowered primary agent is the one exception.** `conductor` on Pi is a skill at
`.pi/skills/conductor/SKILL.md`, and skill frontmatter carries no `tools` or `extensions` key, so
the overlay is structurally inexpressible there. Its degradation record names `kyber-weave` and
says the entry point reaches the server only through the session it runs in — the same reading as
Devin's lowered conductor. Every entitled subagent file carries the full overlay.

**What remains an owner probe** is the in-session listing: that a live session shows these names
and answers a call. The spelling is not a probe — it was established by executing the adapter's
published naming code over the roster and corroborated by a live MCP `tools/list`, where the
adapter's own `doctor` reported `kyber-weave: ok, 8 tools`. Then compare provenance as everywhere
else: call `docs_status`, whose `root=` must equal `git rev-parse --show-toplevel`.

### Warp

Warp supports MCP (docs.warp.dev/agents/capabilities/mcp, read 2026-10-31), and the server is
configured in Warp itself. There is no per-agent grant to make here: Squad's Warp surface is role
skills, and a skill carries no tool scoping, so every deployed role — `conductor`'s lowered skill
included — runs in a session whose MCP you configure. The file and key are Warp's own; Squad
writes neither, and no per-agent claim is made.

### VS Code and GitHub Copilot in the editor

Copilot's agent mode reads `.vscode/mcp.json`. Two things differ from every other JSON
client here, and both fail silently as "no tools appeared": the file is **not** `.mcp.json`
at the root, and the key is **`servers`**, not `mcpServers`.

```json
{
  "servers": {
    "kyber-weave": {
      "type": "stdio",
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "${workspaceFolder}"],
      "env": {
        "KYBER_WEAVE_REPO_ROOT": "${workspaceFolder}"
      }
    }
  }
}
```

`${workspaceFolder}` is worth using here rather than `.`: VS Code does not guarantee the
server's working directory, and in a multi-root workspace `.` is genuinely ambiguous. The
`env` entry is belt and braces — either mechanism alone resolves the root.

### GitHub Copilot coding agent

Configured in the repository's settings under **Copilot → coding agent → MCP
configuration**, not in a file in the tree. `type` and `tools` are both required, and
`tools` must name the tools to expose or use `["*"]`:

```json
{
  "mcpServers": {
    "kyber-weave": {
      "type": "local",
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."],
      "tools": ["*"]
    }
  }
}
```

The coding agent runs in an **ephemeral container that has never heard of Kyber-Weave**, so
this configuration alone starts nothing. Install the binary in
`.github/workflows/copilot-setup-steps.yml`, which runs before the agent starts:

```yaml
name: Copilot setup steps
on: workflow_dispatch
jobs:
  copilot-setup-steps:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v5
      - name: Install Kyber-Weave
        run: |
          curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
          echo "$HOME/.local/bin" >> "$GITHUB_PATH"
```

`--repo-root .` is safe in this one case because the agent's working directory is the
checkout. Everywhere else, prefer an explicit path.

### Codex CLI

TOML, not JSON, in `~/.codex/config.toml` — or `.codex/config.toml` in a trusted project.
Note the underscore: the table is `mcp_servers`, not `mcpServers`.

```toml
[mcp_servers.kyber-weave]
command = "kyber-weave-mcp"
args = ["--repo-root", "/path/to/your/repo"]
startup_timeout_sec = 10
```

Or from the CLI:

```bash
codex mcp add kyber-weave -- kyber-weave-mcp --repo-root /path/to/your/repo
```

Codex has no workspace-variable substitution, so an absolute path is the only reliable
option in the user-level config. Verify with `codex mcp list`.

### opencode

`opencode.json` in the repository, or `~/.config/opencode/opencode.json` for all projects.
The distinguishing quirk is that **`command` is a single array** holding the executable and
its arguments together, rather than the `command` / `args` split every other client uses —
and the environment key is `environment`, not `env`:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "kyber-weave": {
      "type": "local",
      "command": ["kyber-weave-mcp", "--repo-root", "."],
      "enabled": true,
      "environment": {
        "KYBER_WEAVE_REPO_ROOT": "."
      }
    }
  }
}
```

### ZCode

ZCode reads its project-scope servers from **two** files, tried in this order: `zcode.json`,
then `.zcode/config.json`. The files are resolved by walking from the working directory up
to the git worktree root, so the file can sit at the worktree root rather than at the
directory the process started in; with no `.git` above it, the starting directory is the
only one that counts. The key path is nested, and it is the one thing here that is not
shaped like any other client's:

```text
mcp.servers.<server-name>
```

The tier paths, the `.zcode/` marker, and the `mcp.servers` key path are pinned against
`ZCodeMcpConfiguration`, verified against zai-org/ZCode 3.14.0 on 2026-09-21. **The body of
an entry inside `mcp.servers` is not pinned anywhere in this repository**, so no entry is
printed here and none is written: an invented body produces a configuration ZCode cannot
load, which is worse than a step that is visibly the operator's. Read the body's keys from
ZCode's own `mcpSchema` — the source the 2026-09-21 verification used — and declare the
server named `kyber-weave` there, launched as `kyber-weave-mcp --repo-root .`.

The user tier (`<storage>/cli/config.json`) is **user scope**: the check never reads it and
the repair never writes it. A server wired only there is reported as not wired for the
repository, which is the right report for a configuration the team shares.

**Nothing writes this harness yet.** `docs mcp-wire --target zcode` refuses with
`KW-MCP-WIRE-005`, names the file and the reason, and prints the place the entry goes. The
declared widening path is one verified fact: pin the entry body against `mcpSchema`, add it
to the catalog row, and flip that row's writability — a catalog edit, not a structural
change ([ADR 0051](../adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md)).

### From source

Any of the above works against a source build by swapping the command:

```bash
dotnet run --project src/KyberWeave.Mcp -- --repo-root .
```

In a client config that means `"command": "dotnet"` with the rest as arguments. Build once
with `-c Release` first — a client that times out during startup is usually waiting on a
first-run compile.

### Resolving the repository root

The project-local configuration emitted by the Squad package uses `--repo-root .`. That
binding is resolved against the client's working directory, which must be the directory
initialized by `kyber-weave docs init` (the command defaults to the current directory). For
global or headless clients, use an explicit absolute path or the generic
`KYBER_WEAVE_REPO_ROOT` setting. The root is resolved in this order:

1. `--repo-root <path>`
2. the `KYBER_WEAVE_REPO_ROOT` environment variable
3. the working directory itself, only when it contains a Kyber-Weave host configuration

The server never walks up to a parent Git repository and never serves an unbound default
corpus. If the selected directory was not initialized, startup fails on stderr with an
actionable `kyber-weave docs init "<path>"` command. This prevents two repositories under a
shared parent directory from accidentally reading one another's documentation.

### Asserting the root with `--expect-root`

Resolution decides which root the server binds to; `--expect-root` asserts which root the
client believes that is. After the ordinary resolution above, the server compares the two
and refuses to serve when they differ. It is a client-asserted safety rail against the
reported failure mode — a same-named server pinned to another checkout by a user-scope or
headless harness config — not a replacement for correct project-local wiring.

```bash
kyber-weave-mcp --repo-root . --expect-root /Users/you/git/your/repo
```

The value may also come from the `KYBER_WEAVE_EXPECT_ROOT` environment variable, honoured
only when the flag is absent: **the CLI flag wins over the environment**, so a shell export
cannot override a project pin. A relative value, and a value from the environment, resolve
against the same working directory `--repo-root .` uses.

A mismatch from either source is a refuse-to-serve: the server writes `KW-MCP-ROOT-002: …`
to **stderr** and exits 1. That is distinct from the `KW-MCP-ROOT-001` an unbound or
uninitialized root produces. Nothing is served either way, so no answer can come from the
wrong corpus.

### The ontology comes from the host config

Once the root is resolved, the server reads `.kyber-weave/kyber-weave.yml` from it and
serves the same corpus [`docs validate`](governance.md) does — every `docs-root`, and the
catalog wherever [config](../configuration.md) puts it. Run `kyber-weave docs init .` first
when adopting a repository without a host configuration.

A config that cannot be read is reported on **stderr** as `KW-CONFIG-001` and the server
keeps running on defaults. That combination — a corpus that looks empty and a line on
stderr the client may not surface — is worth checking before blaming the repo root.
This fallback applies to the retrieval corpus initialized at startup. The analysis and
glossary tools reload the current config for each call and return an unavailable response
until the invalid config is fixed; they do not analyze on defaults.

## Checking and wiring

The recipes above are written by hand, one file and one key path at a time, and nothing
above notices when one of them is missing. That gap is what the check and the two repair
commands close: the repository declares which servers its agent instructions rely on,
`docs init` reports where the declaration is not honoured, and a named repair writes it.

### The declaration

The declaration is the `mcp:` section of `.kyber-weave/kyber-weave.yml` — the same host
configuration every other setting lives in, and the full schema is in
[configuration](../configuration.md#mcp-servers):

```yaml
mcp:
  required-servers:
    kyber-weave:
      command: [kyber-weave-mcp, --repo-root, .]
      provenance: first-party
      level: required
```

`command` is argv — the program first, then its arguments — never a shell line, for the
same reason `review.gates` takes argv. `provenance` is `first-party` (a binary Kyber-Weave
ships, today `kyber-weave-mcp`) or `third-party` (a binary you install, such as `codegraph`),
and it decides which fix an absent binary gets. `level` is `required` (the default when the
key is absent) or `recommended`, and it decides whether a gap is a warning or an `info`
line. An optional `install` carries the exact manual command to print when a third-party
binary is absent; Kyber-Weave prints it and never runs it.

`docs init` scaffolds the section with exactly one server when it creates a host
configuration, and appends it — leaving every other byte alone — when an existing file has
none. Once the section exists it is yours: no run of `docs init` ever rewrites it.

The declaration is independent of Squad. It is not read from `toolchain.yml`'s
`required-mcp-tools` roster or from `products/kyber-squad/mcp.json`, and the check and the
repair read neither.

### The check

`docs init` checks the wiring on every successful scaffold, after the skill deploy and the
retired-skill warnings and before its `Next:` block, and prints one unfolded section:

```text
MCP wiring:
  ok    factory: kyber-weave wired
  warn  KW-MCP-WIRE-001 cursor: server 'kyber-weave' is not wired (.cursor/mcp.json)
        Fix: kyber-weave docs mcp-wire --target cursor
  warn  KW-MCP-WIRE-003 factory: server 'kyber-weave' is wired but 'kyber-weave-mcp' is not on PATH
        This Kyber-Weave installation is broken; run: kyber-weave update
  info  KW-MCP-WIRE-004 claude-code: not checked (no '.mcp.json' and no marker to detect it)
```

What it reports:

| Id | Level | Raised when |
|---|---|---|
| `KW-MCP-WIRE-001` | warning for a `required` server, `info` for a `recommended` one | a declared server is not wired for a harness in play, or the file could not be read |
| `KW-MCP-WIRE-002` | same as above | the entry is wired but its launch command differs from the declaration; the line names both |
| `KW-MCP-WIRE-003` | same as above | the entry is wired — or would be — but the launch binary does not resolve on `PATH` |
| `KW-MCP-WIRE-004` | `info`, always | the harness is not in play, has no marker to detect it, or keeps its configuration in repository settings |

A harness is "in play" when its repository-scope configuration file or its marker directory
exists — `.factory/`, `.cursor/`, `.vscode/`, `.codex/`, `.zcode/`, or the configuration file
itself where no marker exists. Detection is a heuristic and the output claims no more than
that: a harness with neither signal gets one `info` line naming what was looked for, never a
warning. Claude Code has no marker besides `.mcp.json`, so a repository with neither is
informed rather than failed.

The check writes nothing, reads **repository scope only** — no file under the home directory
is opened, for any harness — and does not change `docs init`'s exit code: a scaffold that
succeeded exits 0 with findings, exactly as the APM-missing and retired-skill paths do. A
declaration the configuration loader rejects is the one exception: it fails the run through
the existing `KW-CONFIG-001` path, before the check runs. A repository that declares no
servers prints no section at all.

`--no-mcp-check` skips the check, the prompt and any repair, and prints one line saying so.

### The prompt, and what happens without a terminal

When the check found at least one fixable gap, `docs init` offers a yes/no question naming
exactly which files and which servers the repair would write. It defaults to **no**.
Confirming performs the repair through the same write path `docs mcp-wire` uses and reports,
per file, what was written, then the receipt's location. Declining writes nothing — the
findings and their printed commands stand, so the same repair is one command away.

**When stdin is not an interactive terminal — redirected, piped, no TTY, CI — the command
never prompts, never writes, and the output ends with the printed manual repair command.**
`docs init` has no consent flag and acquires none: a non-interactive run that wants the
repair runs `docs mcp-wire`, whose invocation is the consent.

### `docs mcp-wire`

```bash
kyber-weave docs mcp-wire [path] [--target <targets>] [--dry-run]
```

`path` defaults to `.`. `--target` is a comma list of harness slugs.

- **No `--target`**: a preview. It lists the harnesses in play and the gaps found, names each
  writable harness no marker can detect in this tree — naming one is the only way to write
  it — and writes nothing. Exits 0.
- **A named target is written whether or not its harness is in play.** The selection is the
  consent, and for Claude Code and opencode, whose only repository-scope artefact is the
  configuration file, "not in play" and "file absent" are the same condition: the canonical
  file is created.
- **A named target that already holds every declared server** is reported and skipped at
  exit 0, not treated as an error — otherwise a second run would fail on the first one's
  success.
- **A refused target** exits 1, names the file and the reason, and writes nothing to that
  file: a slug the catalog does not know, a malformed or foreign document, an entry with the
  same server name already present, a harness the repair does not write, an absent binary.
  Other named targets still proceed.
- `--dry-run` prints the exact per-file change, writes nothing — not even the receipt — and
  exits what the real run would have exited.

### `docs mcp-unwire`

```bash
kyber-weave docs mcp-unwire [path] [--target <targets>]
```

Removal works from the receipt, not from the declaration, so a repository whose `mcp:`
section is malformed can still be unwound. An `inserted` file is restored to the bytes the
repair found; a `created` file is deleted. With no `--target` it lists the files the receipt
records and removes nothing. It exits 0 when every named file was restored or deleted, and 1
on any refusal: a slug the catalog does not know, no receipt record, a file that is gone, or
a current digest that differs from the recorded one.

That last case is the point of the verb. **A file that changed after the repair is drift, and
drift is refused** (`KW-MCP-WIRE-006`) rather than restored over someone else's edit.

### The receipt

Every write is recorded in `.kyber-weave/receipts/mcp-wiring.json` — schema version 1, one
record per file, latest write wins — holding the file's path, the mode (`created` or
`inserted`), the servers written, the pre-write and post-write SHA-256 digests, a UTC
timestamp, and, for an inserted file, the pre-write bytes it captured — a digest proves a
restore is safe, but it cannot produce the bytes to restore. `docs init` adds `receipts/` to
`.kyber-weave/.gitignore` beside `cache/`.

It is the writer's local undo record and nobody else's. Teammates receive the committed
configuration file and never need the receipt; their removal path is `git revert`.

### What the repair writes, and what it will not

| Harness | Target slug | Repository-scope file | Server key | Repair |
|---|---|---|---|---|
| Factory | `factory` | `.factory/mcp.json` | `mcpServers` | Writes |
| Claude Code | `claude-code` | `.mcp.json` | `mcpServers` | Writes |
| Cursor | `cursor` | `.cursor/mcp.json` | `mcpServers` | Writes |
| VS Code | `vscode` | `.vscode/mcp.json` | `servers` | Writes |
| opencode | `opencode` | `opencode.json` | `mcp` | Writes |
| Codex CLI | `codex` | `.codex/config.toml` | `mcp_servers` | Snippet only |
| ZCode | `zcode` | `zcode.json`, `.zcode/config.json` | `mcp.servers` | Snippet only |
| Copilot coding agent | `copilot-coding-agent` | none — repository settings | `mcpServers` | Snippet only |

The three snippet-only rows are checked and never written, and each says why:

- **Codex** is refused because its repository-scope relative-path semantics are unverified:
  the runbook's verified form is an absolute path, which cannot go into a committed file
  without breaking every other teammate. The unblocking source is Codex's own documentation
  or source for how a project-scope `--repo-root .` resolves; verifying it adds the form
  here and flips that catalog row's writability.
- **ZCode** is refused because the entry body inside `mcp.servers` is pinned nowhere in this
  repository — see [ZCode](#zcode). The unblocking source is ZCode's `mcpSchema`.
- **The Copilot coding agent** has no file to write: its configuration is repository
  settings, and the binary has to be installed by the workflow step in
  `.github/workflows/copilot-setup-steps.yml` first. See [GitHub Copilot coding
  agent](#github-copilot-coding-agent).

Widening any of the three is a pinned fact plus a catalog edit, never a change to the write
bounds ([ADR 0051](../adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md)).

The five writable rows all follow one policy, and each bound is a rule rather than a
convenience:

- **Insertion-only, textually.** A harness document is never parsed into a model and written
  back out. The splice is computed in full before the first byte moves, so a refusal writes
  nothing and a partial write cannot happen.
- **Bytes the repair did not write are not touched.** Comments, key order, formatting and a
  trailing comma survive byte-for-byte; the repair never reformats, reorders, or
  re-serializes.
- **An existing entry is never modified.** A file already holding an entry with the declared
  server's name is refused for that file, with the difference reported.
- **An absent binary is never installed and never written.** A third-party server whose
  binary does not resolve on `PATH` is refused for every harness; a missing
  `kyber-weave-mcp` is reported as a broken Kyber-Weave installation, to be repaired with
  `kyber-weave update`.
- **Repository scope only.** `~/.factory/mcp.json`, `~/.cursor/mcp.json`,
  `~/.codex/config.toml`, `~/.config/opencode/opencode.json` and ZCode's user storage are
  never written and never read; they are reported with the manual snippet.
- **The root follows the harness's own substitution rule.** VS Code gets
  `${workspaceFolder}`; Factory, Claude Code, Cursor and opencode get `.`, the
  workspace-relative form each documents. Codex substitutes no workspace variable and its
  verified form is an absolute path, which is one more reason its entry is the operator's to
  paste and not the repair's to write.

Five more clients above have recipes here and no catalog row — Command Code, Kilo,
Antigravity, Pi, and Warp. Nothing checks or writes them; the recipe is the whole grant.

## Provenance on every answer

Every docs tool response — hits, misses, and empty or error prose alike — leads with
exactly one provenance line:

```text
provenance: root=/absolute/repo/root rev=1a2b3c4 dirty=no documents=72
```

- `root` — the absolute repository root the server is bound to, normalised with
  `Path.GetFullPath`.
- `rev` — the short HEAD SHA, or the literal `unavailable` when Git cannot be read.
- `dirty` — `yes` or `no` when `git status` succeeds, otherwise `unknown`. Untracked files
  are ignored.
- `documents` — the live document count for the corpus; `0` is allowed.

Git identity is read lazily and cached until the corpus is rebuilt, so the header does not
shell out on every call. A missing Git, a directory that is not a repository, or a failed
command are all success paths: the line still renders with `rev=unavailable` and
`dirty=unknown` rather than inventing a SHA or omitting the root.

The line is emitted whole and any character budget is taken from the body, so the one tell
that a same-named server answered from another checkout is never the part truncated away.
Compare `root=` to `git rev-parse --show-toplevel` before trusting an answer: a mismatch
means the server is bound to a different repository, not this checkout.

## The tools

All eight are declared `ReadOnly` and `OpenWorld = false`, which the SDK maps to the
protocol's `readOnlyHint` and `openWorldHint`. A client can act on an annotation; it cannot
act on a sentence claiming the same thing, which is why that claim is no longer prose.

### How the descriptions are written

Every description is **routing metadata plus a calling contract**, not documentation. A
capability clause states what comes back, the trigger clause — the longest part — names the
conditions that should fire it, and the response semantics say what an empty result means.

Two rules are worth stating because breaking them is subtle and the damage shows up far
away:

**A description claims no authority over other tools.** `Call this instead of Grep` is a
global directive arriving through a routing hint, competing with the harness's own
tool-selection rules. It is replaced by the condition that wins the same decision on
merit — superseded documents are outside the corpus, so a match is current guidance.

**Exclusions are properties, never noun lists.** `Do not use for source code, tests, CI
logs` puts those nouns in permanently resident text held back only by a negation, and
negation is the first thing to degrade when a model scans many tools at once. Read out of
context it becomes a prohibition on reading tests. Only `docs_explore` carries an
exclusion, because it is the broadest of the eight; the narrower seven state their territory
positively so no request falls between them.

Where a constraint can live on a parameter it goes there instead — for example how
`charBudget` caps **`agent_coding_standards`** prose sits on that parameter, because a
property description binds at the point of use. `docs_explore` has no budget parameters.

### `docs_explore(query, cursor = null)`

One answer for a question, symbol, route, component, or document id — not a ranked list of
documents. After the relevance floor, leaves inside qualifying documents compete; the
winning **leaf** (preamble, lead-in before a `##` section's first `###`, or a `###`) is
returned whole up to a **24,000-character** ceiling. Past that, the reply is marked
incomplete and the optional **`cursor`** continues the **same** leaf on the next call with
the same question.

The response reads, in order:

1. the provenance line (always whole)
2. `confidence: 0.00`–`1.00` — `tanh((winning topic − best other topic) / 2)`, halved when
   incomplete; stop at **0.60 or higher** when the answer is not incomplete
3. a short reading rule (stop, continue with `cursor`, or ask a narrower question)
4. when incomplete, one line naming the cursor token to pass on the next call
5. `source: {path} — {## heading} › {### heading}`, or `source: {path}` for the preamble
6. the leaf prose

There is no frontmatter block, no skipped-heading list, and no code-join list on this
tool. Index sections (plan inventories, the ADR inventory, the todo inventory, the
policy-line ledger, the reference inventory) are not answer text — a matching inventory row
is answered from the document it links to.

Optional frontmatter **`lead`** and **`sections`** on governed documents declare section
claims; see [the documentation ontology](../documentation-ontology.md) and [ADR
0046](../adr/0046-docs-explore-section-answer.md).

A miss says so plainly, reports how many documents were considered, and suggests naming a
component, doc-id, or symbol instead. Treat that as permission to fall back to grep.

### `agent_coding_standards(technology, topic = null, charBudget = 12000)`

The local answer for one stack: which standard this repository writes that technology by,
and what it says. A portable agent arrives holding the standard its own stack ships; this
returns *this* repository's, which is why it is a lookup by declared key rather than a
ranked guess at whichever standard is worded most like the question.

`technology` is required and accepts either the slug (`csharp`, `test`) or the
configuration registry's property name for it (`csharp-coding-standard`,
`<csharp-coding-standard>`), matched case-insensitively. Resolution trims the value, strips
one surrounding `<>` pair and the literal suffix `-coding-standard`, then matches corpus
documents whose `doc-type` is `coding-standard` by their declared `technology`. **The
corpus decides, not the registry**: declaring a technology in `ontology.technologies`
without a standard scaffolded at `docs/standards/<technology>/README.md` is still a miss.
Only a *pair* of angle brackets is the registry's prose form — a half-typed `"<csharp"` is
not quietly repaired into a hit.

`topic` is what the caller is about to do (`naming`, `error handling`, `pull request
review`). Omitted, the whole standard comes back in document order under budget. Provided,
the sections that answer it are chosen by the same rarity-weighted section ranking
[`Excerpt`](retrieval.md#excerpt-budgeting-agent_coding_standards) uses inside one standard.
A topic no section clears returns no prose — it does not fall back to the whole standard.

`charBudget` is total characters of prose across the standards returned, clamped to
1000–120000 and defaulting to 12000. One technology can be answered by more than one
standard — a README and an appendix beside it — and they share that one budget, ordered by
path so the same request answers the same way twice.

The provenance line is emitted whole and the budget applies to the body, so what did not fit
is named instead of silently dropped: `[omitted for space, ask again with a larger
charBudget: …]` when the budget was what bit, and `[also in this document, not matching this
topic: …]` when the section was dropped for irrelevance and no larger budget will bring it
back.

The response reads, in order:

1. the provenance line
2. a count line naming the technology asked for and the status that answered — `1 standard
   for technology 'csharp' (status CURRENT).`
3. a `###` heading with the standard's title, then the identity block — `path:`, `id:`,
   `doc-type:`, `status:`, `component:` — and a `technology:` line echoing the corpus value
   verbatim, not the caller's spelling. The declared value is what a later lookup is defined
   against, so echoing the request would hide a standard that answers under a different
   name. A standard declares no `component` by ontology design, so that field reads
   `component: (none)`. The `doc-type:` is the ontology's own spelling rather than the enum
   member's name, so a coding standard reads `doc-type: CODING-STANDARD` — the value a
   reader can look up, not `CODINGSTANDARD`, which no document or rule id uses.
4. the rule-id index
5. the chosen sections, with the `[complete document]` marker when nothing was omitted
6. nothing else — a coding standard declares no `code-refs` by ontology design, so the
   response never carries code joins.

**The rule-id index is emitted ahead of the prose.** It lists the `KW-*` ids the standard
states, deduped: the ids its section headings and bodies cite, in document order — the order
the standard itself reads — then any id its frontmatter title states that no section repeats.
An id stated in both appears once, at the position its section gave it. The title's ids come
last rather than first, though the title precedes the body in the file, because the 50-id
window below is a window over the body: a title id placed at the front of it would let
frontmatter displace a section id from what the caller actually reads. The ids are what a
finding cites, a suppression names, and the repository's own documentation indexes, and
clipping the prose is recoverable by asking again with a larger budget while losing the ids
is not, so the index is placed where a budget can never reach it. A standard that states no
ids gets an honest empty line — `rule ids: (this standard states no rule ids)` — rather than
an absent one, which would read as ids the tool failed to find. Both standards in this
repository state no ids today.

The index is capped at 50 ids. A standard that states more names the first 50 and then
reports how many more it states; an id past the cap is replaced with `[rule id withheld]`
everywhere the response echoes the document's own text — the headings and bodies of the
returned sections, the title echoed in the identity block, and the headings the omission
markers name, whichever of the two markers carries them. The same replacement, never a
deletion, so a reader sees that an id was cited rather than that a sentence lost a word. The
identity block's `path:`, `id:` and `technology:` lines print as they stand, because each is
a key the caller reuses in a later call and none can hold a rule id. So the cap the response
states is a cap it keeps, and an omitted id cannot leak further down and contradict the count.

**A standard whose status is not `current` still answers**, carrying a warning line above
its identity block that names the status. A caller about to follow a draft has to know it is
not the settled answer, and a status field two lines down is the part of a response a reader
skims past.

Four replies are not a standard at all:

- A **miss** — a technology no standard answers for — says so, names the technologies this
  repository does declare, and reports how many documents were considered. That is a real
  miss: the answer for that technology is not written down here, not a lookup that failed
  quietly.
- An **empty or whitespace `technology`** is a caller error rather than a lookup, and returns
  usage prose naming both accepted key forms.
- A standard whose body carries **no `##` headings** is reported as such in place of the
  prose, below the identity block that names its path, rather than handed back as an identity
  block followed by nothing: `no sections: this standard's body carries no ## headings, so
  there is nothing to excerpt from it.`
- A standard that **has** headings but none of them matching the caller's `topic` is the other
  case, and must not be read as the one above. It reports
  `no section matches the topic '<topic>'` and then names the headings the standard does carry,
  with the relevance marker a partially returned excerpt uses —
  `[also in this document, not matching this topic: <headings>]`. The body does carry sections,
  so this reply never claims otherwise, and no prose comes back: the topic selected nothing, and
  a larger `charBudget` cannot fit a section the topic did not select.

### `docs_for_symbol(symbol)`

The documents whose `code-refs` **formally claim** a symbol — not those that mention it in
prose, which is exactly the distinction grep cannot make. Run it before renaming, moving,
or deleting anything to find the documentation that must change with it.

An empty result distinguishes the two cases the caller cares about: the symbol is
undocumented, or it is discussed in prose that never claimed ownership of it.

### `docs_analysis_candidates(kind?, cursor?, limit = 20, charBudget = 12000)`

Runs the repository's current configured documentation analysis and returns candidates in
stable kind, term, and candidate-id order. `kind` is optional and accepts `duplicate`,
`conflict`, or `terminology`. Pass the returned candidate id as `cursor` to continue after
that item.

The tool is conversational rather than a bulk export: the hard candidate limit is 20,
the hard response budget is 12,000 characters, and evidence per candidate is capped. The
response keeps local cost/cache metrics and line-addressable evidence inside the same
budget. Use [`docs review export`](analysis.md#cli-and-exit-behavior) when a reviewing
agent needs the versioned rubric and hashes for reusable verdicts.

### `docs_glossary(term)`

Looks up one managed glossary term case-insensitively and returns its proposed, approved,
and rejected senses, definitions, scopes, and aliases. An unknown term is an ordinary
empty result, not an error. Output is capped at 20 senses and 12,000 characters.

Neither analysis tool accepts a write parameter, so neither can import a
verdict or change the glossary. Reusable decisions enter through `docs review import`, and
glossary proposals enter through `docs glossary --write` at the CLI. The MCP reader reloads
the current config and corpus for each call; embeddings remain loopback-only and are not
called unless the local cache is safely ignored.

### `docs_validate(mergeReady = false, base = "origin/HEAD", charBudget = 12000)`

Runs the same schema conformance gate as [`kyber-weave docs validate`](governance.md) against
the corpus the server already bound at startup. There is **no repository-path argument**: the
root is `DocumentIndexHost.RepositoryRoot` for this process, disclosed by the provenance line
on every response. Compare that `root=` to `git rev-parse --show-toplevel` before trusting a
pass or fail.

`mergeReady` maps to the CLI's `--merge-ready`. Default `false` is the **planning gate** —
the check an architect runs while a plan still sits in its active folder. With
`mergeReady: false`, open-work rules such as `KW-DOC-LIFECYCLE-003` do not run; with
`mergeReady: true`, they do, so a branch that still carries an active plan fails until the
plan is archived. Pull-request CI passes `mergeReady: true`; in-progress plan work on a branch
must leave it `false`.

`base` is consulted only when `mergeReady` is `true`. It matches the CLI default
(`origin/HEAD`) and selects archive-closeout scope the same way [`docs validate
--merge-ready`](governance.md) does.

`charBudget` caps **finding text** in the body (default 12000, clamped 1000–120000). The
provenance line and verdict line are always emitted whole and never count toward the budget.

The response reads, in order:

1. the provenance line
2. `result: pass` or `result: fail` with error and warning counts
3. findings — errors and criticals before warnings, each with rule id, severity, path, line,
   message, and hint when the gate supplies one

The verdict line is never truncated. When the budget omits later findings, the body ends with
an omitted count so the caller knows how many more exist.

The document walk reads the tree on disk, not `git ls-files`, so an untracked plan file under
the active plans folder is visible to this gate the same way it is to the CLI.

### `docs_drift(charBudget = 12000)`

Runs the same entity-drift gate as [`kyber-weave docs drift`](governance.md) against the
server's resolved repository root — again with no path argument and the same provenance
contract as [`docs_validate`](#docs_validatemergeready--false-base--originhead-charbudget--12000).

`charBudget` uses the same default, clamp, and truncation rules as `docs_validate`. Response
order matches: provenance, `result: pass` or `result: fail` with counts, then findings with
errors and criticals before warnings.

Warnings such as `KW-DOC-DRIFT-003` do **not** fail the gate; they still appear in the
findings when present. A missing or unreadable CodeGraph index is reported as critical
`KW-DOC-DRIFT-001` with the existing "Drift cannot be verified" message — not as `result:
pass`.

### `docs_status()`

Reports the provenance of the corpus this server is answering from, with no query and no
arguments. This is the cheap session-start check: call it once before trusting any other
docs tool, and compare its `root=` to `git rev-parse --show-toplevel`. It returns exactly
the provenance line above — nothing else — so an empty corpus is not a failure: the line
still reports the root, a count of `0`, and unavailable revision markers when Git cannot be
read.

## Reading a code join

```
code joins:
  DocumentIndex -> src/KyberWeave.Core/Docs/Search/DocumentIndex.cs:23 [class]
  Authority -> src/KyberWeave.Core/Docs/Search/DocumentIndex.cs:357 [method] (+2 other same-named)
```

Two annotations mean the resolver had to guess and you should verify:

- `(+N other same-named)` — several symbols share the name; a declaration was preferred
- `(outside this document's source-root)` — the match came from elsewhere in the repository

## Staleness

The index reloads on demand. Document mtimes and file count drive a corpus rebuild;
`.codegraph/codegraph.db` mtime and length drive a joins-only rebuild. No restart is needed
after editing documentation or after the CodeGraph daemon rewrites its index.

## Troubleshooting

| Symptom | Cause |
|---|---|
| The server never starts | `kyber-weave-mcp` is not on the PATH the client inherits — use an absolute path |
| The client lists no tools at all | Wrong file or wrong key for that client — check the table in [Wiring it up](#wiring-it-up) |
| A Factory Task-dispatched droid lists no MCP tools | Check the droid's own `mcpServers` whitelist in `.factory/droids/<name>.md` **and** the server entry in `.factory/mcp.json` or `~/.factory/mcp.json`. The app-level file alone is not enough: an empty or `[]` list in the droid file excludes every server regardless of what the session has, and an omitted key would inherit rather than exclude. Every droid the shipped squad renders is granted `kyber-weave` — `conductor` included — so a `[]` no longer means "this is the orchestrator": it means the droid's capability profile grants no read entitlement, or the squad declares no servers ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). Droid-level visibility is Squad's whitelist, not your client config — see [Factory](#factory) |
| A ZCode agent lists no MCP tools, and `squad doctor` fails the install | ZCode expands no wildcard and `toolchain.yml`'s `required-mcp-tools` are hard connected-server requirements for every agent, `conductor` included. Declare the `kyber-weave` server in the ZCode MCP configuration, or the install does not pass |
| A Kilo agent has no `permission` key, or an MCP call prompts instead of running | The key is emitted only for an agent entitled to `kyber-weave`, and `allow` is an approval rather than a visibility grant — check the agent's capability profile, then the `mcp` key in `kilo.jsonc`. An agent with no key is outside the entitlement by design; a subagent that prompts will fail closed. A `kyber_weave_*` key would match nothing, so check the hyphen. See [Kilo](#kilo) |
| An Antigravity agent lists no MCP tools | Check the agent file's `mcpServers` entry and the workspace `.agents/mcp_config.json`, then the client's `mcp(server/*)` policy patterns — MCP tools default to Ask there. Adding an MCP tool name to `tools:` does not help and makes the executor construction fail with `unknown component` on stderr. See [Antigravity](#antigravity) |
| A Pi subagent lists no MCP tools | Check the derived `ext:pi-mcp-adapter/mcp__kyber_weave__…` entries in the agent file, the adapter version (the per-tool names need `pi-mcp-adapter` 6.0.0 or later), and the adapter's own server configuration plus its first-use trust approval. A lowered primary agent is a skill and carries no overlay at all. See [Pi](#pi) |
| A droid lists no MCP tools on a target whose session has them | On Factory the droid's own `mcpServers` value governs; on Cursor, Codex, and Warp nothing per-agent governs it at all, because reach is inherited from the session. Identify which case you are in before editing a rendered file — install and update own those bytes |
| Every query is a miss | Wrong repo root, or `docs-root` in [config](../configuration.md) names a tree the documents are not in — check stderr for `KW-CONFIG-001` |
| "no CodeGraph index was readable" | No `.codegraph/codegraph.db`, or `sqlite3` missing from PATH |
| Joins show `(unresolved)` | The symbol is in `code-refs` but not in the index — run [`docs drift`](governance.md) |
| Analysis warns that the cache is unsafe | Run `docs init` to merge `.kyber-weave/.gitignore` with `cache/`; until then deterministic/lexical analysis continues and no document text is sent for embeddings |
| `docs_glossary` returns no senses | The configured glossary is absent, the term is not present, or its spelling differs; use `docs glossary .` to preview proposals |
| `agent_coding_standards` reports no standard for a technology | The key names no technology this corpus declares a standard for — check the Config Reg block of the repository root [`AGENTS.md`](../../AGENTS.md) and `ontology.technologies` in [`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml). Spelling alone cannot be it: the match is case-insensitive, so a miss means the technology is undeclared, or is declared with no standard scaffolded at `docs/standards/<technology>/README.md` |
| Client reports a protocol error | Something wrote to stdout; check that you launched `kyber-weave-mcp`, not `kyber-weave` |
| Answers look right but named files do not exist, or the document count is wrong | A same-named server is bound to a different checkout — compare the `provenance: root=` line to `git rev-parse --show-toplevel`, call `docs_status`, and pin the server with `--expect-root` |
| Piped requests exit 0 with nothing on stdout | A release up to `v0.1.7-rc.13` dropped replies once stdin closed — upgrade, or keep stdin open until the replies arrive. See [Closing stdin](#closing-stdin) |

## Related

- [Retrieval and ranking](retrieval.md) — how results are chosen and budgeted
- [Documentation analysis and review](analysis.md) — findings, review exchange, cache, and glossary
- [DocGraph architecture](architecture.md) — the index behind these tools
- [Installing Kyber-Weave](../install.md) — getting the binary on PATH
