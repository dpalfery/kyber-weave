---
id: adr/0042-release-sbom-cyclonedx-syft
title: "Every Release Ships an Aggregate CycloneDX SBOM, With the Coverage It Does and Does Not Have"
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-21
---

# ADR 0042: Every Release Ships an Aggregate CycloneDX SBOM, With the Coverage It Does and Does Not Have

## Status

Accepted, 2026-10-21. Records the decision to attach one aggregate CycloneDX JSON SBOM to
every GitHub Release, the tool and format chosen, the coverage that SBOM has, and — as
part of the same decision — the coverage it demonstrably does not have.

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
   in CycloneDX JSON format version 1.6.** One document per release, not one per
   artifact. The aggregate keeps the release asset inventory simple (20 → 21 assets)
   and matches the fact that every asset in a release is built from the same locked
   dependency set. Per-artifact SBOMs are a follow-up, not a rejection.

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

9. **SBOM attestation is out of scope for this decision.** The SBOM's integrity rests
   on `SHA256SUMS.txt`. Signing or attesting it is a follow-up issue, together with
   per-artifact scanning of the published archives.

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

- **One SBOM per artifact.** Rejected for now: it multiplies the release asset list
  and the manifest entries for no extra information, since every asset in a release is
  built from the same locked dependency set. It is the natural home for the
  per-artifact follow-up, where byte-level accuracy is the goal.

- **GitHub's dependency-graph SBOM export.** Rejected by the issue: it reflects
  repository manifests rather than what is built, and it is not attached to the release.

## Consequences

- **A consumer gets a machine-readable inventory of the third-party libraries a
  release depends on, and can feed it to a scanner.** It is covered by the same
  manifest as the binaries, so the existing verification command checks it.

- **The SBOM is not a statement about what is inside an archive.** It is an inventory of
  the build inputs. A consumer who needs the embedded runtimes accounted for must scan
  the published archives; that gap is documented rather than papered over, and it is
  tracked as a follow-up.

- **Adding a release asset still requires editing the expected asset list in
  `scripts/verify-release-checksums.sh`**, as [ADR 0027](../adr/0027-release-integrity-checksums-signing-deferred.md)
  records. The SBOM added one entry to that list and one count to the test that
  asserts it.

- **The Syft pin is a maintenance obligation.** A version bump changes the pinned
  SHA-256 constants in `scripts/generate-sbom.sh` and the cache key in the two
  workflows. The generator fails closed if the two disagree, so a stale pin is a loud
  failure rather than a silent drift.

- **The generator is the only place that knows how the SBOM is built.** The release
  workflow, the CI job and `scripts/release-local.sh` all call
  `scripts/generate-sbom.sh`, so the published SBOM and the locally produced one are
  the same document for the same version.

## Related

- [Issue #383: Generate and publish an SBOM with each release](https://github.com/dpalfery/kyber-weave/issues/383) — the tracker for this decision
- [ADR 0027: Release Integrity via SHA-256 Checksums with Code Signing Deferred](../adr/0027-release-integrity-checksums-signing-deferred.md) — the integrity mechanism the SBOM inherits
- [`docs/install.md#the-sbom-what-each-release-ships`](../install.md#the-sbom-what-each-release-ships) — the consumer-facing description of the SBOM and its limits
- [`docs/distribution.md#the-sbom-asset`](../distribution.md#the-sbom-asset) — the maintainer documentation of the asset and its generator
- [`scripts/schemas/PROVENANCE.md`](../../scripts/schemas/PROVENANCE.md) — the schema's upstream provenance and the validator's scope
