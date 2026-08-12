---
id: distribution
title: Distribution and release flow
doc-type: reference
status: current
component: Distribution
owner: dpalfery
last-reviewed: 2026-08-11
---

# Distribution and release flow

Maintainer-facing. For installing Kyber-Weave, see [install.md](install.md).

Kyber-Weave distributes **self-contained single-file binaries** — no .NET runtime for end
users. GitHub Releases are the source of truth for every binary; every other mechanism
reads from them.

## The documented install path

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
```

`scripts/install.sh` is the one install channel Kyber-Weave documents. It resolves the
latest release tag (or pre-release tags when `--prerelease` / `KYBER_WEAVE_PRERELEASE=1`
is set, or a specific release via `--version`), verifies SHA-256 against `SHA256SUMS.txt`,
follows HTTPS-only redirects, and installs to `~/.local/bin` without sudo.

The script is served from the **default branch**, not versioned with a release. It only
ever reads Release assets, so it stays backward-compatible with older tags and a script
fix never requires a re-release. Keep it that way when editing it.

It covers `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. Windows is out of scope — no
`.zip` handling — so Windows users take the Release asset directly.

## Other published artifacts

The `npm/` wrapper and `homebrew/` formula live in-tree for local experiments and
manual packaging, but the release workflow does **not** publish them. The documented
install path is `scripts/install.sh` against GitHub Release assets.

`nuget.org` is never used. A `dotnet tool` package may go to GitHub Packages as an
optional secondary channel for .NET specialists.

## RID matrix

`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`

Asset names follow `kyber-weave-<rid>` and `kyber-weave-mcp-<rid>`, `.tar.gz` everywhere
except `win-x64`, which is `.zip`. Windows archives contain `*.exe`; others contain
extensionless binaries.

## Release flow

Cut a release in either way (the `v*` tag is the version source of truth):

1. **Push a tag:** `git tag v0.1.1 && git push origin v0.1.1` (or pre-release tags like `v0.2.0-rc.1` or `v0.2.0-dev.1`)
2. **Or Run workflow:** Actions → Release → Run workflow → enter `0.1.1` (or `v0.1.1`, `0.2.0-rc.1`, `0.2.0-dev.1`).  
   The workflow creates tag `v0.1.1` at that commit via `gh release create --target`  
   (it does not `git push` a tag, so the workflow is not re-triggered).

Then `.github/workflows/release.yml` stamps that version onto the binaries, publishes
each RID, and creates a GitHub Release with archives and `SHA256SUMS.txt` (passing
`--prerelease=auto` for pre-release tags and `--generate-notes` for changelogs).
Pushes `PackAsTool` nupkgs (including pre-release versions) to GitHub Packages
(`https://nuget.pkg.github.com/dpalfery`) — never to nuget.org.

Check **dry_run** on a manual run to build artifacts only (no tag, no Release).

No npm or Homebrew secrets are required. Since the install script reads only Release
assets, creating the GitHub Release alone is enough for the documented install path.

### Tag conventions and pre-releases

Kyber-Weave uses standard semantic versioning tags:

- **Stable releases:** `v<major>.<minor>.<patch>` (e.g. `v0.1.1`, `v1.0.0`)
- **Release candidates:** `v<major>.<minor>.<patch>-rc.<n>` (e.g. `v0.2.0-rc.1`)
- **Development builds:** `v<major>.<minor>.<patch>-dev.<n>` (e.g. `v0.2.0-dev.1`)

When a version containing a hyphen (`-`) is processed:

- `.github/workflows/release.yml` passes `--prerelease=auto` to `gh release create`. GitHub Releases marks the release as a pre-release, keeping it off `/releases/latest` so standard `install.sh` users stay on stable releases.
- Release notes automatically include a pre-release callout banner highlighting the candidate version.
- Nuget tool packages (`.nupkg`) carrying pre-release versions are published to GitHub Packages, allowing testing via `dotnet tool update --prerelease`.

### Binary versioning and inspection

Binaries built by `.github/workflows/release.yml` embed the release tag and Git commit SHA via `AssemblyInformationalVersionAttribute`. Users and automated tools can inspect the version of installed binaries at any time:

```bash
kyber-weave --version
# or
kyber-weave -v
```

And for the MCP server:

```bash
kyber-weave-mcp --version
# or
kyber-weave-mcp -v
```

Output format: `kyber-weave <version>` (e.g. `kyber-weave 0.1.0+714f187ab97d66e1199c33d5aaa0c9ab76ffae0f` or `kyber-weave 0.2.0-rc.1`).

## Continuous integration security

`.github/workflows/ci.yml` is the PR gate for this repository. Besides build, test, pack,
and RID publish smoke, it runs blocking security jobs modeled on the same tools used in
sibling product repos — without Azure or container scans, which do not apply here:

| Job | What it covers |
|---|---|
| Build and test | Restore, `dotnet format` (whitespace + curated style), NuGet audit, build, test, pack |
| CodeQL (`csharp`, `javascript-typescript`) | SAST with `security-extended` queries |
| Trivy filesystem | Dependency, misconfig, secret, and license findings at HIGH/CRITICAL |
| Semgrep Community | Additional SAST (`p/default`, ERROR) |
| gitleaks | Secret scan of the full history fetch |
| Skill and docs gate | Dogfoods the PR's CLI: `skill validate` / `lint` / `scan` on `.apm/skills/kyber-weave-docs`, plus `docs validate` |

Formatting is gated by [`.editorconfig`](../.editorconfig): whitespace plus a small style
pack (file-scoped namespaces, usings, predefined types, `var` when apparent). The
`analyzers` format subcommand is intentionally unused — CA quality rules already fail
the build.

Findings upload as SARIF to the GitHub Security tab (`security-events: write`). Dependabot
covers NuGet, GitHub Actions, and the npm wrapper weekly. NuGet Audit is on for transitive
packages; HIGH/CRITICAL advisories fail restore under `TreatWarningsAsErrors`.

`docs drift` stays a local/author gate until CI can provision a CodeGraph index — see
[the workflow runbook](ci-pipelines/workflows-runbook.md).

## Smoke tests

End-to-end, without touching a real bin directory:

```bash
sh scripts/install.sh --install-dir "$(mktemp -d)" --version 0.1.1
```

A local self-contained publish, using the same flags as the release workflow:

```bash
dotnet publish src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release \
  -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./artifacts/cli-osx-arm64
```

## Related

- [Installing Kyber-Weave](install.md) — the user-facing path
- [Wiring Kyber-Weave into CI](ci-pipelines/workflows-runbook.md) — installing in a runner
