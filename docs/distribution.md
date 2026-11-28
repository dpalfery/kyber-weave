---
id: distribution
title: Distribution and release flow
doc-type: reference
status: current
component: Distribution
owner: dpalfery
last-reviewed: 2026-11-26
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

It installs `kyber-weave`, `kyber-weave-mcp`, `kyber-weave-arbiter`, and — from releases
that publish it — `kyberdash`. `--no-mcp`, `--no-arbiter`, and `--no-kyberdash` narrow
that set.

Once those binaries are on PATH, `kyber-weave update` reads the same Release assets and
checksums and replaces the running CLI, the sibling MCP, an installed `kyber-weave-arbiter`,
and an installed `kyberdash` in
place. An absent arbiter is reported rather than created: `install.sh --no-arbiter` is an
explicit opt-out, and a machine that installed before the arbiter existed never chose to run
it. `--release-candidate` installs the highest-versioned non-draft release, stable or
pre-release, so it picks `v1.0.0` over `v1.0.0-rc.1`. The script's `--prerelease` considers
pre-releases only. Both compare by SemVer precedence across every page of the Releases list,
not by GitHub's creation order. A positional version pins a tag without colliding with global
`kyber-weave --version`.

**A rename is not a new asset.** This release renames the three MCP servers' configuration keys
and their nineteen tools. No executable, archive, asset name, or `--version` output changed, so
`install.sh` and `kyber-weave update` install and replace exactly the files they always have. The
operator-side work the rename still needs is in [Renamed MCP servers and
tools](install.md#renamed-mcp-servers-and-tools).

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

The Arbiter is the second case: `kyber-weave-arbiter-<rid>` assets first appear in
**0.1.7-rc.17** (`ARBITER_MIN_VERSION`, `SelfUpdater.ArbiterMinVersion`). A release older
than that floor installs and updates the CLI and MCP alone.

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
optional secondary channel for .NET specialists and maintainers. It is **not an end-user
install path**: that registry requires authentication, and [install.md](install.md)
documents only `scripts/install.sh` and the Windows `.zip`.

## RID matrix

The .NET binaries publish `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`.

Asset names follow `kyber-weave-<rid>`, `kyber-weave-mcp-<rid>`, and
`kyber-weave-arbiter-<rid>`, `.tar.gz` everywhere
except `win-x64`, which is `.zip`. Windows archives contain `*.exe`; others contain
extensionless binaries.

KyberDash compiles from `dash/server/` as a self-contained single-file `.NET 10`
executable and retains the historical RID archive names (`linux-x64`, `linux-arm64`,
`darwin-x64`, `darwin-arm64`, `win-x64`, where macOS archives stay `darwin-*` while CLI, MCP,
and Arbiter assets use `osx-*`). Assets follow `kyberdash-<rid>` with the same archive
extensions. Two mappings translate between the sets and must stay in step:
`kyber_weave_kyberdash_rid` in `install.sh` and `PlatformRid.KyberDashRid` for the
self-updater.

All three tools share one `SHA256SUMS.txt` per release, so a KyberDash archive is verified
by exactly the mechanism the .NET binaries are. The Arbiter is the third .NET binary in
that manifest, alongside the CLI and the MCP server.

## Squad release assets and packaging

In addition to binary executables, each GitHub Release publishes two version-matched Squad distribution packages:

| Asset Name | Format | Contents |
|---|---|---|
| `kyber-squad-<version>.zip` | APM bundle | All canonical agents with their owned references, the Squad skills and the Kyber-Weave skills from `.apm/skills/` with their supplemental resources, profiles, schemas, and `mcp.json` |
| `kyber-squad-plugin-<version>.zip` | Agent Plugins v1 | All portable skills (the same Squad and Kyber-Weave skills), their supplemental resources, and MCP server configuration; never agents or agent-owned resources |

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

`squad pack` also adds the Kyber-Weave skills from `.apm/skills/` beside the Squad skills, so the
release carries the same skills that `squad install` deploys to each user-level folder. It loads the
real Squad tree and the composed tree before it writes an archive, and it exits 1 with no archive
when a companion skill clashes with a Squad skill or agent name, ignoring letter case, or when a
symbolic link sits at `.apm`, `.apm/skills`, a companion skill's directory, its `SKILL.md`, or any file
or folder inside that skill. A refused pack leaves the previous archive in place, and so does a failed
write: each archive is written to a temporary file beside its target and replaces it only when complete. See
[ADR 0049](../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md).

Both formats package `skills/` recursively: every `SKILL.md` file (Squad and Kyber-Weave) plus their
retained resources. Every one of the retained Squad resources has a
reviewed disposition in the
[skill-resource dispositions audit](kyber-squad/skill-resource-dispositions.md): non-policy
content stays in its skill directory as its durable home, portable policy lives in the
`products/kyber-squad/standards/` templates, and nothing was deleted; their omission from a
fixed Copilot skill surface is not a package contract.

`products/kyber-squad/` is canonical and package authority. The repository no longer carries a
project-level self-deployment: the root `.github/agents/`, `.github/skills/`,
`.kyber-weave/squad.lock.yml`, and `.kyber-weave/squad.receipt.json` were removed.

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
each RID, builds the Squad archives via `squad pack`, generates the aggregate SBOM plus
one SBOM per published archive, attests every SBOM, and runs a
pre-publish manifest check before creating a GitHub Release. This check verifies that
`SHA256SUMS.txt` names exactly the expected 51 assets (25 archives, 25 per-artifact
SBOMs, and the aggregate SBOM — see
[Manifest completeness and the pre-publish check](#manifest-completeness-and-the-pre-publish-check))
and that each hash is correct; if any asset is missing or unexpected, the release fails before `gh release create` runs (fail-closed, prevents publication).

On success, the workflow creates a GitHub Release with the 51 assets, the manifest, and release notes (see [Release notes](#release-notes); passing `--prerelease` for hyphenated pre-releases), then runs post-publish verification steps that check the published assets against the expected set, byte-compare the manifest, and verify each archive's CycloneDX SBOM attestation and its SLSA build provenance. If a post-publish check detects a mismatch, it fails the job (the release is already public, so these checks are detective only).

### Re-running a release: the chain receipt

Every build, SBOM and publish job in `.github/workflows/release.yml` restores one shared,
version-bound **chain receipt** — key `release-chain-<version>`, path `.release-chain` — and runs
one verdict step that compares the receipt's `saved_at_epoch` against the workflow's
`RECEIPT_MAX_AGE_SECONDS` (four hours). A re-run of the same version inside that window skips the
build chain: the seven build and SBOM jobs do no work, and the two publish jobs verify the
published state instead of publishing again.

On a hit, `release` verifies the published asset names against the receipt's recorded list as
sorted sets, `diff`s the published `SHA256SUMS.txt` against the recorded manifest, and verifies
every published archive with `gh attestation verify` — `Verify SBOM attestations` pinning
`https://cyclonedx.org/bom`, `Verify provenance attestations` pinning
`https://slsa.dev/provenance/v1`. `gh-packages` proves each recorded `name@version` is still
published. A missing asset, an extra asset, a manifest that does not match, a missing attestation
or a missing package version fails the job. A hit does not re-check the macOS signature or the
notarization: the publishing run verified those on the archives it published, and the hit path's
per-archive `gh attestation verify` proves those archives are still the bytes that run attested.
The manifest `diff` proves only that `SHA256SUMS.txt` is unchanged.

A stale, absent or unparsable receipt rebuilds the chain. The receipt is written only in the
`release` job, only after every post-publish check passed, and only on the miss path, so a skip can
never extend its own lifetime.

The save step carries `continue-on-error: true` on purpose: two runs can race for one cache key,
and a save that loses that race must not fail a publish that already succeeded. Losing the save
costs nothing but a full rebuild on the next run, so the tolerance is fail-open rather than a
hidden failure. It is the one place in either workflow where `continue-on-error` is not an
advisory scan ([the GitHub Actions standard](standards/github-actions/github-actions.standards.md#receipts-and-skips)).

### Release notes

Each Release page shows a **what changed** section first, then the install block and the asset
table. A pre-release notice, when there is one, sits above the what-changed section. The section is GitHub's generated list of merged pull requests since the comparison
base, followed by a **Full Changelog** compare link. Commits pushed without a pull request
appear only through that compare link. The list is not grouped into categories: the repository
has no `.github/release.yml`, so every merged pull request appears in one list.

The comparison base is computed by the `version` job, which runs
`scripts/next-release-version.sh --notes-base --for <tag>`, with the tag it just computed, for a manually dispatched run:

| Release being cut | Compared with |
|---|---|
| Stable (e.g. `0.1.7`) | The highest lower stable tag. Every RC on the line in between is covered by that one comparison, so the stable notes show everything since the last stable. |
| RC (e.g. `0.1.7-rc.10`) | The highest lower stable or RC tag. |

Rules that apply to both:

- The tag being released is never its own base, and `-dev.N` tags are ignored.
- The major-version guard is not applied to the tags below the released tag, so `v1.0.0-rc.2`
  compares with `v1.0.0-rc.1` even though no stable `v1.0.0` exists yet. A mistyped higher-major
  tag (for example `v1.0.6-rc.6` while releasing `v0.1.7-rc.10`) is above the released tag and
  is never the base.
- If no earlier tag qualifies (for example, the first release), the base is empty and the
  notes request omits `previous_tag_name`.

**Tag push.** A tag pushed by hand (`git tag v0.2.0 && git push origin v0.2.0`) has no
computed base. The notes request omits `previous_tag_name`, and GitHub uses its default
previous release.

**Failure is fail-closed.** Notes are generated inside the `release` job, after the approval
click. The job asks the generate-notes API up to two times, with a 10-second pause between
attempts. If both fail, the job stops before `gh release create`, so no tag and no Release
exist. Re-running the workflow is clean.

**Order on the page.** A pre-release notice (hyphenated versions only) comes first, then the
what-changed section, then the install heading. The notes are passed to `gh release create`
with `--notes-file`.

**Post-publish check.** After the Release is live, the job reads the notes back and verifies:

- the compare link `…/compare/<previous>...<tag>` is present (skipped when there is no base);
- the changes section appears before the install heading;
- the prerelease flag matches the version (set when it contains a hyphen).

As with the asset check, a failure here fails the job but does not unpublish the Release.

**Out of scope.** Grouping the list into categories by pull-request label is not configured.
There is no `.github/release.yml`, so GitHub's default flat list is used. Adding that file is
a separate change to the pipeline.

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
- Release notes automatically include a pre-release callout banner highlighting the candidate version. The banner sits above the what-changed section. Each tag type compares against a different earlier tag; see [Release notes](#release-notes).
- Nuget tool packages (`.nupkg`) carrying pre-release versions are published to GitHub Packages, allowing testing via `dotnet tool update --prerelease`. This is a maintainer/testing channel only, not a supported end-user install path (see [Other published artifacts](#other-published-artifacts)).

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

And for the Arbiter hook binary:

```bash
kyber-weave-arbiter --version
# or
kyber-weave-arbiter -v
```

Output format: exactly `kyber-weave <version>`, with the `+<sha>` build metadata removed. For example, a release candidate prints `kyber-weave 0.2.0-rc.1`. The output never contains a `+` suffix. The [local release loop](#verifying-a-release-locally) enforces this exact format on the published binaries; the release workflow's publish step does not run this check itself.

The same release also stamps the release version onto `dash/server/KyberDash.{Cli,Api,Mcp}` (`-p:Version` in `.github/workflows/release.yml` and `scripts/release-local.sh`) when publishing the `kyberdash`, `kyberdash-api`, and `kyberdash-mcp` binaries. The KyberDash Settings views, in the web dashboard and the tray, read `/api/kyber/version` from the engine (`Program.cs` assembly informational version with `+<sha>` stripped), so for a release they show the same version that `kyberdash --version` prints.

## Code signing status

Release integrity rests on HTTPS transport security plus a complete `SHA256SUMS.txt` manifest that covers all 51 assets (25 archives, 25 per-artifact SBOMs, and the aggregate SBOM). A pre-publish check verifies manifest completeness and correctness before `gh release create` runs, failing the job and preventing publication if assets are missing or unexpected (fail-closed). Post-publish checks verify the published assets and each archive's SBOM attestation and build provenance after the release is live; on mismatch they fail the job but the release is already public (detective, not preventive). Authenticode, GPG, and other code signatures are deferred per [issue #132](https://github.com/dpalfery/kyber-weave/issues/132);
Azure signing was ruled out on cost; GPG-signed manifests are not implemented; SignPath
Foundation is a possible but unverified future option. See [ADR 0027](adr/0027-release-integrity-checksums-signing-deferred.md)
for the complete decision record.

| Artifact | Signing Status | Notes |
|---|---|---|
| Windows tray installer (`kyberdash-tray-win-x64-setup.exe`) | **Not signed** | Deferred per [issue #132](https://github.com/dpalfery/kyber-weave/issues/132). Users typically see SmartScreen warning. |
| Windows `.exe` files (`kyber-weave`, `kyber-weave-mcp`, `kyber-weave-arbiter`, `kyberdash` in `win-x64` archives) | **Not signed** | Deferred per [issue #132](https://github.com/dpalfery/kyber-weave/issues/132). Users typically see SmartScreen warning. |
| macOS tray (`kyberdash-tray-darwin-*.zip`) | Developer ID-signed and notarized | Team ID `J2UNNQ466J`. Verified by `.github/workflows/release.yml` signature and notarization checks. |
| macOS CLI and MCP binaries | Not Developer ID-signed | `install.sh` clears the macOS quarantine attribute at install time ([scripts/install.sh:735–742](../scripts/install.sh)). |
| macOS KyberDash binary | Ad-hoc signed | Signed in the `build-kyberdash` job, `Publish KyberDash single-file for ${{ matrix.rid }}` step ([.github/workflows/release.yml](../.github/workflows/release.yml)). |
| Linux binaries | Unsigned | Standard for Linux. HTTPS transport and hash verification provide integrity. |

### Manifest completeness and the pre-publish check

The release workflow enforces that `SHA256SUMS.txt` lists exactly the 51 expected assets and
never itself. The published release contains those 51 assets plus `SHA256SUMS.txt` (52 files total).

The pre-publish check runs before `gh release create` and fails the job if the manifest is incomplete or incorrect, preventing publication. Post-publish checks run after the release is created and compare the published asset names against the 51 expected assets plus `SHA256SUMS.txt`, byte-compare the published manifest, and verify each archive's SBOM attestation and build provenance; on mismatch they fail the job (the release is already public, so these checks are detective only). `Verify published release assets` reads the chain receipt on a re-run, and the attestation checks download the published archives in that case; [Re-running a release](#re-running-a-release-the-chain-receipt) covers what a hit verifies.

- **5** CLI: `kyber-weave-linux-x64.tar.gz`, `kyber-weave-linux-arm64.tar.gz`,
  `kyber-weave-osx-x64.tar.gz`, `kyber-weave-osx-arm64.tar.gz`, `kyber-weave-win-x64.zip`
- **5** MCP: `kyber-weave-mcp-linux-x64.tar.gz`, `kyber-weave-mcp-linux-arm64.tar.gz`,
  `kyber-weave-mcp-osx-x64.tar.gz`, `kyber-weave-mcp-osx-arm64.tar.gz`, `kyber-weave-mcp-win-x64.zip`
- **5** Arbiter: `kyber-weave-arbiter-linux-x64.tar.gz`, `kyber-weave-arbiter-linux-arm64.tar.gz`,
  `kyber-weave-arbiter-osx-x64.tar.gz`, `kyber-weave-arbiter-osx-arm64.tar.gz`, `kyber-weave-arbiter-win-x64.zip`
- **5** KyberDash: `kyberdash-darwin-arm64.tar.gz`, `kyberdash-darwin-x64.tar.gz`,
  `kyberdash-linux-arm64.tar.gz`, `kyberdash-linux-x64.tar.gz`, `kyberdash-win-x64.zip`
- **3** Tray: `kyberdash-tray-darwin-arm64.zip`, `kyberdash-tray-darwin-x64.zip`, `kyberdash-tray-win-x64-setup.exe`
- **2** Squad: `kyber-squad-<version>.zip`, `kyber-squad-plugin-<version>.zip`
- **25** Per-artifact SBOMs: one `<archive-stem>.cdx.json` per published archive above
  (`artifact_sbom_name` in `scripts/syft-bootstrap.sh`), e.g.
  `kyber-weave-linux-x64.cdx.json`, `kyber-weave-arbiter-linux-x64.cdx.json`, `kyberdash-tray-win-x64-setup.cdx.json`,
  `kyber-squad-<version>.cdx.json`
- **1** Aggregate SBOM: `kyber-weave-sbom-<version>.cdx.json`

If an expected asset is missing or an unexpected file is present, the release fails before
publish with a diagnostic naming the cause. Each new asset requires updating the `EXPECTED_ASSETS` array in `scripts/verify-release-checksums.sh`; the matching build job and the count test must also be updated. Adding a supported platform or distribution format is a
deliberate step that must be coordinated with the release automation.

### The SBOM asset

Every release carries one aggregate Software Bill of Materials, `kyber-weave-sbom-<version>.cdx.json`
(CycloneDX JSON, format version 1.6), plus one per-artifact SBOM per published
archive (`<archive-stem>.cdx.json`, 25 in total). The aggregate is a
machine-readable inventory of the third-party components the release depends on
— NuGet, npm and Cargo — so that a consumer can feed it to a vulnerability
scanner or a licence audit without unpacking the binaries. Each per-artifact
SBOM is a byte-level file inventory of one archive. What each covers, and what
each deliberately does not, is documented in
[install.md](install.md#the-sbom-what-each-release-ships).

The aggregate is generated by `scripts/generate-sbom.sh <out-dir> <version>`, which is shared by the
release workflow, the CI validation job and `scripts/release-local.sh`, so all three produce
the same document for the same version.

What the aggregate SBOM covers, and what it deliberately does not, is documented in
[install.md](install.md#the-sbom-what-each-release-ships). In short: it is the
repository's dependency closure, not a byte-level scan of a published archive, and it
does **not** list the .NET runtime that the self-contained single-file binaries embed
(the single largest component of what actually ships) or test-only dependencies. The per-artifact
SBOMs close the byte-level half of that gap: each lists its archive's shipped
files, and states via `kyber-weave:sbom:not-measured` where runtime coverage was
not measurable (`dotnet-single-file`, `tauri-bundle`; Squad bundles
carry no marker). Every single-file .NET archive (`kyber-weave-*`, `kyber-weave-mcp-*`,
`kyber-weave-arbiter-*`, `kyberdash-*`) carries both the file catalogue and
the `dotnet-single-file` marker. SBOM attestation, once a follow-up, now ships with every release —
see below.

Two properties of that generator are worth knowing before changing it:

- **The generator is pinned by SHA-256.** It downloads a pinned Syft release tarball and
  verifies the hash before running it, and re-verifies the cached tarball on every run. A
  mismatch aborts rather than producing an SBOM from an unverified binary. `nuget.org` is
  never used: the generator is not a `dotnet tool`.
- **The .NET coverage depends on a publish, not on the lock files.** The .NET cataloger
  reads `*.deps.json`, which only a publish produces, so the script publishes
  `KyberWeave.Cli`, `KyberWeave.Mcp`, `KyberWeave.Arbiter`, and the three `dash/server`
  hosts (`KyberDash.Cli`, `KyberDash.Api`, `KyberDash.Mcp`)
  framework-dependent and non-single-file into a throwaway directory and copies only the
  `deps.json` files into the scanned tree. Without the
  .NET SDK it falls back to the checked-in `packages.lock.json` files — which under-report
  .NET, because `src/KyberWeave.Mcp` and the `dash/server` projects may have no lock files — and says so in its output rather
  than emitting a quietly incomplete SBOM.

The generated document is validated against the vendored CycloneDX 1.6 JSON schema before
it is accepted; see [`scripts/schemas/PROVENANCE.md`](../scripts/schemas/PROVENANCE.md) for
the schema's provenance and the validator's scope.

#### Per-artifact SBOMs

Each of the 20 published archives ships beside a sibling `<archive-stem>.cdx.json`,
named by `artifact_sbom_name` in `scripts/syft-bootstrap.sh` (strip `.tar.gz` /
`.zip` / `.exe`, append `.cdx.json`) so the generator and
`scripts/verify-release-checksums.sh` share one mapping and cannot drift apart.
They are generated by `scripts/generate-artifact-sboms.sh <assets-dir> <version>`,
which unpacks each archive into a throwaway directory and scans the extracted
tree with the same pinned Syft 1.55.0 the aggregate uses (one pin source in
`scripts/syft-bootstrap.sh`, re-verified on every run including from cache).
The scan runs with the file cataloger enabled
(`SYFT_FILE_METADATA_SELECTION=all`), so every shipped byte is listed as a file
component even when no package cataloger fires.

Unlike the aggregate generator, per-artifact validation does **not** require
npm/cargo/nuget ecosystems — an extracted archive rarely contains lockfiles, so
demanding them would fail every binary-only archive. The vendored schema, the
non-empty component list, and the resolvable dependency graph are still
enforced, and a rejected SBOM is removed so a later step cannot publish it
(fail-closed).

In `.github/workflows/release.yml` this is the `artifact-sboms` job: it runs
after `build`, `build-kyberdash`, `build-tray`, and `pack-squad` so every
archive exists, downloads only the build outputs (not the parallel aggregate
`sbom` job's upload, to avoid racing it), runs
`scripts/generate-artifact-sboms.sh`, and uploads the `*.cdx.json` siblings as
the `artifact-sboms` artifact for the release job.

#### SBOM attestations

Every SBOM is attested before anything publishes, in the `attest-sbom` job
(`needs: version, build, build-kyberdash, build-tray, pack-squad, sbom,
artifact-sboms`). The job downloads all archives plus both SBOM shapes, then
attests each one with `actions/attest@v4.2.2` in SBOM mode — not the deprecated
`attest-sbom` action — with the archive as the subject and the SBOM as the
predicate:

```yaml
- name: Attest kyber-weave-linux-x64 SBOM
  uses: actions/attest@v4.2.2
  with:
    subject-path: attest-assets/kyber-weave-linux-x64.tar.gz
    sbom-path: attest-assets/kyber-weave-linux-x64.cdx.json
```

Each per-artifact SBOM is attested against its own archive (25 attestations);
the aggregate SBOM is attested once against all archives via a glob over
`*.tar.gz`, `*.zip`, and `*.exe` (never the `*.cdx.json` predicates beside
them). Subject is always the archive; predicate is always the SBOM; the
predicate type is `https://cyclonedx.org/bom`, which the release job's
post-publish `Verify SBOM attestations` step pins when it runs
`gh attestation verify <archive> --repo <owner/repo> --predicate-type
https://cyclonedx.org/bom` over every published archive. Beside that step,
`Verify provenance attestations` runs the same check with `--predicate-type
https://slsa.dev/provenance/v1`, because the same job also mints build
provenance for every archive.

The job needs exactly four permissions: `id-token: write` (mints the OIDC token
for the Sigstore signing certificate), `attestations: write` (persists the
attestation), `artifact-metadata: write` (creates the storage record), and
`contents: read` (reads the repo context). No extra secrets or services.

Cost bound: attestation is one additional `ubuntu-latest` job with 27
`actions/attest` steps (25 per-artifact SBOM attestations, the aggregate SBOM
attestation, and one build-provenance attestation over every archive). Each
step is seconds of Sigstore signing with no downloads beyond the already-built
assets, so the release's added cost is bounded by minutes on the standard hosted
runner — no new runner class, no paid service, no per-attestation billing
beyond normal Actions minutes.

#### Build provenance

The same job mints **SLSA build provenance** for every archive, in one step
beside the SBOM attestations:

```yaml
- name: Attest build provenance for all archives
  uses: actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2
  with:
    subject-path: attest-assets/*.tar.gz, attest-assets/*.zip, attest-assets/*.exe
```

No `sbom-path` and no custom predicate: `actions/attest` runs in provenance
mode, binding each archive to the workflow's identity rather than making a
claim about its contents. The attestation reuses the `id-token` the SBOM
attestations already mint, so the job's permission block is unchanged, and it
is gated on the chain verdict like every other step in the job: a chain hit
mints nothing and verifies the published provenance instead.

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
   if you have only a subset of the 51 assets.
4. **SBOM attestation**: `gh attestation verify <archive> -R dpalfery/kyber-weave --predicate-type https://cyclonedx.org/bom`
   verifies that an archive is the one its SBOM was attested against (see
   [install.md](install.md#verifying-sbom-attestations)).
5. **Build provenance**: `gh attestation verify <archive> -R dpalfery/kyber-weave --predicate-type https://slsa.dev/provenance/v1`
   verifies that the archive was built by this repository's release workflow.

SmartScreen reputation is tracked per file, so binaries from a new release will typically warn
again. The documented hash-verification and **More info → Run anyway** flow in
[install.md](install.md#windows-unsigned-binaries-and-smartscreen) is the supported
path for Windows users until signing is implemented.

## The self-update sequence

`kyber-weave update` runs in two processes. The updating process downloads, verifies and swaps
the binaries, then does nothing but the hand-off. Every later step runs in the new kyber-weave,
so its assemblies and its hook templates come from the release being installed
([ADR 0065](adr/0065-the-self-updater-hands-everything-after-the-swap-to-the-new-binary.md)).
When the release is already installed ("already on"), nothing is swapped, and the same post-update
steps run in the updating process itself.

1. **Stage and verify.** Downloads the Release assets and checks each against `SHA256SUMS.txt`
   before installing anything.
2. **Rehearse.** Once, before the first binary is committed, the installed `kyber-weave --version`
   runs with its output discarded, under a 30-second bound. This loads the code that starts the
   hand-off while the old image is still whole. A rehearsal that cannot start, or that outlasts
   its bound, is logged as `the hand-off rehearsal did not complete (<message>); the hand-off will
   report any failure` and does not stop the update. A rehearsal that starts and exits non-zero is
   not logged, and does not stop the update either.
3. **Commit.** Each binary is swapped in. The hand-off starts only after the last one is committed.
4. **Hand off.** The updating process starts the installed `kyber-weave update <version>
   --finish-update`, passing on `--no-kyberdash`, `--no-menubar`, `--no-arbiter` and `--no-restart`
   when they were given. The updating process prints `finishing the
   update in the new kyber-weave…` before it starts the child, so that line comes from the old
   process. The child's output is inherited. The wait is 25 minutes, which covers the worst case of the tray restart steps (six captures at 120 s, the tray update at 300 s, two plugin steps at 120 s). On expiry the whole process tree is
   killed. The updating process exits with the child's exit code.
5. **Finish.** The new process runs the KyberDash steps as a changed release, then the arbiter hook
   refresh, in its own image. It downloads nothing and never starts another hand-off. On success it
   prints no outcome line.

Finish mode refuses before doing any work, with these messages:

- no version named: `usage: kyber-weave update <version> --finish-update. Name the version the update installed.`
- a version other than its own: `--finish-update finishes the update to the version that runs it. This kyber-weave is <current>, but <version> was asked for.`
- `--release-candidate`: `--finish-update cannot be combined with --release-candidate. It finishes one named version.`

`--finish-update` is a hidden option. Only the updater passes it.

**One outcome line.** The updating process prints the only outcome line on success:
`updated kyber-weave <version> → <path>`. A failure after the install is reported without rolling
anything back, in one of these forms:

- An IO, access, process or timeout failure after the install: `kyber-weave <version> is installed,
  but the last steps did not finish: <cause>. Run kyber-weave update <version> to finish them.` Exit 1.
- The new kyber-weave exits non-zero: `kyber-weave <version> is installed, but the post-update steps
  did not finish: the new kyber-weave exited <code>, and its output above says why. The binaries are
  already updated. Run kyber-weave update <version> to finish them.` The exit code is the child's.

A failed arbiter hook refresh is a warning with exit 0:
`warning: arbiter hooks were not refreshed: <cause>. The binaries are already updated; run kyber-weave
update <version> to retry.` The cause is `the project registry could not be read: <message>` when
the registry could not be read, `a program component could not be loaded (<assembly>): <message>`
for a failed assembly load, or otherwise the exception message. A corrupt registry still refreshes
the global hooks.

A pinned older release does not know `--finish-update`, so its hand-off fails. The message then
gives the pinned retry, `kyber-weave update <version>`. The first update away from a build that
predates this design (rc.21) is still run by that build's own updater, so it can show the old
`Could not load file or assembly YamlDotNet` error once. The binaries are installed. Run
`kyber-weave update <version>` with the version you are on, once more, and it takes the "already on"
path and refreshes the hooks. Do not use the bare form: it resolves the latest stable release, which on
a pre-release build can be an older one that predates `--finish-update`.

## Verifying a release locally

An update is always started by the **old** binary. The old binary runs the staging, the rehearsal, the swap and the hand-off; the steps after the swap (the KyberDash reconcile and the arbiter hook refresh) run in the new binary. A fix to what the old binary runs therefore
cannot be proven by the release that contains it — only by updating away from a build that
predates it. A fix to the finish-mode steps is exercised by the release that contains it, because the new binary runs them. Tag-and-wait cycles get the first kind wrong silently: the release ships, the same failure
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
[`ReleaseOrigin`](../src/KyberWeave.Application/Update/ReleaseOrigin.cs). It accepts **loopback
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

Run against a **published single-file binary**, never `dotnet run`. The failures this exists
to catch are a hand-off that does not start the new binary, and an old updating process that, after the
swap, cannot load an assembly the new bundle lays out at a different offset. Only a published single-file
bundle has that layout, so neither failure exists in any other shape.

The loop unpacks the `kyber-weave` and `kyber-weave-mcp` archives that `release-local.sh` publishes,
and after the self-update asserts that `kyber-weave --version` prints exactly `kyber-weave <version>`
and `kyber-weave-mcp --version` prints exactly `kyber-weave-mcp <version>`. An unstripped `+<sha>`
suffix or a default version therefore fails the loop. The release workflow's own publish step
(`.github/workflows/release.yml`) does not run `kyber-weave --version`; that exact-match check is the
loop's, not a step the workflow repeats.

### KyberDash in the loop

`release-local.sh` builds `kyberdash-<rid>.tar.gz` (or `.zip` on Windows) for this machine
the way the `build-kyberdash` job does: build the React dashboard (`npm --prefix dash run build:web`),
pack `dash/dist/dash` into `dash/dist-sea/web.json` with `dash/scripts/pack-sea-web.mjs`, and
publish the three `dash/server` hosts (`KyberDash.Cli`, `KyberDash.Api`, `KyberDash.Mcp`)
self-contained single-file with `web.json` embedded as `KyberDash.Api.web.json` (plus ad-hoc
`codesign` on macOS). The archive carries `kyberdash`, `kyberdash-api`, and `kyberdash-mcp`.
Both the
loop and the release job run `dash/scripts/sea-web-smoke.mjs` and `dash/scripts/sea-mcp-smoke.mjs`
against the staged binary — confirming `kyberdash web` serves the embedded SPA and `kyberdash mcp`
initializes over stdio, lists the eight `dash_*` tools, calls `dash_list_available_analytics_operations`,
and exits cleanly with no checkout and no access to operator state. The job is the authority.
`ReleaseTests.LocalKyberDashBuildMatchesTheReleaseJob` and `AssertEmbedsWebDashboard` pin those
web-build, packing, publish, archive contents (`kyberdash`, `kyberdash-api`, `kyberdash-mcp` and
`THIRD_PARTY_NOTICES.md`), and
smoke-test commands. The build needs `dotnet`, `node`, and `npm` on `PATH`. A failed build fails
the loop; `--no-kyberdash` is the explicit opt-out.

Each case starts from a fresh copy of a staged build, with its own `HOME`, so the tray
record the updater reads is a fixture and never this machine's:

| Case | Asserts |
|---|---|
| `kyberdash-replaced` | An installed `kyberdash` is replaced, and the new one answers `--version`. |
| `kyberdash-opt-out` | `--no-kyberdash` updates the CLI and leaves `kyberdash` and the tray alone. |
| `tray-opt-out` | `--no-menubar` replaces `kyberdash` and leaves the tray alone. |
| `tray-delegated` | With a `tray.json`, the update runs the new `kyberdash`'s tray restart verbs (`menubar --quit` first), and that step's failure fails the update by name. |
| `kyberdash-floor` | A release below the KyberDash floor, which carries no `kyberdash` archive, still updates the CLI and MCP. It needs no build, so it runs under `--no-kyberdash` too. |

The main self-update also asserts that an absent `kyberdash` stays absent.

### Arbiter hooks after the swap

The `arbiter-hooks-after-swap` case covers the step the other cases never reach. It needs the Squad
assets, so `--skip-squad` skips it. It installs global and project arbiter state with the product,
points the global Cursor hook at a legacy `/legacy/kyber-weave-arbiter` path, and then updates to its
own release, `99.0.0-local.layout`. That release is published from the same tree with 6000 bytes of
padding in its Copyright metadata. The padding moves every assembly after `kyber-weave.dll` to a new
offset, because two builds of one tree lay out identically and hide the defect. The case fails unless
the from and to binaries differ in bytes and the to file is at least a page larger. It proves that a
real swap exits 0, that the output has no `Could not load file or assembly` and no `kyber-weave: error:`,
that the refresh is reported, and that the stale hook is rewritten to run `kyber-weave-arbiter` again.

The tray step cannot succeed in the loop. Linux has no tray, and the local release carries
no tray installer. So `tray-delegated` proves that the step ran and failed by name, not
that a tray was installed. Its first run found the tray step had never run at all: the root
`--version` swallowed the updater's `menubar --update --version <v>`, so `kyberdash`
printed its version and exited 0.

Cross-RID builds are supported by `dotnet publish`, though macOS ad-hoc `codesign` requires a macOS host.

## Continuous integration security

`.github/workflows/ci.yml` is the PR gate for this repository. Besides build, test, pack,
and RID publish smoke, it runs blocking security jobs modeled on the same tools used in
sibling product repos — without Azure or container scans, which do not apply here:

| Job | What it covers |
|---|---|
| Build and test | Restore, `dotnet format` (whitespace + curated style), NuGet audit, build, test, pack |
| CodeQL (`csharp`, `javascript-typescript`) | SAST with `security-extended` queries, no rule or path exclusions |
| Trivy filesystem | Dependency, misconfig, secret, and license findings at HIGH/CRITICAL |
| Semgrep Community | Additional SAST (`p/default`, ERROR) |
| gitleaks | Secret scan of the full history fetch |
| Skill and docs gate | Dogfoods the PR's CLI: `skill validate` / `lint` / `scan` on `.apm/skills/kyber-docs-authoring`, `.apm/skills/kyber-docs-integrity-review`, `.apm/skills/kyber-docs-onboarding-and-assessment`, and `.apm/skills/kyber-weave-cli`, plus `docs validate`. `codegraph`, `kw-onboarding` and `kyber-weave-model-profile` are gated only by the review council's `skill-*` gates, and `spec-conductor` by neither |

CodeQL runs with zero `paths`/`paths-ignore` exclusions and zero `query-filters`: after retiring
the TypeScript live-gate script (`opencode-live-gate/run.mjs`), no `js/http-to-file-access`
suppression is needed. The owner and review date live in `.github/codeql/codeql-config.yml`,
and `CodeQlConfigTests` asserts both that metadata and the absence of rule or path exclusions.

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
