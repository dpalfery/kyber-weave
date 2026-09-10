---
id: install
title: Installing Kyber-Weave
doc-type: runbook
status: current
component: Distribution
owner: dpalfery
last-reviewed: 2026-09-10
---

# Installing Kyber-Weave

Kyber-Weave ships as **self-contained platform binaries**. There is no .NET runtime to
install and no SDK requirement.

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
```

This installs the **latest stable release tag** to `~/.local/bin` without sudo, placing up to
three binaries on your PATH:

| Binary | Purpose |
|---|---|
| `kyber-weave` | The CLI — [docs](docgraph/governance.md), [skill](context-hygiene/skills.md), [agent](context-hygiene/agents.md), and [squad](kyber-squad/onboarding.md) commands |
| `kyber-weave-mcp` | The [MCP server](docgraph/mcp-runbook.md) that serves documentation to an agent |
| `kyberdash` | [KyberDash](dash/README.md) — token, cost, and context observability across agentic coding harnesses ([runbook](dash/runbook.md)) |

`kyberdash` installs only from releases that publish it — see
[KyberDash and the version floor](#kyberdash-and-the-version-floor).

Make sure `~/.local/bin` is on your PATH:

```bash
export PATH="$HOME/.local/bin:$PATH"
```

## Options

| Flag | Environment variable | Effect |
|---|---|---|
| `--version <v>` | `KYBER_WEAVE_VERSION` | Install a specific release (e.g. `0.1.1` or `0.2.0-rc.1`) instead of latest |
| `--prerelease` | `KYBER_WEAVE_PRERELEASE=1` | Resolve and install candidate/pre-release builds (e.g. `v*-rc.*`, `v*-dev.*`) |
| `--install-dir <d>` | `KYBER_WEAVE_INSTALL_DIR` | Target directory (default `~/.local/bin`) |
| `--no-mcp` | `KYBER_WEAVE_NO_MCP=1` | CLI only; skip the MCP server |
| `--no-kyberdash` | `KYBER_WEAVE_NO_KYBERDASH=1` | CLI + MCP; skip the KyberDash binary |
| `--with-menubar` | `KYBER_WEAVE_WITH_MENUBAR=1` | macOS only; also install the signed menubar app to `~/Applications` after verifying its SHA-256 and code signature |

Pinning a specific version or install directory:

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
  | sh -s -- --version 0.1.1 --install-dir /usr/local/bin
```

Installing the latest pre-release (Release Candidate or development build):

```bash
# Using CLI flag
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
  | sh -s -- --prerelease

# Using environment variable
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
  | KYBER_WEAVE_PRERELEASE=1 sh
```

## What the script does

1. Detects your OS and architecture and picks the matching RID
2. Resolves the latest stable release tag (or newest pre-release tag when `--prerelease` is active), unless `--version` pinned an explicit version
3. Downloads `SHA256SUMS.txt` and each binary archive over HTTPS, following HTTPS-only redirects
4. **Verifies every binary against its published checksum** before installing
5. Extracts into the install directory

A checksum mismatch aborts the install. The script never needs sudo when installing to the
default location.

## KyberDash and the version floor

KyberDash ships as a Node single-executable, so its archives are keyed by Node's RID names —
`darwin-x64` and `darwin-arm64` where the .NET binaries use `osx-x64` and `osx-arm64`. The
script maps your platform to both names and verifies every archive against the same
`SHA256SUMS.txt`.

`kyberdash-<rid>` assets first appear in **0.1.7-rc.9**. Releases before that publish none, so
the script checks the resolved version against that floor and installs the CLI and MCP alone
when it is not met:

```
kyber-weave: release 0.1.6 predates KyberDash (first published in 0.1.7-rc.9); skipping kyberdash
```

This is why the floor exists rather than an unconditional download: `install.sh` is served
unversioned from the default branch and has to stay installable against every tag it can
resolve. All archives are verified before any binary is placed, so one missing asset would
otherwise abort the whole install and leave you with no CLI either.

Until 0.1.7 is promoted to stable, reach KyberDash with `--prerelease`:

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
  | sh -s -- --prerelease
```

`kyberdash --version` reports the KyberDash product version (for example `0.9.23`), not the
Kyber-Weave release tag it shipped in. The two version lines are independent.

## Supported platforms

`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` — and for KyberDash, the same platforms
under Node's names: `linux-x64`, `linux-arm64`, `darwin-x64`, `darwin-arm64`.

Windows binaries (`win-x64`) are published on the release, but the install script does not
handle `.zip` extraction — download the asset from
[Releases](https://github.com/dpalfery/kyber-weave/releases) and place it on your PATH
manually. After that, `kyber-weave update` replaces the Windows binaries in place.

## Verify

Verify the installation and output the build-stamped binary version:

```bash
kyber-weave --version
# or short flag
kyber-weave -v
```

Output example: `kyber-weave 0.1.0+714f187ab97d66e1199c33d5aaa0c9ab76ffae0f` or `kyber-weave 0.2.0-rc.1`.

Verify the MCP server binary version:

```bash
kyber-weave-mcp --version
# or short flag
kyber-weave-mcp -v
```

Verify KyberDash, when it was installed:

```bash
kyberdash --version
```

View CLI general help:

```bash
kyber-weave --help
```

## Updating

`kyber-weave update` replaces the running CLI, the sibling `kyber-weave-mcp`, and an
installed `kyberdash` in the same directory from GitHub Release assets, after verifying
SHA-256 against `SHA256SUMS.txt`. It is the self-update path for binaries installed by this
script (or placed from a Release by hand). It refuses `dotnet run` and `dotnet tool` installs.

```bash
kyber-weave update                    # latest stable Release
kyber-weave update --release-candidate  # newest listed Release, including -rc and -dev
kyber-weave update 0.2.0              # pin a tag (leading v is optional)
kyber-weave update --no-mcp           # CLI only
kyber-weave update --no-kyberdash     # leave an installed kyberdash unchanged
```

KyberDash is **replaced, never introduced**: update touches `kyberdash` only when it already
sits beside the CLI. `--no-kyberdash` at install time is an opt-out that update respects, and
a machine that installed before KyberDash existed never chose to run it. Both skips are
logged. To add it later, re-run `install.sh`. Updating to a release below the version floor
leaves `kyberdash` alone for the same reason the install skips it.

`--release-candidate` matches `install.sh --prerelease`: it reads the GitHub Releases
list (not `/releases/latest`) and takes the newest non-draft tag. Do not combine it with
a pinned version — pin the candidate tag instead (`kyber-weave update 0.2.0-rc.1`).

Global `kyber-weave --version` still prints the running binary; pinning is a positional
argument so the two do not collide.

## Then initialize your repository

```bash
kyber-weave docs init .
```

This scaffolds host config, the catalog, and the ontology reference; safely merges the
narrow `.kyber-weave/.gitignore` entry for local analysis cache state; and deploys the
`kyber-weave-docs` authoring skill via APM. Pass `--kyber-standards` to seed the full suite
of 10 Kyber Squad coding standards templates. It does not create an empty glossary. See
[Adopting DocGraph](docgraph/onboarding.md) for the whole path.

## External dependencies

Two features reach for host-owned tools. **Kyber-Weave installs neither of them** — it
detects them, uses them if present, and degrades with a clear message if not. Nothing is
installed on your machine behind your back.

| Tool | Needed by | Without it |
|---|---|---|
| [APM](https://microsoft.github.io/apm) | `docs init` skill deployment; `squad install` and `squad pack` target compilation | Docs corpus is still scaffolded; Squad commands report missing APM toolchain |
| CodeGraph + `sqlite3` | `docs drift`, `docs export-graph` | Everything else works, including all of [retrieval](docgraph/retrieval.md) |

### APM

The Agent Package Manager distributes the `kyber-weave-docs` authoring skill, resolves
the harness layout for each runtime, and powers target compilation for `kyber-weave squad`.
Install it once:

```bash
curl -sSL https://aka.ms/apm-unix | sh
```

Windows PowerShell: `irm https://aka.ms/apm-windows | iex`. Homebrew:
`brew install microsoft/apm/apm`. Verify with `apm --version`.

If you would rather not install it for docs, run `kyber-weave docs init . --no-skill` — the
scaffolding is the part that matters, and the skill can be added at any time.

### CodeGraph

[`docs drift`](docgraph/governance.md) resolves documented symbols against a **CodeGraph
index** at `.codegraph/codegraph.db`. CodeGraph is a separate, host-owned tool;
Kyber-Weave opens that index read-only and never creates or writes it. The `sqlite3` CLI
must also be on PATH.

## Installing in CI

See the [workflow runbook](ci-pipelines/workflows-runbook.md).

## From source

Requires .NET SDK 10 (pinned in `global.json`).

```bash
dotnet restore KyberWeave.sln
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```

## Related

- [Distribution and release flow](distribution.md) — how binaries are built and published
- [Deploying Kyber-Squad](kyber-squad/onboarding.md) — installing and managing multi-harness agent squads
- [Configuration](configuration.md) — adapting Kyber-Weave to your repository
