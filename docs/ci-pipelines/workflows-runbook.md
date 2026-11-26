---
id: ci-pipelines/workflows-runbook
title: Wiring Kyber-Weave into CI
doc-type: runbook
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-11-26
---

# Wiring Kyber-Weave into CI

Copy-ready workflows live in [`templates/github-actions/`](../../templates/github-actions/).
This runbook explains what they do and what to change.

## Installing in a runner

The install script is served from the default branch and installs the **latest stable release
tag** by default. It only ever reads Release assets, so it stays compatible with older tags and a
script fix never requires a re-release ([distribution.md](../distribution.md)).

```yaml
steps:
  - name: Install Kyber-Weave
    run: |
      set -euo pipefail
      # Download then execute (not curl|sh) — keeps CI SAST clean; binaries the
      # script installs are still SHA-256 verified against the Release.
      curl -fsSL -o "${RUNNER_TEMP}/kyber-weave-install.sh" \
        https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh
      sh "${RUNNER_TEMP}/kyber-weave-install.sh" --no-mcp
      echo "$HOME/.local/bin" >> "$GITHUB_PATH"
```

`--no-mcp` skips the MCP server, which a CI gate never needs. Every binary is verified
against the release's `SHA256SUMS.txt` over HTTPS.

Tracking latest stable means a new release reaches your gates without a code change — which is
the intent, and also means a release can change gate behaviour on a build that touched
nothing. Where a job must be reproducible, add `--version 0.1.1` to pin it.

### Testing pre-release and development tags in CI

Kyber-Weave uses tag conventions to distinguish stable releases from pre-release candidates:

- **Release Candidates:** `v*-rc.*` (e.g. `v0.2.0-rc.1`)
- **Development Builds:** `v*-dev.*` (e.g. `v0.2.0-dev.1`)

To install the latest candidate release in a staging or integration CI runner, pass `--prerelease` (or set `KYBER_WEAVE_PRERELEASE=1`):

```yaml
steps:
  - name: Install Kyber-Weave Candidate
    run: |
      set -euo pipefail
      curl -fsSL -o "${RUNNER_TEMP}/kyber-weave-install.sh" \
        https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh
      sh "${RUNNER_TEMP}/kyber-weave-install.sh" --no-mcp --prerelease
      echo "$HOME/.local/bin" >> "$GITHUB_PATH"
```

To pin a specific candidate tag in CI, pass `--version 0.2.0-rc.1`.

### Verifying installed version in CI logs

To log and verify the exact Kyber-Weave version used during pipeline execution:

```yaml
  - name: Log installed version
    run: kyber-weave --version
```

Running `kyber-weave --version` (or `kyber-weave -v`) outputs `kyber-weave <version>` (e.g. `kyber-weave 0.1.0+commit_sha`), ensuring full auditability in CI job logs. See [install.md](../install.md) for the full option set.

## The three gates

| Template | Runs |
|---|---|
| `kyber-weave-docs-gate.yml` | `docs validate`, `docs drift` |
| `kyber-weave-skill-gate.yml` | `skill validate`, `skill lint`, `skill scan`, `skill route` |
| `kyber-weave-agent-gate.yml` | `agent validate`, `agent sync-check`, `agent scan` |
| `kyber-weave-review-gate.yml` | `review gates` (the deterministic per-technology checks) |

Each pins its version, sets `permissions: contents: read`, and cancels superseded
pull-request runs through a concurrency group.

## The review-gate template

`kyber-weave-review-gate.yml` runs `review gates` — the deterministic checks declared in
the `review:` section of `.kyber-weave/kyber-weave.yml`, written by `kyber-weave docs
init` — and fails the job when a blocking gate fails (exit `1`). Which gates run comes
from that section; this workflow supplies only the toolchains and the base to compare
against.

**It is copied by the host, never generated.** No Kyber-Weave command writes into
`.github/workflows/`: copy the template there and adapt it, like the other three in
`templates/github-actions/`.

`--base` is derived from the event: pull requests compare against the PR target
(`origin/${{ github.base_ref }}`), pushes against the pushed range (`${{
github.event.before }}`), and anything else against the remote default branch
(`origin/HEAD`). Full history is a hard requirement — the template checks out with
`fetch-depth: 0`, because `--base` resolves through `git merge-base`, which a
single-commit checkout cannot answer. The `--out` report lands in `artifacts/`.

The template passes `--council` on its `review gates` call, because CI is the council for gates marked
`full-suite: true` (`test`, `ts-test`, `cargo-test`). Without it those gates are refused. A host that
pins an older `kyber-weave` must update it before `--council` works: an older CLI warns about the
unknown key and runs the gates as before. The `ci.yml` in this repository is unchanged: its raw
`dotnet test` steps are not wrapped in `review gates`, so they still run the full suite on the PR.

Every toolchain step is disabled (`if: false`): set it to `true` for the technologies
the repository uses, so the gates can run their formatters, linters, builds and tests —
.NET via `global.json`, Node with one `npm ci` per package root, Rust with rustfmt and
clippy, Go via `go.mod` (the shipped Go format gate is `gofmt -l` with `fail-on-output`, so it fails on a diff on every Go version), Python with `ruff`, `mypy` and `pytest` installed by `pip`.

## Merge-ready in pull-request CI

The docs gate template runs `docs validate --merge-ready --base origin/${{ github.base_ref }}`
on pull requests. `--merge-ready` adds the two merge gates — open work
(`KW-DOC-LIFECYCLE-003`, whole corpus) and archive closeout (`KW-DOC-LIFECYCLE-004`,
change-scoped). `--base` names the ref the closeout scope is diffed against; the default
is `origin/HEAD`, which the template overrides with the PR base ref. On `push` events
there is no base ref, so pass `--base ${{ github.event.before }}` instead.

Full history is a hard requirement, not an optimization: the template checks out with
`fetch-depth: 0` because the scope resolves through `git merge-base`, which a
single-commit checkout cannot answer. A shallow checkout fails closed as `C0`
(`Archive closeout scope is unknown`) with a fetch hint, rather than reporting a clean
run. See [governance](../docgraph/governance.md) for the `C0`–`C9` conditions and the
gate-vs-lens split.

## `docs drift` needs a CodeGraph index

This is the one gate with an external prerequisite. `docs drift` resolves documented
symbols against `.codegraph/codegraph.db`, which **Kyber-Weave does not create** —
CodeGraph is a separate, host-owned tool. Without it the gate fails with a single
critical `KW-DOC-DRIFT-001` rather than a per-document error storm.

Two workable patterns:

**Build the index in the job** — run your CodeGraph indexer after checkout, before drift.

**Restore it from cache** — cheaper, and adequate because a stale index produces false
positives rather than false negatives:

```yaml
- uses: actions/cache@v4
  with:
    path: .codegraph
    key: codegraph-${{ github.sha }}
    restore-keys: codegraph-
```

`sqlite3` must also be on PATH; it is present on GitHub-hosted Ubuntu runners.

If you cannot provide an index, run `docs validate` alone. It needs no index and still
enforces the whole [ontology](../documentation-ontology.md).

## Uploading SARIF

Scan gates emit SARIF for GitHub code scanning, which turns findings into annotations on
the diff instead of buried log output:

```yaml
- name: Scan
  run: kyber-weave skill scan ./skills --format sarif > kyber-weave-skills.sarif
  continue-on-error: true

- uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: kyber-weave-skills.sarif
```

`continue-on-error` on the scan step is deliberate — upload the findings first, then let a
later step decide the build outcome. Otherwise a failing scan skips the upload and you
lose the annotations that explain the failure. This needs `permissions: security-events: write`.

## Tightening the gate

Scans gate on `Critical` alone by default. Once your baseline is clean, tighten:

```bash
kyber-weave skill scan ./skills --fail-on error
```

Add `--no-info` everywhere in CI; informational findings are noise in a log.

## Configuration in CI

Commands read [`.kyber-weave/kyber-weave.yml`](../configuration.md) from the target path
automatically. Pass `--config` only for a non-standard location. A malformed file fails as
`KW-CONFIG-001`, not a stack trace.

## Dash harness-collection e2e (`dash-e2e`)

This repository's own `ci.yml` runs a `dash-e2e` job (after `ts-gates`) that
enforces the per-harness completeness predicate continuously:

- **Purpose:** deterministic per-harness completeness enforcement — a measured
  harness renders measured dimensions with no lying zero, an honest-empty
  harness renders a visible session as unmeasurable with a reason, and a
  zero-record harness stays honest-empty and is never claimable as complete.
- **Scope:** Playwright-only on `ubuntu-latest`. No macOS runner, no funded
  model call, no new secret. The spec boots its own isolated dashboard from a
  self-seeded temp `canon.db` and never touches the live `4747` host. The boot
  keeps its `stateDir` inside that temp root and disables the JobHost
  maintenance pass: the fixture seeds the derived session and run rows directly,
  so a first-tick projection would rebuild them from an empty `records` table
  and serve an empty dashboard. Each test also asserts the runs payload carries
  the fixture run, which is what proves the served data came from the temp
  store rather than the operator's own `~/.kyberdash/canon.db`.
- **Scenarios:** the OpenCode scenario replays recorded OpenCode 2.2.1 events through the production
  plugin, receiver, adapter and projection. The honest-empty row and the other rows are rendering-only
  seeds. None of them is capture proof. Live OpenCode capture is verified only by the OpenCode live gate,
  which runs on the owner's machine and not in CI ([runbook §1b](../dash/runbook.md#1b-opencode-capture-enable-and-verify)).
- **Steps:** install dash deps (`npm ci`), `npx playwright install
  --with-deps`, then the conformance spec. The spec runs from the repository
  root with the pinned `dash` Playwright and the isolated config (`npx
  --prefix dash playwright test --config dash/playwright.isolated.config.ts
  dash/e2e/harness-collection.spec.ts`). That config is the only Playwright
  configuration the job loads, so `dash/playwright.config.ts` — whose
  `webServer` targets the live host — stays unloaded. The spec boots and tears down its own
  isolated C# engine, so the job fails when the run leaves its server behind.
- **Leftover processes:** no job carries a leftover-process step any more. The
  `scripts/proc-guard.sh check-leftovers` helper and the workflow steps that replaced it went
  with the Node engine, which was the only thing spawning recursive children. The .NET jobs end
  with `dotnet build-server shutdown` (`if: always()`) instead, and the conformance spec tears
  down the isolated engine it booted and fails the job when that server is still running.

Local equivalent (must stay GREEN; the command runs directly, with no wrapper — see
[Running tests safely](../dash/runbook.md#running-tests-safely)):

```bash
npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts
```

### Process cleanup steps in `dash-e2e`, `build-test` and `integration-test`

No job inspects the process table for leftovers: there is no wrapper, no start-epoch marker
and no `check-leftovers` step in `ci.yml`. Every job that builds .NET ends with the same
`dotnet build-server shutdown` step, because the MSBuild and `VBCSCompiler` servers outlive every
build by design. Its condition is `always() && steps.verdict.outputs.skip != 'true'`: it has to run
when an earlier step failed, and it must not run when a receipt skipped the build, because then no
server was ever started. The receipt save follows it in each of the three jobs.

| Job | Steps before the shutdown |
|---|---|
| `dash-e2e` | `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts …`, the only Playwright invocation in CI |
| `build-test` | `dotnet format`, `dotnet build`, `dotnet test`, `dotnet pack` |
| `integration-test` | `dotnet build`, the integration test project |

`ts-gates` starts no long-lived server, so it needs no cleanup step. `dash-e2e` also fails on
its own terms when the isolated C# engine it booted is still running at teardown, because the
spec stops that engine and waits for it to empty.

The CI test steps call their tools directly, and so do local runs: nothing wraps them any more,
because the retired watchdog and process ledger went with the Node engine. `kyber-weave review
gates` is what refuses a full-suite gate outside the council (see the review council
architecture). A run that hangs is stopped by you or by the tool's own timeout; there is no
absolute process cap.

`dash-e2e` runs only `harness-collection.spec.ts`. `refresh-filters.spec.ts` is in the isolated
config but not in CI, and the other Playwright specs have no sanctioned invocation. That gap is
tracked in [the Playwright webServer specs todo](../todo/kyberdash-playwright-webserver-specs.md).

## This repository's own CI: path-targeted jobs and the CodeQL neutral check

`.github/workflows/ci.yml` fans out from a `changes` job ("Detect changed areas") that
classifies the pull-request diff, then gates the expensive jobs with job-level `if:`
expressions — never a workflow-level `paths:` filter. Only "CI Summary" is a required
check, and a skipped job counts as passed, while a workflow that never starts would leave
the required check pending. The comments on the `changes` job in `ci.yml` state the
invariant; the `codeql` job's `if: needs.changes.outputs.codeql != '[]'` is one instance
of it.

Five jobs always run, even on docs-only pull requests: `build-test`, `integration-test`, `gitleaks`,
`pr-naming` (the pull request naming check), and `skill-docs-gate` (the skill and docs gate).
`pr-naming` is a pull-request job only, and it carries no path filter because every pull request
has a name. The .NET tests read files under `docs/`,
`products/`, `.kyber-weave/`, `.github/`, `scripts/`, and a few under `dash/`, so no path
set can prove them irrelevant.

`build-test` runs every unit project, the content tests, the policy tests and the architecture test
(`KyberWeave.Unit.slnf` lists them). `integration-test`, displayed as **Integration tests**, runs
`tests/KyberWeave.Integration.Tests` — the real Squad deployments, real child programs and
file-system work — on every pull request, docs-only ones included, and `CI Summary` requires both. The split exists so the
unit job can finish inside its 120-second budget; development agents do not run the integration
project. `squad-filesystem-contract` keeps its macOS and Windows legs and runs the same project with
a `SquadDeploymentState` filter.

Anything that is not a pull request — a push to `main`, the weekly Monday cron, a manual
dispatch — runs everything. So does any edit to `.github/workflows/ci.yml` itself or to
anything under `.github/codeql/`: a change to the filters is itself fully exercised.

### The pull request naming check and its recheck

`ci.yml` runs the check as the `pr-naming` job, displayed as **Pull request name**, gated on
`if: github.event_name == 'pull_request'`. It is its own job so a name that cannot pass fails in
seconds rather than after the build has spent a runner. It runs
`scripts/check-pr-name.sh --title … --branch … --author …` against the branch
`[<owner>.<agent>.]<harness>/<type>/<slug>` and the title `<type>(<scope>): <summary> [<harness>]`:
`<type>` is a conventional-commit type, `<slug>` is lowercase hyphen-separated words, `<scope>` is
one word, and `<harness>` is one of the thirteen catalogue tokens or `manual`. A pull request
authored by `dependabot[bot]` is exempt, because GitHub generates its branch and title; the
exemption follows the author, not the branch prefix. `kyber-weave run start` creates a branch in
this shape, so a run taken with it starts with a name that can pass
([`run start`](../cli-command-surface.md#run--run-start)).

**`CI Summary` needs it.** The summary job lists `pr-naming` in `needs:` and fails on its result
like any other job, so a pull request can never look green without having run the check. The job
skips on every other event, and that is safe because a push to `main` and the schedule have no
pull request to name and `CI Summary` is not a required check there.

**The job reads the API, not the event payload.** That is why a corrected title can turn the
required check green: `.github/workflows/pr-naming-recheck.yml` runs on
`pull_request: edited` with `if: github.event.changes.title != null`, finds the latest completed
CI run for the head commit, and re-runs that run's naming job and then `CI Summary`. CI itself
deliberately does not listen to `edited`: adding it would cancel and restart every CI run whenever
someone fixed a typo in a description, throwing away a full test cycle for an event that changes
nothing but the pull request's text.

The join between the two files is each job's display name — `Pull request name` and `CI Summary` —
because the API identifies a job by its `name:`, never by its key in the workflow. **Renaming
either job in `ci.yml` means editing `NAMING_JOB` or `SUMMARY_JOB` in `pr-naming-recheck.yml` in
the same change**; a rename that misses there fails the recheck step rather than passing silently.

### The expected neutral "1 configuration not found" check

A pull request that touches no .NET code skips the `csharp` CodeQL matrix leg, and a
docs-only pull request skips both legs. GitHub then reports a neutral
"1 configuration not found" check such as `CodeQL Analysis (csharp) /language:csharp`
(both languages on a docs-only change). This is expected and safe, for three reasons:

1. The neutral check cannot fail CI Summary — only `failure` or `cancelled` fails the
   gate; `skipped` passes.
2. Pushes to `main` and the weekly Monday cron always analyze both languages, so full
   coverage is continuous.
3. A pull request that cannot change C# cannot introduce C# alerts; there is nothing for
   the skipped leg to catch.

See the `changes` and `codeql` job comments in `.github/workflows/ci.yml` for the gating
logic.

## Content-hash receipts and skips

Both of this repository's workflows skip work on unchanged inputs. `ci.yml` does it per job, so a
re-run of one pull request's own commit reuses what that commit already proved instead of redoing
a build or a scan. `release.yml` does it once for the whole release chain, so a re-run of a release
that already published verifies the published artifacts instead of rebuilding them.

A **receipt** is a small JSON file a job writes after it passes. It records the job, the
fingerprint of every input the job read (`input_hash`), the commit and run that wrote it, the
SHA-256 digests of what it produced, and `saved_at_epoch` — the write time, in seconds, that the
freshness check reads. The lookup key contains the fingerprint, so a different tree cannot reach
the receipt. Every way of failing to read one runs the job in full: that is the mechanism's safe
direction throughout.

### The four receipt steps in a CI job

| Step | What it does |
|---|---|
| `Compute input fingerprint` (`id: fp`) | `hashFiles()` over the job's fingerprint set, written to the step outputs as `hash`. An empty fingerprint becomes `empty-<run id>`, which can only miss. |
| `Look up the receipt` (`id: receipt`) | `actions/cache/restore` of the `.receipt` path under `receipt-<job>-<leg>-<hash>`. Exact key, no `restore-keys`. |
| `Decide the verdict` (`id: verdict`) | Validates the receipt, compares its age against `RECEIPT_MAX_AGE_SECONDS`, and sets `skip` to `true` only when both hold. |
| `Save the receipt`, `Store the receipt` | Writes `.receipt/receipt.json` and saves it under the same key. Reached only on the miss path. |

Fourteen jobs carry the pattern: `build-test`, `integration-test`, `squad-filesystem-contract`,
`sbom`, `publish-smoke`, `update-loop`, `skill-docs-gate`, `ts-gates`, `dash-e2e`, `tray-rust`,
`codeql`, `trivy`, `semgrep` and `gitleaks`. `changes`, `pr-naming` and `ci-summary` do not: their
run is decided by the event or by the path filter.

The verdict is one script, repeated verbatim in every job so no job can drift. It checks in this
order, and each failure writes its own line to the step summary naming why the job runs in full:

1. The restore missed — no prior receipt. A fork pull request says so, naming cache isolation.
2. `.receipt/receipt.json` is absent, or is not JSON carrying `saved_at_epoch` and `input_hash`.
3. `saved_at_epoch` is not digits only.
4. The age is negative: the receipt is dated in the future.
5. The receipt's `input_hash` is not this run's fingerprint.
6. The age exceeds `RECEIPT_MAX_AGE_SECONDS`.

A fresh hit writes the receipt as JSON into the step summary and sets `skip=true`. Every step
after the verdict composes `steps.verdict.outputs.skip != 'true'` onto its own condition, so a hit
skips the job's work inside a job that still reports success, and `CI Summary` keeps reading a
success. A step that already had a condition keeps it and gains the skip guard beside it — the
`codeql` job's `if: matrix.language == 'csharp'` becomes `matrix.language == 'csharp' &&
steps.verdict.outputs.skip != 'true'`. No job-level `if:` reads a receipt: the
path filter still decides whether a job starts, and the receipt decides only what happens once it
has started.

### The fingerprint sets

Four sets, fixed per job, each pinned by `CiContentHashSkipTests`:

| Set | Covers | Jobs |
|---|---|---|
| Whole tree | Every tracked top-level entry: 25 directories as `<dir>/**` and 24 root files by exact name | `build-test`, `integration-test`, `squad-filesystem-contract`, `publish-smoke`, `update-loop`, `skill-docs-gate`, `codeql`, `trivy`, `semgrep`, `gitleaks` |
| SBOM | Every alternation of the `changes` job's sbom filter: the lockfiles, `**/*.csproj`, `**/Directory.*.props`, `global.json`, the three SBOM scripts, `dash/tray/vendor/**` and both workflow files | `sbom` |
| Dash | `dash/**`, `global.json`, `Directory.Build.props` and `ci.yml` | `ts-gates`, `dash-e2e` |
| Tray | `dash/tray/**`, `dash/.nvmrc` and `ci.yml` | `tray-rust` |

The whole-tree set names every entry instead of globbing the root: a bare `'*'` or `'**'` hashes
the same files without proving the list complete, and a missing entry is a silent skip — the job
would reuse a receipt from a run that never read the file. The test compares the set against
`git ls-tree HEAD`, so a new top-level directory or file fails until the enumeration is updated.

Every set includes `ci.yml` itself, so any workflow edit invalidates every key. The SBOM set is the
one that has to track a filter, and the test fails when the `changes` job's sbom alternations and
the set's arguments disagree.

### Keys, cache scope and the freshness ceiling

A key is `receipt-<job>-<leg>-<hash>`. The leg is the dimension the job varies on:
`${{ matrix.os }}` for the three multi-OS jobs, `${{ matrix.language }}` for `codeql`, and
`${{ runner.os }}` otherwise. There are no `restore-keys`: a prefix would hand a job the receipt of
a different tree, which is the one way this mechanism could skip work over input it never
fingerprinted. A miss costs one full run, which is the safe direction. The key carries no age and
no week, so the ceiling can be tightened without invalidating a key.

Cache scope is what makes the mechanism safe on a pull request:

- **A pull request's saves are scoped to that pull request.** Only a later run of the same pull
  request reads them.
- **Only default-branch runs populate the scope every workflow and branch can read.** A push to
  `main` writes a receipt that a pull request branched from it can hit.

`RECEIPT_MAX_AGE_SECONDS` is one workflow variable, `14400` — four hours — in both workflows, and
no job overrides it. It bounds how long a skip is honoured: a re-run within four hours of a green
run on the same tracked inputs skips, and a re-run after that pays the full cost again even though
nothing changed. That is the trade the owner accepted. The fingerprint covers only tracked files,
so a longer window would let a re-run trust a proof that an upstream change the tree does not
record — a republished package, a moved toolchain image, a new advisory — has invalidated.

### Scripts run under `bash`

Every receipt script (`id: fp`, `id: verdict`, `id: save`, and the `id: digest` step where a job
has one) declares `shell: bash`. A job with a Windows leg — `squad-filesystem-contract` and
`tray-rust` — would run these scripts under `pwsh` by default, where `date +%s` and the verdict's
integer arithmetic do not work. The policy test fails a receipt step that omits it.

### Fork pull requests

A pull request from a fork reads no cache from the base repository — cache isolation — so its
restore always misses and its jobs always run in full. The save and the attestation steps carry an
explicit same-repository guard, `github.event_name != 'pull_request' ||
github.event.pull_request.head.repo.full_name == github.repository`, so a fork writes neither a
receipt nor an attestation into the base repository.

### The release chain: one receipt for the whole build

`release.yml` writes a **chain receipt**: one shared, version-bound record for the release being
built. Nine jobs restore it under the single key `release-chain-<version>` and the
`.release-chain` path, and run one verdict step — `build`, `build-kyberdash`, `pack-squad`,
`build-tray`, `sbom`, `artifact-sboms`, `attest-sbom`, `release` and `gh-packages`. Every
`release-chain-` key line in the file is that same key: one receipt for the chain, not one per
job.

The seven build and SBOM jobs gate every step on `steps.verdict.outputs.skip != 'true'` except
four: the checkout, the cache restore, the verdict itself, and `build-tray`'s `if: always()`
keychain cleanup, which must run even when the build failed. The two publish jobs gate only their
publishing steps; their verification always runs.

The verdict requires only that the file exist and `saved_at_epoch` be digits — the key is already
bound to one version — and compares the age against the ceiling. A stale, absent or unparsable
receipt rebuilds the chain, and the verdict says which. The receipt
is recorded only in the `release` job, and only after the release, its asset inventory, its notes,
its SBOM attestations and its provenance attestations have all verified — a half-published release
leaves no receipt. It records the version, the tag, the writing run's URL, the published asset
names, the published `SHA256SUMS.txt` verbatim, and the package versions pushed to GitHub Packages.

On a hit the publish jobs verify instead of re-publishing, and every check fails closed:

| Step | On a hit |
|---|---|
| `Verify published release assets` | Compares the published asset names against the receipt's recorded list as sorted sets, so a missing or an extra asset exits `1`. Downloads the published `SHA256SUMS.txt` and `diff`s it against the manifest the receipt recorded. |
| `Verify SBOM attestations` and `Verify provenance attestations` | Download the published archives with `gh release download`, then verify each one with `gh attestation verify` — pinning `https://cyclonedx.org/bom` and `https://slsa.dev/provenance/v1`. Both exit `1` when no archive was found, so a hit cannot pass vacuously. |
| `Verify the published package versions` | Looks each recorded `name@version` up through the GitHub Packages API with `gh api`, and exits `1` when a version is absent. |
| `Report the run that published this release` | Writes the prior run's URL to the step summary. |

What a hit carries rather than re-checks is the signing work. The publishing run verified the
macOS Developer ID signature, the team and the notarization assertions on the archives whose
hashes the recorded manifest lists, and the hit path fails unless the published manifest is
byte-identical to that recorded one. `Verify the signature, the team and the notarization` lives in
`build-tray` behind the skip guard, so it does not run a second time.

The receipt save is miss-only and carries `continue-on-error: true`. Miss-only because a hit that
refreshed `saved_at_epoch` would make the skip permanent; the ceiling has to expire on its own. The
failure tolerance covers a cache-reserve race on the shared key: a save that loses that race must
not fail a publish that already succeeded, and its failure mode is fail-open — the next run
re-executes. It is the one sanctioned exception to the rule that `continue-on-error` appears only
on the advisory scans, and the [coding standard](../standards/github-actions/github-actions.standards.md#receipts-and-skips)
states it as one.

## Related

- [CI Pipelines architecture](architecture.md) — severities, formats, exit codes
- [Rule reference](rule-reference.md) — every id you might suppress
- [Installing Kyber-Weave](../install.md) — the install script in detail
