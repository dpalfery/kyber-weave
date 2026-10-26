---
name: kyber-weave-cli
description: "Use when installing, updating, or configuring the Kyber-Weave CLI or the KyberDash engine: canonical install, update, and configure commands for both products, with flags and exit codes. Do NOT use for authoring governed documents — that is `kyber-docs-authoring`."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Installing, updating, and configuring Kyber-Weave and KyberDash

Two products ship from this repository. **Kyber-Weave** is the governance CLI and its
sibling MCP server. **KyberDash** is the telemetry engine behind the terminal report, the
web dashboard, and the tray. Getting either one onto a machine, keeping it current, or
pointing it at a configuration is a set of commands, and those commands are here.

The canonical page is `docs/cli-command-surface.md` — `<docs-root>/cli-command-surface.md`
under the Config Reg, where `<docs-root>` resolves to `docs` in this repository. Read it
before running a verb this skill does not list. It is the authority for the complete flag
and exit-code contract; this skill is the entry point, not a second copy of that page.

If you are here to author or validate governed documents rather than run the products,
stop — that is the `kyber-docs-authoring` skill. This skill does not write documentation.

## Install

Both binaries install from the same one-liner on macOS and Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
```

It places `kyber-weave`, `kyber-weave-mcp`, and — from releases that publish it —
`kyberdash` in `~/.local/bin`. No .NET runtime and no sudo are needed. On Windows,
download the `.zip` archives from Releases, verify the SHA-256 hash, and extract them
onto `PATH`.

`install.sh` is the only supported user install path. npm, Homebrew, and `dotnet tool`
are **not** supported; do not offer them. See the install decision block and the
unsupported-channel note in `docs/install.md`, and the flag table at
`docs/cli-command-surface.md#installsh-flags`.

## Verify the download

```bash
sha256sum -c SHA256SUMS.txt          # Linux
shasum -a 256 -c SHA256SUMS.txt      # macOS
kyber-weave --version
kyberdash --version
```

The one-liner verifies the checksum itself. For a manual download see
`docs/cli-command-surface.md#verify-the-download` and `docs/install.md`.

## Update

`kyber-weave update` replaces the running Release binaries after SHA-256 verification:
the CLI, the sibling `kyber-weave-mcp`, an installed `kyberdash`, and an installed
KyberDash tray. Tray-only updates go through the KyberDash binary.

```bash
kyber-weave update                      # latest stable, all components
kyber-weave update --release-candidate  # newest release, including RC and -dev tags
kyber-weave update --no-kyberdash       # leave the KyberDash binary alone
kyber-weave update --no-menubar         # leave the tray alone
kyberdash menubar --update              # update the tray alone
```

`kyber-weave update` does not wrap `install.sh`, and it refuses `dotnet run` and
`dotnet tool` installs. Details: `docs/cli-command-surface.md#update` and the Updating
section of `docs/install.md`.

## Configure

Kyber-Weave reads `.kyber-weave/kyber-weave.yml` at the repository root. The `docs init`
verb scaffolds it along with the catalog and the ontology reference, then deploys the
authoring skill through APM.

```bash
kyber-weave docs init .                       # scaffold config, catalog, ontology; deploy the skill
kyber-weave docs init . --docs-root docs --target agent-skills
```

KyberDash shares one settings store across the CLI, the web dashboard, and the tray. Read
and write it with the shared settings verbs — never by editing the store directly.

```bash
kyberdash dash settings show
kyberdash dash settings show --json
kyberdash dash settings set folder_import.scheduled on
kyberdash dash settings set jobs.refresh_cadence_minutes 30
kyberdash dash settings set dashboard.port 4747
```

The Kyber-Weave key surface is in `docs/configuration.md`; the initialization walkthrough
is in `docs/docgraph/onboarding.md`. KyberDash install, update, and configure are in
`docs/dash/onboarding.md`. The configure flow for both products is at
`docs/cli-command-surface.md#configure`.

## Where the full surface lives

Every entry below is a section of `docs/cli-command-surface.md`. Follow the anchor rather
than re-deriving a flag.

| Need | Anchor |
|---|---|
| Canonical install, verify, update, and configure flows for both products | `docs/cli-command-surface.md#canonical-flows` |
| Copy-ready command sequences for an agent | `docs/cli-command-surface.md#command-sequences-for-agents` |
| Every `kyber-weave` branch, verb, and flag | `docs/cli-command-surface.md#kyber-weave-reference` |
| Every `kyberdash` verb and flag | `docs/cli-command-surface.md#kyberdash-reference` |
| Exit-code contract for both binaries | `docs/cli-command-surface.md#exit-codes` |
| `install.sh` flags and environment variables | `docs/cli-command-surface.md#installsh-flags` |

## Example

A user asks to move KyberDash to a different dashboard port. The answer is one verb, not
a file edit:

```bash
kyberdash dash settings set dashboard.port 4747
```

A user asks to take a new Kyber-Weave release without touching KyberDash:

```bash
kyber-weave update --no-kyberdash
```

## Never

- **ALWAYS** read the owning page before running a verb this skill does not list; do not
  invent a flag or a subcommand.
- **NEVER** offer npm, Homebrew, or `dotnet tool` as an install path for either product —
  `install.sh` is the only supported user path.
- **NEVER** edit KyberDash's settings store by hand; use `kyberdash dash settings set`.
- **NEVER** author, rewrite, or validate governed documentation from this skill — that is
  the `kyber-docs-authoring` skill.
