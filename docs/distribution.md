---
id: distribution
title: Distribution and release flow
doc-type: reference
status: current
component: Distribution
owner: dpalfery
last-reviewed: 2026-09-29
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

`scripts/install.sh` is the one first-install channel Kyber-Weave documents. It resolves the
latest release tag (or pre-release tags when `--prerelease` / `KYBER_WEAVE_PRERELEASE=1`
is set, or a specific release via `--version`), verifies SHA-256 against `SHA256SUMS.txt`,
follows HTTPS-only redirects, and installs to `~/.local/bin` without sudo. Unset, it refuses
any URL that is not HTTPS. The loopback exception is
[`KYBER_WEAVE_RELEASE_ORIGIN`](#verifying-a-release-locally).

It installs `kyber-weave`, `kyber-weave-mcp`, and — from releases that publish it —
`kyberdash`. `--no-mcp` and `--no-kyberdash` narrow that set.

Once those binaries are on PATH, `kyber-weave update` reads the same Release assets and
checksums and replaces the running CLI, the sibling MCP, and an installed `kyberdash` in
place. `--release-candidate` installs the highest-versioned non-draft release, stable or
pre-release, so it picks `v1.0.0` over `v1.0.0-rc.1`. The script's `--prerelease` considers
pre-releases only. Both compare by SemVer precedence across every page of the Releases list,
not by GitHub's creation order. A positional version pins a tag without colliding with global
`kyber-weave --version`.

The script is served from the **default branch**, not versioned with a release. It only
ever reads Release assets, so it stays backward-compatible with older tags and a script
fix never requires a re-release. Keep it that way when editing it.

### Per-asset version floors

Backward compatibility is not free once an asset is added mid-line. A release before that
asset existed publishes nothing under its name, and because the script verifies every
archive before installing any binary, one absent asset aborts the entire install — the
user ends up with no CLI and no MCP either, not merely a missing extra.

KyberDash is the current case: `kyberdash-<rid>` assets first appear in **0.1.7-rc.9**.
`KYBERDASH_MIN_VERSION` in `install.sh` and `SelfUpdater.KyberDashMinVersion` hold that
floor, and both compare against it with SemVer 2.0.0 precedence rather than string order —
`kyber_weave_semver_compare` and `ReleaseVersion.Compare` respectively, since an ordinal
comparison sorts `rc.10` below `rc.9`. Any future asset added to an existing release line
needs the same treatment.

It covers `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`. Windows is out of scope for
the script — no `.zip` handling — so Windows users take the Release asset directly.
`kyber-weave update` does extract `win-x64` `.zip` assets when replacing an existing
install.

## Other published artifacts

The `npm/` wrapper and `homebrew/` formula live in-tree for local experiments and
manual packaging, but the release workflow does **not** publish them. The documented
install path is `scripts/install.sh` against GitHub Release assets. Neither carries
KyberDash; adding it there would mean maintaining a second unpublished channel.

`nuget.org` is never used. A `dotnet tool` package may go to GitHub Packages as an
optional secondary channel for .NET specialists.

## RID matrix

The .NET binaries publish `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`.

Asset names follow `kyber-weave-<rid>` and `kyber-weave-mcp-<rid>`, `.tar.gz` everywhere
except `win-x64`, which is `.zip`. Windows archives contain `*.exe`; others contain
extensionless binaries.

KyberDash is a Node single-executable (SEA), so it publishes Node's stable RID names
instead: `linux-x64`, `linux-arm64`, `darwin-x64`, `darwin-arm64`, `win-x64` — macOS is
`darwin-*` where .NET is `osx-*`. Assets follow `kyberdash-<rid>` with the same archive
extensions. Two mappings translate between the sets and must stay in step:
`kyber_weave_kyberdash_rid` in `install.sh` and `PlatformRid.KyberDashRid` for the
self-updater.

All three tools share one `SHA256SUMS.txt` per release, so a KyberDash archive is verified
by exactly the mechanism the .NET binaries are.

## Squad release assets and packaging

In addition to binary executables, each GitHub Release publishes two version-matched Squad distribution packages:

| Asset Name | Format | Contents |
|---|---|---|
| `kyber-squad-<version>.zip` | APM bundle | All 21 canonical agents with their 10 owned references, 23 skills with 66 supplemental resources, profiles, schemas, and `mcp.json` |
| `kyber-squad-plugin-<version>.zip` | Agent Plugins v1 | All 23 portable skills, their 66 supplemental resources, and MCP server configuration; never agents or agent-owned resources |

### Packaging via `squad pack`

Release CI and maintainers build Squad assets using `kyber-weave squad pack`:

```bash
# Build APM distribution zip
kyber-weave squad pack --format apm --out ./artifacts

# Build Agent Plugins v1 zip
kyber-weave squad pack --format plugins --out ./artifacts

# Build both distribution artifacts
kyber-weave squad pack --format all --out ./artifacts
```

`squad pack` requires the current working directory to be the repository root containing `KyberWeave.sln` and `products/kyber-squad/squad.yml`. It does not fall back to embedded binaries or network sources.

Both formats package `skills/` recursively: 23 `SKILL.md` files plus 66 retained resources, for
89 skill-tree files. Every retained resource has a reviewed disposition in the
[skill-resource dispositions audit](kyber-squad/skill-resource-dispositions.md): non-policy
content stays in its skill directory as its durable home, portable policy lives in the
`products/kyber-squad/standards/` templates, and nothing was deleted; their omission from the
exact 24-file Copilot golden skill surface of the pinned Hotshot fixture is not a package
contract.

`products/kyber-squad/` is canonical and package authority. The root `.github/agents/`,
`.github/skills/`, `.kyber-weave/squad.lock.yml`, and `.kyber-weave/squad.receipt.json` are an
intentional stale self-deployment and its tracked state, not release-package inputs. This work
leaves them untouched for a human refresh after a fresh Kyber-Weave release candidate.

### Version lockstep and toolchain validation

- **Version Lockstep**: The CLI, MCP server, and Squad archive must share the same normalized semantic version, including any pre-release identifier.
- **Toolchain Qualification**: `products/kyber-squad/toolchain.yml` defines the required APM capabilities (`agent-ir/v1`, `semantic-permissions/v1`, `structured-degradation/v1`, `agent-to-skill-lowering/v1`). Release packaging validates against the pinned official APM release and its recorded platform archive SHA-256 hashes.
- **Checksum Verification**: Squad assets are included in `SHA256SUMS.txt`. Client-side `kyber-weave squad install` and `update` download the asset over HTTPS, verify its SHA-256 against `SHA256SUMS.txt`, and stage it into an isolated temporary location before rendering.

## Release flow

The usual cut is Actions → Release → Run workflow, **from `main`**. Only
`dpalfery` can start it (the workflow refuses any other actor; CODEOWNERS
requires the same review on changes to the pipeline itself). There is no
version field:

- Leave **Make production release** unchecked (the default) to publish the next
  RC. `scripts/next-release-version.sh` increments only the RC number
  (`0.1.7-rc.9` → `0.1.7-rc.10`; after a stable `0.1.6` with no RC line,
  `0.1.7-rc.1`).
- Check it to publish the next stable: promote the current RC line
  (`0.1.7-rc.9` → `0.1.7`) or, when the highest origin tag is already stable,
  bump patch (`0.1.7` → `0.1.8`).

That run is the whole cut except one click from David Palfery. Builds start
immediately. Publish waits on the `release` environment.

**Where to approve:** open the workflow run you just started (Actions → Release →
that run). After the build jobs go green, two jobs sit on a yellow clock named
**David Palfery must approve**. At the **top right of that same page**, click
**Review deployments**, tick `release`, then **Approve and deploy**. GitHub also
emails a “review pending deployment” notice. Nobody else is a reviewer, so
nobody else can publish.

`GITHUB_TOKEN` then creates tag `v…` at that commit via `gh release create
--target` (it does not `git push` a tag, and events from `GITHUB_TOKEN` do not
re-trigger the workflow). There is no PAT and no version to type.

Pushing a tag remains the path for a minor, major, or `-dev.N` cut:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Then `.github/workflows/release.yml` stamps that version onto the binaries, publishes
each RID, builds the Squad archives via `squad pack`, and runs a pre-publish manifest check
before creating a GitHub Release. This check verifies that `SHA256SUMS.txt` names exactly
the expected 20 assets (see [Manifest completeness and the pre-publish check](#manifest-completeness-and-the-pre-publish-check))
and that each hash is correct; if any asset is missing or unexpected, the release fails before `gh release create` runs (fail-closed, prevents publication).

On success, the workflow creates a GitHub Release with the 20 assets, the manifest, and changelogs (passing `--prerelease` for hyphenated pre-releases), then runs a post-publish verification step that checks the published assets against the expected set and byte-compares the manifest. If the post-publish check detects a mismatch, it fails the job (the release is already public, so this check is detective only).

The workflow also pushes `PackAsTool` nupkgs (including pre-release versions) to GitHub Packages (`https://nuget.pkg.github.com/dpalfery`) — never to nuget.org.

The `refs/tags/v*` ruleset still blocks rewriting or deleting a published tag
except for repository admins. Creating a tag is allowed so the workflow can mint
one. Only `dpalfery` can run this workflow; a tag push from anyone else is
refused at the authorize job.

No npm or Homebrew secrets are required. Since the install script reads only Release
assets, creating the GitHub Release alone is enough for the documented install path.

### Tag conventions and pre-releases

Kyber-Weave uses standard semantic versioning tags:

- **Stable releases:** `v<major>.<minor>.<patch>` (e.g. `v0.1.1`, `v1.0.0`)
- **Release candidates:** `v<major>.<minor>.<patch>-rc.<n>` (e.g. `v0.2.0-rc.1`)
- **Development builds:** `v<major>.<minor>.<patch>-dev.<n>` (e.g. `v0.2.0-dev.1`)

When a version containing a hyphen (`-`) is processed:

- `.github/workflows/release.yml` passes `--prerelease` to `gh release create`. GitHub Releases marks the release as a pre-release, keeping it off `/releases/latest` so standard `install.sh` users stay on stable releases.
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

## Code signing status

Release integrity rests on HTTPS transport security plus a complete `SHA256SUMS.txt` manifest that covers all 20 archives and installers. A pre-publish check verifies manifest completeness and correctness before `gh release create` runs, failing the job and preventing publication if assets are missing or unexpected (fail-closed). A post-publish check verifies the published assets after the release is live; on mismatch it fails the job but the release is already public (detective, not preventive). Authenticode, GPG, and other code signatures are deferred per [issue #132](https://github.com/dpalfery/kyber-weave/issues/132);
Azure signing was ruled out on cost; GPG-signed manifests are not implemented; SignPath
Foundation is a possible but unverified future option. See [ADR 0027](adr/0027-release-integrity-checksums-signing-deferred.md)
for the complete decision record.

| Artifact | Signing Status | Notes |
|---|---|---|
| Windows tray installer (`kyberdash-tray-win-x64-setup.exe`) | **Not signed** | Deferred per [issue #132](https://github.com/dpalfery/kyber-weave/issues/132). Users typically see SmartScreen warning. |
| Windows `.exe` files (`kyber-weave`, `kyber-weave-mcp`, `kyberdash` in `win-x64` archives) | **Not signed** | Deferred per [issue #132](https://github.com/dpalfery/kyber-weave/issues/132). Users typically see SmartScreen warning. |
| macOS tray (`kyberdash-tray-darwin-*.zip`) | Developer ID-signed and notarized | Team ID `J2UNNQ466J`. Verified by `.github/workflows/release.yml` signature and notarization checks. |
| macOS CLI and MCP binaries | Not Developer ID-signed | `install.sh` clears the macOS quarantine attribute at install time ([scripts/install.sh:735–742](../scripts/install.sh)). |
| macOS KyberDash binary | Ad-hoc signed | Signed after the Node SEA injection in the `build-kyberdash` job, `Build Node SEA for ${{ matrix.rid }}` step ([.github/workflows/release.yml](../.github/workflows/release.yml)). |
| Linux binaries | Unsigned | Standard for Linux. HTTPS transport and hash verification provide integrity. |

### Manifest completeness and the pre-publish check

The release workflow enforces that `SHA256SUMS.txt` lists exactly the 20 expected assets and
never itself. The published release contains those 20 assets plus `SHA256SUMS.txt` (21 files total).

The pre-publish check runs before `gh release create` and fails the job if the manifest is incomplete or incorrect, preventing publication. A post-publish check runs after the release is created and compares the published asset names against the 20 expected assets plus `SHA256SUMS.txt` and byte-compares the published manifest; on mismatch it fails the job (the release is already public, so this check is detective only).

- **5** CLI: `kyber-weave-linux-x64.tar.gz`, `kyber-weave-linux-arm64.tar.gz`,
  `kyber-weave-osx-x64.tar.gz`, `kyber-weave-osx-arm64.tar.gz`, `kyber-weave-win-x64.zip`
- **5** MCP: `kyber-weave-mcp-linux-x64.tar.gz`, `kyber-weave-mcp-linux-arm64.tar.gz`,
  `kyber-weave-mcp-osx-x64.tar.gz`, `kyber-weave-mcp-osx-arm64.tar.gz`, `kyber-weave-mcp-win-x64.zip`
- **5** KyberDash: `kyberdash-darwin-arm64.tar.gz`, `kyberdash-darwin-x64.tar.gz`,
  `kyberdash-linux-arm64.tar.gz`, `kyberdash-linux-x64.tar.gz`, `kyberdash-win-x64.zip`
- **3** Tray: `kyberdash-tray-darwin-arm64.zip`, `kyberdash-tray-darwin-x64.zip`, `kyberdash-tray-win-x64-setup.exe`
- **2** Squad: `kyber-squad-<version>.zip`, `kyber-squad-plugin-<version>.zip`

If an expected asset is missing or an unexpected file is present, the release fails before
publish with a diagnostic naming the cause. Each new asset requires updating the `EXPECTED_ASSETS` array in `scripts/verify-release-checksums.sh`; the matching build job and the count test must also be updated. Adding a supported platform or distribution format is a
deliberate step that must be coordinated with the release automation.

### Verification paths

Users and maintainers can verify asset integrity by:

1. **During download**: compare the SHA-256 hash of the downloaded file with the line in
   `SHA256SUMS.txt` ([Windows PowerShell](install.md#verifying-a-download),
   [macOS/Linux](install.md#verifying-a-download)).
2. **Before installation**: `install.sh` and `kyber-weave update` verify all binaries
   against `SHA256SUMS.txt` before installing.
3. **Whole-release download verification**: `shasum -a 256 --ignore-missing -c SHA256SUMS.txt`
   (macOS) or `sha256sum --ignore-missing -c SHA256SUMS.txt` (Linux) verifies the entire
   downloaded release. The `--ignore-missing` flag allows verification of a partial download
   if you have only a subset of the 20 assets.

SmartScreen reputation is tracked per file, so binaries from a new release will typically warn
again. The documented hash-verification and **More info → Run anyway** flow in
[install.md](install.md#windows-unsigned-binaries-and-smartscreen) is the supported
path for Windows users until signing is implemented.

## Verifying a release locally

A self-updater is always executed by the **old** binary. A fix to the update path therefore
cannot be proven by the release that contains it — only by updating away from a build that
predates it. Tag-and-wait cycles get this wrong silently: the release ships, the same failure
reappears, and the fix looks broken when it was simply never the code that ran.

Changes to the self-updater, `install.sh`, the Squad release path, or how `kyberdash` is built
or updated must run the local release loop. It publishes the working tree as a stand-in
Release, serves it from loopback, and drives a real self-update, a `squad install`, and the
KyberDash cases against published single-file binaries. Nothing reaches github.com. The
KyberDash build downloads Node from nodejs.org once, then reuses the cached copy:

```bash
./scripts/update-loop.sh                  # publish, serve, self-update, squad install, KyberDash
./scripts/update-loop.sh --keep           # leave the sandbox in place to inspect
./scripts/update-loop.sh --from <git-ref> # update away from an older build
./scripts/update-loop.sh --no-kyberdash   # skip the kyberdash build and the cases that need it
```

Three pieces are usable separately:

| Script | Does |
|---|---|
| [`scripts/release-local.sh`](../scripts/release-local.sh) | Publishes one RID with `release.yml`'s exact flags into `.local-release/v<version>/`, plus Squad archives, the `kyberdash` single-executable, and `SHA256SUMS.txt`. |
| [`scripts/local-release-server.py`](../scripts/local-release-server.py) | Serves that tree as the GitHub Releases endpoints the CLI reads. Loopback only. |
| [`scripts/update-loop.sh`](../scripts/update-loop.sh) | Drives the two together and asserts the outcome. |

The self-updater reaches that server through `KYBER_WEAVE_RELEASE_ORIGIN`, resolved by
[`ReleaseOrigin`](../src/KyberWeave.Cli/Update/ReleaseOrigin.cs). It accepts **loopback
authorities only**, and permits plain HTTP only for a loopback URL under an active override —
a redirect off the local server still has to be HTTPS. Those restrictions are the point of
the type; `ReleaseOriginTests` pins them, and widening them needs a reason you can state.

`scripts/install.sh` reads the same variable. A legal origin is only `http` or `https`,
with no userinfo, and host `127.0.0.1`, `localhost`, or `[::1]`, with an optional port.
Every other value is rejected, including other `127.*` addresses. While an `http` override
is active, curl uses `--proto '=http,https'` and keeps `--proto-redir '=https'`. An `http`
origin refuses wget. An `https` loopback origin keeps `curl --proto '=https'
--proto-redir '=https'` and `wget --https-only`. Unset or non-loopback configuration still
refuses non-HTTPS URLs: an unset origin rejects them in the downloader, and a non-loopback
origin is rejected before any download.

The loop exports `KYBER_WEAVE_RELEASE_ORIGIN` after the loopback server is listening.
`--from working` and a git ref then stage by
`install.sh --install-dir "$BIN" --version <from>`. `--from installed` keeps the copy,
because it means the binaries already on this machine. The loop serves
`http://127.0.0.1` with the port the server printed, so that staging needs curl.

Run against a **published single-file binary**, never `dotnet run`. The failure this exists
to catch — a running image replacing itself and then failing to load an assembly it had not
yet touched — does not exist in any other shape.

### KyberDash in the loop

`release-local.sh` builds `kyberdash-<node-rid>.tar.gz` for this machine the way the
`build-kyberdash` job does: an ESM bundle behind the CommonJS shim, injected into a prebuilt
Node from nodejs.org, and ad-hoc signed on macOS. Both also build the web dashboard
(`npm --prefix web ci` and `npm --prefix web run build`), pack it as the `web.json` SEA asset
with `scripts/pack-sea-web.mjs`, add that asset to the SEA blob alongside the CLI, and
smoke-test the result with `scripts/sea-web-smoke.mjs` — confirming that the built binary's
`kyberdash web` serves the SPA rather than the "not built" page. The job is the authority.
`ReleaseTests.LocalKyberDashBuildMatchesTheReleaseJob` pins those web-build, packing, and
smoke-test commands too, alongside the fuse, the postject version, and Node's version in
`dash/.nvmrc`. `BuildKyberDashEmbedsTheWebDashboard` pins the order — tsup, then the web
build, then the packer, then the SEA blob injection, then the smoke test — and confirms the
archive still contains exactly `kyberdash` and `THIRD_PARTY_NOTICES.md`, unchanged by the
embedded dashboard. (`dash/tsup.sea.config.ts` also now sets `removeNodeProtocol: false`, so
the SEA bundle keeps the `node:` prefix on builtins such as `node:sea` that have no bare
alias, rather than tsup's default of stripping it.) The build needs `node` and `npm` on
`PATH`. A failed build fails the loop; `--no-kyberdash` is the explicit opt-out.

Each case starts from a fresh copy of a staged build, with its own `HOME`, so the tray
record the updater reads is a fixture and never this machine's:

| Case | Asserts |
|---|---|
| `kyberdash-replaced` | An installed `kyberdash` is replaced, and the new one answers `--version`. |
| `kyberdash-opt-out` | `--no-kyberdash` updates the CLI and leaves `kyberdash` and the tray alone. |
| `tray-opt-out` | `--no-menubar` replaces `kyberdash` and leaves the tray alone. |
| `tray-delegated` | With a `tray.json`, the update runs the new `kyberdash menubar --update`, and that step's failure fails the update by name. |
| `kyberdash-floor` | A release below the KyberDash floor, which carries no `kyberdash` archive, still updates the CLI and MCP. It needs no build, so it runs under `--no-kyberdash` too. |
| `recovery-manifest` | When the build fails after `npm version` has stamped `dash/package.json`, the manifest and its lockfile come back byte for byte. A stand-in `npx` forces the failure. |
| `recovery-cache` | A cached Node download that does not match its `SHASUMS256.txt` fails the build and is removed, not trusted by the next run. |

The main self-update also asserts that an absent `kyberdash` stays absent.

The tray step cannot succeed in the loop. Linux has no tray, and the local release carries
no tray installer. So `tray-delegated` proves that the step ran and failed by name, not
that a tray was installed. Its first run found the tray step had never run at all: the root
`--version` swallowed the updater's `menubar --update --version <v>`, so `kyberdash`
printed its version and exited 0.

Cross-RID builds stay out of reach. The Node that makes the blob has to run here.

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
- [Kyber-Squad onboarding](kyber-squad/onboarding.md) — installing and updating agent squads
- [Kyber-Squad architecture](kyber-squad/architecture.md) — packaging and toolchain design
- [Wiring Kyber-Weave into CI](ci-pipelines/workflows-runbook.md) — installing in a runner
