---
id: ci-pipelines/workflows-runbook
title: Wiring Kyber-Weave into CI
doc-type: runbook
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-10-28
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
- **Steps:** install dash deps (`npm ci`), `npx playwright install
  --with-deps`, then the conformance spec. The spec runs from the repository
  root with the pinned `dash` Playwright (`npx --prefix dash playwright test
  dash/e2e/harness-collection.spec.ts`) so `dash/playwright.config.ts` —
  whose `webServer` targets the live host — stays unloaded.

Local equivalent (must stay GREEN 3/3):

```bash
npx --prefix dash playwright test dash/e2e/harness-collection.spec.ts
```

## Related

- [CI Pipelines architecture](architecture.md) — severities, formats, exit codes
- [Rule reference](rule-reference.md) — every id you might suppress
- [Installing Kyber-Weave](../install.md) — the install script in detail
