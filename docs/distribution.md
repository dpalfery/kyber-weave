---
id: distribution
title: Distribution and release flow
doc-type: reference
status: current
component: Distribution
owner: dpalfery
last-reviewed: 2026-08-02
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
latest release tag (or `--version`), verifies SHA-256 against `SHA256SUMS.txt`, follows
HTTPS-only redirects, and installs to `~/.local/bin` without sudo.

The script is served from the **default branch**, not versioned with a release. It only
ever reads Release assets, so it stays backward-compatible with older tags and a script
fix never requires a re-release. Keep it that way when editing it.

It covers `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. Windows is out of scope — no
`.zip` handling — so Windows users take the Release asset directly.

## Other published artifacts

The release pipeline still publishes an npm wrapper and a Homebrew formula. They work, but
they are **not currently documented install paths** — the docs deliberately present one
way in. Treat them as published artifacts to keep building, not as channels to point users
at.

`nuget.org` is never used. A `dotnet tool` package may go to GitHub Packages as an
optional secondary channel for .NET specialists.

## RID matrix

`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`

Asset names follow `kyber-weave-<rid>` and `kyber-weave-mcp-<rid>`, `.tar.gz` everywhere
except `win-x64`, which is `.zip`. Windows archives contain `*.exe`; others contain
extensionless binaries.

## Release flow

1. Push a `v*` tag, e.g. `v0.1.1`
2. `.github/workflows/release.yml` publishes each RID and creates a GitHub Release with archives and `SHA256SUMS.txt`
3. If `NPM_TOKEN` is set, publishes the npm wrapper (wrapper only — binaries still come from the Release)
4. If `HOMEBREW_TAP_TOKEN` is set, updates the tap formula with the new SHA-256 values
5. Optionally pushes `PackAsTool` nupkgs to GitHub Packages — never to nuget.org

Without those secrets, Release assets still publish and the dependent steps skip with a
workflow warning. Since the install script reads only Release assets, **step 2 alone is
enough for the documented install path to work.**

### Required secrets

| Secret | Purpose |
|---|---|
| `NPM_TOKEN` | Publishing the npm wrapper |
| `HOMEBREW_TAP_TOKEN` | PAT with `contents:write` on the Homebrew tap |

## Continuous integration security

`.github/workflows/ci.yml` is the PR gate for this repository. Besides build, test, pack,
and RID publish smoke, it runs blocking security jobs modeled on the same tools used in
sibling product repos — without Azure or container scans, which do not apply here:

| Job | What it covers |
|---|---|
| CodeQL (`csharp`, `javascript-typescript`) | SAST with `security-extended` queries |
| Trivy filesystem | Dependency, misconfig, secret, and license findings at HIGH/CRITICAL |
| Semgrep Community | Additional SAST (`p/default`, ERROR) |
| gitleaks | Secret scan of the full history fetch |
| Skill and docs gate | Dogfoods the PR's CLI: `skill validate` / `lint` / `scan` on `.apm/skills/kyber-weave-docs`, plus `docs validate` |

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
