---
id: standards/github-actions
title: GitHub Actions coding standard
doc-type: coding-standard
status: current
technology: github-actions
owner: dpalfery
last-reviewed: 2026-11-26
---

# GitHub Actions coding standard

How workflow files are written in this repository. Agents and skills resolve this document as
`<github-actions-coding-standard>` in the repository root [`AGENTS.md`](../../../AGENTS.md), so
what it says here outranks the defaults a portable agent shipped with.

Two sets of files are in scope, and both are first-party:

- This repository's own CI:
  [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml) (build, test, security scans,
  skill and docs gates, and the `CI Summary` required check),
  [`release.yml`](../../../.github/workflows/release.yml) (RID build matrix, SBOMs,
  attestations, GitHub Release), and
  [`gate-catalogue-toolchains.yml`](../../../.github/workflows/gate-catalogue-toolchains.yml)
  (one real-toolchain proof of the catalogue gates against their fixtures).
- The copy-ready workflows this product ships to hosts under
  [`templates/github-actions/`](../../../templates/github-actions/README.md). They are checked in
  here, pinned the same way, and pinned by tests.

`REVIEW.md` maps this technology to `.github/workflows/*.yml` and `templates/github-actions/*.yml`
and names the review sections that apply. This document says how a workflow is written; it does
not restate the review rules.

This is a decision record. Rules that the workflow runner, the actions themselves, or a pinned
policy test already enforce are named by the test, not restated as prose.

## Naming and layout

### Names are identifiers

- `CI Summary` is the required status check. Renaming it, or wrapping it in a second workflow,
  removes the merge gate without anything turning red.
- Job keys (`changes`, `build-test`, `ts-gates`, `dash-e2e`, `tray-rust`, ...) are read by name by
  the repository-policy tests, and job `name:` values are what a reviewer reads in the pull
  request's check list. Rename either deliberately, and update the test that pins it in the same
  change.
- **A job another workflow re-runs is addressed by its display `name:`.** The API identifies a job
  by `name:`, never by its key in the workflow. `pr-naming-recheck.yml` re-runs `ci.yml`'s
  `pr-naming` job and then `CI Summary` by those two strings, so a rename that misses the recheck
  fails its step rather than reporting a green summary nobody re-checked. `ci.yml` says the same at
  the `pr-naming` job: rename one, rename both.
- The gate names in `.kyber-weave/kyber-weave.yml` are the identifiers a review cites. A workflow
  step that runs a gate repeats that gate's argv rather than an equivalent, so the command in the
  workflow and the command in the gate cannot disagree. `ts-gates` states this at the job.

### Templates are a product contract

- Every toolchain step in
  [`templates/github-actions/kyber-weave-review-gate.yml`](../../../templates/github-actions/kyber-weave-review-gate.yml)
  is `if: false` until the host enables it. A template that installs and builds stacks the host
  does not use spends that host's minutes and proves nothing. The test name says it:
  `EveryToolchainStepIsDisabledUntilTheHostEnablesIt`.
- A template checkout takes `fetch-depth: 0`, because `--base` resolves through `git merge-base`
  and a single-commit checkout cannot answer it.
- A template's `kyber-weave review gates` call passes `--council`, because the CI caller is allowed
  to run the full suite and the gates would otherwise refuse it.
- Changing a template is a change to what hosts copy, so it is pinned by content tests and reviewed
  as a contract change rather than as a YAML tidy-up.

## Coding standards

### Triggers select work by job, not by workflow

- `ci.yml` runs on `pull_request`, on `push` to `main`, on a weekly `schedule` (the CodeQL and
  dependency scans have to run when the tree is quiet), and on `workflow_dispatch`.
- **A path filter is a job-level `if:` on an output of the `changes` job, never a workflow-level
  `paths:`.** Only `CI Summary` is a required check, and a skipped job counts as passed while a
  required check that never starts stays pending. `ci.yml` also re-runs every job when `ci.yml`
  itself or `.github/codeql/` changes, so an edit to the filters is exercised by the same run.
- `gate-catalogue-toolchains.yml` is the one workflow with a workflow-level `paths:` filter. It is
  not a required check: it exists to prove the catalogue's gates against their fixture projects.
- `pull_request_target` and `workflow_run` run with the base repository's secrets. Neither is used
  here, and neither is introduced. A workflow that never checks out and executes fork code cannot
  leak those secrets.

### Concurrency

- CI takes `group: ci-${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress` true only
  for pull requests, so a force-push cancels its own superseded run and nothing else.
- Release takes a per-ref group with `cancel-in-progress: false`. A cancelled release has already
  pushed a tag and not its assets, which is a state a human has to clean up by hand.

### Advisory is marked as advisory

`trivy`, `semgrep` and `gitleaks` carry `continue-on-error: true`, still upload their SARIF, and
read as "(advisory)" in the summary table. They are the only jobs that do. **`continue-on-error`
appears nowhere else**, with one stated exception: the receipt save, which is miss-only and whose
failure mode is the next run re-executing ([Receipts and skips](#receipts-and-skips)). A blocking
step that can fail silently makes the reviewer re-check by hand what the workflow was supposed to
check. A disabled test or scan needs a linked issue and a date.

### Determinism

- `global.json` pins SDK `10.0.100` with `rollForward: latestFeature`. Workflows read the band from
  `env.DOTNET_VERSION` (`10.0.x`), so a patch release arrives without a workflow edit while the
  exact SDK still comes from the file.
- Template toolchain steps name a concrete runtime version (`node-version: "24"`).
  `ReviewGateWorkflowTemplateTests.RuntimeVersionsArePinnedNotFloating` rejects a floating value.
  `latest` in CI means the build that passes today fails on a morning nobody changed anything.
- Jobs run on a Linux runner except where the platform is the subject — the Windows capture tests,
  and the macOS signing and notarization in the release builds — and the job says why it is
  somewhere else.

## Testing

### Tests that pin workflows

The workflow YAML is a test subject in
[`tests/KyberWeave.RepoPolicy.Tests/`](../../../tests/KyberWeave.RepoPolicy.Tests/AGENTS.md):
`ReviewGateWorkflowTemplateTests`, `ReviewGateFullSuiteConfigTests`,
`ReleaseTests`, `ReleaseNotesTests`, `SbomTests`, `CiContentHashSkipTests` and
`ReleaseReceiptSkipTests` read it. They run as the `test-policy` gate,
which selects on the same paths as `build`.

- An edit to a workflow that one of them reads runs that class by name before the change is
  reported done; the project's `AGENTS.md` gives the filtered command.
- A new structural invariant gets a test there rather than a paragraph here. A rule this document
  states without a test behind it is a rule a future edit can remove silently.

## Commands

### Release shape

- **Build and publish are separate.** Every publish job depends on the builds and the SBOM jobs, so
  nothing reaches a GitHub Release before its inputs exist.
- **Approval happens once.** `authorize` → `version` → `approve`, then every build job carries
  `needs: [version, approve]`. No build job asks a second time.
- An upload sets an explicit `retention-days` (14, or 7 for the short-lived scan output), and a
  release upload, whose file is the point of the job, also sets `if-no-files-found: error`. A
  missing asset is a failed job, not an empty artifact.
- A repository ruleset blocks rewriting or deleting a tag except for administrators; this workflow
  creates tags, it does not move them.
- nuget.org is forbidden, as both workflow headers state. GitHub Release, GitHub Packages and
  Homebrew are the distribution paths.
- A release job is safe to re-run against the same tag: a fresh chain receipt makes the build jobs
  skip and the publish jobs verify the published state instead of publishing again
  ([Receipts and skips](#receipts-and-skips)).

### Timeouts, caches and leftovers

- A job whose suite can hang carries `timeout-minutes` (`dash-e2e` is 25), and a long single step
  carries its own budget rather than inheriting the six-hour runner default.
- A cache key names what the cached content depends on — the tray's apt cache keys on the package
  set and the week. A key that never changes serves a stale tree and hides a broken restore.
- **A job that can hang carries `timeout-minutes`, and the run's leftovers are checked by the
  workflow itself.** The repository's former watchdog helper for this (`scripts/proc-guard.sh
  check-leftovers`) is retired, so a job that starts a server, a watcher or a browser states its
  bound in the step's own timeout and cleans up in an `if: always()` step.

### Receipts and skips

A **receipt** is a JSON record a job writes after it passes, holding the fingerprint of every input
it read and the epoch second it was written. A later run restores it under a key that contains that
fingerprint, validates it, and skips the job's work only when the receipt is this tree's and still
fresh. `ci.yml` keeps one per job; `release.yml` keeps one for the whole release chain. The
mechanism is described in
[the CI runbook](../../ci-pipelines/workflows-runbook.md#content-hash-receipts-and-skips); these
are the rules a workflow edit has to keep.

- **Three eligibility conditions.** A job may carry a receipt only when all three hold: its result
  is a function of the tracked tree, so the same tracked inputs give the same verdict; its skip is
  invisible to the required check, so the skip happens inside the job and `CI Summary` still reads a
  success; and its work has no external side effect that a repeat would duplicate. A job the
  `changes` path filter decides never carries one, and no job-level `if:` ever reads a receipt. A
  job whose work publishes skips the publication and verifies the published state instead — the
  release chain's two publish jobs are the model.
- **A receipt's age bounds the skip, and the ceiling is one variable.** `RECEIPT_MAX_AGE_SECONDS`
  is declared once, at workflow level, and is `14400` — four hours — in both workflows; no job
  overrides it. A re-run inside the ceiling skips — on the same tracked inputs for a CI job, on the
  same version for the release chain — and a later one re-executes. Never put an age, a week or a
  verdict value into a cache key: then the ceiling could only be tightened by invalidating every
  key.
- **The fingerprint enumeration names every tracked top-level entry.** Directories as `<dir>/**`,
  root files by exact name, no bare root glob (`'*'`, `'.*'`, `'**'`, `'**/*'`). A glob hashes the
  same files without proving the list complete, and a missing entry is a silent skip — the job
  would reuse a receipt from a run that never read the file. A narrower set belongs only to a job
  that reads fewer inputs, and it must still cover every alternation of the `changes` filter that
  selects that job.
- **The enumeration stays pinned by its policy test.** `CiContentHashSkipTests` compares the
  whole-tree set against `git ls-tree HEAD`; `ReleaseReceiptSkipTests` pins the chain key, the
  gated steps, the miss-only save and the fail-closed hit path. A new top-level entry, a new job or
  a new `changes` alternation fails them until the enumeration or the job list is updated, and the
  new case is added to the test in the same change that needs it.
- **The CI verdict validates the receipt before it trusts it.** A cache entry is data, not proof.
  The verdict requires the file to exist, to parse as JSON, to carry `saved_at_epoch` and
  `input_hash`, to have an epoch in digits only, not dated in the future, and to carry this run's
  fingerprint — and only then compares the age against the ceiling. Every failure path runs the job
  in full and writes its own line to the step summary naming why. The release chain's verdict is
  simpler because its key is already bound to one version, so there is no `input_hash` to compare:
  the file must exist, `saved_at_epoch` must be digits, the receipt must not be dated in the future,
  and the age must be at or under the ceiling.
- **Every receipt script declares `shell: bash`.** The Windows legs default to `pwsh`, where
  `date +%s` and integer arithmetic over the epoch do not work as written.
- **The key is exact, and carries no `restore-keys`.** `receipt-<job>-<leg>-<hash>`, with the leg
  the matrix dimension the job varies on, or one shared `release-chain-<version>` for a chain. A
  near-miss restore would let a receipt skip work over input it never fingerprinted.
- **A save happens only on a miss, and `ci.yml` also requires the same repository.** In `ci.yml`
  the save and the attestation steps carry `github.event_name != 'pull_request' ||
  github.event.pull_request.head.repo.full_name == github.repository`, so a fork writes neither a
  receipt nor an attestation into the base repository. `release.yml` runs only on a tag push or a
  manual dispatch, so it needs no such guard. In both workflows the save is unreachable on a hit,
  so a hit never refreshes the age. A pull request's saves are scoped to that pull request; only
  default-branch runs populate the scope every workflow and branch can read.
- **A miss-only save is failure-tolerant, and that is the one `continue-on-error` exception.** The
  receipt-writing step and its `actions/cache/save` both carry `continue-on-error: true` in
  `ci.yml`, and the release chain's save step carries it too, because two runs can race for one
  cache key and a save that loses must not fail a job that was green; losing it only means the next
  run re-executes. The exception is fail-open; an advisory marker is not, and no blocking step
  gains the flag to hide a failure.
- **In `ci.yml`, only a build product is attested, in provenance mode, by a job that mints a
  token.** `actions/attest` there takes `subject-path` and nothing else — no `sbom-path`, no
  `predicate` — so it binds the product to the workflow's identity. A job that attests declares
  `id-token: write`, `attestations: write` and `artifact-metadata: write`, with a comment naming
  why, and a job that mints a token must attest. Only a job's build products are digested into the
  receipt; test reports and SARIF files are neither digested nor attested. The release workflow has
  the other shape: `attest-sbom` mints
  SBOM-predicate attestations with `sbom-path`, plus one provenance attestation over every
  archive.

### Build diagnostics

- **CI captures no MSBuild binary log today.** A failed .NET step is reproduced by building the
  failing project alone with `/bl:app.binlog` and replaying the log (the github-actions skills
  file has the procedure). A workflow step that starts capturing one follows the rest of this
  list.
- A captured binlog is named uniquely so parallel jobs never overwrite each other: `/bl:{}` on
  .NET 8 SDK or MSBuild 17.8+, `-bl:{{}}` on a `pwsh` runner, an explicit name that differs per
  step and matrix leg on an older SDK, such as
  `/bl:${{ github.job }}-${{ strategy.job-index }}-build.binlog` for the build step and
  `/bl:${{ github.job }}-${{ strategy.job-index }}-test.binlog` for the test step.
  This repository's SDK is 10, so `/bl:{}` applies.
- A binlog is uploaded on failure only: `if: failure()`, `actions/upload-artifact` with a
  `**/*.binlog` path, a name that includes `github.run_id`, and an explicit `retention-days`. A
  clean step keeps binlogs: `git clean -fdx -e "*.binlog"`.
- `-v:diagnostic` is not a default. It writes 10-50 MB per project; a binlog and a replay is
  cheaper.

### The workflow runs the commands a contributor runs

- The argv in `review.gates` of [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml)
  is the command, and a workflow step repeats it. CI therefore reproduces a local failure instead
  of approximating it.
- KyberDash and tray local commands run directly (`npm --prefix dash …`, `npx --prefix dash
  playwright test …`, `cargo … --manifest-path dash/tray/src-tauri/Cargo.toml …`), and `.NET`
  commands run directly via `dotnet` (root `AGENTS.md`). `kyber-weave review gates` refuses an
  unfiltered full-suite gate from a dev agent and admits the council and CI, which pass `--council`.
  A workflow step that runs the full suite is legal precisely because CI is one of those two callers.
- The skill and docs gates build the pull request's own CLI and run it with `--no-build`; they
  never install a released binary. The behaviour under test is the commit's.

### Docs and skill gates

- Each skill directory is gated under the same three verbs (`skill validate`, `skill lint`,
  `skill scan`), and each run uploads its own SARIF under a path-derived category. A new skill
  needs a path in the array and its own upload step; the comment in `ci.yml` says what happens
  otherwise.
- The docs gate runs `docs validate . --merge-ready --base <base> --format table`, resolving the
  base from the pull request, the push's `before` SHA, or `origin/HEAD`.
- **`docs drift` is deliberately absent from CI.** It needs a host-owned CodeGraph index, so
  without an indexer or cache it fails closed on `KW-DOC-DRIFT-001`. A workflow does not add it
  without provisioning that index. Local authors and reviewers still run it — root `AGENTS.md`
  requires it before a pull request.

## Security and data handling

### Permissions

- Every workflow declares a top-level `permissions:` block; `contents: read` is the floor. A
  workflow that needs more says which job needs it and what for.
- Every job narrows with its own `permissions:`. `ci.yml` says why at the Trivy job: a job without
  a block would inherit `security-events: write` it never needs.
- `security-events: write` appears only where a SARIF file is uploaded. `id-token: write` appears
  only in the job that mints the Sigstore signing certificate, and that block carries a comment
  naming what each permission is for — the `attest-sbom` job is the model.

### Supply chain

- **Every `uses:` pins a full commit SHA, with the version in a trailing comment**:
  `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7`. A tag is mutable, so the
  action reviewed is not necessarily the action that runs. This holds for first-party `actions/*`
  too.
- Dependabot tracks the `github-actions` ecosystem (`.github/dependabot.yml`). A bump changes the
  pin, so it is reviewed like any other workflow change.
- Nothing downloads and executes an unread script. The install script in the templates and the
  runbook is downloaded to a file and then run, so a SAST tool can read the step — the review-gate
  template says so at the install step.

### Untrusted input never reaches a shell

A pull request's branch name, title and event payload are attacker-controlled text. `${{ github.* }}`
never appears in a `run:` block; it arrives through `env:` and is quoted at the point of use. The
`changes` job takes `EVENT_NAME` and `BASE_SHA` this way, and the docs gate takes `EVENT_NAME`,
`BASE_REF` and `EVENT_BEFORE`. `ci.yml` states the rule where it happens:
"a hostile ref cannot reach the shell".

`ReviewGateWorkflowTemplateTests.NoRunBlockInterpolatesGitHubContextIntoShell` pins it for the
templates, which is where a host copies the mistake from.

### Secrets

- A secret reaches a step through `env:`, never interpolated into the command line. It is never
  echoed, never written to an artifact, and never put in a step summary.
- Secrets are scoped to the environment that uses them. The Apple signing and notarization
  credentials live in `environment: release`, and `build-tray` says why it is the one build job
  that names it. The human approval lives in `environment: release-approval`.
- Prefer a federated, short-lived credential. `attest-sbom` signs with the OIDC token rather than a
  stored key, and `release.yml` records in its header that no PAT is needed: `GITHUB_TOKEN` mints
  the tag after approval.

## Done checklist

### Completion

A workflow change is proved the way a code change is — a build, the tests covering it, and a
format check — and the three are not optional because the file is YAML:

```bash
dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release
dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<TestClass>"
git diff --check
actionlint .github/workflows/<file>.yml
```

- **The tests are the policy classes.** `ReviewGateWorkflowTemplateTests`,
  `ReviewGateFullSuiteConfigTests`, `ReleaseTests`, `ReleaseNotesTests`, `SbomTests`,
  `CiContentHashSkipTests` and `ReleaseReceiptSkipTests` read the
  workflow files; a change to one of them runs that class by name, filtered, before the change is
  reported. `test-policy` is the gate that runs the whole class set, and the unfiltered product
  suite stays with the review council and CI.
- **The build check is the linter where the repository declares one.** `actionlint` parses the
  file, resolves expressions and shellchecks every `run:` block, so a workflow that does not parse
  fails there the way a build fails. Where the repository declares a workflow-linter task gate,
  that gate is the check; where it declares none, run the linter by hand and say so.
- **The format check is `git diff --check` plus the shape of the files beside it.** This
  repository declares no YAML formatter gate, so the mechanical part is that the diff carries no
  trailing whitespace and no mixed indentation; the rest is the structure this document requires.
  A repository that declares a YAML formatter runs it instead, and says which one here.
- **The workflow's own commands are run by hand.** Every test, build or lint step is a command a
  contributor can run locally; run the ones the change touches and put the result in the pull
  request. A step whose command was never exercised is untested whatever the YAML says.
