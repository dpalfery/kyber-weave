---
id: adr/0042-release-sbom-cyclonedx-syft
title: "Every Release Ships an Aggregate CycloneDX SBOM, With the Coverage It Does and Does Not Have"
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-25
---

# ADR 0042: Every Release Ships an Aggregate CycloneDX SBOM, With the Coverage It Does and Does Not Have

## Status

Accepted, 2026-10-21. Records the decision to attach one aggregate CycloneDX JSON SBOM to
every GitHub Release, the tool and format chosen, the coverage that SBOM has, and — as
part of the same decision — the coverage it demonstrably does not have.

Amended, 2026-10-25 ([issue #417](https://github.com/dpalfery/kyber-weave/issues/417)):
keep the aggregate and add one SBOM per published archive (20) plus a CycloneDX
SBOM attestation for every SBOM. Decision 1 below records the amendment; the new
coverage contract D1–D8 records what the per-artifact SBOMs do and do not cover
and how attestation binds each SBOM to its archive.

## Context

Every release ships self-contained binaries (CLI, MCP, KyberDash, tray, Squad bundles)
plus `SHA256SUMS.txt`. There was no machine-readable inventory of the third-party
components inside a release, so a consumer or security reviewer could not feed the
release to a vulnerability scanner or a licence audit without unpacking every archive
and reconstructing the dependency closure by hand.

Two constraints shaped the answer. `nuget.org` is never used as a distribution channel
in this repository ([ADR 0027](../adr/0027-release-integrity-checksums-signing-deferred.md)
and `docs/distribution.md`), so a generator cannot be acquired from it — which rules out
`dotnet tool install CycloneDX`. And the release path already downloads and verifies one
external binary (Node, for the KyberDash SEA build), so the supply-chain cost of the
answer was a real consideration, not a formality.

The three ecosystems in this repository resolve their dependencies in three different
ways, and the .NET one is the awkward case:

- **npm** — four `package-lock.json` files (`dash/`, `dash/web/`, `dash/tray/ui/`,
  `npm/`).
- **Cargo** — `dash/tray/src-tauri/Cargo.lock`.
- **NuGet** — `packages.lock.json` exists for `KyberWeave.Cli` and `KyberWeave.Core`
  but **not** for `KyberWeave.Mcp`, and no `RestorePackagesWithLockFile` is set anywhere,
  so the checked-in lock files are neither complete nor build-enforced. A scan that
  relies on them silently under-reports .NET.

The .NET cataloger does not read `packages.lock.json` for the full closure. It reads
`*.deps.json`, and a `deps.json` exists only as the output of a publish. That is why the
.NET input is a publish and not a lock file.

## Decision

1. **Every release attaches one aggregate SBOM, `kyber-weave-sbom-<version>.cdx.json`,
   in CycloneDX JSON format version 1.6 — and, since 2026-10-25, one per-artifact
   SBOM per published archive (`<archive-stem>.cdx.json`, 20 in total).**
   Keep-aggregate-and-add-per-artifact: the aggregate keeps its versioned name
   and its dependency-closure meaning, and the per-artifact SBOMs sit beside
   their archives. The release asset inventory grows from 20 archives + 1
   aggregate (21 manifest entries) to 20 archives + 20 per-artifact SBOMs + 1
   aggregate (41 manifest entries). Per-artifact SBOMs were a follow-up in the
   original decision, not a rejection — this amendment is that follow-up
   landing. The coverage contract D1–D8 below is part of the same amendment.

2. **The generator is Anchore Syft, pinned by version *and* SHA-256, downloaded from
   its GitHub release tarball.** A checksum mismatch aborts the run rather than
   producing an SBOM from an unverified binary, and the cached tarball is re-verified
   on every run — including when it was restored from a CI cache — so a restored cache
   is checked before it is used rather than trusted. Syft is used because one tool
   covers all three ecosystems, which keeps the release path at exactly one downloaded
   binary.

3. **The .NET input is a throwaway, framework-dependent, non-single-file `dotnet
   publish` of `KyberWeave.Cli` and `KyberWeave.Mcp`, of which only the resulting
   `*.deps.json` files enter the scanned tree.** A single-file publish folds the
   dependency closure into the executable and emits no readable `deps.json`; a
   self-contained one adds the runtime packs as components of their own. The publish
   happens *outside* the scanned tree because a publish directory is full of PE
   binaries, and Syft's .NET binary cataloger turns each DLL it cannot pair with a
   claimed runtime path into a separate "dotnet binary" component — a bogus NuGet
   package per shipped DLL, including for our own assemblies. The `deps.json` alone
   carries the whole closure and does not need the DLLs on disk.

4. **The npm and Cargo inputs are the repository's lock files.** Syft parses
   `package-lock.json` and `Cargo.lock` directly, so neither needs its toolchain
   installed and neither needs an `npm ci`.

5. **The generated document is validated against the CycloneDX 1.6 JSON Schema before
   it is accepted, and nothing is written unless every check passes.** The schema is
   vendored under `scripts/schemas/`, pinned to an upstream commit with its hashes
   recorded in `scripts/schemas/PROVENANCE.md`, so validation is hermetic and offline
   and the release path still runs exactly one downloaded binary. `cyclonedx-cli` was
   considered and rejected: it publishes no checksum manifest with its releases, so
   its hash would have to be taken on trust from this repository. The validator
   implements the Draft 7 subset the schema uses and is checked against the reference
   implementation.

6. **The SBOM is a release asset and is covered by `SHA256SUMS.txt`.** The expected
   asset list in `scripts/verify-release-checksums.sh` gains the SBOM, so the
   fail-closed pre-publish check rejects a release whose SBOM is missing, and the
   manifest names it. The SBOM's integrity therefore rests on the same mechanism as
   every other asset under [ADR 0027](../adr/0027-release-integrity-checksums-signing-deferred.md).

7. **The SBOM's coverage limits are documented, not buried.** It is the repository's
   dependency closure, not a byte-level scan of a published archive. It does **not**
   list the .NET runtime that the self-contained CLI and MCP binaries embed — the
   single largest component of what actually ships — because a framework-dependent
   publish's `deps.json` lists NuGet packages only. It does not list the Node runtime
   that the KyberDash single-executable embeds, for the same class of reason. It does
   not list test-only dependencies. `docs/install.md` states all of this to the
   consumer.

8. **The `packages.lock.json` path is a local-development fallback, not release-grade
   coverage.** No lock file is added for `KyberWeave.Mcp` and no
   `RestorePackagesWithLockFile` is introduced. The generator states the degradation
   loudly and the nuget ecosystem check drops to a warning rather than failing, so a
   developer without the SDK is not blocked. The release and CI jobs always have the
   SDK, so what is published is the full closure — the staged publish recovers 32
   NuGet packages the lock files miss.

9. **SBOM attestation was out of scope for the original decision; since 2026-10-25
   it ships with every release (D6–D7).** The SBOM's integrity rested on
   `SHA256SUMS.txt`. Attesting it, together with per-artifact scanning of the
   published archives, was a follow-up issue — now
   [issue #417](https://github.com/dpalfery/kyber-weave/issues/417), decided
   below. The manifest mechanism from [ADR 0027](../adr/0027-release-integrity-checksums-signing-deferred.md)
   is unchanged; attestation binds each SBOM to its archive on top of it.

## Per-artifact SBOMs and attestations — coverage contract D1–D8 (issue #417)

D1. **Keep the aggregate and add one SBOM per published archive.**
   `scripts/generate-artifact-sboms.sh <assets-dir> <version>` emits 20
   siblings named by `artifact_sbom_name` in `scripts/syft-bootstrap.sh` as
   `<archive-stem>.cdx.json` (strip `.tar.gz` / `.zip` / `.exe`, append
   `.cdx.json`); `scripts/verify-release-checksums.sh` shares that one function
   so the manifest and the generator cannot drift apart. The aggregate keeps its
   versioned name and is still built by `scripts/generate-sbom.sh`.

D2. **One Syft pin serves both generators.**
   Syft 1.55.0, pinned by version *and* SHA-256 in `scripts/syft-bootstrap.sh`
   and sourced by both generators, downloaded from its Anchore GitHub release
   tarball, re-verified on every run including from cache. Bumping the pin
   means changing the version and the hashes together, in that one file.

D3. **A per-artifact file catalogue is provenance, not runtime coverage.**
   Each SBOM is produced by unpacking its archive into a throwaway directory
   and scanning the extracted tree with the file cataloger enabled
   (`SYFT_FILE_METADATA_SELECTION=all`), so every shipped byte is listed as a
   file component. That list proves which bytes shipped. It does not prove
   which runtime dependencies those bytes contain: a file-level catalogue is
   not runtime coverage, and the document never claims otherwise.

D4. **Archives whose runtime closure cannot be measured say so loudly — and a
   Node single-executable carries both the catalogue and the marker.**
   `kyber-weave-*` / `kyber-weave-mcp-*` (single-file .NET publishes, closure
   folded into the executable) carry `kyber-weave:sbom:not-measured` =
   `dotnet-single-file`; `kyberdash-tray-*` (opaque Tauri bundles/installers)
   carry `tauri-bundle`; `kyberdash-*` Node single-executables (runtime and
   dashboard bundled in the SEA blob) carry `node-sea` **alongside** the file
   catalogue entry — both, never one instead of the other. Squad skill zips
   carry no marker: their file list *is* their content, with no hidden runtime
   closure to disclaim.

D5. **Per-artifact validation drops the ecosystem requirement and keeps
   everything else fail-closed.**
   Unlike the aggregate, a per-artifact SBOM is not required to contain
   npm/cargo/nuget ecosystems — an extracted archive rarely contains lockfiles,
   so demanding them would fail every binary-only archive. The vendored
   CycloneDX 1.6 schema, the non-empty component list, and the resolvable
   dependency graph are still enforced; a rejected SBOM is deleted so no later
   step can publish it, and nothing is written until every check passes.

D6. **Attest with `actions/attest` in SBOM mode, not the deprecated
   `attest-sbom` action.**
   The `attest-sbom` job in `.github/workflows/release.yml` uses
   `actions/attest@v4.1.1` with `sbom-path` and `subject-path`. The deprecated
   `attest-sbom` action is deliberately not used; SBOM mode is what gives the
   CycloneDX predicate type rather than a provenance claim.

D7. **Subject is always the archive; predicate is always the SBOM.**
   Each per-artifact SBOM is attested against its own archive (20
   attestations, `subject-path: <archive>`, `sbom-path: <stem>.cdx.json`); the
   aggregate SBOM is attested once against every published archive via a glob
   over `*.tar.gz` / `*.zip` / `*.exe` that never matches the `*.cdx.json`
   predicates beside them. Verification pins the predicate type —
   `gh attestation verify <archive> -R dpalfery/kyber-weave --predicate-type
   https://cyclonedx.org/bom` — which rejects a provenance attestation
   masquerading as an SBOM. Permissions are exactly `id-token: write`
   (OIDC/Sigstore), `attestations: write`, `artifact-metadata: write`, and
   `contents: read`.

D8. **The manifest and the release job gate all of it.**
   `SHA256SUMS.txt` covers all 41 assets; the pre-publish check fails closed
   before `gh release create` on any missing or unexpected entry. The `release`
   job `needs: version, build, build-kyberdash, build-tray, pack-squad, sbom,
   artifact-sboms, attest-sbom`, so nothing publishes unattested, and its
   post-publish steps verify the asset inventory, byte-compare the manifest,
   and verify each archive's attestation (detective: the release is already
   public if they fail).

## Alternatives considered

- **`dotnet tool install CycloneDX`.** The obvious .NET answer, and it is unavailable:
  `nuget.org` is never used as a distribution channel here, so the release pipeline
  must not acquire a build-time tool from it. This is the constraint the issue itself
  names.

- **`cyclonedx-cli validate` for the schema check.** It is the canonical validator and
  embeds the schemas. Rejected because it publishes no checksum manifest with its
  releases, so pinning it means recording a hash this repository computed itself. The
  schema is vendored instead, which keeps the release path at one downloaded binary
  and makes validation hermetic.

- **Scanning the publish directory in place.** Rejected: it produces one bogus NuGet
  component per shipped DLL. Publishing outside the scanned tree and copying in only
  the `*.deps.json` avoids it.

- **Relying on `packages.lock.json` for .NET.** Rejected: `KyberWeave.Mcp` has no lock
  file, so the MCP server's entire NuGet closure — `ModelContextProtocol`,
  `ModelContextProtocol.Core` and the whole `Microsoft.Extensions.Hosting` tree — would
  be absent from the published SBOM, with nothing to signal the omission.

- **One SBOM per artifact.** Rejected in the original decision as multiplying the
  release asset list for no extra information, since every asset is built from
  the same locked dependency set. Revisited and adopted on 2026-10-25 (D1–D5
  above) once byte-level accuracy, not dependency-closure equality, became the
  goal: the per-artifact SBOMs catalogue shipped bytes and mark unmeasurable
  runtimes, which the aggregate by construction cannot.

- **GitHub's dependency-graph SBOM export.** Rejected by the issue: it reflects
  repository manifests rather than what is built, and it is not attached to the release.

## Consequences

- **A consumer gets a machine-readable inventory of the third-party libraries a
  release depends on, and can feed it to a scanner.** It is covered by the same
  manifest as the binaries, so the existing verification command checks it.

- **The SBOM is not a statement about what is inside an archive — the per-artifact
  SBOMs are, with stated limits.** The aggregate remains an inventory of the
  build inputs. Each per-artifact SBOM is a file-level catalogue of its archive
  (D3), with embedded runtimes marked not-measured rather than papered over
  (D4). A consumer who needs runtime closures accounted for gets an honest
  boundary, not a silent omission.

- **Adding a release asset still requires editing the expected asset list in
  `scripts/verify-release-checksums.sh`**, as [ADR 0027](../adr/0027-release-integrity-checksums-signing-deferred.md)
  records. The aggregate SBOM added one entry to that list; the per-artifact
  amendment adds twenty more plus twenty attestation bindings, taking the
  manifest from 21 to 41 entries (42 files published with `SHA256SUMS.txt`).
  The `artifact_sbom_name` mapping in `scripts/syft-bootstrap.sh` is shared so
  the two lists cannot drift apart.

- **The Syft pin is a maintenance obligation.** A version bump changes the pinned
  version and SHA-256 constants in `scripts/syft-bootstrap.sh` and the cache key
  in the release workflow's SBOM jobs. The generators fail closed if the two
  disagree, so a stale pin is a loud failure rather than a silent drift.

- **The generators are the only places that know how the SBOMs are built.** The release
  workflow, the CI job and `scripts/release-local.sh` all call
  `scripts/generate-sbom.sh` for the aggregate, so the published aggregate SBOM
  and the locally produced one are the same document for the same version.
  `scripts/generate-artifact-sboms.sh` is the single per-artifact counterpart,
  sharing the Syft pin above.

## Related

- [Issue #383: Generate and publish an SBOM with each release](https://github.com/dpalfery/kyber-weave/issues/383) — the tracker for the original decision
- [Issue #417: Per-artifact SBOMs and attestation](https://github.com/dpalfery/kyber-weave/issues/417) — the tracker for the D1–D8 amendment
- [ADR 0027: Release Integrity via SHA-256 Checksums with Code Signing Deferred](../adr/0027-release-integrity-checksums-signing-deferred.md) — the integrity mechanism the SBOM inherits
- [`docs/install.md#the-sbom-what-each-release-ships`](../install.md#the-sbom-what-each-release-ships) — the consumer-facing description of the SBOM and its limits
- [`docs/distribution.md#the-sbom-asset`](../distribution.md#the-sbom-asset) — the maintainer documentation of the asset and its generator
- [`scripts/schemas/PROVENANCE.md`](../../scripts/schemas/PROVENANCE.md) — the schema's upstream provenance and the validator's scope
