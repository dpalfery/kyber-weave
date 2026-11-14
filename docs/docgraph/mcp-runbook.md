---
id: docgraph/mcp-runbook
title: Running the DocGraph MCP server
doc-type: runbook
status: current
component: DocGraph
source-root: src/KyberWeave.Mcp
owner: dpalfery
last-reviewed: 2026-11-11
decided-by:
  - adr/0051-dotnet-cli-spawns-stdio-children-through-one-bounded-session
  - adr/0052-docs-branch-writes-repository-scope-mcp-configuration
  - adr/0061-code-editing-runs-stop-on-unusable-tools
  - adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools
code-refs:
  - CodeGraphFreshness
  - CodeGraphProvisioner
  - CorpusProvenance
  - DocsTools
  - DrainingStreamServerTransport
  - HarnessMcpCatalog
  - HarnessMcpEntry
  - JsonServerSplice
  - LaunchCommandResolver
  - McpConfigurationLink
  - McpServerNames
  - McpServerRenamer
  - McpWiringFix
  - McpWiringFixes
  - McpWiringInspector
  - McpWiringReceipt
  - McpWiringRepairer
  - RepositoryDoctor
---

# Running the DocGraph MCP server

`kyber-weave-mcp` serves [the documentation graph](architecture.md) to an agent over stdio
MCP. It is the delivery surface for the whole feature: everything else produces the corpus,
this hands it to a model. Clients register it as **`kw-docs`**.

**KyberDash MCP (`kw-dash`)** is a different server: `kyberdash mcp` exposes read-only
analytics against a running KyberDash engine. What it is, how it differs from `kw-docs`, and
how to set it up in every harness are documented on [KyberDash MCP
(`kw-dash`)](../dash/mcp.md), not on this page.

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
| [Kilo](#kilo) | `kilo.jsonc` | `mcp` | Per-agent reach is the agent frontmatter `permission` map entry `kw-docs_*: allow` — an approval grant, not isolation |
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
    "kw-docs": {
      "type": "stdio",
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

Or write it from the CLI, which is equivalent:

```bash
claude mcp add --scope project kw-docs -- kyber-weave-mcp --repo-root .
```

Servers from a committed `.mcp.json` are **not trusted automatically** — a checkout cannot
approve its own servers. Each user approves once on first run, and the server sits at
`⏸ Pending approval` in `/mcp` until then. After a rename the approval is worth re-checking:
Claude Code's documentation does not say whether its record is keyed by server name, so
`claude mcp reset-project-choices` — which resets the project choices outright — is the
reliable move, and an inference about the key is not a substitute for it.

### Factory

`.factory/mcp.json` in the repository root, or `~/.factory/mcp.json` to get it in every
project. The entry is the same shape as the Claude Code row:

```json
{
  "mcpServers": {
    "kw-docs": {
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
  beside its canonical source — today exactly `kw-docs`, launched as
  `kyber-weave-mcp --repo-root .` — and it is granted to every droid whose capability profile
  allows `filesystem.read`, the pure orchestrator `conductor` included. One of that server's tools
  is `docs_get_coding_standard_for_technology`, and the orchestrator is asked to hold specialists
  to standards it
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
with every other client: call `docs_get_source_repository_and_revision`, which takes no
arguments, and check that its `repo=` equals `git rev-parse --git-common-dir` in the repository
you meant.

**CodeGraph, and the boundary that follows from it.** The same file can launch the CodeGraph
server:

```json
{
  "mcpServers": {
    "kw-docs": {
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
names only `kw-docs`. `codegraph_explore` is therefore session-level
on Factory today — available to you in the main conversation and not claimed as droid-visible.
A role that needs it, such as `architect` or `research-agent`, gets it by dispatch from a
session that has it, not by holding the tool itself. Leave the entry in place for your own use
and do not read a green droid file as evidence that a droid can resolve symbols.

`docs drift` reads a `.codegraph/` index and reports a missing or unreadable one as critical
rather than passing, so `docs_detect_drift_between_documents_and_code` called from any client
needs that index to exist. In this
repository, `codegraph init .` at the repository root is pre-authorized by the root
[`AGENTS.md`](../../AGENTS.md). Prefer `kyber-weave doctor --ensure .` over the raw verb: it
restores any tracked file the initialization rewrote — including the marked CodeGraph block —
and never touches a file that was already dirty, which is the before-and-after check made
mechanical.

### Cursor

`.cursor/mcp.json` in the repository, or `~/.cursor/mcp.json` to get it in every project.
The shape matches Claude Code:

```json
{
  "mcpServers": {
    "kw-docs": {
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
    "kw-docs": {
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

**What Squad emits.** For every agent entitled to `kw-docs` — `conductor` included —
`KiloRenderer` writes one frontmatter key ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)):

```yaml
permission:
  kw-docs_*: allow
```

An agent outside the entitlement renders no `permission` key at all. That absence is the
withholding, and it is deliberate: a `deny` would assert a decision the capability lattice cannot
derive, since it declares no MCP capability.

**An approval grant, not isolation.** Kilo's `permission` values are `ask`, `allow`, `deny`, or
null, and `allow` means the call runs without the ask prompt. It hides nothing: only a `deny` whose
pattern is `*` removes a tool from the model. The entry above is what keeps the ten `kw-docs`
tools callable from a subagent nobody is watching, because an ask that no one answers fails
closed. It restricts nothing, and deleting it from a rendered file would not hide the tools — it
would put the prompt back.

**The hyphen belongs in the key.** Kilo sanitizes the server name only for characters outside
`[a-zA-Z0-9_-]`, so `kw-docs` survives verbatim and the server wildcard is `kw-docs_*`. A
`kw_docs_*` spelling parses cleanly and matches nothing. Verified against `@kilocode/cli`
7.8.18 (`Kilo-Org/kilocode` tag `v7.8.18`) by running Kilo's own matcher:
`kw-docs_docs_get_source_repository_and_revision` matches `kw-docs_*`;
`kw_docs_docs_get_source_repository_and_revision` does not. The verification predates the rename;
the rewrite above keeps its outcome — the hyphen is still in the server key, and the sanitizer
still leaves it alone.

**Your half:** the server entry in `kilo.jsonc`'s `mcp` key under the key `kw-docs`, launched as
`kyber-weave-mcp --repo-root .`. An agent file cannot add a server and `squad install` writes no
harness settings file, so a rendered grant with no `mcp` entry reaches nothing. Then check the repository
rather than assuming it — call `docs_get_source_repository_and_revision` (its only argument is the optional `projectPath`)
and confirm its `repo=` equals `git rev-parse --git-common-dir` in the repository you meant.

Do not hand-add a permission key to a rendered agent file: install and update own those bytes.

### Antigravity

Workspace-scoped, at `.agents/mcp_config.json` in the repository
(antigravity.google/docs/mcp, read 2026-10-31):

```json
{
  "mcpServers": {
    "kw-docs": {
      "command": "kyber-weave-mcp",
      "args": ["--repo-root", "."]
    }
  }
}
```

MCP tools default to **Ask** on Antigravity, so approval goes through that client's
`mcp(server/*)` policy patterns rather than through an agent file.

**What Squad emits.** For every agent entitled to `kw-docs` — `conductor` included — the
renderer adds one array entry to the agent file under `.agents/agents/<name>/agent.md`
([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)):

```yaml
mcpServers:
  - name: kw-docs
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
The single-key map spelling (`- kw-docs: {command, args}`) listed and ran but was never
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
provenance rather than trusting the answer: call `docs_get_source_repository_and_revision` and
check that its `repo=` equals `git rev-parse --git-common-dir`.

### Pi

Pi reaches MCP through the `pi-mcp-adapter` extension, which reads its servers from a project
`.mcp.json`, a user-global `~/.config/mcp/mcp.json` or `~/.agents/mcp.json`, or Pi's own
agent-directory `mcp.json`. A project-scoped server needs a trust approval the first time the
project uses it. Declare `kw-docs` there launched as `kyber-weave-mcp --repo-root .`; as on
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
ext:pi-mcp-adapter/mcp__kw_docs__docs_answer_question_from_repo_documentation
ext:pi-mcp-adapter/mcp__kw_docs__docs_detect_drift_between_documents_and_code
ext:pi-mcp-adapter/mcp__kw_docs__docs_find_documents_citing_code_symbol
ext:pi-mcp-adapter/mcp__kw_docs__docs_find_duplicates_conflicts_ambiguous_terms
ext:pi-mcp-adapter/mcp__kw_docs__docs_get_coding_standard_for_technology
ext:pi-mcp-adapter/mcp__kw_docs__docs_get_source_repository_and_revision
ext:pi-mcp-adapter/mcp__kw_docs__docs_look_up_glossary_term_definition
ext:pi-mcp-adapter/mcp__kw_docs__docs_validate_governed_document_schema
```

The emitted `tools` value is one CSV line: the capability-derived built-in names first, then the
agent's own declared `pi-tools`, then the derived entries above. An agent that already declares one
of them renders it once, and `research-agent` keeps its hand-authored `context7` pair beside the
derived `kw-docs` entries.

**The namespace maps the hyphen to an underscore.** The adapter names a tool
`mcp__<namespace>__<tool>` and maps every character outside `[A-Za-z0-9_]` to `_` in the server
namespace first, so `kw-docs` is reached as `kw_docs`. An entry spelled
`mcp__kw-docs__<tool>` names no registered Pi tool. This differs from Claude, ZCode, Devin, and
Command Code on purpose: those pass the server name through verbatim, and Pi does not.

**Version floor: `pi-mcp-adapter` 6.0.0, current release 7.0.0.** The deferred-tool surface this
naming belongs to arrived in 6.0.0, whose changelog first records tools becoming a deferred tool
named `mcp__<server>__<tool>`; 7.0.0 is the supported target. On 5.x there is no per-tool deferred
registration at all — the tools would be named
`kw-docs_docs_get_coding_standard_for_technology` — so the proxy
entry `ext:pi-mcp-adapter/mcp` still works and only the enumerable per-tool names resolve to
nothing. Upgrade the adapter rather than editing the names.

**The lowered primary agent is the one exception.** `conductor` on Pi is a skill at
`.pi/skills/conductor/SKILL.md`, and skill frontmatter carries no `tools` or `extensions` key, so
the overlay is structurally inexpressible there. Its degradation record names `kw-docs` and
says the entry point reaches the server only through the session it runs in — the same reading as
Devin's lowered conductor. Every entitled subagent file carries the full overlay.

**What remains an owner probe** is the in-session listing: that a live session shows these names
and answers a call. The spelling is not a probe — it was established by executing the adapter's
published naming code over the roster and corroborated by a live MCP `tools/list`, where the
adapter's own `doctor` reported `kyber-weave: ok, 8 tools` — the report was read before the
rename and names the key as it then stood. Then compare provenance as everywhere else: call
`docs_get_source_repository_and_revision`, whose `repo=` must equal `git rev-parse --git-common-dir`.

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
    "kw-docs": {
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
    "kw-docs": {
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
[mcp_servers.kw-docs]
command = "kyber-weave-mcp"
args = ["--repo-root", "/path/to/your/repo"]
startup_timeout_sec = 10
```

Or from the CLI:

```bash
codex mcp add kw-docs -- kyber-weave-mcp --repo-root /path/to/your/repo
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
    "kw-docs": {
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
server named `kw-docs` there, launched as `kyber-weave-mcp --repo-root .`.

The user tier (`<storage>/cli/config.json`) is **user scope**: the check never reads it and
the repair never writes it. A server wired only there is reported as not wired for the
repository, which is the right report for a configuration the team shares.

**Nothing writes this harness yet.** `docs mcp-wire --target zcode` refuses with
`KW-MCP-WIRE-005`, names the file and the reason, and prints the place the entry goes. The
declared widening path is one verified fact: pin the entry body against `mcpSchema`, add it
to the catalog row, and flip that row's writability — a catalog edit, not a structural
change ([ADR 0052](../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md)).

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
`docs init` reports where the declaration is not honoured, and a named repair writes it. The keys
themselves were renamed in this release, which is a separate command's job:
[`docs mcp-migrate`](#docs-mcp-migrate) renames an old key where it can prove the entry is this
product's, and reports every entry it cannot.

### The declaration

The declaration is the `mcp:` section of `.kyber-weave/kyber-weave.yml` — the same host
configuration every other setting lives in, and the full schema is in
[configuration](../configuration.md#mcp-servers):

```yaml
mcp:
  required-servers:
    kw-docs:
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

**`first-party` is bound to the binary this product ships.** The class is a statement about
who ships the binary, not a preference, and the whole first-party remedy rests on it: an
absent first-party binary is reported as *this Kyber-Weave installation is broken; run
`kyber-weave update`*, which is only true while every loaded first-party server really is the
shipped one. So the claim is checked at load time against argv[0] — the element the check
probes and a harness launches — and a declaration that claims it about any other program
fails to load as `KW-CONFIG-001`, naming the offending program. The match is a
case-insensitive filename **equality** rather than a containment — a directory prefix and
the `.exe` suffix a Windows install appends still qualify, and a wrapper command cannot pass
a claim about the thing it wraps, but a program that merely carries the shipped name inside
it (`fake-kyber-weave-mcp`) is somebody else's binary. A binary you install yourself is
`third-party` whatever it does, even when it serves this repository's documentation.

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
  ok    factory: kw-docs wired
  warn  KW-MCP-WIRE-001 cursor: server 'kw-docs' is not wired (.cursor/mcp.json)
        Fix: kyber-weave docs mcp-wire --target cursor
  warn  KW-MCP-WIRE-003 factory: server 'kw-docs' is wired but 'kyber-weave-mcp' is not on PATH
        This Kyber-Weave installation is broken; run: kyber-weave update
  info  KW-MCP-WIRE-004 claude-code: not checked (no '.mcp.json' and no marker to detect it)
```

What it reports:

| Id | Level | Raised when |
|---|---|---|
| `KW-MCP-WIRE-001` | warning for a `required` server, `info` for a `recommended` one | a declared server is not wired for a harness in play, or the file could not be read — including a configuration that reached the repository as a symbolic link, which is not read at all, and one this process may not inspect, which is reported as a gap rather than as a configuration the repository does not have |
| `KW-MCP-WIRE-002` | same as above | the entry is wired but its launch command differs from the declaration, once the harness's own spelling of the repository root is allowed for; the line names both. Never fixable, so never offered as a repair |
| `KW-MCP-WIRE-003` | same as above | the entry is wired — or would be — but the launch binary does not resolve: a command name is looked up on `PATH`, and a command that names a path is resolved against the repository under inspection |
| `KW-MCP-WIRE-004` | `info`, always | the harness is not in play, has no marker to detect it, or keeps its configuration in repository settings |

**The comparison allows one substitution, and only one.** The repair writes the harness's own
spelling of the repository root, so VS Code gets `${workspaceFolder}` where the declaration
says `.`, and Factory, Claude Code, Cursor and opencode keep the declaration's `.`. The check
therefore compares a wired root token against the declaration's `.` **as that harness's own
spelling of the same directory**, and compares every other token as raw text. The tolerance is
deliberately not global: `${workspaceFolder}` in a Claude Code entry is a directory literally
named that to a harness which substitutes nothing, so it must not compare equal there. This is
what makes the second-run contract below true for every writable harness — the file the repair
wrote is a file the check passes, so a second `mcp-wire` over it reports `nothing to write` at
exit 0 rather than refusing its own success.

**The spelling is the one held under `KYBER_WEAVE_REPO_ROOT`.** A harness's token is read from
the value its entry keeps under that environment key — the same name the MCP server resolves
its own root from — and not from whichever environment entry happens to come first. Key order
in an entry body is the harness author's choice, and a block that listed another variable first
would otherwise have made that variable's value the spelling the repair substitutes and the
check accepts: one wrong token, agreed on by both sides of the round trip. A harness whose
entry carries no such key substitutes nothing, and the declaration's own `.` is what it keeps.

**A launch command is resolved the way a harness would start it.** A bare name is looked up
along `PATH` in order, so a later entry never masks the earlier answer and an empty entry means
the repository root. A command that names a path — `./bin/kyber-weave-mcp`, `tools/serve-mcp`,
`C:serve-mcp` — is not something `PATH` resolves, so it is taken as given when fully qualified
and resolved against the **repository under inspection** when it is not. That is the directory
`docs init` or `docs mcp-wire` was pointed at, never the one you happened to run from: the
declaration is committed, so the same entry has to answer the same from a checkout, a CI
runner, and a shell somewhere else. No process is started either way — this is a file-existence
test, not a `--version` probe.

**An entry that names no launch command is a gap with an edit, not an offer.** A harness file
holding an entry under the declared server's name whose body is empty, or whose command cannot
be read, is reported as not wired (`KW-MCP-WIRE-001`) with the command the entry should carry.
It is never a `KW-MCP-WIRE-002` difference, because there is no wired command to compare
against, and it is never offered as a repair, because the repair refuses an entry that already
exists. Deleting the entry is the route back to a repair that works, and the hint says so.

A harness is "in play" when its repository-scope configuration file or its marker directory
exists — `.factory/`, `.cursor/`, `.vscode/`, `.codex/`, `.zcode/`, or the configuration file
itself where no marker exists. Detection is a heuristic and the output claims no more than
that: a harness with neither signal gets one `info` line naming what was looked for, never a
warning. Claude Code has no marker besides `.mcp.json`, so a repository with neither is
informed rather than failed.

The one exception is the harness whose configuration is repository settings rather than a file
— today the Copilot coding agent. There is no file to read and no repair to offer, so its
`info` line is followed by the advice that closes it: the entry to add to the repository's
Copilot MCP configuration and the workflow step that installs the binary. That advice is
printed for that shape of harness and for no other; every other undetectable harness still
reports the single line naming the marker that was looked for.

The check writes nothing, reads **repository scope only** — no file under the home directory
is opened, for any harness — and does not change `docs init`'s exit code: a scaffold that
succeeded exits 0 with findings, exactly as the APM-missing and retired-skill paths do. A
declaration the configuration loader rejects is the one exception: it fails the run through
the existing `KW-CONFIG-001` path, before the check runs. A repository that declares no
servers prints no section at all.

**What it proves, and what it does not.** The check reads configuration bytes and never
starts a harness. A clean report means each in-play harness's file declares the server with
the command the repository declared, and that the binary resolves — the whole of what a check
confined to the repository can establish. Whether a live session then lists that server's
tools is a separate observation, made once after wiring: open the harness in this repository
and confirm the declared server's tools appear. Until it is made, read "wired" as wired
correctly on disk, not as reachable from a running agent.

**Everything it prints that came out of a file is stripped and capped first.** A server name,
a declared command, a finding's hint, the snippet to paste, and the paths and names a receipt
records are all somebody else's text by the time they reach this report: `kyber-weave.yml` is
whatever was cloned, a harness configuration belongs to the harness, and the receipt is local
state a teammate or an earlier tool can have written. Terminal escape sequences are removed
whole — including the two-character forms, so no fragment is left behind — line breaks are
folded into a hint's single line, and a value is capped at 512 characters a line and 4096
characters a block, marked when it was cut. A truncated entry is a better outcome than a value
that clears the scrollback or colours the operator's console.

`--no-mcp-check` skips the check, the prompt and any repair, and prints one line saying so.

### The prompt, and what happens without a terminal

When the check found at least one fixable gap, `docs init` offers a yes/no question naming
exactly which files and which servers the repair would write. It defaults to **no**.
Confirming performs the repair through the same write path `docs mcp-wire` uses and reports,
per file, what was written, then the receipt's location. Declining writes nothing — the
findings and their printed commands stand, so the same repair is one command away.

The question is built from fixable gaps alone — a server that is not wired, on a harness the
repair writes, with no link or denial on the path to the file. Four findings are reported and
never offered. A differing launch command (`KW-MCP-WIRE-002`) is never fixable, because
closing it means modifying an entry that already exists and the repair never touches one; its
hint names the exact command the entry should carry, quotes the wire command only with the
caveat that it refuses the existing entry, and so amounts to an edit by hand — or a deletion
of the entry, after which the wire command can add it back. An absent binary
(`KW-MCP-WIRE-003`) is never fixable either, because the remedy is installing software and
Kyber-Weave installs none but its own. Neither is a configuration that reached the repository
as a symbolic link, nor one this process may not inspect, because the repair refuses the file
the check refused to read. None of the four appears in the question. An entry that exists but
names no launch command is excluded from it for the same reason, and reported with the edit
instead.

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
- **A named target that already holds every declared server, launched as declared,** is
  reported and skipped at exit 0, not treated as an error — otherwise a second run would fail
  on the first one's success. The test is the check's own, so a root the repair wrote in the
  harness's spelling counts as the declared `.`: wiring VS Code writes
  `${workspaceFolder}`, and the run after it reports `nothing to write` instead of refusing
  the entry the first run created.
- **A refused target** exits 1, names the file and the reason, and writes nothing to that
  file: a slug the catalog does not know, a malformed or foreign document, an entry with the
  same server name already present, a harness the repair does not write, an absent binary, a
  configuration that is a symbolic link — refused before it is read, so not one byte of the
  target is opened — a configuration this process may not inspect, a configuration that still
  holds a proven retired MCP server key — where the wire command defers to `docs mcp-migrate`,
  because adding the new entry beside the old one would be one server under two names — and a
  receipt that could not be persisted. Other named targets still proceed.
- **A write it cannot record is a write it does not make.** The undo record is persisted
  before the harness file is touched, so a target whose record cannot be written is refused
  (`KW-MCP-WIRE-005`) rather than left as a changed file with no account of what it held. See
  [The receipt](#the-receipt).
- `--dry-run` prints the exact per-file change — the file, the harness, the servers it would
  gain, and whether the file exists yet — writes nothing, not even the receipt, and exits what
  the real run would have exited:

  ```text
    would create .factory/mcp.json (factory) adding kw-docs
    would insert into .vscode/mcp.json (vscode) adding kw-docs
  ```

  The real run prints the same lines with `created` and `inserted into` in place of `would
  create` and `would insert into`.

### `docs mcp-unwire`

```bash
kyber-weave docs mcp-unwire [path] [--target <targets>]
```

Removal works from the receipt, not from the declaration, so a repository whose `mcp:`
section is malformed can still be unwound. An `inserted` file is restored to the bytes the
repair found; a `created` file is deleted. With no `--target` it lists the files the receipt
records and removes nothing. It exits 0 when every named file was restored or deleted, and 1
on any refusal: a slug the catalog does not know, no receipt record, a file that is gone, a
current digest that differs from the recorded one, a file that is a symbolic link — refused
ahead of the receipt lookup, because restoring the recorded bytes through the link would
write them into whichever file it names — or a file this process may not read.

That digest comparison is the point of the verb. **A file that changed after the repair is
drift, and drift is refused** (`KW-MCP-WIRE-006`) rather than restored over someone else's
edit.

**An interrupted write is recognised rather than refused.** Because the record is persisted
before the file, a run killed in between can leave a record for a write that never landed.
Removal therefore compares the file's digest against *both* recorded digests, and the one
digest that is not drift is the pre-write one: the file then already holds the state the
removal was asked for, so the record is dropped and the file is left alone. A `created`
record whose file does not exist at all is the same window seen from the other side — nothing
was ever written, so the record goes and nothing is reported as a foreign edit. A receipt
that cannot be read, that was written under another schema version, or whose shape this build
cannot trust — a null file list, a record with no path or no digest — is treated as an empty
receipt instead: Kyber-Weave only removes what it can show it wrote, so a receipt that proves
nothing refuses everything rather than restoring from a record it cannot check.

### `docs mcp-migrate`

```bash
kyber-weave docs mcp-migrate [path] [--target <slugs>] [--dry-run]
```

This release renames the three servers and their nineteen tools: `kyber-weave` is now `kw-docs`,
`kyberdash` is now `kw-dash`, and `kyber-weave-arbiter` is now `kw-arbiter`. **The executables did
not move** — `kyber-weave-mcp`, `kyberdash mcp` and `kyber-weave-arbiter serve` are launched
exactly as before — so an existing wiring still starts a server, under a key the repository's
declaration no longer names. Renaming the key is this verb's whole job, and it will not touch an
entry it cannot prove is ours.

`path` defaults to `.`, and the file set is the catalog's: the same repository-scope files
[the check](#the-check) reads and [`docs mcp-wire`](#docs-mcp-wire) writes.

- **No `--target`**: a preview. Every old key found is listed with its proof result and the
  writable targets that would take it. Nothing is written, and it exits 0 — as it does when there
  is nothing to do.
- **A named target is written**, and the host declaration moves with it: the `mcp:` section of
  `.kyber-weave/kyber-weave.yml` is migrated in the same run. `declaration` is a slug in its own
  right, for a repository that has no harness files at all. A named target that refuses exits 1 and
  the other named targets still proceed.
- `--dry-run` prints the same report and writes nothing — no bytes, and no receipt. It exits what
  the real run would have exited.

**An old key alone is not proof, and it is not enough to rename.** The key is renamed only when the
entry carrying it also proves the launch: `argv[0]`'s file name equals the shipped binary, in any
directory, case-insensitively, with the optional `.exe` a Windows install appends — plus the
argument that binary needs, which is `serve` for the arbiter and `mcp` for the dash. Anything else
is reported with the edit to make by hand and left exactly as it stands. Codex, ZCode and the
Copilot repository settings are reported with the snippet and never written, for the same reasons
they are snippet-only for `docs mcp-wire`.

| File state | Launch proven | Action | Reported as |
|---|---|---|---|
| Old key absent | n/a | nothing | nothing |
| Old present, new absent | yes | rewrite the key's bytes only | `KW-MCP-WIRE-007`; renamed |
| Old and new present, same launch | yes | remove the old member, byte-exact | `KW-MCP-WIRE-007`; removed duplicate |
| Old and new present, different launch | yes | refuse, zero bytes, show both | `KW-MCP-WIRE-009`, exit 1 |
| Old present | no | never touched | `KW-MCP-WIRE-008`, with the manual edit |
| Symlink, denied, malformed, foreign shape | n/a | refuse, zero bytes | `KW-MCP-WIRE-009`, exit 1 |
| Root is the operator's home directory | n/a | refuse before the first read, zero bytes, no receipt | `KW-MCP-WIRE-009`, exit 1 |
| Codex, ZCode, Copilot settings | n/a | report only | `KW-MCP-WIRE-008`, with the snippet |

The bounds are [`docs mcp-wire`](#docs-mcp-wire)'s, held for the same reasons: the catalog-only
file set, repository scope only — no user-scope file is read or written — the whole plan computed
before the first byte moves, the receipt recorded before the bytes, every byte outside the renamed
key preserved, and idempotence, so a second run over a migrated tree reports nothing to do at
exit 0. The receipt is the journal in [The receipt](#the-receipt): a change this tool cannot record
is a change it does not make.

**A grant is not a key, and this verb does not rewrite one.** A rendered agent file that carries
`mcp__kyber-weave__<tool>`, `kyber-weave_*` or `kyber-weave/*` is re-rendered by `kyber-weave squad
update`, not edited here, and a harness's own permission allow-list that names an old tool is the
operator's to change. [The upgrade steps](../install.md#renamed-mcp-servers-and-tools) name every
surface no tool reads.

### The receipt

Every write is recorded in `.kyber-weave/receipts/mcp-wiring.json` — schema version 1, one
record per file, latest write wins — holding the file's path, the mode (`created` or
`inserted`), the servers written, the pre-write and post-write SHA-256 digests, a UTC
timestamp, and, for an inserted file, the pre-write bytes it captured — a digest proves a
restore is safe, but it cannot produce the bytes to restore. `docs init` adds `receipts/` to
`.kyber-weave/.gitignore` beside `cache/`.

**It is a journal, and the order is the mechanism.** Each record is persisted *before* the
harness file it describes is replaced. Written the other way round, a run killed in between
leaves a mutated harness configuration with no account of its previous bytes: the removal
refuses it as drift and the undo is unreachable outside version control. Written first, the
same kill leaves a record for a write that never landed, and the removal recognises that from
the bytes still on disk — which is why removal treats the pre-write digest as "already
undone" rather than as drift. The record also gates the write: a change this tool cannot
record is a change it does not make, reported as `KW-MCP-WIRE-005` with the receipt path in
the hint.

**What an operator does with it.** Read its `files` list to see which harness files a run
wrote and in which mode; run `docs mcp-unwire --target <harness>` to undo one, and
`docs mcp-unwire` with no target to list what the record holds. It is the writer's local undo
record and nobody else's: teammates receive the committed configuration file and never need
it, and their removal path is `git revert`. It can be deleted once the write has been
committed and reviewed — deleting it retires the undo along with it. A hand-edited record is
not refused for being hand-edited: removal acts on what the record proves against the file,
and a record whose digests no longer describe the bytes on disk is refused as drift like any
other. A receipt whose shape does not match schema version 1 is treated as empty instead —
and the shape check holds every field to the rule that produced it: a path inside the tree,
at least one non-blank server name, a non-blank post-write digest, a write timestamp, and,
where the record carries them, a non-blank pre-write digest and payload. A record that cannot
say what it wrote proves nothing a removal may act on, so it loads as nothing.

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
  here and flips that catalog row's writability. The snippet it prints is a real TOML table,
  with every segment of the header spelled as a TOML key: one holding a dot, a bracket, a
  quote or a newline is quoted, so a server name cannot turn into a nested table or close the
  header and put whatever follows it into the file the operator pastes.
- **ZCode** is refused because the entry body inside `mcp.servers` is pinned nowhere in this
  repository — see [ZCode](#zcode). The unblocking source is ZCode's `mcpSchema`.
- **The Copilot coding agent** has no file to write: its configuration is repository
  settings, and the binary has to be installed by the workflow step in
  `.github/workflows/copilot-setup-steps.yml` first. See [GitHub Copilot coding
  agent](#github-copilot-coding-agent).

Widening any of the three is a pinned fact plus a catalog edit, never a change to the write
bounds ([ADR 0052](../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md)).

The five writable rows all follow one policy, and each bound is a rule rather than a
convenience:

- **Insertion-only, textually.** A harness document is never parsed into a model and written
  back out. The splice is computed in full before the first byte moves, so a refusal writes
  nothing and a partial write cannot happen.
- **Bytes the repair did not write are not touched.** Comments, key order, formatting and a
  trailing comma survive byte-for-byte; the repair never reformats, reorders, or
  re-serializes.
- **An existing entry is never modified.** A file already holding an entry with the declared
  server's name is refused for that file, with the difference reported. That is also why a
  written entry's environment block comes from the harness's own template rather than from a
  merge: within it, only the value holding the repository root is rewritten to the harness's
  spelling, and any other variable the block carries travels as it stands instead of being
  overwritten with a second copy of the root.
- **A name is written in the host's own form, never a stripped one.** A JSON harness gets a
  JSON string literal and a TOML snippet gets a TOML key, so a name holding a quote, a dot, a
  bracket or a control character is escaped rather than mangled — a raw name in a table header
  would become a nested table or end the header outright. The console report is the same
  values made safe the other way: stripped of terminal sequences and capped before printing,
  so an escape sequence in a name reaches neither the file nor the operator's terminal.
- **An absent binary is never installed and never written.** A third-party server whose
  binary does not resolve on `PATH` is refused for every harness; a missing
  `kyber-weave-mcp` is reported as a broken Kyber-Weave installation, to be repaired with
  `kyber-weave update`.
- **Repository scope only.** `~/.factory/mcp.json`, `~/.cursor/mcp.json`,
  `~/.codex/config.toml`, `~/.config/opencode/opencode.json` and ZCode's user storage are
  never written and never read; they are reported with the manual snippet.
- **A symbolic link is refused, not followed.** A repository-scope path that is a link names a
  file outside the tree just as surely as a home-directory path does, so the check does not
  read it and neither verb writes through it. A file this process may not inspect is refused
  on the same three paths, because "denied" and "absent" are opposite answers and only one of
  them may be read as "this harness has nothing to say". See [Symbolic links and unreadable
  files](#symbolic-links-and-unreadable-files).
- **The root follows the harness's own substitution rule.** VS Code gets
  `${workspaceFolder}`; Factory, Claude Code, Cursor and opencode get `.`, the
  workspace-relative form each documents. Codex substitutes no workspace variable and its
  verified form is an absolute path, which is one more reason its entry is the operator's to
  paste and not the repair's to write.

Five more clients above have recipes here and no catalog row — Command Code, Kilo,
Antigravity, Pi, and Warp. Nothing checks or writes them; the recipe is the whole grant.

### Symbolic links and unreadable files

A repository-scope configuration cannot be a symbolic link. The link's own path is inside the
tree, so every containment check passes, while the bytes behind it live wherever it points —
which makes `ln -s ~/.mcp.json .mcp.json`, the idiom for sharing one configuration across
checkouts, a way to turn a repository-scope write into a write to the user scope. One guard
decides this question (`McpConfigurationLink` in Core) and all three paths ask it, so the read
path, the write path and the removal path cannot give three different answers:

| Path | What happens | Rule id |
|---|---|---|
| the check | the linked file is not read; the finding says the path is a symbolic link and was not read; the harness is reported as not wired and is not offered as a repair | `KW-MCP-WIRE-001` |
| the repair | refused **before** the read, so zero bytes of the target are opened and zero written; the diagnostic names the file and that it is a symbolic link | `KW-MCP-WIRE-005` |
| the removal | refused ahead of the receipt lookup; zero bytes written; the diagnostic names the file | `KW-MCP-WIRE-006` |
| the migration | refused before the read and again before the write; zero bytes written; the diagnostic names the file | `KW-MCP-WIRE-009` |

**A path this process may not inspect is the second refusal**, and it is the one that used to
be silent. `File.Exists` answers `false` for a file it cannot reach just as it does for one
that is not there, so a guard built on it would have reported a permission problem as "this
harness has nothing configured" — and the repair, told the file was absent, would have taken
the path that creates one over it. The guard therefore has three states rather than two —
absent, present, and *denied* — and a denial is reported as a gap, on the same three paths:

| Path | What happens | Rule id |
|---|---|---|
| the check | the file is not read; the finding says access to the named segment was denied and that a file this process cannot inspect cannot be told from one that is absent; the harness is reported as not wired **and no repair is offered** | `KW-MCP-WIRE-001` |
| the repair | refused before the read, so nothing is read and nothing is written over a file it could not inspect; the hint names the permission to fix | `KW-MCP-WIRE-005` |
| the removal | refused ahead of the receipt lookup, because restoring recorded bytes over a file this process cannot read would be a guess | `KW-MCP-WIRE-006` |

No repair is offered for a denial, for the same reason a link gets none: the repair asks the
same guard the same question, so sending the operator to a command that will refuse again is
worse than silence. A denial and a link are also kept as separate sentences, because their
remedies differ — one is a permission on a real file inside the repository, the other is a file
outside it — and a single "cannot read this" would leave the operator to guess which they have.

Every segment below the repository root is tested, not only the file itself, so a linked
`.vscode/` beside an ordinary `mcp.json` is refused the same way and the clause names the
segment: `<path> lies behind the symbolic link '.vscode'`. A link whose target is gone is
still a link, and still not something these commands may read through or write into. A parent
reference is recognised as a whole path segment, so a file named `..foo` inside the tree is a
file inside the tree rather than a way out of it.

The remedy is the same on all three paths for a link: copy the content out of the linked file,
delete the link, leave the real file in the tree, and run the command again. The check's hint
says that rather than naming the wire command, because pointing at a repair that will refuse
again is worse than silence. For a denial the remedy is the permission, on the file or on a
directory above it. With this guard in place, [ADR
0052](../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md)'s promise that
these commands never write and never read a user-scope configuration is enforced rather than
merely intended.

## Provenance on every answer

Every docs tool response — hits, misses, and empty or error prose alike — leads with
exactly one provenance line:

```text
provenance: root=/absolute/repo/root branch=feature/x repo=/absolute/repo/.git rev=1a2b3c4 dirty=no documents=72
```

- `root` — the absolute repository root the server is bound to, normalised with
  `Path.GetFullPath`.
- `branch` — the branch this checkout is on, read from the checkout's `HEAD`. A detached
  `HEAD` reports `detached`; a `HEAD` that cannot be read reports `unavailable`.
- `repo` — the git directory every worktree of this repository shares: `.git` itself in a
  normal checkout, and the common directory a linked worktree's `commondir` file names.
- `rev` — the short HEAD SHA, or the literal `unavailable` when Git cannot be read.
- `dirty` — `yes` or `no` when `git status` succeeds, otherwise `unknown`. Untracked files
  are ignored.
- `documents` — the live document count for the corpus; `0` is allowed.

Git identity is read lazily and cached until the corpus is rebuilt, so the header does not
shell out on every call. A missing Git, a directory that is not a repository, or a failed
command are all success paths: the line still renders with `rev=unavailable` and
`dirty=unknown` rather than inventing a SHA or omitting the root. `branch=` and `repo=` cost
no child process at all — both are read from the checkout's own metadata files.

The line is emitted whole and any character budget is taken from the body, so the one tell
that a same-named server answered from another checkout is never the part truncated away.

**Compare `repo=`, not `root=`.** A `root=` that differs from your own working tree is
**another worktree of this repository** when `repo=` matches: same repository, different
checkout, so an answer about code may describe a different branch. Only a different `repo=`
means the server is bound to a different repository. `branch=` names the branch the answer
came from, which is what decides whether a code answer is about your branch.

## The tools

### Naming your worktree with `projectPath`

The server binds one root at startup, but agent runs work in linked worktrees created after it
started, so an answer from the bound root can describe a different branch than the one being
edited. Every tool therefore takes a `projectPath` (optional, except on the two plan lifecycle tools), the same name CodeGraph's
`codegraph_explore` uses: the absolute path of a worktree of this repository. Omit it and the
call is answered from the server's root, as before.

- **Same repository only.** Git is asked for the path's working-tree top level and common git
  directory (`git rev-parse --show-toplevel --git-common-dir`). The common directory must equal
  the server root's; a path in another repository, a path that is not inside a git working
  tree, and a path that is not a directory are refused with a `projectPath was refused:` line
  after the provenance line, and nothing is answered from the server root in their place. A
  subdirectory of a worktree names that worktree.
- **The provenance line shows the root used.** `root=`, `branch=`, `rev=`, `dirty=` and
  `documents=` describe the worktree the answer came from, so a caller can confirm its own
  checkout answered.
- **Per-root state.** The document host, analysis reader and CodeGraph refresh schedule are
  built on first use for each root and kept in a registry of the four most recently used
  worktrees (the server root is separate and never evicted). The least recently used one is
  dropped when a fifth is named, and is rebuilt on its next call.
- **The index is the worktree's own.** The CodeGraph joins and the freshness refresh use the
  `.codegraph/` of the named worktree, never the server root's.

All ten are declared `OpenWorld = false`, and all but the two plan lifecycle tools are declared
`ReadOnly`; the SDK maps those to the protocol's `openWorldHint` and `readOnlyHint`. A client can act on an annotation; it cannot
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
context it becomes a prohibition on reading tests. Only
`docs_answer_question_from_repo_documentation` carries an exclusion, because it is the broadest of
the ten; the narrower nine state their territory positively so no request falls between them.

Where a constraint can live on a parameter it goes there instead — for example how
`charBudget` caps **`docs_get_coding_standard_for_technology`** prose sits on that parameter,
because a property description binds at the point of use. The explore tool has no budget
parameters.

### Name limits, and why the names are long

A tool is bounded twice: the name itself, and the qualified `mcp__<server>__<tool>` string a client
builds from it. The tightest figures anyone reports are:

| Bound | Limit | Source |
|---|---|---|
| Tool name | `^[a-zA-Z0-9_-]{1,64}$`, and no `__` — in the tool name or the server key | the protocol's tool-name grammar |
| Qualified `mcp__<server>__<tool>` | at most 64 characters | older Claude Code reports and third-party documentation |
| Server and tool together | at most 60 characters | Cursor's limit, reported in its forum rather than its documentation |

These are the tightest figures any client reports, not a figure every vendor has verified — two of
the three are not vendor documentation at all — so the names are chosen against the smallest of
them rather than the one that happens to be written down. The current maxima sit inside them: the
longest tool name is 47 characters (`docs_finalize_approved_plan_and_write_task_file`), the longest server-plus-tool pair is 55
(`kw-arbiter` with `arbiter_check_configuration_and_key_readiness`), and the longest qualified name
is 62 (`mcp__kw-arbiter__arbiter_check_configuration_and_key_readiness`). A test pins all three
bounds (`McpToolNameContractTests`), so a name that would break a client breaks the build instead.

The names are long because a tool name is the routing metadata a model reads before it decides
whether to look at the description at all, and `docs_query` or `arbiter_rules` states nothing about
what comes back. A name that states the question wins that decision on merit; a short one has to be
explained before it can be chosen. The cost is paid in the qualified form, which is why the three
figures above are a budget rather than a remark.

### `docs_answer_question_from_repo_documentation(query, cursor = null, projectPath = null)`

One answer for a question, symbol, route, component, or document id — not a ranked list of
documents. After the relevance floor, leaves inside qualifying documents compete; the
winning **leaf** (preamble, lead-in before a `##` section's first `###`, or a `###`) answers
with a **window**: its best-matching paragraphs plus neighbouring context, about **3,000
characters**. A window is a complete answer, so it is not marked incomplete and keeps its
confidence; when the leaf has more, one line offers the optional **`cursor`** for the rest of
the leaf (no confidence penalty). A continuation returns up to the **24,000-character** ceiling
and is marked incomplete only if the leaf runs past it. Lists, tables and code blocks are
never cut inside, so one larger than the window comes back whole.

The response reads, in order:

1. the provenance line (always whole)
2. `confidence: 0.00`–`1.00` — `tanh((winning topic − best other topic) / 2)`, halved when
   incomplete; stop at **0.60 or higher** when the answer is not incomplete
3. a short reading rule (stop, continue with `cursor`, or ask a narrower question)
4. when the window is not the whole leaf, one line naming the cursor token to pass on the next
   call if the window did not answer (or, past the ceiling, the incomplete notice)
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

### `docs_get_coding_standard_for_technology(technology, topic = null, charBudget = 12000, projectPath = null)`

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
[`Excerpt`](retrieval.md#excerpt-budgeting-docs_get_coding_standard_for_technology) uses inside
one standard.

**A topic matches on its words.** Vectorising a topic produces its individual words plus one
fused compound per adjacent pair — `error handling` becomes `error`, `handling` and
`errorhandling` — and rarity weighting then multiplies each term by its inverse document
frequency, so the fused pair, which almost no document states, dominates the query. A
synthesised compound that no candidate section states is therefore dropped from the topic
vector before scoring: a bridge to a compound nobody writes bridges to nothing, while
question scaffolding and corpus-ubiquitous terms are already removed by rarity weighting;
informative words the caller wrote stay in the query whether or not a section states them.

Measured on this corpus against the 0.02 section floor the ranking uses, the fused pair was
what held the tool's own documented example one ten-thousandth below the floor:

| Call | Section that answers it | Before: fused pair kept | After: synthesised compound dropped |
|---|---|---|---|
| `csharp` + `error handling` | Errors and diagnostics | 0.0199 — a miss | 0.0239 — returned |
| `rust` + `error handling` | Errors | 0.0828 | 0.0996 |
| `typescript` + `error handling` | Async work, resources, and errors | 0.0575 | 0.0692 |
| `test` + `start processes` (the compound IS stated) | Tests that start processes | 0.0764 | 0.0764 — unchanged |
| `csharp` + `mocking time` (nothing answers) | — | 0.0092 — a miss | 0.0093 — still a miss |

Dropping **every** term no section states was measured and rejected. Removing that mass
rescales the scores that remain, and it turned `csharp` with `mocking time` — a topic nothing
in the standard answers — from a correct miss into a confident wrong answer. The rule is
therefore narrower than "keep only stated terms": a compound the vectorizer synthesised goes,
an informative word the caller wrote stays.

**A topic that no section clears still returns the standard.** The reply states the topic in
one line — `no section matches the topic '<topic>', so the standard follows without it.` — and
then returns the standard's sections in document order inside the same `charBudget`, with the
budget marker below when the cut bit. The fallback is the tool's decision, not the index's:
`DocumentIndex.CodingStandards` still returns no prose for a topic nothing answers, so a
caller of the index itself sees a genuine miss. The tool turns that miss into the prose the
caller came for, because withholding it made the miss expensive exactly for the callers with
no shell to go and read the file with.

`charBudget` is total characters of prose across the standards returned, clamped to
1000–120000 and defaulting to 12000. One technology can be answered by more than one
standard — a README and an appendix beside it — and they share that one budget, ordered by
path so the same request answers the same way twice.

The provenance line is emitted whole and the budget applies to the body, so what did not fit
is named instead of silently dropped, and the reply says which of the two kinds of omission it
was — only one of them is recoverable:

- `[omitted for space, ask again with a larger charBudget: …]` when the budget was what bit,
  followed by a second line, `[this cut is expected: the document is longer than the
  charBudget, so what did not fit is left out rather than missing]`, so a reader never reads
  a size cut as a gap in the answer and opens the file to find out what is missing.
- `[also in this document, not matching this topic: …]` when the section was dropped for
  irrelevance and no larger budget will bring it back. This marker never suggests one.

A budget cut is normal rather than a defect — this repository's standards are longer than the
12,000-character default — so the expected-cut line is stated beside the marker, not deduced
by the caller from two numbers.

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
  case, and must not be read as the one above. It states the miss in one line —
  `no section matches the topic '<topic>', so the standard follows without it.` — and then
  returns the standard's sections in document order under the same `charBudget`, carrying the
  omission marker above when the budget cut something. The body does carry sections, so this
  reply never claims otherwise, and the prose is the point: a caller that receives no prose
  opens the file, which is the cost this tool exists to prevent.

### `docs_find_documents_citing_code_symbol(symbol, projectPath = null)`

The documents whose `code-refs` **formally claim** a symbol — not those that mention it in
prose, which is exactly the distinction grep cannot make. Run it before renaming, moving,
or deleting anything to find the documentation that must change with it.

An empty result distinguishes the two cases the caller cares about: the symbol is
undocumented, or it is discussed in prose that never claimed ownership of it.

### `docs_find_duplicates_conflicts_ambiguous_terms(kind?, cursor?, limit = 20, charBudget = 12000, projectPath = null)`

Runs the repository's current configured documentation analysis and returns candidates in
stable kind, term, and candidate-id order. `kind` is optional and accepts `duplicate`,
`conflict`, or `terminology`. Pass the returned candidate id as `cursor` to continue after
that item.

The tool is conversational rather than a bulk export: the hard candidate limit is 20,
the hard response budget is 12,000 characters, and evidence per candidate is capped. The
response keeps local cost/cache metrics and line-addressable evidence inside the same
budget. Use [`docs review export`](analysis.md#cli-and-exit-behavior) when a reviewing
agent needs the versioned rubric and hashes for reusable verdicts.

### `docs_look_up_glossary_term_definition(term, projectPath = null)`

Looks up one managed glossary term case-insensitively and returns its proposed, approved,
and rejected senses, definitions, scopes, and aliases. An unknown term is an ordinary
empty result, not an error. Output is capped at 20 senses and 12,000 characters.

Neither analysis tool accepts a write parameter, so neither can import a
verdict or change the glossary. Reusable decisions enter through `docs review import`, and
glossary proposals enter through `docs glossary --write` at the CLI. The MCP reader reloads
the current config and corpus for each call; embeddings remain loopback-only and are not
called unless the local cache is safely ignored.

### `docs_validate_governed_document_schema(mergeReady = false, base = "origin/HEAD", charBudget = 12000, projectPath = null)`

Runs the same schema conformance gate as [`kyber-weave docs validate`](governance.md) against
the corpus the server already bound at startup, or the worktree `projectPath` names. The root
used is disclosed by the provenance line on every response. Compare that `repo=` to `git rev-parse --git-common-dir` before trusting a
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

### `docs_detect_drift_between_documents_and_code(charBudget = 12000, projectPath = null)`

Runs the same entity-drift gate as [`kyber-weave docs drift`](governance.md) against the
server's resolved repository root, or the worktree `projectPath` names, with the same provenance
contract as
[`docs_validate_governed_document_schema`](#docs_validate_governed_document_schemamergeready--false-base--originhead-charbudget--12000-projectpath--null).

`charBudget` uses the same default, clamp, and truncation rules as
`docs_validate_governed_document_schema`. Response
order matches: provenance, `result: pass` or `result: fail` with counts, then findings with
errors and criticals before warnings.

Warnings such as `KW-DOC-DRIFT-003` do **not** fail the gate; they still appear in the
findings when present. An index older than the code collapses the unresolved references into one
`KW-DOC-DRIFT-004` error naming `codegraph sync .` instead of listing each as drift. A missing or unreadable CodeGraph index is reported as critical
`KW-DOC-DRIFT-001` with the existing "Drift cannot be verified" message — not as `result:
pass`.

### `docs_get_source_repository_and_revision(projectPath = null)`

Reports the provenance of the corpus this server is answering from, with no query and only the
optional `projectPath`. This is the cheap session-start check: call it once before trusting any other
docs tool, and compare its `repo=` to `git rev-parse --git-common-dir` — a different `root=`
with the same `repo=` is another worktree of this repository, and a different `repo=` is the
wrong repository. It returns exactly
the provenance line above — nothing else — so an empty corpus is not a failure: the line
still reports the root, a count of `0`, and unavailable revision markers when Git cannot be
read. It is also the one tool that always answers: under the stop rule every other tool can
return `STATUS: TOOL_FAILURE`, and this one cannot, so a failure can be diagnosed.

### `docs_finalize_approved_plan_and_write_task_file(projectPath, planFile, approvalMode, approval)`

Finalizes an approved Draft plan: records the approval in its `## Decisions` table (an open `AP` row
or an open "approve and execute" row is answered in place, otherwise a new `AP<n>` row is added; in
autonomous mode any other row still `RECOMMENDED` is accepted as `autonomous: recommendation
accepted`), sets the frontmatter `status` to
`current` and the body status line and the plan index row to Ready, and writes the task file with
the same code as `kyber-weave docs tasks <plan> --write`. `kyber-weave docs finalize` is its CLI
twin. `projectPath` is required (the run's worktree); `approvalMode` is `autonomous` or
`supervised`; `approval` is one line, at most 300 characters, recorded as the provenance.

It edits only the plan, its task file and the plan index, then commits exactly those three files on
the current branch of `projectPath` (conventional message, never a push), and refuses, changing
nothing, with `KW-DOC-FINALIZE-008` when other files are staged, and with
`KW-DOC-FINALIZE-001` (not a plan file under the plans folder), `-002` (not Draft), `-003` (no usable
approval, or an autonomous approval of a plan whose frontmatter says `autonomy: supervised`), `-004`
(no Decisions table, or a decision still `OPEN`, or `RECOMMENDED` in supervised mode) or `-005` (the task file cannot be
written from the plan, or the finalized files fail validation; it writes, validates, and restores
the original bytes). A plan already Ready with a current task file reports `result: already-ready`.
The response is the provenance line, `result: finalized | already-ready | refused`, one line per
finding, and on success `TASK_FILE: <path>`, the ready queue (one line per task: id, title, role,
dependencies, session; then the tasks that may run now) and `dispatch the first ready tasks now`. The packet self-containment check
(`KW-DOC-TASKS-008` to `-011`) needs a model, so it is not run here; the architect runs
`docs tasks --check` before `PLAN_READY`. See
[ADR 0078](../adr/0078-the-conductor-finalizes-and-archives-plans-through-two-docs-tools.md).

### `docs_archive_plan_after_closeout_is_complete(projectPath, planFile)`

Archives a plan through the same step as `kyber-weave docs closeout <plan> --archive`: sets it
Archived, moves it to the archive, removes its task file, takes its row out of the plan index, adds
one to the archived plan inventory and stages the files. `projectPath` is required. When the
closeout evidence is incomplete it refuses with exactly the findings `docs closeout` reports,
changes nothing, and ends with a `next:` line sending those findings to `docs-dev`. On success it
ends with a `next:` line to run `docs_validate_governed_document_schema` with `mergeReady` true and
`docs_detect_drift_between_documents_and_code`.

## Reading a code join

```
code joins:
  DocumentIndex -> src/KyberWeave.Core.Docs/Search/DocumentIndex.cs:23 [class]
  Authority -> src/KyberWeave.Core.Docs/Search/DocumentIndex.cs:357 [method] (+2 other same-named)
```

Two annotations mean the resolver had to guess and you should verify:

- `(+N other same-named)` — several symbols share the name; a declaration was preferred
- `(outside this document's source-root)` — the match came from elsewhere in the repository

## Staleness

The index reloads on demand. Document mtimes and file count drive a corpus rebuild;
`.codegraph/codegraph.db` mtime and length drive a joins-only rebuild. No restart is needed
after editing documentation or after the CodeGraph daemon rewrites its index.

## The server refreshes CodeGraph, and its tools fail when it cannot

The server keeps the CodeGraph index its own answers depend on reasonably fresh, so an agent
with no shell is not served a stale index. The rules ([ADR 0061](../adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md)):

- **On the first tool call, then at most once every 60 seconds.** The interval is a constant in
  `CodeGraphFreshness`, not a setting. The refresh runs `sync` on an existing index and `init`
  when `.codegraph/` is absent, under a 30-second bound.
- **Busy lock means the answer goes ahead.** If another process holds the index lock — a
  worker's `doctor --ensure`, the CodeGraph daemon — the response carries one note line above
  its body saying the index is being refreshed by another process, and it answers from the
  index as it stands. Contention is never reported as a failure.
- **A missing binary or a failed `sync` fails the tools.** Under the default stop rule the
  response is `STATUS: TOOL_FAILURE`, then `tool:`, `reason:` and `fix:` lines, and nothing
  else — a caller reading a degraded answer cannot tell it from a complete one. With
  `stop-on-tool-failure: false` the same detail arrives as a `warning:` line above the answer,
  and the answer still comes from the documents on disk.
- **One exception, deliberately.** `docs_get_source_repository_and_revision` always answers, so
  a failure can be diagnosed; and
  `docs_detect_drift_between_documents_and_code` still answers **with** the failure, because
  its verdict is the CodeGraph-failure report the caller asked for. The two plan lifecycle tools read
  no code join and never consult the index. The other six tools return the failure block.
- **Nothing reaches stdout.** The refresh runs through the process runner, which captures both
  streams: stdout stays JSON-RPC.
- **Only where the repository declares the server.** The refresh and the failure apply only
  when `codegraph` is one of the `mcp.required-servers` in `.kyber-weave/kyber-weave.yml`.
  A repository that never declared it takes no index lock, reports no failure and no warning,
  and answers exactly as it did before the refresh existed — which is what keeps a host that
  wants nothing to do with CodeGraph working.
- **Settings are read at each refresh**, so a change to `stop-on-tool-failure` or to the
  declaration takes effect without restarting the server.
- **The schedule is opt-in at the tools level too.** A `DocsTools` built without an explicit
  refresh schedule answers from the documents on disk and consults no CodeGraph at all: the
  schedule is a gate as much as a refresh, and a caller that passes nothing is asking a
  document question rather than asking to be told the index is unusable. A CI runner has no
  index to satisfy such a gate, so a test that builds the tools directly would otherwise lose
  every answer. Keeping an index fresh is a decision the process makes once, at the
  composition roots, which pass the schedule explicitly.
- **`--no-codegraph-refresh` turns it off.** `kyber-weave doctor --check` starts the server with
  that flag, because `--check` promises it never runs `init`, `sync` or `index`, and its live
  probe makes a real docs call.

## The live check: `doctor --check`

`kyber-weave doctor` checks the two pieces a repository needs before its agents can use docs
tooling: a CodeGraph index, which [`docs drift`](governance.md) needs, and a docs MCP server that
answers for this checkout. It is the same core that `docs init --provision` calls, and the
conductor and bug-crusher run-start step invokes it in its `--ensure` mode through the CLI. The
verb's flags and exit codes are in the
[command surface](../cli-command-surface.md#doctor--repository-documentation-tooling).

### What the check proves

`--check` is read-only. It runs these steps, and each one has its own check id:

1. **`codegraph.binary`**: a `codegraph --version` probe runs from `PATH`. A start failure means the
   executable is missing, which is exit `3`. Any other failure, including a probe that does not
   finish, is a broken binary, which is exit `1`.
2. **`codegraph.index`**: `.codegraph/` exists and is writable. A missing directory is a failure here
   in `--check`, because `--check` never creates one.
3. **`mcp.start`**: `kyber-weave-mcp` starts. The check resolves the bare `PATH` name with no
   repository-build fallback, so it proves what an agent will launch.
4. **`mcp.handshake`**: the server answers `initialize` within the MCP budget. The probe then sends
   `notifications/initialized`.
5. **`mcp.docs_explore`**: a `tools/call` to `docs_answer_question_from_repo_documentation` with
   the query `README` returns a non-error result. `docs_get_source_repository_and_revision` would
   prove only that the process is alive; the explore tool also proves that the index loaded. **The
   check id predates the rename and does not follow it**: tool names move, `doctor --json`
   consumers branch on this string, so the id stays and the reason and fix lines name the tool that
   is actually called.
6. **`mcp.root`**: the `provenance: root=` line in that result names the git root. This catches a
   server that answers from another checkout.

`repository.root` is the refusal case. It runs before any other check, and it fails when the path is
not inside a git repository or when the repository root is the home directory. The exit is `2`, and
no check after it runs.

The MCP session has a total budget of 20 seconds (`--mcp-timeout`). The session is always disposed,
so a server that stops answering is killed rather than left behind. The rule and its rationale are in
[ADR 0051](../adr/0051-dotnet-cli-spawns-stdio-children-through-one-bounded-session.md).

### Check ids

| Check id | Fails when | Fix line |
|---|---|---|
| `repository.root` | The path is not in a git repository, git cannot run, or the root is `$HOME` | `run kyber-weave doctor from inside a git repository`, or `cd into a repository and run kyber-weave doctor` |
| `codegraph.binary` | `codegraph` is not on `PATH`, or `codegraph --version` does not finish | `install the codegraph CLI so that 'codegraph' is on PATH, then run: kyber-weave doctor --check` |
| `codegraph.index` | No `.codegraph/` in `--check`, the directory is not writable, the index is damaged, `init`, `sync` or `index` fails, the index lock is busy, or the run exceeds `--index-timeout` | Depends on the cause; see [Provisioning rules](#provisioning-rules) |
| `codegraph.restore` | `--ensure` and `--post-checkout`, and only when it reports something: the index preparation modified a tracked file that was clean before it ran, and the run reverted that file. Absent when the preparation wrote nothing | The paths it restored. Nothing is wrong with the repository; a file the index itself rewrote is not the operator's work |
| `codegraph.explore` | `--ensure` only: the one `codegraph explore` proof did not answer, even after a single repair with `codegraph index` | Run `codegraph explore README` from the root for the child's own error, then `kyber-weave doctor --ensure` again |
| `mcp.start` | `kyber-weave-mcp` cannot be started | `kyber-weave update` |
| `mcp.handshake` | The server did not answer `initialize` within the MCP budget, or closed its output first | Used up the budget: `kyber-weave doctor --check --mcp-timeout <2 x current>`. Refused because the folder has no `.kyber-weave/kyber-weave.yml`: `kyber-weave docs init "<root>"`, or commit that file to the base branch so new worktrees get it. Any other early exit: run the server directly to read its own error |
| `mcp.docs_explore` | `docs_answer_question_from_repo_documentation` returned an error, did not answer, or its reply was not a tool result | `kyber-weave update`, or a retry with a larger `--mcp-timeout` |
| `mcp.root` | The server answers from a different checkout, or its reply names no provenance root | `stop the stale kyber-weave-mcp server and re-run: kyber-weave doctor --check`, or `kyber-weave update` |

### Output contract

Each check prints one line. A passing check prints `PASS <check-id> <reason>`. A failing check
prints `FAIL <check-id> <reason>`, then an indented fix line:

```text
PASS codegraph.binary found on PATH
FAIL codegraph.index no CodeGraph index at .codegraph/
  fix: kyber-weave doctor --provision
```

The reason is short and never carries the child's environment. A child's stderr appears only as
its first non-empty line, truncated to 160 characters. A fix line is a command an agent can run as
written, except `codegraph.binary`, which asks the user to act first (see the next section).

`--json` writes one object instead, with `root`, `mode` (`check`, `provision` or `ensure`), `exitCode`, and a
`checks` array of `{ id, passed, reason, fix }`. The non-interactive hint is suppressed in that mode, so
stdout stays valid JSON.

### Nothing installs CodeGraph

`doctor` never installs CodeGraph, and it never fetches an installer. When `codegraph` is not on
`PATH`, the `codegraph.binary` fix line asks you to install the CodeGraph CLI so that `codegraph` is
on `PATH`, then to re-run `kyber-weave doctor --check`. This repository has no authoritative install
command or package source, so the fix line names none, and this runbook gives none. Exit `3` means
the decision to install belongs to a person.

### The worker's first command: `doctor --ensure`

```bash
kyber-weave doctor --ensure .
```

`--ensure` is the one command a worker runs before it starts work, in the tree it will work
in — the canonical checkout, or the worktree when the task runs in one. It leaves the tooling
usable rather than only reporting on it ([ADR 0061](../adr/0061-code-editing-runs-stop-on-unusable-tools.md)):

1. `codegraph sync` when `.codegraph/` exists, `codegraph init .` when it does not.
2. **Restore any tracked file the preparation just rewrote** — and never a file that was
   already dirty before the run. The set of dirty tracked files is recorded before the index
   work starts, so a rewind can only undo what the index itself wrote; reverting the
   operator's own work would be unrecoverable data loss disguised as housekeeping. Reports
   `codegraph.restore` when it restored something.
3. **One `codegraph explore README` call**, proving the index answers rather than only exists.
4. **One docs-server call**, proving the MCP server answers for this checkout.
5. **One repair and one retry.** A failed step runs `codegraph index .` once and retries once.
   One repair covers the transient damage this exists for; a loop would turn a genuinely broken
   CodeGraph into a slow hang instead of a stop.

Exit codes are the `doctor` contract below plus one behaviour: `--ensure` **reads
`stop-on-tool-failure` itself** and applies it, so no agent judges a degraded result. Under the
default the run's failures are failures and the exit is `1`; under
`stop-on-tool-failure: false` the same checks are reported as warnings and the exit is `0`,
except a missing binary, which stays `3` because installing it is the user's decision either
way. `--check` never applies the setting: a caller that asked for a verdict gets a verdict.

### The worktree index hook

A new `git worktree add` starts with no CodeGraph index, because `.codegraph/` is not tracked. `doctor --ensure`
installs a git hook that indexes each new worktree in the background, so the index is ready when an agent or a
person looks. The decision and its alternatives are in [ADR 0068](../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md).

**Modes.**

- `doctor --ensure` installs the hook block when it is missing, refreshes it in place when it is out of date, and
  removes it when `worktree-index-hook: false` is set.
- `doctor --check` and `doctor --provision` only report the hook. They change nothing.

**Where it lives.** The block sits between two marker lines in `post-checkout`, in the hooks folder git uses for the
clone (`git rev-parse --git-path hooks`). Linked worktrees share that folder, so one install covers them all. A
`core.hooksPath` is honoured. If the hooks folder is tracked by git, the doctor reports it and writes nothing, so no
change is left for you to commit by accident. An existing hook keeps all its other lines: the block goes directly
after its `#!` line.

**What it does.** The block acts only on the first checkout of a new worktree (previous HEAD all zeros, flag `1`), so
an ordinary branch switch costs one shell test. It then starts `kyber-weave doctor --post-checkout .` in the
background, with every stream redirected. It never fails a checkout, and it does nothing when `kyber-weave` is not on
`PATH`.

**The check: `githook.post-checkout`.** It is reported only for a repository with governed docs. It never changes the
exit code. A problem is a passing check whose reason starts with `warning:`:

| Reason starts with | Meaning | Fix |
|---|---|---|
| `installed and current`, `installed the post-checkout hook`, `installed:`, `not installed:`, `removed` | The hook matches the setting | None |
| `warning: the post-checkout hook is missing its kyber-weave block` (or out of date) | `--check` or `--provision` found no current block | `kyber-weave doctor --ensure .` |
| `warning: ... is not a POSIX shell script` | The `#!` names something other than sh, bash, dash, ksh or zsh | Add the block by hand, or remove the hook |
| `warning: ... has no #! line` | The hook has no shebang | Add a shell `#!` line, then `doctor --ensure` |
| `warning: ... uses CRLF line endings` | Windows line endings | Convert to LF, then `doctor --ensure` |
| `warning: ... exists without the executable bit` | Unix only; the hook is disabled, so kyber-weave will not enable it | Add the executable bit or remove the hook, then `doctor --ensure` |
| (none) | A partial block: a begin marker with no end marker, or the reverse | Nothing to do. `doctor --ensure` removes the stray marker and the block lines after it that are still its own, keeps every other line, and writes one whole block. |
| `warning: core.hooksPath points at ... which git tracks` | A tracked hooks folder | Point `core.hooksPath` outside the repository, or untrack the folder |
| `warning: ... still has the kyber-weave block but worktree-index-hook is false` | Switched off, but the block remains | `kyber-weave doctor --ensure .` |

**`--post-checkout` (the hook's own mode).** People do not run it; the hook does. It indexes the tree it is given
with the same guard and lock as `--ensure`, and it differs in three ways. It skips the docs-server and Copilot
probes. It restores only the tracked files that `codegraph init` writes. The list is kept in code and is empty today, because
a measured `codegraph init .` modifies no tracked file. So an edit made in the new tree while the run is going is
never reverted. Any other newly changed file is
named in a passing `codegraph.restore` line as left alone. It exits `0` when the index is ready, the repository has
no governed docs, or `worktree-index-hook: false` is set; `1` when the index failed; `3` when `codegraph` is not on
`PATH`; and `2` when the path is not inside a git repository. Combined with `--check`, `--provision` or `--ensure` it
exits `2`.

**The log.** Each background run appends to `kyber-weave-index.log` in the new worktree's own git directory. The file
is never truncated, so it grows with each run. Read it with:

```bash
cat "$(git rev-parse --absolute-git-dir)/kyber-weave-index.log"
```

It holds the `PASS` and `FAIL` lines of the run. If git cannot say where the git directory is, the output is discarded.

**Windows.** Git for Windows runs the same block through its bundled shell. This has not been verified live on
Windows. If `kyber-weave` is not found on `PATH` there, the block does nothing, which is the safe failure.

**Removing it.** Set `worktree-index-hook: false` in `.kyber-weave/kyber-weave.yml`, then run
`kyber-weave doctor --ensure .`. The block is taken out and the other lines of the hook are kept; the file is deleted
when only its `#!` line is left. You can also delete the lines between `# >>> kyber-weave worktree index >>>` and
`# <<< kyber-weave worktree index <<<` by hand. Doing neither leaves the hook running on every new worktree.

### Provisioning rules

Provisioning is the `--provision` mode. A bare run provisions only in an interactive terminal with
`CI` unset (see the [command surface](../cli-command-surface.md#doctor--repository-documentation-tooling)).

- **Repository-local only.** The root is `git rev-parse --show-toplevel` of the given path. A run
  never provisions `$HOME`, and it never provisions another checkout.
- **Init when absent, sync otherwise.** When `.codegraph/` is absent, the run executes
  `codegraph init .`. When it exists, the run executes `codegraph sync .`, which brings an
  existing index up to date; both run with the root as the working directory. `codegraph index .`
  is the **repair** step, never the routine one: it rebuilds the whole index for no new
  information, and `--ensure` is the only mode that runs it, once, after a failed step. A
  `.codegraph/` folder that was never initialised, such as one holding only a committed
  `.gitignore`, makes `sync` refuse, so the run executes `codegraph init .` instead and reports
  that command's result.
- **Bounded.** Each child is bounded by `--index-timeout` (default 600 seconds). When the bound is
  reached, the child's process tree is killed and the check fails as `codegraph.index`.
- **Never delete.** Doctor never removes `.codegraph/`. The only file it creates and removes is a
  write-probe inside that directory, used to test writability. A directory that is corrupt, not
  writable, or owned by another user fails with `git clean -fdx -- .codegraph and run kyber-weave
  doctor`. That removes only untracked and ignored files, so a committed `.codegraph/.gitignore`
  survives. A person makes the removal.
- **Never install the binary.** See [Nothing installs CodeGraph](#nothing-installs-codegraph).
- **Lock file in the OS temp directory.** Concurrent provisioning runs for one repository are
  serialised by a lock file named `kyber-weave-codegraph-<hash>.lock` in the OS temporary directory,
  where the hash is a prefix of the SHA-256 of the root path. A second run waits up to
  `--index-timeout` for the lock. If it still cannot take the lock, it fails as `codegraph.index busy`.
  The lock file is not removed after a run, so a later run finds it unlocked.
- **Daemon left alone.** Doctor does not stop or restart a running CodeGraph daemon. `index` runs
  alongside it.

`docs init --provision` runs the same provisioning against the git root of the scaffolded path. It
does not change the default: without the flag, `docs init` starts no process.

## Git worktrees and MCP binding

When tasks execute concurrently in isolated git worktrees, MCP servers such as `kyber-weave-mcp`
and `codegraph` must follow the **worktree MCP canonical binding** protocol:

- **Canonical binding and argument splicing**: Worktree configurations must bind MCP servers to the
  canonical repository root rather than ephemeral worktree directories. The configuration helper
  splices `--repo-root` and `--path` to absolute canonical paths.
- **Tracked configuration restoration**: Worktree setup must use isolated per-task scratch paths for
  every tracked MCP configuration it touches; never rewrite a tracked configuration in place. If one
  is touched anyway, restore it (`git checkout -- <path>`) before integration. The project-scope
  files are `.mcp.json`, `.factory/mcp.json`, `.cursor/mcp.json`, `.vscode/mcp.json`,
  `.agents/mcp_config.json`, `opencode.json`, `zcode.json`, `.zcode/config.json`, and `kilo.jsonc`;
  a harness with no project-scope file has nothing to restore.
- **Symlink prohibition**: Worktree setup protocols strictly prohibit .codegraph symlinks.
  Never symlink `.codegraph/` into a worktree; background file watchers and concurrent writes can
  corrupt the canonical database or trigger cross-process lock contention. Worktrees must fail closed
  if a `.codegraph` symbolic link or reparse point is detected.
- **A worktree does not share the canonical checkout's index.** Every new worktree indexes itself
  with `kyber-weave doctor --ensure`, at about 8 seconds and about 223 MB on disk until cleanup
  removes it. This replaces the earlier rule that a worktree indexed itself only when a task's
  acceptance criteria asked for it: the shared index was measured returning the wrong code — a
  `codegraph explore "StoredDeploymentIdentity"` found a method added on the worktree's branch in
  0.37 s from the worktree, and did not find it from the canonical checkout, whose index belonged
  to another branch. A guarantee that the index answers for the tree the worker is in costs disk,
  not correctness.
- **Work in the canonical checkout when you can.** Branching in place on a clean, idle checkout keeps
  the server, the architect and the workers on one revision. A worktree is for a dirty or busy
  checkout and for parallel workers. The run-start command that does this automatically arrives with
  the run-start surface and is not in this repository yet; until it ships, branching in place is the
  operator's choice.

### The worktree limitation, per role

The MCP servers stay bound to the canonical checkout on purpose, so a worker in a worktree on
another branch can get answers about the wrong code. `branch=` in the provenance line is what
makes that visible. This is the rule for each role
([ADR 0061](../adr/0061-code-editing-runs-stop-on-unusable-tools.md)):

| Role | Code questions | Docs and standards | If the check fails |
|---|---|---|---|
| Developers, `test-dev`, `docs-dev`, investigator (shell) | `codegraph explore` from the worktree's shell, against its own index. Never the MCP `codegraph_explore` when `branch=` differs. | `kw-docs` for standards, which rarely differ by branch. Direct read of the worktree's copy for any document being edited. | `doctor --ensure` follows `stop-on-tool-failure` |
| `code-reviewer` (shell) | Same, before any lens | CLI `docs validate` and `docs drift` in the worktree | Same |
| `review-lens`, `review-triage` (no shell) | The packet carries `.agent_scratchpad/<task-id>/review-diff.patch` and, when the run uses one, the absolute worktree path. The lens passes that path as `codegraph_explore`'s `projectPath`, which answers from the worktree's own index; with no worktree named it uses the server's default project. Direct reads only when that call cannot answer for the packet's branch: an error, a not-indexed reply, or a provenance `branch=` that differs, naming the mode as `projectPath: error`, `projectPath: not-indexed` or `projectPath: wrong-branch <got> vs <wanted>` in the direct-read reason. They read the worktree the packet names, and the checkout itself only on a run that names no worktree. | `kw-docs` for standards | A lens that cannot read reports `SKIPPED` with the reason |
| `architect` (shell) | The packet carries `RUN_BASE`, `RUN_BRANCH` and, when present, `WORKTREE: <absolute path>`. If provenance `rev=` and `branch=` match, normal discovery. If not, `codegraph explore "…"` from the worktree's shell against its own index (`kyber-weave doctor --ensure` creates it), counted as a CLI discovery call. | The server cannot see a plan written into a worktree, so the architect writes to `<WORKTREE>/docs/plans/…` and runs `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .` there. Where a harness gives it no shell it returns `STATUS: BLOCKED` naming "server root differs: run the docs checks in the worktree" and is re-invoked with `DOCS_CHECKS: <results>`. | `BLOCKED` when no worktree path was given |

**A session started inside a worktree is already correct**, because `--repo-root .` resolves
there; in supervised mode, restart inside the worktree instead of fighting the binding.
**A worktree created after the server starts is outside its reach**: the server refreshes the
index of the root it is bound to, and nothing else.

## Troubleshooting

| Symptom | Cause |
|---|---|
| The server never starts | `kyber-weave-mcp` is not on the PATH the client inherits — use an absolute path |
| The client lists no tools at all | Wrong file or wrong key for that client — check the table in [Wiring it up](#wiring-it-up) |
| A Factory Task-dispatched droid lists no MCP tools | `squad install` and `squad update` print `factory-mcp-not-configured` when no Factory config file defines a server the droids are allowed. Check the droid's own `mcpServers` whitelist in `.factory/droids/<name>.md` **and** the server entry in `.factory/mcp.json` or `~/.factory/mcp.json`. The app-level file alone is not enough: an empty or `[]` list in the droid file excludes every server regardless of what the session has, and an omitted key would inherit rather than exclude. Every droid the shipped squad renders is granted `kw-docs` — `conductor` included — so a `[]` no longer means "this is the orchestrator": it means the droid's capability profile grants no read entitlement, or the squad declares no servers ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md)). Droid-level visibility is Squad's whitelist, not your client config — see [Factory](#factory) |
| A ZCode agent lists no MCP tools, and `squad doctor` fails the install | ZCode expands no wildcard and `toolchain.yml`'s `required-mcp-tools` are hard connected-server requirements for every agent, `conductor` included. Declare the `kw-docs` server in the ZCode MCP configuration, or the install does not pass |
| A Kilo agent has no `permission` key, or an MCP call prompts instead of running | The key is emitted only for an agent entitled to `kw-docs`, and `allow` is an approval rather than a visibility grant — check the agent's capability profile, then the `mcp` key in `kilo.jsonc`. An agent with no key is outside the entitlement by design; a subagent that prompts will fail closed. A `kw_docs_*` key would match nothing, so check the hyphen. See [Kilo](#kilo) |
| An Antigravity agent lists no MCP tools | Check the agent file's `mcpServers` entry and the workspace `.agents/mcp_config.json`, then the client's `mcp(server/*)` policy patterns — MCP tools default to Ask there. Adding an MCP tool name to `tools:` does not help and makes the executor construction fail with `unknown component` on stderr. See [Antigravity](#antigravity) |
| A Pi subagent lists no MCP tools | Check the derived `ext:pi-mcp-adapter/mcp__kw_docs__…` entries in the agent file, the adapter version (the per-tool names need `pi-mcp-adapter` 6.0.0 or later), and the adapter's own server configuration plus its first-use trust approval. A lowered primary agent is a skill and carries no overlay at all. See [Pi](#pi) |
| A droid lists no MCP tools on a target whose session has them | On Factory the droid's own `mcpServers` value governs; on Cursor, Codex, and Warp nothing per-agent governs it at all, because reach is inherited from the session. Identify which case you are in before editing a rendered file — install and update own those bytes |
| Every query is a miss | Wrong repo root, or `docs-root` in [config](../configuration.md) names a tree the documents are not in — check stderr for `KW-CONFIG-001` |
| "no CodeGraph index was readable" | No `.codegraph/codegraph.db`, or `sqlite3` missing from PATH |
| Joins show `(unresolved)` | The symbol is in `code-refs` but not in the index — run [`docs drift`](governance.md) |
| Analysis warns that the cache is unsafe | Run `docs init` to merge `.kyber-weave/.gitignore` with `cache/`; until then deterministic/lexical analysis continues and no document text is sent for embeddings |
| `docs_look_up_glossary_term_definition` returns no senses | The configured glossary is absent, the term is not present, or its spelling differs; use `docs glossary .` to preview proposals |
| `docs_get_coding_standard_for_technology` reports no standard for a technology | The key names no technology this corpus declares a standard for — check the Config Reg block of the repository root [`AGENTS.md`](../../AGENTS.md) and `ontology.technologies` in [`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml). Spelling alone cannot be it: the match is case-insensitive, so a miss means the technology is undeclared, or is declared with no standard scaffolded at `docs/standards/<technology>/README.md` |
| Client reports a protocol error | Something wrote to stdout; check that you launched `kyber-weave-mcp`, not `kyber-weave` |
| Answers look right but named files do not exist, or the document count is wrong | A same-named server is bound to a different checkout — compare the `provenance: repo=` line to `git rev-parse --git-common-dir`, call `docs_get_source_repository_and_revision`, and pin the server with `--expect-root`. A matching `repo=` with a different `root=` is another worktree of your own repository, not a foreign one |
| A tool answers `STATUS: TOOL_FAILURE`, or carries a `warning: codegraph is unavailable` line | The repository declares `codegraph` under `mcp.required-servers` and the server could not read the index: the `fix:` line names the repair, usually `kyber-weave doctor --ensure .`. Under the default stop rule the tool refuses to answer with joins missing; `stop-on-tool-failure: false` turns the same detail into the warning. See [the refresh rules](#the-server-refreshes-codegraph-and-its-tools-fail-when-it-cannot) |
| A tool carries `note: codegraph refresh skipped` | Another process holds the index lock and is refreshing it — a worker's `doctor --ensure`, or the CodeGraph daemon. Not a failure: the answer comes from the index as it stands, and the next call after the 60-second interval refreshes it |
| Piped requests exit 0 with nothing on stdout | A release up to `v0.1.7-rc.13` dropped replies once stdin closed — upgrade, or keep stdin open until the replies arrive. See [Closing stdin](#closing-stdin) |

## Related

- [Retrieval and ranking](retrieval.md) — how results are chosen and budgeted
- [Documentation analysis and review](analysis.md) — findings, review exchange, cache, and glossary
- [DocGraph architecture](architecture.md) — the index behind these tools
- [Installing Kyber-Weave](../install.md) — getting the binary on PATH
