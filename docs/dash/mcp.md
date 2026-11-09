---
id: dash/mcp
title: KyberDash MCP (kw-dash) — what it is and how to set it up
doc-type: onboarding
component: KyberDash
source-root: dash
owner: dpalfery
last-reviewed: 2026-11-09
status: current
---

# KyberDash MCP (`kw-dash`)

**KyberDash MCP** is a stdio Model Context Protocol (MCP) server that answers **read-only
analytics questions** against a **KyberDash engine that is already running**. You register it
in a client under the key **`kw-dash`**, launch it as **`kyberdash mcp`**, and it reports
`serverInfo` as **`kyberdash-analytics`**. It is **not** the Kyber-Weave documentation server
([`kw-docs`](../docgraph/mcp-runbook.md)), not the arbiter ([`kw-arbiter`](../kyber-arbiter/README.md)),
and not CodeGraph.

The eight `dash_*` tools query the same engine and `/api/kyber/analytics/*` routes as the web
dashboard, with an attach-only lifecycle: the adapter never starts, stops, or migrates the
engine. For the full tool contract, limits, and envelopes, see [Conversational analytics over
MCP](architecture.md#conversational-analytics-over-mcp-kyberdash-mcp) in the architecture
guide and [§2j Conversational analytics over MCP](runbook.md#2j-conversational-analytics-over-mcp-kyberdash-mcp)
in the runbook.

---

## How `kw-dash` differs from `kw-docs` and `kw-arbiter`

| Client key | Launch argv | What it is for |
|---|---|---|
| **`kw-docs`** | `kyber-weave-mcp --repo-root <repo>` | Governed **documentation** for a repository — guides, runbooks, ADRs, standards. Ships with Kyber-Weave (`provenance: first-party`). |
| **`kw-dash`** | `kyberdash mcp` | **Read-only KyberDash analytics** over a running local engine — sessions, costs, findings, outcomes. Installed with the `kyberdash` binary (`provenance: third-party` in host config). |
| **`kw-arbiter`** | `kyber-weave-arbiter serve` | **Delegation and review gates** for Kyber-Squad (when enabled). |

`docs init` scaffolds only **`kw-docs`** into `.kyber-weave/kyber-weave.yml`. Adding **`kw-dash`**
is optional: declare it when agent instructions should rely on KyberDash analytics from this
repository's checked-in harness files. KyberDash is **not** on the Kyber-Squad MCP roster — no
`squad install` step writes `kw-dash` for you.

---

## Prerequisites

1. **Install `kyberdash`** — same installer as Kyber-Weave ([adoption guide](onboarding.md#install)).
2. **Start the engine before the MCP adapter attaches:**

   ```bash
   kyberdash web --no-open
   ```

   The MCP process only attaches; it does not start `web`.

3. **Know what `engine_unavailable` means.** When no running engine is reachable (missing or
   stale `server.json` receipt, wrong port, or engine stopped), tools answer with
   `engine_unavailable` and tell you to run `kyberdash web --no-open`. Only
   `dash_list_available_analytics_operations` is designed to surface that state without a
   corpus scan.

---

## Launch and client key

| Item | Value |
|---|---|
| Client configuration key | `kw-dash` (formerly `kyberdash`) |
| Launch | `kyberdash mcp` — program `kyberdash`, argument `mcp` |
| Transport | stdio JSON-RPC |
| Reported server name | `kyberdash-analytics` |
| Engine discovery | Default: `server.json` in the standard KyberDash config directory; override with `--server-url http://127.0.0.1:<port>` or `--state-dir <dir>` |

```bash
kyberdash web --no-open          # engine first (or attach to one already running)
kyberdash mcp                    # then connect the client
kyberdash mcp --server-url http://127.0.0.1:4747   # non-default port
```

No KyberDash command reads or writes a harness user-scope MCP file. Setup is always **your**
edit plus a **harness restart** after you save.

---

## Declare `kw-dash` in the repository

Optional: record the server in `.kyber-weave/kyber-weave.yml` so `docs init` can check
repository-scope harness files and `docs mcp-wire` can publish the entry for named targets.

```yaml
mcp:
  required-servers:
    kw-dash:
      command: [kyberdash, mcp]
      provenance: third-party
      level: recommended
      install: curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh -s -- --prerelease
```

**`provenance: third-party` is required** for `kyberdash`. Host configuration accepts
`first-party` only when the program is `kyber-weave-mcp`; anything else fails load as
`KW-CONFIG-001`. See [MCP servers](../configuration.md#mcp-servers) in the configuration
reference.

Wire repository-scope files after you declare the server:

```bash
kyber-weave docs mcp-wire . --target cursor,claude-code,factory,opencode
```

Flags, refusals, dry-run, and symlink rules are in the [MCP runbook — checking and
wiring](../docgraph/mcp-runbook.md#checking-and-wiring). `mcp-wire` writes **repository-scope**
files only; it does not read or write user-scope paths such as `~/.cursor/mcp.json`.

You can declare **`kw-docs`** and **`kw-dash`** together — they serve different jobs and use
different programs. The [DocGraph MCP runbook](../docgraph/mcp-runbook.md) documents
`kw-docs`; this page is the setup home for `kw-dash`.

---

## User-scope setup by harness

For every harness below, add **one server entry** with key **`kw-dash`** and launch
**`kyberdash mcp`**. Restart the harness after saving. User-scope files are edited **by
hand**; Kyber-Weave does not write them (Codex, ZCode, and Copilot repository settings are
**snippet-only** on `mcp-wire` — reported, never written).

| Harness | Where you edit (user scope) | Notes |
|---|---|---|
| **Claude Code** | `~/.claude.json` via `claude mcp` | `claude mcp add --scope user kw-dash -- kyberdash mcp` |
| **Cursor** | `~/.cursor/mcp.json` | JSON `mcpServers.kw-dash` — see below |
| **Codex CLI** | `~/.codex/config.toml` | TOML `[mcp_servers.kw-dash]` — absolute paths if not in the repo; see [Codex](../docgraph/mcp-runbook.md#codex-cli) |
| **OpenCode** | `~/.config/opencode/opencode.json` | `mcp.kw-dash` with `command` as a single argv array — see [opencode](../docgraph/mcp-runbook.md#opencode) |
| **Factory** | `~/.factory/mcp.json` | Same JSON shape as Claude Code `mcpServers` — see [Factory](../docgraph/mcp-runbook.md#factory) |
| **Copilot** | `~/.copilot/mcp-config.json` | User-scope MCP config; repository settings are snippet-only on wire |
| **Kilo** | `kilo.jsonc` `mcp` key | Server entry plus per-agent `kw-dash_*: allow` in permissions where needed — see [Kilo](../docgraph/mcp-runbook.md#kilo) |
| **ZCode** | ZCode user / project MCP storage | Declare key `kw-dash` with argv `kyberdash`, `mcp`; body shape is pinned in [ZCode](../docgraph/mcp-runbook.md#zcode) |

**Cursor example** (`~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "kw-dash": {
      "command": "kyberdash",
      "args": ["mcp"]
    }
  }
}
```

With a non-default engine port: `"args": ["mcp", "--server-url", "http://127.0.0.1:<port>"]`.

**Claude Code project scope** uses a repository-root `.mcp.json` with the same JSON shape when
you want the whole team to share the declaration; user scope uses `claude mcp` as above. Tool
grants follow the client: Claude Code `mcp__kw-dash__<tool>`, OpenCode and Kilo
`kw-dash_<tool>`, Copilot `kw-dash/<tool>`.

The install guide lists the same user-scope locations in [Renamed MCP servers and
tools](../install.md#renamed-mcp-servers-and-tools).

---

## Rename from `kyberdash` to `kw-dash`

Entries added before the purpose rename may still use the old key `kyberdash`. The executable
and argv stay **`kyberdash mcp`**.

- **Repository-scope harness files:** `kyber-weave docs mcp-migrate .` to preview, then
  `kyber-weave docs mcp-migrate . --target <harnesses>`. See [docs
  mcp-migrate](../docgraph/mcp-runbook.md#docs-mcp-migrate) — do not duplicate that procedure
  here.
- **Claude Code user scope (example):**

  ```bash
  claude mcp get kyberdash
  claude mcp remove kyberdash --scope user
  claude mcp add --scope user kw-dash -- kyberdash mcp
  ```

After any rename, restart the harness and re-check tool allow-lists that referenced the old
server or tool names.

---

## Related

- [KyberDash adoption guide](onboarding.md) — install, update, and shared settings
- [KyberDash MCP in architecture](architecture.md#conversational-analytics-over-mcp-kyberdash-mcp) — tool contract and limits
- [Running the DocGraph MCP server (`kw-docs`)](../docgraph/mcp-runbook.md)
- [Host configuration — MCP servers](../configuration.md#mcp-servers)
- [Install — renamed MCP servers and tools](../install.md#renamed-mcp-servers-and-tools)
