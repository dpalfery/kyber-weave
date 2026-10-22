---
id: install
title: Installing Kyber-Weave
doc-type: runbook
status: current
component: Distribution
owner: dpalfery
last-reviewed: 2026-10-16
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

## Which product, and which install path

Two products ship from this repository, and the same installer places both. Choose by what
you are trying to do:

| Product | What it is | Start with |
|---|---|---|
| **Kyber-Weave** | The `kyber-weave` CLI and `kyber-weave-mcp` server: documentation governance, skill and agent linting, multi-harness deployment, and CI diagnostics. | This page |
| **KyberDash** | The `kyberdash` binary: local token, cost, and context observability across coding harnesses. | [KyberDash adoption guide](dash/onboarding.md) |

There is one documented first-install path per platform; the KyberDash tray is a
post-install add-on, not a peer path. Everything else is deliberately unsupported:

| Path | Platform | How |
|---|---|---|
| Installer script (**the documented path**) | Linux, macOS | the one-liner above |
| Release `.zip` | Windows | [download and verify](#verifying-a-download) |
| KyberDash tray (post-install add-on) | macOS, Windows | `kyberdash menubar`, or `install.sh --with-menubar` on macOS |

After installing, run [Verify](#verify), then read [Updating](#updating). Configuration
splits the same way as the products: [Configuration](configuration.md) for Kyber-Weave, and
[KyberDash configure](dash/onboarding.md#configure) for KyberDash.

## Install channels that are not supported

Only the paths in the table above are supported. The rest are stated here so that
"unsupported" is distinguishable from "undocumented":

- **npm.** The `npm/` wrapper in the repository (`@dpalfery/kyber-weave`) is in-tree for
  local experiments and manual packaging; the release workflow does not publish it, and it
  does not carry KyberDash. There is no public npm package for `kyberdash` either
  (`npm view kyberdash` returns 404), so there is no `npm install` path for either product.
- **Homebrew.** The in-tree `homebrew/` formula is unpublished for the same reason. There is
  no `brew install` path.
- **`dotnet tool`.** Tool packages are published, when they are, to **GitHub Packages** — an
  authenticated registry — for maintainers and .NET specialists, not for installing
  Kyber-Weave on a fresh machine. `kyber-weave update` also refuses `dotnet run` and
  `dotnet tool` installs. See [Other published artifacts](distribution.md#other-published-artifacts)
  for the maintainer view.

The supported path remains `scripts/install.sh`, or the Windows `.zip` from
[Releases](https://github.com/dpalfery/kyber-weave/releases).

## Options

| Flag | Environment variable | Effect |
|---|---|---|
| `--version <v>` | `KYBER_WEAVE_VERSION` | Install a specific release (e.g. `0.1.1` or `0.2.0-rc.1`) instead of latest |
| `--prerelease` | `KYBER_WEAVE_PRERELEASE=1` | Resolve and install candidate/pre-release builds (e.g. `v*-rc.*`, `v*-dev.*`) |
| `--install-dir <d>` | `KYBER_WEAVE_INSTALL_DIR` | Target directory (default `~/.local/bin`) |
| `--no-mcp` | `KYBER_WEAVE_NO_MCP=1` | CLI only; skip the MCP server |
| `--no-kyberdash` | `KYBER_WEAVE_NO_KYBERDASH=1` | CLI + MCP; skip the KyberDash binary |
| `--with-menubar` | `KYBER_WEAVE_WITH_MENUBAR=1` | macOS only; also install the signed menubar app to `~/Applications` after verifying its SHA-256 and code signature |
| — | `KYBER_WEAVE_RELEASE_ORIGIN` | Install from a loopback stand-in instead of GitHub. Unset keeps the GitHub release roots |

`KYBER_WEAVE_RELEASE_ORIGIN` has no flag. `scripts/install.sh` reads it. A legal origin is only `http` or
`https`, with no userinfo, and host `127.0.0.1`, `localhost`, or `[::1]`, with an optional
port. Every other value is rejected, including other `127.*` addresses. While an `http`
override is active, curl uses `--proto '=http,https'` and keeps `--proto-redir '=https'`.
An `http` origin refuses wget. An `https` loopback origin keeps
`curl --proto '=https' --proto-redir '=https'` and `wget --https-only`. Unset configuration
still refuses any URL that is not HTTPS. A non-loopback value is rejected before a
download, so it installs nothing and does not fetch a non-HTTPS URL.

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
3. Downloads `SHA256SUMS.txt` and each binary archive over HTTPS, following HTTPS-only redirects. A legal `KYBER_WEAVE_RELEASE_ORIGIN` is the only case that may start on plain HTTP, and redirects stay HTTPS-only
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

## The KyberDash tray

KyberDash ships a macOS and Windows tray as its own release asset, from `0.1.7-rc.13`. The
tray is a display layer over the same engine as the CLI and the browser dashboard, so
install it only after `kyberdash` is on your PATH:

```bash
kyberdash menubar            # download, verify, install, and launch the tray
```

On macOS you can fold the tray into the initial install with `--with-menubar`, which
downloads the signed app, verifies its SHA-256 and its code signature, and places it in
`~/Applications`:

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
  | sh -s -- --prerelease --with-menubar
```

`--with-menubar` is macOS only, and it cannot be combined with `--no-kyberdash`: the tray is
installed by the `kyberdash` binary itself, so skipping that binary would leave nothing to run
it. On Linux, `kyberdash menubar` exits `1` — the tray ships for macOS and Windows only. For
the rest of the tray flow, see [Install the tray](dash/onboarding.md#install-the-tray) in the
KyberDash adoption guide.

## Supported platforms

`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` — and for KyberDash, the same platforms
under Node's names: `linux-x64`, `linux-arm64`, `darwin-x64`, `darwin-arm64`.

Windows binaries (`win-x64`) are published on the release, but the install script does not
handle `.zip` extraction — download the asset from
[Releases](https://github.com/dpalfery/kyber-weave/releases) and place it on your PATH
manually. After that, `kyber-weave update` replaces the Windows binaries in place.

## Verifying a download

Every release publishes `SHA256SUMS.txt`, a manifest of SHA-256 hashes for all assets.
Verify any downloaded archive or installer before extracting or running it.

**Windows PowerShell:**

Run from the directory containing your downloaded files and `SHA256SUMS.txt`. To verify a single file:

```powershell
$name = "kyber-weave-win-x64.zip"
$expected = (Get-Content SHA256SUMS.txt | Select-String -SimpleMatch $name) -split '  ' | Select-Object -First 1
$hash = (Get-FileHash $name -Algorithm SHA256).Hash
if ($hash -eq $expected) { Write-Host "OK: $name" } else { Write-Host "FAILED: $name" }
```

To verify all downloaded files at once (skipping missing ones, like `--ignore-missing` in bash):

```powershell
(Get-Content SHA256SUMS.txt) | foreach {
    $parts = $_ -split '  '
    $name = $parts[1]
    if (Test-Path $name) {
        $hash = (Get-FileHash $name -Algorithm SHA256).Hash
        if ($hash -eq $parts[0]) { Write-Host "OK: $name" } else { Write-Host "FAILED: $name" }
    }
}
```

**macOS and Linux:**

Run from the directory containing your downloaded files and `SHA256SUMS.txt`. On macOS, use `shasum`:

```bash
shasum -a 256 --ignore-missing -c SHA256SUMS.txt
```

On Linux, use `sha256sum`:

```bash
sha256sum --ignore-missing -c SHA256SUMS.txt
```

The `--ignore-missing` flag allows you to verify a subset of files if you downloaded
only some of the release assets. A mismatch causes the command to exit with a non-zero
status and print a `FAILED` line.

## The SBOM: what each release ships

Every release also publishes `kyber-weave-sbom-<version>.cdx.json` — a **Software Bill of
Materials** in [CycloneDX](https://cyclonedx.org/) JSON, format version 1.6. It is a
machine-readable inventory of the third-party components the release depends on, across
the three ecosystems this repository ships:

| Ecosystem | What the SBOM covers | Where it comes from |
|---|---|---|
| .NET (NuGet) | `KyberWeave.Cli`, `KyberWeave.Mcp`, `KyberWeave.Core` | the resolved `*.deps.json` of a throwaway publish |
| Node (npm) | KyberDash, the web dashboard, the tray UI, the npm wrapper | `package-lock.json` |
| Rust (Cargo) | the KyberDash tray | `Cargo.lock` |

It exists so that the inventory can be fed to a vulnerability scanner or a licence audit
without unpacking every binary. See
[issue #383](https://github.com/dpalfery/kyber-weave/issues/383) for the rationale.

### Verifying the SBOM

The SBOM is listed in `SHA256SUMS.txt` like every other asset, so the same command that
verifies the binaries verifies it — there is nothing extra to download or trust:

```bash
sha256sum --ignore-missing -c SHA256SUMS.txt
```

### What the SBOM does and does not tell you

It is a dependency inventory, not a statement about what is running on your machine, and
not a scan of the published archives. Four limits are worth stating plainly.

- **It describes the repository's dependency closure, not any individual RID's binary.**
  Every asset in a release is built from the same locked dependency set, so the closure
  is the same for all of them — but this is an inventory of the inputs, not a
  measurement of the bytes inside an archive.
- **The embedded .NET runtime is not in it.** The shipped `kyber-weave` and
  `kyber-weave-mcp` binaries are self-contained: they embed the entire .NET runtime,
  which is the single largest component of what actually ships. The SBOM is built from
  a *framework-dependent* publish, because that is what produces the readable
  dependency manifest, and a framework-dependent manifest lists NuGet packages only.
  The .NET runtime therefore does not appear as a component.
- **The embedded Node runtime is not in it either.** `kyberdash` is a Node
  single-executable, and the runtime is a platform input rather than an npm
  dependency.
- **Test-only dependencies are not in it.** The SBOM covers what the release ships,
  not the test suite.

So: the SBOM answers *"which third-party libraries does this release depend on?"*. It
does not answer *"what is in this archive?"*, and in particular it does not account
for the two runtimes the release embeds. For a byte-level inventory, scan the published
archives themselves.

## Windows: unsigned binaries and SmartScreen

The Windows tray installer (`kyberdash-tray-win-x64-setup.exe`) and all Windows `.exe`
files inside release archives (`kyber-weave`, `kyber-weave-mcp`, and `kyberdash` in
`win-x64` archives) are **not Authenticode-signed**. Signing has been deferred
([issue #132](https://github.com/dpalfery/kyber-weave/issues/132)); see
[Code signing status](distribution.md#code-signing-status) for the rationale and integrity
model.

### SmartScreen warning and **More info -> Run anyway**

When you run an unsigned Windows `.exe`, SmartScreen typically displays **"Windows protected
your PC"**. To proceed:

1. Click **More info** to expand the prompt.
2. Click **Run anyway** at the bottom of the expanded panel.

This is normal for unsigned binaries downloaded from the internet. SmartScreen reputation is
tracked per file, so binaries from a new release will typically warn again. Verifying the
SHA-256 hash first (see [Verifying a download](#verifying-a-download) above) confirms the
binary is intact and matches the published manifest.

### Mark-of-the-Web and extracted `.exe` files

When you extract a `.zip` file containing Windows binaries on NTFS, Windows marks the
extracted files with the "Mark-of-the-Web" (MOTW) attribute to indicate they came from
an untrusted source. This causes SmartScreen to show the warning above on first run.

To remove the MOTW attribute:

- **In File Explorer**: Right-click the `.exe` file, select **Properties**, check
  **Unblock** at the bottom of the **General** tab, and click **Apply**.
- **In PowerShell**:
  ```powershell
  Unblock-File -Path "path\to\kyber-weave.exe"
  ```

### Automated verification

If you use `kyberdash menubar` to install the tray, it verifies the SHA-256 hash of the
installer and confirms its code signature on macOS before running it. Manual downloads
from [Releases](https://github.com/dpalfery/kyber-weave/releases) have no built-in
verification; hash verification is your responsibility.

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
kyber-weave update --no-menubar       # leave an installed KyberDash tray unchanged
```

When a tray is installed, `kyber-weave update` also hands off to `kyberdash menubar --update`,
which quits the running tray, replaces it, and relaunches it — restoring the previous app if
any step fails. Update the tray directly with:

```bash
kyberdash menubar --update
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
of 11 Kyber Squad coding standards templates. It does not create an empty glossary. See
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

### Codex Cloud

Set the Codex Cloud environment setup script to:

```bash
bash scripts/codex-cloud-setup.sh
```

The script installs a compatible .NET 10 SDK and `sqlite3` for repository tooling,
then installs the Kyber-Weave CLI and MCP binaries without KyberDash. It installs
APM, runs `docs init` for the Codex target to deploy the documentation skill, and
installs or updates Kyber-Squad in the cloud user's global Codex home. It also
adds the CLI install directory to `~/.bashrc` so it remains on `PATH` in later
cloud sessions.

By default, the script installs the latest prerelease. Set `KYBER_WEAVE_VERSION`
to pin a release, or set `KYBER_WEAVE_PRERELEASE=0` to select the latest stable
release.

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
- [KyberDash adoption guide](dash/onboarding.md) — installing, updating, and configuring KyberDash
