# Distribution

Kyber-Weave distributes **self-contained single-file binaries** (no .NET runtime for end users). Channels:

| Channel | Package / location | Notes |
| --- | --- | --- |
| **Install script** | `scripts/install.sh` | `curl … \| sh` for macOS/Linux; resolves latest tag (or `--version`), verifies SHA-256 from `SHA256SUMS.txt`, HTTPS-only redirects; installs to `~/.local/bin` without sudo |
| **npm** | `@dpalfery/kyber-weave` | Thin Node wrapper; downloads Release assets for the package version tag; verifies SHA-256 from `SHA256SUMS.txt`; HTTPS-only redirects |
| **GitHub Releases** | `kyber-weave-<rid>.tar.gz` / `.zip`, `kyber-weave-mcp-<rid>.*` | Source of truth for binaries |
| **Homebrew** | `dpalfery/kyber-weave` tap → `kyber-weave` | Formula installs CLI + MCP from Release assets |
| GitHub Packages | `KyberWeave.Tool` / `KyberWeave.Mcp` | Optional secondary `dotnet tool` channel |
| nuget.org | — | **Forbidden** |

## RID matrix

- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`
- `win-x64`

Asset names (examples):

- `kyber-weave-linux-x64.tar.gz`
- `kyber-weave-mcp-osx-arm64.tar.gz`
- `kyber-weave-win-x64.zip`
- `kyber-weave-mcp-win-x64.zip`

Windows archives contain `*.exe`; others contain extensionless binaries.

## Release flow

1. Push a `v*` tag (e.g. `v0.1.0`).
2. `.github/workflows/release.yml` publishes each RID, creates a GitHub Release with archives + `SHA256SUMS.txt`.
3. If `NPM_TOKEN` is set, publishes `@dpalfery/kyber-weave@<version>` (wrapper only; binaries still come from the Release).
4. If `HOMEBREW_TAP_TOKEN` is set, updates `dpalfery/homebrew-kyber-weave` `Formula/kyber-weave.rb` with SHA256s.
5. Optionally pushes PackAsTool nupkgs to GitHub Packages (never nuget.org).

## Secrets the maintainer must add

| Secret | Where | Purpose |
| --- | --- | --- |
| `NPM_TOKEN` | GitHub repo Actions secrets | `npm publish` for `@dpalfery/kyber-weave` |
| `HOMEBREW_TAP_TOKEN` | GitHub repo Actions secrets | PAT with `contents:write` on `dpalfery/homebrew-kyber-weave` |

Without these secrets, Release assets still publish; npm and Homebrew tap updates are skipped with a workflow warning.

## Install script

`scripts/install.sh` is served straight from the default branch via
`raw.githubusercontent.com`, so it is **not** versioned with a release — keep it
backward-compatible with older tags. It only ever reads Release assets, so a
script change never requires a re-release.

Covers `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. Windows is out of
scope (no `.zip` handling); Windows users get npm.

```bash
# smoke the script end-to-end without touching a real bin dir
sh scripts/install.sh --install-dir "$(mktemp -d)" --version 0.1.1
```

## Local smoke

```bash
dotnet publish src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release \
  -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./artifacts/cli-osx-arm64

cd npm
KYBER_WEAVE_SKIP_DOWNLOAD=1 npm pack --dry-run
```
