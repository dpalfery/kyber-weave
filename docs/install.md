---
id: install
title: Installing Kyber-Weave
doc-type: runbook
status: current
component: Distribution
owner: dpalfery
last-reviewed: 2026-08-15
---

# Installing Kyber-Weave

Kyber-Weave ships as **self-contained platform binaries**. There is no .NET runtime to
install and no SDK requirement.

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
```

This installs the **latest stable release tag** to `~/.local/bin` without sudo, placing two
binaries on your PATH:

| Binary | Purpose |
|---|---|
| `kyber-weave` | The CLI — [docs](docgraph/governance.md), [skill, and agent](context-hygiene/skills.md) gates |
| `kyber-weave-mcp` | The [MCP server](docgraph/mcp-runbook.md) that serves documentation to an agent |

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

## Supported platforms

`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`

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

View CLI general help:

```bash
kyber-weave --help
```

## Updating

`kyber-weave update` replaces the running CLI and the sibling `kyber-weave-mcp` in the
same directory from GitHub Release assets, after verifying SHA-256 against
`SHA256SUMS.txt`. It is the self-update path for binaries installed by this script (or
placed from a Release by hand). It refuses `dotnet run` and `dotnet tool` installs.

```bash
kyber-weave update                    # latest stable Release
kyber-weave update --release-candidate  # newest listed Release, including -rc and -dev
kyber-weave update 0.2.0              # pin a tag (leading v is optional)
kyber-weave update --no-mcp           # CLI only
```

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
`kyber-weave-docs` authoring skill via APM. It does not create an empty glossary. See
[Adopting DocGraph](docgraph/onboarding.md) for the whole path.

## External dependencies

Two features reach for host-owned tools. **Kyber-Weave installs neither of them** — it
detects them, uses them if present, and degrades with a clear message if not. Nothing is
installed on your machine behind your back.

| Tool | Needed by | Without it |
|---|---|---|
| [APM](https://microsoft.github.io/apm) | `docs init` deploying the authoring skill | The corpus is still scaffolded; the command prints the `apm install` line to run later |
| CodeGraph + `sqlite3` | `docs drift`, `docs export-graph` | Everything else works, including all of [retrieval](docgraph/retrieval.md) |

### APM

The Agent Package Manager distributes the `kyber-weave-docs` authoring skill and resolves
the harness layout for each runtime. Install it once, before `docs init`:

```bash
curl -sSL https://aka.ms/apm-unix | sh
```

Windows PowerShell: `irm https://aka.ms/apm-windows | iex`. Homebrew:
`brew install microsoft/apm/apm`. Verify with `apm --version`.

If you would rather not install it, run `kyber-weave docs init . --no-skill` — the
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
- [Configuration](configuration.md) — adapting Kyber-Weave to your repository
