---
id: dash/onboarding
title: KyberDash adoption guide — install, update, and configure
doc-type: onboarding
component: KyberDash
source-root: dash
owner: dpalfery
last-reviewed: 2026-10-16
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

`kyber-weave update` updates an installed `kyberdash` along with the CLI and MCP server. When
a tray is installed, `kyber-weave update` hands off to `kyberdash menubar --update`, which
quits the running tray, replaces it, and relaunches it — restoring the previous app if any
step fails. You can also update the tray directly:

```bash
kyberdash menubar --update
```

The CLI-only update flags (`--no-kyberdash`, `--no-mcp`, `--no-menubar`) let you narrow what
`kyber-weave update` touches; `kyber-weave update --help` lists them.

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
