---
id: ci-pipelines/workflows-runbook
title: Wiring Kyber-Weave into CI
doc-type: runbook
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-10-30
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
  `webServer` targets the live host — stays unloaded. The isolated config also
  sets `KYBERDASH_SPAWN_GUARD=deny` and attaches the process-ledger teardown,
  which fails the job if the run leaves a child behind.
- **Leftover step:** the job ends with an `if: always()` step that runs
  `scripts/proc-guard.sh check-leftovers --root "$GITHUB_WORKSPACE" --since
  "$JOB_START_EPOCH"`. The first step of the job writes `JOB_START_EPOCH` to
  `$GITHUB_ENV`. The step fails the job (exit 87) when a process re-parented to
  PID 1 whose command line contains the workspace path, and that started after
  the job began, is still running. A job whose tests passed still fails when one
  survives.

Local equivalent (must stay GREEN; the command runs through the watchdog, see
[Running tests safely](../dash/runbook.md#running-tests-safely)):

```bash
scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts
```

### Leftover-process steps in `ts-gates`, `dash-e2e` and `build-test`

Three jobs in `ci.yml` end with the same leftover step. Each records the job's start
epoch in its first step and fails when a process it left behind is still running:

| Job | Test steps before the leftover step | Notes |
|---|---|---|
| `ts-gates` | `npm test` (dash), the lint and reachability gates, and the tray UI test | Dash and tray TypeScript gates. |
| `dash-e2e` | `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts …` | The only Playwright invocation in CI. |
| `build-test` | `dotnet test`, then `dotnet build-server shutdown` | The shutdown stops the MSBuild and `VBCSCompiler` servers, which outlive every build by design, so they are not counted as leftovers. |

The step runs `check-leftovers`, which lists orphaned processes (parent PID 1) whose command
line contains the workspace path and that started after the job began. The runner's own
processes do not carry the workspace path, so they are not counted. The check is POSIX-only;
these three jobs run on `ubuntu-latest`.

The CI test steps call their tools directly and are not wrapped by the watchdog. The leftover
step is the CI backstop; the watchdog wraps local runs and the review gate commands declared in
`.kyber-weave/kyber-weave.yml` (see the review council architecture).

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

Three jobs always run, even on docs-only pull requests: `build-test`, `gitleaks`, and
`skill-docs-gate` (the skill and docs gate). The .NET tests read files under `docs/`,
`products/`, `.kyber-weave/`, `.github/`, `scripts/`, and a few under `dash/`, so no path
set can prove them irrelevant.

Anything that is not a pull request — a push to `main`, the weekly Monday cron, a manual
dispatch — runs everything. So does any edit to `.github/workflows/ci.yml` itself or to
anything under `.github/codeql/`: a change to the filters is itself fully exercised.

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

## Related

- [CI Pipelines architecture](architecture.md) — severities, formats, exit codes
- [Rule reference](rule-reference.md) — every id you might suppress
- [Installing Kyber-Weave](../install.md) — the install script in detail
