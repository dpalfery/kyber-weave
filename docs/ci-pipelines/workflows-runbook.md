---
id: ci-pipelines/workflows-runbook
title: Wiring Kyber-Weave into CI
doc-type: runbook
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-08-01
---

# Wiring Kyber-Weave into CI

Copy-ready workflows live in [`templates/github-actions/`](../../templates/github-actions/).
This runbook explains what they do and what to change.

## Installing in a runner

The install script is served from the default branch and installs the **latest release
tag**. It only ever reads Release assets, so it stays compatible with older tags and a
script fix never requires a re-release ([distribution.md](../distribution.md)).

```yaml
steps:
  - name: Install Kyber-Weave
    run: |
      curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh \
        | sh -s -- --no-mcp
      echo "$HOME/.local/bin" >> "$GITHUB_PATH"
```

`--no-mcp` skips the MCP server, which a CI gate never needs. Every binary is verified
against the release's `SHA256SUMS.txt` over HTTPS.

Tracking latest means a new release reaches your gates without a code change — which is
the intent, and also means a release can change gate behaviour on a build that touched
nothing. Where a job must be reproducible, add `--version 0.1.1` to pin it. See
[install.md](../install.md) for the full option set.

## The three gates

| Template | Runs |
|---|---|
| `kyber-weave-docs-gate.yml` | `docs validate`, `docs drift` |
| `kyber-weave-skill-gate.yml` | `skill validate`, `skill lint`, `skill scan`, `skill route` |
| `kyber-weave-agent-gate.yml` | `agent validate`, `agent sync-check`, `agent scan` |

Each pins its version, sets `permissions: contents: read`, and cancels superseded
pull-request runs through a concurrency group.

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

## Related

- [CI Pipelines architecture](architecture.md) — severities, formats, exit codes
- [Rule reference](rule-reference.md) — every id you might suppress
- [Installing Kyber-Weave](../install.md) — the install script in detail
