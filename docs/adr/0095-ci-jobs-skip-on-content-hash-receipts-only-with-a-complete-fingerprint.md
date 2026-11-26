---
id: adr/0095-ci-jobs-skip-on-content-hash-receipts-only-with-a-complete-fingerprint
title: CI jobs skip on content-hash receipts only when the fingerprint covers the job's whole input set
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-26
component: CI Pipelines
decided-by: []
---

# ADR 0095: CI jobs skip on content-hash receipts only when the fingerprint covers the job's whole input set

## Status

Accepted, 2026-11-26. Records the decisions of the plan [CI content-hash skip receipts and
provenance](../archive/plans/2026-11-26-ci-content-hash-skip-and-provenance.md), including the two
owner decisions taken at its approve-and-execute gate: one uniform four-hour ceiling for CI and the
release chain, and `integration-test` left skip-eligible despite its AI-graded tests.

## Context

GitHub Actions runs every job of every workflow on every commit. A job whose inputs are identical to
a run that already passed redoes its work, and GitHub links no evidence from the earlier run to this
one. So `ci.yml` and `release.yml` now look up a small JSON **receipt** under a fingerprint of the
job's inputs: a fresh receipt under the same fingerprint proves those inputs already passed, the
job's work steps are skipped, and the step summary shows the URL of the run that proved it.

The risk a required check cannot afford is that a skip is green. A fingerprint that misses one input
the job reads turns CI green over input nobody tested, and nothing downstream can tell. This record
is the rule that decides when a skip is allowed at all; the mechanism itself is described in
[the CI runbook](../ci-pipelines/workflows-runbook.md#content-hash-receipts-and-skips) and the
rules a workflow edit must keep are in
[the GitHub Actions standard](../standards/github-actions/github-actions.standards.md#receipts-and-skips).

## Decision

1. **A skip happens inside the job, never on the job.** The heavy steps carry
   `if: steps.verdict.outputs.skip != 'true'`; no job gains a job-level `if:` and no job, display
   name or `needs:` edge changes. `CI Summary` is the repository's single required check and reads
   every job through `needs`, so a job that skips its work inside a job that still succeeds keeps the
   required check green and honest. A job-level `if:` would remove the job's verdict from the run
   instead, and would also break `CiAlwaysRunJobsTests` (which pins the job set with no `needs:` or
   `if:`) and `pr-naming-recheck.yml` (which joins CI jobs by display name).
2. **Three eligibility conditions, all required.** A job may carry a receipt only when (a) every
   input that can change its verdict is inside the fingerprint — a job that reads anywhere in the
   repository hashes the whole tracked tree; (b) nothing outside the job consumes an output that
   exists only in this run; and (c) identical inputs under the pinned toolchains cannot produce a
   different verdict, within the receipt's maximum age. Condition (b) is why the release build chain
   skips as one unit and its publish jobs verify the published state rather than being skipped
   themselves. Condition (c) is why the age ceiling exists at all.
3. **The whole-tree fingerprint names every tracked top-level entry.** Directories as `<dir>/**`,
   root files by exact name, with no bare `'*'`/`'.*'` and no `hashFiles('**')`. Every set also
   includes the workflow file, so any workflow edit invalidates every key. Three jobs read a provably
   narrower universe and hash exactly that: the SBOM job hashes the `changes` job's own SBOM inputs,
   and the dash and tray jobs hash `dash/**` and `dash/tray/**` respectively, each with the root
   `global.json`, `Directory.Build.props`, `dash/.nvmrc` and `ci.yml` where those files are inputs to
   its verdict.
4. **The fingerprint enumeration is pinned complete by a test, and cannot rot.** A repository-policy
   test compares the whole-tree argument list against `git ls-tree HEAD`, so a new top-level entry
   fails until the list covers it. A narrower set must still cover every alternation of the
   `changes` filter that selects its job.
5. **A receipt's age bounds the skip, and one variable sets the bound.** The verdict step compares
   the restored receipt's `saved_at_epoch` against the workflow's own
   `RECEIPT_MAX_AGE_SECONDS`, which is `14400` — four hours — in both workflows, with no job
   override. A stale receipt is a miss whose summary line names the age and the ceiling. The cache key
   carries the content hash and no age, so the ceiling can be tightened without invalidating a key.
6. **One receipt per CI job, keyed by content; one receipt for the release chain, keyed by version.**
   A CI job's key is `receipt-<job>-<leg>-<hash>`, where the leg is the matrix dimension the job
   varies on, so a macOS leg never satisfies a Windows leg. The release chain's key is
   `release-chain-<version>`: the chain skips or runs as one unit, and its receipt is written only by
   the `release` job after the release, its inventory, its manifest, its SBOM attestations and its
   provenance attestations have all verified, so a hit always names a fully published release. A
   version-only key is enough there because a fresh dispatch for an existing tag is refused by the
   `version` job and a tag push names the version in the ref, so only a re-run of the same run
   reaches a hit; an administrator rewriting a tag is the one path to a different tree under the same
   version, and the repository's ruleset blocks that.
7. **Restore is exact-key only, and a save happens only on a miss.** No `restore-keys`: a near-miss
   restore would let a receipt skip work over input it never fingerprinted, while a miss costs one
   full run, which is the safe direction. Receipts restore in every event and save only from
   same-repository runs, so pushes to the default branch populate the scope every branch can read and
   a pull request's saves are readable only by that pull request's later runs.
8. **A fork pull request never skips and never writes.** A fork cannot read the base repository's
   caches, so its restore always misses and every eligible job runs in full; the save and attestation
   steps carry a same-repository guard so nothing is written that no later run can read. The step
   summary names cache isolation as the cause.
9. **A receipt is data, validated before it is trusted, and its steps run under `bash`.** A missing
   receipt file, a file that is not JSON carrying `saved_at_epoch` and `input_hash`, a non-numeric
   epoch, a future epoch (which would compute a negative, always-fresh age), and an `input_hash` that
   differs from this run's fingerprint are each a miss with their own summary line. Every receipt
   step declares `shell: bash`, because the Windows legs default to `pwsh`, where `date +%s` and the
   verdict's integer arithmetic do not work.
10. **A skipped job keeps its evidence in the run.** A fresh hit writes the receipt JSON into the step
    summary and sets `prior_run_url`, so the skip shows what proved it. Artifact-producing jobs mint
    provenance attestations for their build products on the miss path with `actions/attest` in
    provenance mode (no `sbom-path`), under `id-token: write` with a comment naming why; test reports
    and SARIF analyses are recorded as digests only, and a job that produces no artifact carries
    `artifact_digests: null` and `attested: false`. A skipped security scan uploads no SARIF, so
    code scanning sees no new analysis for that scan during the skip window — the same informational
    state the `codeql` job already documents for a skipped matrix leg, and it cannot fail the required
    check.
11. **A release chain hit re-checks what it can and carries what it cannot.** On a hit the publish
    jobs re-verify the published asset inventory as sorted sets, the published `SHA256SUMS.txt` by
    byte-compare, both attestation predicates per archive through `gh attestation verify` (CycloneDX
    SBOM and SLSA provenance), and the recorded package versions through the GitHub Packages API.
    They do not re-run the macOS signature and notarization assertions of the skipped tray builds:
    the publishing run ran those on exactly the bytes it published, and the hit path's manifest
    byte-compare and digest match prove the published bytes are still those. `attest-sbom` skips with
    the chain, because the hitting run builds nothing to attest.
12. **The mechanism fails open to running, never to skipping.** Eviction, a fork, a quota limit, a
    stale receipt, a malformed receipt and a cache-reserve race all miss and run the job in full. The
    receipt save carries `continue-on-error: true` — the only such flag outside `trivy`, `semgrep` and
    `gitleaks`, which are advisory by definition — because a save that loses a race for an immutable
    cache key must never fail a job that was already green; its failure mode is the next run
    re-executing.

### Accepted residuals

- **Security scans skip inside the ceiling.** `codeql`, `trivy`, `semgrep` and `gitleaks` are
  skip-eligible under the four hours, by the owner's decision over a blanket exclusion. Scanner
  rules and vulnerability databases can change inside that window, and a skipped scan uploads no
  SARIF; the four-hour bound and the weekly scheduled run are the mitigation.
- **Runner images and floating toolchains are outside every fingerprint.** `ubuntu-latest` and
  `macos-latest` float, the tray's Rust `stable` resolves to whatever ships, and .NET resolves within
  the `rollForward: latestFeature` band. A hit proves the same tree passed under the toolchains of the
  moment; it cannot prove the same runner image or the same patch release.
- **The AI-graded integration tests strain the input-determines-verdict condition.**
  `ProseRuleGradingTests` can change `integration-test`'s verdict with no tree change (a grader
  outage, a confidence floor, the secret's absence). The owner kept the job eligible, accepting that
  the four-hour ceiling bounds the exposure: a skip never carries an AI-graded verdict across the
  ceiling.
- **The release chain honours the same four hours as CI.** A seven-day release ceiling was
  recommended, to preserve the chain-skip's main win — a re-run the morning after a flaky macOS
  notarization failure — and the owner declined it. A release re-run more than four hours after the
  failed attempt rebuilds the whole chain, RID matrix included.
- **The wins are re-runs, not everyday pull requests.** A push to the default branch cannot restore a
  receipt a pull request saved. The mechanism pays for re-runs after flaky failures, reverts or
  reopened pull requests whose tree a default-branch run already proved, and release re-runs inside
  the ceiling.

## Alternatives Considered

- **A job-level `if:` on the job.** Rejected: the job would disappear from the run rather than report
  a carried verdict, so the required check would lose the very evidence a skip has to preserve. It
  would also break the existing job-set contract and the pull-request naming join.
- **A purely content-keyed skip with no time bound.** Rejected: it would honour a scan receipt from
  last week, and a green CI over a tree whose advisory state nobody rechecked. Content alone does not
  decide a verdict whose inputs move with time.
- **Per-job independent release skips.** Rejected: a skipped build leaves the publish jobs nothing to
  publish. One version-bound chain receipt, verified on the hit path, is what lets the chain skip at
  all.
- **`hashFiles('**')`, or bare `'*'`/`'.*'` for the root.** Rejected: nothing pins whether the glob
  expansion includes `.git`, and that cannot be observed from a static test. Enumerating every
  tracked top-level entry makes the question irrelevant and lets a policy test prove the list
  complete.
- **Excluding the security scans outright.** Rejected by the owner in favour of the four-hour ceiling:
  the scans are the jobs most likely to be re-run against the same tree, and the ceiling bounds the
  staleness the exclusion would have avoided entirely.
- **Receipts for evidence only, with no release skipping.** Rejected: simpler, but a re-run after a
  flaky macOS notarization failure would rebuild the whole RID matrix for nothing.
- **A repository variable instead of a cache.** Rejected: variable names cannot be created per hash
  without a personal access token and a write path this repository forbids.
- **A required-check naming convention that hides skips from the branch-protection list.** Rejected:
  it makes a skip invisible instead of making it sound.

## Consequences

- Every future workflow edit inherits the rule: a new skip-eligible job needs its whole input set in
  the fingerprint, its invariant pinned in `CiContentHashSkipTests` or `ReleaseReceiptSkipTests`, and
  the standard's rules updated in the same change.
- A workflow edit itself invalidates every key, because every set hashes `.github/**` or the workflow
  file, so an edit can never be skipped over.
- The corpus of receipts is small — one JSON file per key — and a content state that stops being
  tested ages out of the cache; eviction costs a full run.
- The rule is stated for future readers in
  [the GitHub Actions standard](../standards/github-actions/github-actions.standards.md#receipts-and-skips),
  and the operator view is in
  [the CI runbook](../ci-pipelines/workflows-runbook.md#content-hash-receipts-and-skips).
- The release chain's twin is in
  [the distribution reference](../distribution.md#re-running-a-release-the-chain-receipt), beside
  the SBOM and provenance attestation paths a re-run verifies.

## Related

- [ADR 0027](0027-release-integrity-checksums-signing-deferred.md) — release integrity rests on HTTPS
  plus a complete, fail-closed checksum manifest; the chain hit re-checks that manifest before it
  trusts a published release
- [ADR 0042](0042-release-sbom-cyclonedx-syft.md) — the SBOM attestation job this change adds build
  provenance beside
- [CI Pipelines runbook](../ci-pipelines/workflows-runbook.md#content-hash-receipts-and-skips) — the
  step pattern, the fingerprint sets, the verdict, fork behaviour and the chain's hit path
- [GitHub Actions standard](../standards/github-actions/github-actions.standards.md#receipts-and-skips)
  — the rules a workflow edit must keep
- [Distribution and release flow](../distribution.md#re-running-a-release-the-chain-receipt) — what a
  release re-run does
