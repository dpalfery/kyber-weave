---
id: standards/github-actions
title: GitHub Actions coding standard
doc-type: coding-standard
status: current
technology: github-actions
owner: dpalfery
last-reviewed: 2026-11-07
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

## Triggers select work by job, not by workflow

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

## Names are identifiers

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

## Permissions

- Every workflow declares a top-level `permissions:` block; `contents: read` is the floor. A
  workflow that needs more says which job needs it and what for.
- Every job narrows with its own `permissions:`. `ci.yml` says why at the Trivy job: a job without
  a block would inherit `security-events: write` it never needs.
- `security-events: write` appears only where a SARIF file is uploaded. `id-token: write` appears
  only in the job that mints the Sigstore signing certificate, and that block carries a comment
  naming what each permission is for — the `attest-sbom` job is the model.

## Supply chain

- **Every `uses:` pins a full commit SHA, with the version in a trailing comment**:
  `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7`. A tag is mutable, so the
  action reviewed is not necessarily the action that runs. This holds for first-party `actions/*`
  too.
- Dependabot tracks the `github-actions` ecosystem (`.github/dependabot.yml`). A bump changes the
  pin, so it is reviewed like any other workflow change.
- Nothing downloads and executes an unread script. The install script in the templates and the
  runbook is downloaded to a file and then run, so a SAST tool can read the step — the review-gate
  template says so at the install step.

## Untrusted input never reaches a shell

A pull request's branch name, title and event payload are attacker-controlled text. `${{ github.* }}`
never appears in a `run:` block; it arrives through `env:` and is quoted at the point of use. The
`changes` job takes `EVENT_NAME` and `BASE_SHA` this way, and the docs gate takes `EVENT_NAME`,
`BASE_REF` and `EVENT_BEFORE`. `ci.yml` states the rule where it happens:
"a hostile ref cannot reach the shell".

`ReviewGateWorkflowTemplateTests.NoRunBlockInterpolatesGitHubContextIntoShell` pins it for the
templates, which is where a host copies the mistake from.

## Secrets

- A secret reaches a step through `env:`, never interpolated into the command line. It is never
  echoed, never written to an artifact, and never put in a step summary.
- Secrets are scoped to the environment that uses them. The Apple signing and notarization
  credentials live in `environment: release`, and `build-tray` says why it is the one build job
  that names it. The human approval lives in `environment: release-approval`.
- Prefer a federated, short-lived credential. `attest-sbom` signs with the OIDC token rather than a
  stored key, and `release.yml` records in its header that no PAT is needed: `GITHUB_TOKEN` mints
  the tag after approval.

## Concurrency

- CI takes `group: ci-${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress` true only
  for pull requests, so a force-push cancels its own superseded run and nothing else.
- Release takes a per-ref group with `cancel-in-progress: false`. A cancelled release has already
  pushed a tag and not its assets, which is a state a human has to clean up by hand.

## Release shape

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
- A release job is safe to re-run against the same tag.

## Timeouts, caches and leftovers

- A job whose suite can hang carries `timeout-minutes` (`dash-e2e` is 25), and a long single step
  carries its own budget rather than inheriting the six-hour runner default.
- A cache key names what the cached content depends on — the tray's apt cache keys on the package
  set and the week. A key that never changes serves a stale tree and hides a broken restore.
- **Three jobs start real processes** — `build-test`, `ts-gates` and `dash-e2e`. Each records
  `JOB_START_EPOCH` as its first step and ends with an `if: always()` step running
  `scripts/proc-guard.sh check-leftovers --root "$GITHUB_WORKSPACE" --since "$JOB_START_EPOCH"`, so
  a job that leaves a child behind fails even when its test step passed. `build-test` shuts the
  .NET build servers down first because they outlive every build by design. `CiLeftoverStepTests`
  pins all three jobs; add the pair of steps to any new job that starts a server, a watcher or a
  browser.

## The workflow runs the commands a contributor runs

- The argv in `review.gates` of [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml)
  is the command, and a workflow step repeats it. CI therefore reproduces a local failure instead
  of approximating it.
- Every local test, gate and e2e command runs through `scripts/proc-guard.sh run --` (root
  `AGENTS.md`). The watchdog refuses an unfiltered full-suite run from a dev agent (exit 89) and
  admits the council and CI, which pass `--council`. A workflow step that runs the full suite is
  legal precisely because CI is one of those two callers.
- The skill and docs gates build the pull request's own CLI and run it with `--no-build`; they
  never install a released binary. The behaviour under test is the commit's.

## Docs and skill gates

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

## Advisory is marked as advisory

`trivy`, `semgrep` and `gitleaks` carry `continue-on-error: true`, still upload their SARIF, and
read as "(advisory)" in the summary table. They are the only jobs that do. **`continue-on-error`
appears nowhere else**: a blocking step that can fail silently makes the reviewer re-check by hand
what the workflow was supposed to check. A disabled test or scan needs a linked issue and a date.

## Determinism

- `global.json` pins SDK `10.0.100` with `rollForward: latestFeature`. Workflows read the band from
  `env.DOTNET_VERSION` (`10.0.x`), so a patch release arrives without a workflow edit while the
  exact SDK still comes from the file.
- Template toolchain steps name a concrete runtime version (`node-version: "24"`).
  `ReviewGateWorkflowTemplateTests.RuntimeVersionsArePinnedNotFloating` rejects a floating value.
  `latest` in CI means the build that passes today fails on a morning nobody changed anything.
- Jobs run on a Linux runner except where the platform is the subject — the Windows capture tests,
  and the macOS signing and notarization in the release builds — and the job says why it is
  somewhere else.

## Templates are a product contract

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

## Tests that pin workflows

The workflow YAML is a test subject in
[`tests/KyberWeave.RepoPolicy.Tests/`](../../../tests/KyberWeave.RepoPolicy.Tests/AGENTS.md):
`CiLeftoverStepTests`, `ReviewGateWorkflowTemplateTests`, `ReviewGateFullSuiteConfigTests`,
`ReleaseTests`, `ReleaseNotesTests` and `SbomTests` read it. They run as the `test-policy` gate,
which selects on the same paths as `build`.

- An edit to a workflow that one of them reads runs that class by name before the change is
  reported done; the project's `AGENTS.md` gives the filtered command.
- A new structural invariant gets a test there rather than a paragraph here. A rule this document
  states without a test behind it is a rule a future edit can remove silently.

## Completion

A workflow change is proved the way a code change is — a build, the tests covering it, and a
format check — and the three are not optional because the file is YAML:

```bash
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<TestClass>"
git diff --check
actionlint .github/workflows/<file>.yml
```

- **The tests are the policy classes.** `CiLeftoverStepTests`, `ReviewGateWorkflowTemplateTests`,
  `ReviewGateFullSuiteConfigTests`, `ReleaseTests`, `ReleaseNotesTests` and `SbomTests` read the
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
