---
id: plans/2026-10-21-issue-383-release-sbom
title: "Generate and Publish an SBOM With Each Release"
doc-type: plan
status: archived
component: Distribution
owner: dpalfery
last-reviewed: 2026-10-21
development-mode: test-first
---

# Generate and Publish an SBOM With Each Release

**Status: Complete 2026-10-21 — executed and archived in the pull request that finished
it.** Authored 2026-10-21 under `autonomy: autonomous`. The scope and the two locked
decisions (D-A, D-B) were relayed by the human after reviewing a local spike; the
remaining decisions were answered during execution and are recorded in §5. The plan is
archived here because the pull request that finished it also archives it, per
`docs/plans/README.md`.

**Development mode:** `test-first` (the default; no opt-out supplied).
**Autonomy:** `autonomous`.

---

## 1. Problem and goal

### 1.1 Request

Issue [#383](https://github.com/dpalfery/kyber-weave/issues/383): releases ship
self-contained binaries plus `SHA256SUMS.txt`, but no Software Bill of Materials. A
consumer or security reviewer cannot see which third-party components are inside a
given release, and cannot feed it to a vulnerability scanner or a licence audit.

### 1.2 Goal

Attach one aggregate CycloneDX JSON SBOM to every GitHub Release, listing the
third-party components the release depends on across the .NET, npm and Cargo
ecosystems; cover it by the existing `SHA256SUMS.txt` manifest; prove generation in CI
without needing a release; and document both where the SBOM is and what it does not
tell a consumer.

### 1.3 Scope (authorized)

- A reusable generator, `scripts/generate-sbom.sh <out-dir> <version>`, shared by the
  release workflow, the CI workflow and the local release script.
- The SBOM as a release asset: the expected-asset list, the manifest, the release
  notes, and the local release tree.
- A CI job that proves generation and validates the output without a real release.
- Documentation of the asset, of how to verify it, and — explicitly — of the coverage
  it does not have.
- A follow-up issue for per-artifact scanning and SBOM attestation.

Out of scope: per-artifact SBOMs, SBOM attestation, and any change to the .NET
lockfile story (D-B).

## 2. Current state (verified before coding)

- `.github/workflows/release.yml` jobs: `authorize` → `version` → `build`,
  `build-kyberdash`, `build-tray`, `pack-squad` → `release`, `gh-packages`. The
  `release` job downloads every artifact with `pattern: "*"` + `merge-multiple: true`,
  runs `scripts/verify-release-checksums.sh release-assets <version>`, publishes with
  `gh release create ... release-assets/*`, then re-verifies the published inventory
  using `verify-release-checksums.sh --list` as the single authoritative asset list.
- `scripts/verify-release-checksums.sh` hardcoded exactly 20 expected assets and
  **failed on any unexpected file**. Adding an SBOM without touching it would have
  broken the release.
- `tests/KyberWeave.Tests/ReleaseTests.cs` duplicated the 20-asset array for fixture
  tests and asserted the count in four places.
- `scripts/release-local.sh` reproduced the same asset names locally.
- Ecosystems present: `KyberWeave.sln` (Cli, Core, Mcp); `dash/`, `dash/web/`,
  `dash/tray/ui`, `npm/` (`package-lock.json`); `dash/tray/src-tauri/Cargo.lock`.
- `packages.lock.json` existed for `KyberWeave.Cli` and `KyberWeave.Core` but **not**
  `KyberWeave.Mcp`, and no `RestorePackagesWithLockFile` was set anywhere — so
  lockfile-only scanning would silently under-report .NET.
- Constraint confirmed: `nuget.org` is never used as a distribution channel, so a
  `dotnet tool install CycloneDX` generator was out.

## 3. What was delivered

| Area | Delivered |
|---|---|
| Generator | `scripts/generate-sbom.sh` — pinned Syft, staged .NET publish, one scan, normalisation, validation, atomic write |
| Schema validation | `scripts/validate-sbom.py` + `scripts/schemas/` (three vendored upstream files, pinned to an upstream commit, hashes in `PROVENANCE.md`) |
| Asset list | `scripts/verify-release-checksums.sh` expects 21 assets; the 21st is the SBOM |
| Release | New `sbom` job in `release.yml`; `release` waits for it; the release notes gained an SBOM row |
| CI | New required `sbom` job in `ci.yml`, aggregated by `ci-summary` |
| Local | `scripts/release-local.sh` generates the SBOM, so the local tree matches the published one |
| Tests | `tests/KyberWeave.Tests/SbomTests.cs` (10 tests) plus the updated asset-count assertions in `ReleaseTests.cs` |
| Docs | `docs/install.md` (consumer view), `docs/distribution.md` (maintainer view), ADR 0042 (the decision) |

## 4. Measured results

Generated locally against the real .NET SDK (10.0.402) with Syft 1.55.0:

| | Components | `pkg:cargo` | `pkg:npm` | `pkg:nuget` | no purl |
|---|---|---|---|---|---|
| With the .NET SDK (what CI and a release produce) | 720 | 486 | 185 | 38 | 11 |
| Without the .NET SDK (the local-development fallback) | 686 | 486 | 185 | 6 | 9 |

The staged publish recovers **32 NuGet packages** the lock-file path misses:
`ModelContextProtocol`, `ModelContextProtocol.Core` and the whole
`Microsoft.Extensions.Hosting` tree — the MCP server's closure, which
`src/KyberWeave.Mcp`'s missing lock file had hidden.

## 5. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which SBOM format? | **CycloneDX JSON**, `specVersion` 1.6, pinned explicitly as `cyclonedx-json@1.6`. | The issue's primary option. The pin is not optional: Syft 1.55.0 defaults to 1.7 (F1). | ANSWERED — orchestrator plan, D1 |
| D2 | Which generator? | **Anchore Syft**, downloaded from a pinned GitHub release tarball and verified by SHA-256, fail-closed on mismatch. | One tool covers .NET, npm and Cargo; avoids nuget.org entirely. The cached tarball is re-verified on every run, including when restored from a CI cache. | ANSWERED — orchestrator plan, D2 |
| D3 | One SBOM or many? | **One aggregate SBOM** per release: `kyber-weave-sbom-<version>.cdx.json`. | Keeps the asset inventory simple (20 → 21). Every asset in a release is built from the same locked dependency set, so per-assifact SBOMs carry no extra information today. Per-artifact scanning is the follow-up. | ANSWERED — orchestrator plan, D3 |
| D4 | What is the .NET input? | A throwaway **framework-dependent, non-single-file `dotnet publish`** of Cli + Mcp; only the resulting `*.deps.json` enters the scanned tree. | A single-file publish emits no readable `deps.json`; a self-contained one adds the runtime packs as components. The publish runs outside the scanned tree (F4). The staged publish recovers 32 NuGet packages the lock files miss. | ANSWERED — orchestrator plan, D4, refined by F3/F4 |
| D5 | What are the Node and Rust inputs? | Scan the repository tree so the four `package-lock.json` files and `Cargo.lock` are cataloged from lock files. | Syft parses both directly, so neither needs its toolchain installed and neither needs an `npm ci`. | ANSWERED — orchestrator plan, D5 |
| D6 | Where does generation live? | **`scripts/generate-sbom.sh <out-dir> <version>`**, shared by `release.yml`, `ci.yml` and `scripts/release-local.sh`. | One implementation, so the published SBOM and the locally produced one are the same document for the same version. | ANSWERED — orchestrator plan, D6 |
| D7 | Is attestation in scope? | **No.** Out of scope for this issue; a follow-up issue covers per-artifact scanning and attestation. | The SBOM's integrity rests on `SHA256SUMS.txt`, which is the same mechanism as every other asset (ADR 0027). | ANSWERED — orchestrator plan, D7; follow-up filed |
| D-A | Scope and publishing? | **Full integration, and a pull request against `main`.** Publishing authorized for this work; `aa05e3d` kept as the base. | — | ANSWERED — human decision relayed by the orchestrator, 2026-10-21 |
| D-B | Do we fix the .NET lock files? | **No.** Rely on the staged `deps.json` path only. Do not add a `packages.lock.json` for `KyberWeave.Mcp` and do not add `RestorePackagesWithLockFile`. The lock-file path stays a **local-development fallback**, documented as incomplete. | The staged publish is complete, so the fallback is never release-grade. Documenting it as a fallback is what keeps nobody from mistaking it for coverage. | ANSWERED — human decision relayed by the orchestrator, 2026-10-21 |
| D8 | How is schema validity checked? | **Vendor the upstream CycloneDX 1.6 schema** (pinned to an upstream commit, hashes recorded in `PROVENANCE.md`) and validate with a purpose-built Draft 7 subset validator, `scripts/validate-sbom.py`. | `cyclonedx-cli` publishes no checksum manifest with its releases, so pinning it means recording a hash this repository computed itself. Vendoring keeps the release path at exactly one downloaded binary and makes validation hermetic. The validator is checked against the reference `jsonschema` implementation. | ANSWERED — autonomous: recommendation accepted (source: D2's fail-closed pin requirement) |
| D9 | Syft emits one component per lock file that names a package. Collapse them? | **Yes — collapse by purl by default**, with `--no-dedupe` to keep the raw list. A consumer counting components should count packages. | Collapsing can make two dependency entries land on the same ref, which CycloneDX rejects (F7); entries sharing a ref are merged, taking the union of their `dependsOn`. | ANSWERED — autonomous: recommendation accepted |
| D10 | First-party code reached the component list. Filter it? | **Yes.** Drop the `packages.lock.json` project reference and the root project of each `deps.json`. | The root component already represents the product; listing them again would claim the release depends on itself. Syft's deps.json cataloger excludes project references by default but its packages-lock cataloger does not, and the deps.json root is emitted in its own right. The deps.json roots are discovered from what the publish produced, not hard-coded. | ANSWERED — autonomous: recommendation accepted |
| D11 | Should CI cache the pinned Syft tarball? | **Yes** — `actions/cache` on the documented cache root `~/.cache/kyber-weave`, keyed by Syft version and runner platform, in both the CI and release jobs. | The generator re-verifies the cached tarball's SHA-256 on every run, so a restored cache is checked before use rather than trusted. Saves a ~15 MB download per run. | ANSWERED — autonomous: recommendation accepted |
| D12 | When is the plan archived? | **In the pull request that finishes it**, as `docs/plans/README.md` requires. | `docs validate --merge-ready` fails on any plan left in `docs/plans/` (`KW-DOC-LIFECYCLE-003`). | ANSWERED — `docs/plans/README.md` policy |
| F1 | Syft 1.55.0 defaults to `specVersion` 1.7, not 1.6. | Pin the output format explicitly (`cyclonedx-json@1.6`). | Otherwise the published SBOM would silently be 1.7 and would not match the documented format. | RESOLVED — D1 |
| F2 | Syft's `dotnet-deps-cataloger` is deprecated and **not enabled by default**; it is removed in Syft v2. | Use the non-deprecated `dotnet-deps-binary-cataloger`, which reads `**/*.deps.json` directly and needs no `--select-catalogers`. | Selecting the deprecated cataloger is the obvious "fix" when a scan returns no .NET packages, and it would work today and break at Syft v2. | RESOLVED — default behaviour confirmed against Syft 1.55.0 |
| F3 | `dotnet.dep-packages-must-claim-dll` defaults to **true**: a deps.json with no `runtime`/`resource`/`compile` asset section catalogs as nothing, silently. | Accepted, and documented in the generator's header and in this plan. | A real `dotnet publish` emits those sections, so the staged file qualifies. A trimmed or hand-written deps.json would catalog as nothing with no diagnostic — the most dangerous failure mode in the pipeline, because it looks exactly like "no .NET dependencies". | RESOLVED — documented; the staged publish is verified to produce the asset sections |
| F4 | Scanning the publish directory in place yields one bogus NuGet component per shipped DLL. | **Publish outside the scanned tree and copy only the `*.deps.json` in.** | The publish directory is full of PE binaries, and Syft's binary cataloger turns each DLL it cannot pair with a claimed runtime path into a separate component — including for our own assemblies. The `deps.json` alone carries the whole closure and does not need the DLLs on disk. | RESOLVED — approved and endorsed by the human |
| F5 | `.github/` is cataloged by Syft's GitHub Actions cataloger. | **Exclude `.github/`.** | Otherwise the published SBOM lists `actions/checkout@v7` and sixteen other CI actions as components of the release. They are build tooling, not shipped code. | RESOLVED — exclude list in `scripts/generate-sbom.sh` |
| F6 | Syft writes absolute build-machine paths into file-type component names. | **Rewrite them to repo-relative paths.** | Otherwise a released artifact publishes the build machine's directory layout. | RESOLVED — normalisation pass in `scripts/generate-sbom.sh` |
| F7 | CycloneDX requires the `dependencies` array to hold **unique** entries; collapsing by purl can make two entries land on the same ref. | **Merge entries that share a ref**, taking the union of their `dependsOn`. | A document that satisfied every structural check was still rejected by the schema. This is why schema validation is a gate and not a nicety. | RESOLVED — D9 |
| F8 | First-party code published as third-party packages. | **Drop them** (D10). | `kyberweave.core` leaked from the lock file at version `UNKNOWN`; the deps.json root projects `kyber-weave` and `kyber-weave-mcp` leaked at their release versions. | RESOLVED — D10 |
| F9 | `packages.lock.json` is not build-enforced — no `RestorePackagesWithLockFile`, and CI restores without `--locked-mode`. | **Not fixed** (D-B). The staged publish is immune to it; the local fallback is not, and is documented as a fallback. | Fixing the lock-file story is a separate change with its own risk, and the release path does not depend on it. | RESOLVED — D-B; the limitation is documented |
| F10 | The staged `dotnet publish` restores from nuget.org. | **No new behaviour.** `docs/distribution.md:73` governs *publishing* to nuget.org; there is no `nuget.config` restricting restore sources, and `release.yml`'s existing `build` job already restores from nuget.org on every release. | Verified by the human: the reading was correct. | RESOLVED — human verification, 2026-10-21 |
| F11 | The aggregate describes the repository's dependency closure, not any individual RID's binary. | **Documented as a limitation**, together with the three others. | Every asset in a release is built from the same locked dependency set, so the closure is the same for all of them — but it is an inventory of inputs, not a measurement of bytes. | RESOLVED — `docs/install.md`, `docs/distribution.md`, ADR 0042 |

## 6. The three limitations, stated plainly

These are documented to the consumer in `docs/install.md` and to the maintainer in
`docs/distribution.md`, and recorded in ADR 0042.

1. **The SBOM describes the repository's dependency closure, not any individual RID's
   binary.** It is an inventory of build inputs, not a byte-level scan of a published
   archive.
2. **The embedded .NET runtime is not in it.** The shipped CLI and MCP binaries are
   self-contained and embed the entire .NET runtime — the single largest component of
   what actually ships. The SBOM is built from a *framework-dependent* publish,
   because that is what produces a readable dependency manifest, and a
   framework-dependent manifest lists NuGet packages only. The Node runtime that the
   KyberDash single-executable embeds is omitted for the same class of reason.
3. **Test-only dependencies are not in it.** The SBOM covers what the release ships,
   not the test suite.

A byte-level inventory requires scanning the published archives. That, and SBOM
attestation, are the follow-up issue.

## 7. Verification

Run locally on the development machine (.NET SDK 10.0.402, Syft 1.55.0):

| Check | Result |
|---|---|
| `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release` | 3027 passed, 0 failed |
| `dotnet run --project src/KyberWeave.Cli -- review gates .` | 18 gates, 0 failed |
| `docs validate . --merge-ready` | no findings |
| `docs drift .` | no findings |
| `dotnet format` whitespace + style | clean |
| `skill validate` / `lint` / `scan` | no findings |
| CycloneDX 1.6 schema validation (official schema, `jsonschema`) | 0 errors |
| Validator against the reference implementation | agree on 109 mutation cases and on both generated documents |
| `scripts/release-local.sh` end-to-end | release tree built, SBOM present, manifest verifies |
| `verify-release-checksums.sh` with the SBOM | 21 assets, manifest written and verified |

The `dotnet` SDK is present on the development machine at `~/.dotnet/dotnet` but is
**not on `PATH` by default**; the commands above were run with
`PATH="$HOME/.dotnet:$PATH"`. `cargo` is not installed, so the Rust side of the
generated SBOM was cataloged from `Cargo.lock` by Syft without a Rust toolchain —
which is the intended path and does not require one.

`docs drift` needed a CodeGraph index, which the worktree lacked. `codegraph init .`
was run under the authorisation in `AGENTS.md`; it changed no tracked file, and the
index is gitignored.

## 8. Closeout mapping

Every durable decision below is now recorded in canonical documentation or in a
durable record.

| Decision | Destination |
|---|---|
| D1, D3 — format, one aggregate | [ADR 0042](../adr/0042-release-sbom-cyclonedx-syft.md) decision 1; [`docs/distribution.md`](../distribution.md#the-sbom-asset) |
| D2, D11 — pinned Syft, cached and re-verified | ADR 0042 decision 2; the generator's header comment in `scripts/generate-sbom.sh` |
| D4, F3, F4 — the .NET input and its two hazards | ADR 0042 decision 3; the generator's header comment |
| D5 — the Node and Rust inputs | ADR 0042 decision 4 |
| D6 — one shared generator | ADR 0042 decision 9; `docs/distribution.md` |
| D7, F11 — attestation out of scope, closure-not-bytes | ADR 0042 decisions 7 and 9; `docs/install.md#the-sbom-what-each-release-ships` |
| D8 — vendored schema, own validator | ADR 0042 decision 5; [`scripts/schemas/PROVENANCE.md`](../../scripts/schemas/PROVENANCE.md) |
| D9, F7 — collapse by purl, merge colliding refs | ADR 0042 decision 5; the generator's normalisation pass |
| D10, F8 — first-party components dropped | ADR 0042 decision 5; the generator's normalisation pass |
| D-A, D-B — scope and the lock-file fallback | ADR 0042 decisions 3 and 8; the generator's header comment |
| F5, F6 — excludes and path rewriting | the generator's header comment and exclude list |
| F9 — the lock files are not build-enforced | ADR 0042 decision 8; `docs/distribution.md` |
| F10 — the staged publish restores from nuget.org | ADR 0042 decision 3 (no new behaviour); no canonical change needed |

**ADR waiver:** none. This plan produced a new ADR (0042) rather than amending one.
ADR 0027 records *integrity* (the `SHA256SUMS.txt` mechanism and the deferral of
signing); the SBOM is an *inventory* with a different subject, a different consumer and
its own durable coverage contract. ADR 0042 states the decision in force and references
0027 for the integrity mechanism the SBOM inherits.

**Documentation waiver:** none, and none needed. The consumer-facing facts are in
`docs/install.md`, the maintainer-facing facts in `docs/distribution.md`, and the
durable decision in ADR 0042.

## 9. Follow-ups

Filed as [issue #411](https://github.com/dpalfery/kyber-weave/issues/411): per-artifact
Syft scans of the published archives, and SBOM attestation (`actions/attest-sbom`). Both
are referenced by ADR 0042 and by the limitations in §6.
