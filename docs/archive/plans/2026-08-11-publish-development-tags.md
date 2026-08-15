---
id: archive/plans/2026-08-11-publish-development-tags
title: Publish Development and Release Candidate Tags
doc-type: plan
status: archived
component: Distribution
owner: dpalfery
last-reviewed: 2026-08-11
---

# Publish Development and Release Candidate Tags

**Status:** Archived
**Archive Date:** 2026-08-11
**Date:** 2026-08-11
**Goal:** Enable publishing and installing development and release-candidate tags across GitHub Releases, install.sh, GitHub Packages, and CLI --version support.

---

## 1. Problem / Motivation

Currently, Kyber-Weave builds and releases artifacts via `.github/workflows/release.yml` triggered on `v*` tags or `workflow_dispatch`. However:
1. When a pre-release or dev tag (such as `v0.2.0-rc.1` or `v0.2.0-dev.1`) is pushed, `release.yml` does not set `--prerelease` on GitHub Release creation. As a result, GitHub API treats candidate builds as the `latest` release, accidentally serving unstable binaries to standard `curl | sh` users.
2. `scripts/install.sh` resolves latest stable releases via `/releases/latest` and has no mechanism (like `--prerelease`) for users or CI pipelines to install candidate builds without knowing the exact version string.
3. `kyber-weave` CLI (`src/KyberWeave.Cli`) lacks a `--version` / `-v` flag to output its current build-stamped version.

## 2. Approved decisions

- **D1:** `release.yml` will automatically detect pre-release version strings (containing `-`, e.g., `0.2.0-rc.1`, `0.2.0-dev.1`) and pass `--prerelease=auto` (or `--prerelease`) to `gh release create`, ensuring pre-releases are not marked as `latest` in GitHub Releases.
- **D2:** `scripts/install.sh` will support a `--prerelease` CLI flag and `KYBER_WEAVE_PRERELEASE=1` environment variable. When passed without `--version`, `install.sh` queries GitHub's `/releases` API list to resolve the newest pre-release tag. Specifying `--version <tag>` continues to work directly.
- **D3:** `release.yml` will push pre-release `.nupkg` packages to GitHub Packages (`https://nuget.pkg.github.com/dpalfery`) during pre-release workflow runs, enabling `.NET` tool users to install and test candidates via `dotnet tool update --prerelease`.
- **D4:** `src/KyberWeave.Cli/Program.cs` will configure `config.SetApplicationVersion(...)` dynamically using `AssemblyInformationalVersionAttribute` (with fallback to `Assembly.GetName().Version`), natively supporting `kyber-weave --version` and `kyber-weave -v`. `src/KyberWeave.Mcp/Program.cs` will also handle `--version` and `-v` flags.

## 3. Investigation findings

- GitHub workflow [release.yml](file:///Users/dave/git/kyber-weave/.github/workflows/release.yml) handles release builds across 5 RIDs (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`), passing `-p:Version=${version}`. `gh release create` is called at lines 240-248 without pre-release flags.
- Installer script [install.sh](file:///Users/dave/git/kyber-weave/scripts/install.sh) resolves `LATEST_API` at line 24 (`https://api.github.com/repos/dpalfery/kyber-weave/releases/latest`), which strictly returns non-prerelease tags.
- [Program.cs (CLI)](file:///Users/dave/git/kyber-weave/src/KyberWeave.Cli/Program.cs) configures `Spectre.Console.Cli` `CommandApp`, but lacks `config.SetApplicationVersion(...)`.
- [Program.cs (MCP)](file:///Users/dave/git/kyber-weave/src/KyberWeave.Mcp/Program.cs) processes custom CLI arguments for `--repo-root` but has no check for `--version`.

## 4. Task list

| # | Phase | Component | Description | Skills |
|---|-------|-----------|-------------|--------|
| 1 | 1 | CLI & MCP | Add `config.SetApplicationVersion(...)` to `src/KyberWeave.Cli/Program.cs` using `AssemblyInformationalVersionAttribute`. Add `--version` / `-v` handling to `src/KyberWeave.Mcp/Program.cs`. | csharp, spectre-console |
| 2 | 1 | Installer Script | Add `--prerelease` flag and `KYBER_WEAVE_PRERELEASE=1` env var to `scripts/install.sh`. Update `resolve_latest_version()` to fetch `/releases` and parse pre-release tags when `--prerelease` is active. | posix-shell |
| 3 | 1 | GitHub Workflow | Update `.github/workflows/release.yml` to pass `--prerelease=auto` (or explicit `--prerelease` if version contains `-`) to `gh release create`, and update release notes text. | github-actions |
| 4 | 2 | Unit Tests | Add unit test in `tests/KyberWeave.Tests/` verifying version retrieval from assembly attributes for CLI/MCP. | csharp, xunit |
| 5 | 2 | Documentation | Update `docs/distribution.md`, `docs/ci-pipelines/workflows-runbook.md`, and `docs/install.md` to document development/RC tags, `--prerelease` flag in `install.sh`, and `kyber-weave --version`. | markdown-docs |

## 5. Sequencing / dependency graph

Task 1 (CLI & MCP versioning), Task 2 (Installer script), Task 3 (Release workflow) can proceed in parallel during Phase 1.
Task 4 (Unit tests) depends on Task 1.
Task 5 (Documentation updates) depends on Tasks 1, 2, and 3.

## 6. Residual decisions / risks

- **API Rate Limits on GitHub API:** Querying `/releases` without auth in `install.sh` has a rate limit of 60 req/hr per IP. This matches existing `install.sh` behavior for `/releases/latest`.
- **Pre-release parsing in POSIX shell:** Must maintain strict POSIX compatibility (no `jq` requirement) in `scripts/install.sh` using `sed`/`awk`/`tr`.

## 7. Out of scope

- Automated npm or Homebrew formula publishing (per `docs/distribution.md`, `install.sh` against GitHub Releases is the sole documented install path).
- Publishing pre-releases to `nuget.org` (strictly forbidden by repository policy).

## 8. Required skills

- `csharp`: .NET 10 C# development and assembly metadata inspection.
- `spectre-console`: Spectre.Console.Cli configuration.
- `posix-shell`: POSIX-compliant shell scripting and GitHub REST API parsing.
- `github-actions`: GitHub Actions workflow syntax and `gh` CLI commands.
- `xunit`: C# unit testing with xUnit.
- `markdown-docs`: Technical documentation update.

## 9. Verification harness

- **CLI & MCP Versioning:** Run `dotnet run --project src/KyberWeave.Cli -- --version` and verify output is `kyber-weave <version>`. Run `dotnet run --project src/KyberWeave.Mcp -- --version` and verify exit code 0.
- **Installer Script:** Run `sh -n scripts/install.sh` for POSIX syntax check. Run `scripts/install.sh --help` and verify `--prerelease` option is documented.
- **Unit Tests:** Execute `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj` and verify all tests pass.
- **Security & Code Quality Gates:** Run `.github/workflows/ci.yml` jobs locally/in CI (Format, CodeQL, Trivy, Semgrep, Gitleaks, Skill-docs-gate).
