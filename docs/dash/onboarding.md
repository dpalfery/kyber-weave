---
id: dash/onboarding
title: KyberDash adoption guide — install, update, and configure
doc-type: onboarding
component: KyberDash
source-root: dash
owner: dpalfery
last-reviewed: 2026-10-29
status: current
---

# KyberDash adoption guide — install, update, and configure

KyberDash is the local telemetry and context-tuning product in this repository: one OTLP
receiver and one canonical store (`~/.kyberdash/canon.db`) feeding three surfaces — the
`kyberdash` terminal report, the browser dashboard (`kyberdash web`), and a macOS/Windows
tray. Nothing leaves your machine; the receiver binds loopback. This guide takes you from
nothing installed to a configured dashboard. For building KyberDash from source, running the
test suites, and operating the store, read the [runbook](runbook.md) instead.

The tray and the web dashboard are display layers over the shared engine (the `kyberdash` CLI
and the `kyberdash web` server); all scheduling, import, clean, pause, the shared settings,
and the OTLP receiver live in that engine, never in a surface. See
[`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md) and
[ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md) for the boundary.

---

## Install

The `kyberdash` binary is installed alongside the Kyber-Weave CLI by the same one-line
installer. It is a self-contained single executable: no Node toolchain and no checkout are
needed to run it.

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
  | sh -s -- --prerelease
```

`--prerelease` is required until `0.1.7` is promoted to stable, because `kyberdash-<rid>`
assets first appear in `0.1.7-rc.9` and the installer skips KyberDash on any release below
that floor. Verify the download against the published checksums before running it — the same
procedure as the CLI, documented in
[Verifying a download](../install.md#verifying-a-download). For the full set of installer
flags, the Node SEA RID names, and how `kyber-weave update` treats an installed `kyberdash`,
see [KyberDash and the version floor](../install.md#kyberdash-and-the-version-floor).

To skip KyberDash and install only the CLI, pass `--no-kyberdash`.

### First run

The released binary carries the three engine entry points and the embedded web dashboard:

```bash
kyberdash kyber otel        # OTLP receiver on 127.0.0.1:4318
kyberdash web               # browser dashboard on http://127.0.0.1:4747
kyberdash report            # terminal report, formatted from the canonical store
```

`kyberdash web` serves the dashboard from a packed asset inside the binary, so it needs no
`npm` and no checkout. `KYBERDASH_DASH_DIR` overrides it with a directory of your own build.

### OpenCode capture plugin

`install.sh` does not install the OpenCode capture plugin. Squad installs it. Run
`kyber-weave squad install --global` or `kyber-weave squad update --global` with the OpenCode target,
and it is written to OpenCode's global plugin directory. Pass `--no-capture-plugin` to skip it. Then
restart OpenCode. Content capture (system prompt, tool definitions, skills) stays off until you turn
OpenCode on in the Settings drawer. The [runbook](runbook.md#harness-opentelemetry-capture) describes
the consent file and the restart steps.

### Install the tray

The tray ships as its own release asset from `0.1.7-rc.13`. On macOS and Windows:

```bash
kyberdash menubar
```

`menubar` downloads the tray, checks its SHA-256, and on macOS also checks its code
signature, team id, and Gatekeeper acceptance before installing it. On macOS the installer
can do this in the same run with `install.sh --with-menubar`. On Linux `kyberdash menubar`
exits `1`: the tray ships for macOS and Windows only.

---

## Update

One command updates KyberDash. Run `kyber-weave update`, and no other command is needed:

- It replaces the `kyberdash` binary beside the CLI, along with the CLI and MCP server.
- It restarts the KyberDash engine. A running engine of an older version is replaced by the new one,
  and this also runs when the release is unchanged. Nothing starts if no engine is running.
- It relaunches the tray when a tray is installed. The tray is quit, replaced, and relaunched, and
  the previous app is restored if a step fails.
- It upgrades the OpenCode capture plugin, but only if that plugin is already installed.

When the plugin file changed, the output says to restart OpenCode. KyberDash cannot restart your
OpenCode process. The engine then re-projects the store on its own start, so you do not run
`kyber build` after an update.

To update the tray alone, use `kyberdash menubar --update`.

Flags: `--no-restart` replaces the binaries and leaves the engine and tray running as they are.
`--no-kyberdash`, `--no-mcp`, and `--no-menubar` narrow what the command touches. `kyber-weave update --help`
lists them all.

---

## Configure

KyberDash's configure surface is a small set of **shared settings** stored in the `canon.db`
metadata, so every surface sees the same answer. Set them from the shipped binary:

```bash
kyberdash dash settings show              # print every setting and its value
kyberdash dash settings show --json       # the same, as a JSON object
kyberdash dash settings set folder_import.scheduled on
kyberdash dash settings set jobs.paused off
kyberdash dash settings set jobs.refresh_cadence_minutes 10
kyberdash dash settings set dashboard.port 4848
```

Both the short name and the `settings.`-prefixed key are accepted on input; `show` reports the
short name. A value that is out of range or of the wrong type is refused with **exit code 2**
before the store is opened, so a refused write leaves the filesystem as it found it.

| Setting (short form) | Default | Effect |
|---|---|---|
| `folder_import.scheduled` | `off` | Whether **scheduled**, tray, and web refreshes read local harness folders. A terminal `dash refresh` and `dash import-history` are never gated by it — a person asking for their own history always gets it. |
| `jobs.paused` | `off` | Pauses the engine's scheduled jobs. Manual refresh, import, and clean, the OTLP receiver's ingest, and the retention purge all carry on. |
| `jobs.refresh_cadence_minutes` | `5` | How often the job host looks for work; a whole number of minutes, `1` to `1440`. |
| `receiver.hosted` | `off` | Whether the `kyberdash web` server runs the OTLP receiver child. |
| `dashboard.port` | `4747` | The loopback port `kyberdash web` listens on. Applies on the next engine restart, not to the running server. |

The same five settings are the dashboard's **Settings** drawer and, over HTTP,
`GET /api/kyber/settings` and `PUT /api/kyber/settings`. A change made in one place is the
change every surface sees.

`dashboard.port` takes effect on the next engine start. Startup precedence is an explicit
`--port` for that run, then the persisted `dashboard.port`, then `4747`. When the configured
port is already taken, the start is refused with an error naming the port and the remedy
rather than sliding onto a random port.

### Pricing and per-tool options

The shared settings cover behavior, not prices. Model pricing is bundled with the release and
refreshed by maintainers. Where a source needs a rate that is not bundled — Devin ACU pricing,
today — set it in `~/.kyberdash/config.json` (for example `devin.acuUsdRate`); until it is
set, Devin cost is reported as unpriced rather than as a measured zero. See the
[telemetry inventory](telemetry-inventory.md) for which sources require a rate.

---

## Ask KyberDash from an MCP client

The same engine answers conversational analytics over MCP. `kyberdash mcp` exposes eight
read-only tools — `dash_status`, `dash_catalog`, `dash_query`, `dash_search_sessions`,
`dash_inspect`, `dash_findings`, `dash_compare`, `dash_outcomes` — over stdio, computed
by the shared engine behind `/api/kyber/analytics/*`. See
[the architecture](architecture.md#conversational-analytics-over-mcp-kyberdash-mcp) for
the tool contract, the limits, and the content/outcome caveats.

### 1. Start the engine first

The MCP adapter attaches to a running engine and never starts one. Start (or attach to)
it, then connect the client:

```bash
kyberdash web --no-open     # start the engine, or attach to the one running (exit 0 either way)
kyberdash mcp               # serve the eight tools over stdio
```

`kyberdash mcp` discovers the engine through its `server.json` receipt. Two flags change
only where it looks — an explicit `--server-url http://127.0.0.1:<port>` wins, and
`--state-dir <dir>` reads an existing receipt from another directory. There is no `--db`
flag: the running engine owns the datastore choice. Only literal loopback origins
(`127.0.0.1` or `[::1]`, with an explicit port) are contacted; anything else is refused.
Stopping the client (EOF, SIGTERM, or closing its read end) ends the MCP session within
a bounded deadline and never stops the shared engine. A failed `server.connect`
releases the keep-alive, signal handlers, and stdout guard instead of leaking them.

### 2. Register the client yourself

KyberDash never writes a client's configuration: no `mcp` subcommand edits user-level
client config, and setup means pasting one entry into the client's own config by hand.
The shape below is an example — consult the client's documentation for where it keeps
MCP servers:

```json
{
  "mcpServers": {
    "kyberdash": {
      "command": "kyberdash",
      "args": ["mcp"]
    }
  }
}
```

With an engine on a non-default port, use `"args": ["mcp", "--server-url",
"http://127.0.0.1:<port>"]` instead. When the client lists tools, it must see exactly
the eight `dash_*` names above; `dash_status` with no engine attached answers
`engine_unavailable` and tells you to run `kyberdash web --no-open`.

### 3. What is (and is not) validated

The stdio shape is verified on every release path: `dash/scripts/sea-mcp-smoke.mjs`
initializes over the SDK client, lists exactly the eight tools, calls `dash_status`
against an isolated fixture engine, then observes a clean exit — run from both the npm
build and the staged single-executable binary with no checkout and no access to your
state (`scripts/release-local.sh` and `.github/workflows/release.yml` wire it alongside
the web smoke). What that proves is the protocol, the tool list, and the attach-only
boundary — not that every host client renders them.

> **[NOT LIVE-VERIFIED]** No host-client integration (Claude Code/Desktop, Cursor,
> Copilot, or any other MCP host) has been observed against this surface. The SDK smoke
> above is necessary but does not prove every host client works. If you connect one,
> record the product and client versions, one successful question, one
> unavailable-data answer, and the clean disconnect — and never authorize a real-data
> analysis merely to satisfy that check.

## Where KyberDash keeps its data

| Path | Holds |
|---|---|
| `~/.kyberdash/canon.db` | The single canonical store: raw records and every derived table. Rebuildable without re-ingesting. |
| `~/.kyberdash/config.json` | Per-tool options that are not shared settings, such as a pricing rate. |
| `~/.kyberdash/tray.json` | The tray's own record, including the `kyberdashPath` it launches. |

Content older than 14 days is emptied from the store on refresh, and the raw records are
never deleted — see [ADR 0018](../adr/0018-kyberdash-content-retention-purge.md).

---

## Next steps

- [KyberDash overview](README.md) — the value proposition and the three surfaces
- [Architecture](architecture.md) — ingest, canonical store, analyses, and the display-layer boundary
- [Telemetry inventory](telemetry-inventory.md) — what each harness actually makes available
- [Runbook](runbook.md) — building from source, operating the store, and the test suites
- [Requirements](requirements.md) — the numbered requirements contract
