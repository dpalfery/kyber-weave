# Kyber-Weave GitHub Actions templates

Sample workflows for host repositories. Copy into `.github/workflows/` and adapt the paths.

These are **product templates**, not live host CI.

| Template | Gates | Feature |
| --- | --- | --- |
| [`kyber-weave-docs-gate.yml`](kyber-weave-docs-gate.yml) | `docs validate` · `docs drift` | [DocGraph](../../docs/docgraph/governance.md) |
| [`kyber-weave-skill-gate.yml`](kyber-weave-skill-gate.yml) | `skill validate` · `skill lint` · `skill scan` (SARIF) | [ContextHygiene](../../docs/context-hygiene/skills.md) |
| [`kyber-weave-agent-gate.yml`](kyber-weave-agent-gate.yml) | `agent validate` · `agent scan` (per harness) · `agent sync-check` | [ContextHygiene](../../docs/context-hygiene/agents.md) |

Full explanation of what these do and why:
[workflow runbook](../../docs/ci-pipelines/workflows-runbook.md).

---

## How to copy

1. Copy one or more YAML files into `.github/workflows/`.
2. Adjust the env paths:
   - Skills: `SKILL_DIRS`
   - Agents: `PROJECT_ROOT` and the `harness` matrix
   - Docs: nothing — the roots come from `ontology.docs-root` in config
3. Optionally add a [`.kyber-weave/kyber-weave.yml`](../../docs/configuration.md) for ontology and harness overrides.
4. Wire job names into branch protection if they should block merges.
5. Keep `security-events: write` if you keep the SARIF upload steps.

## Installing

Every template installs the same way — the script tracks the **latest release** and
verifies each binary against the release's `SHA256SUMS.txt`:

```yaml
- name: Install Kyber-Weave
  run: |
    set -euo pipefail
    curl -fsSL -o "${RUNNER_TEMP}/kyber-weave-install.sh" \
      https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh
    sh "${RUNNER_TEMP}/kyber-weave-install.sh" --no-mcp
    echo "$HOME/.local/bin" >> "$GITHUB_PATH"
```

`--no-mcp` skips the MCP server, which a CI gate never needs. No Node, no .NET runtime —
the binaries are self-contained. Download-then-execute (not `curl | sh`) keeps CI SAST
clean; the script still verifies each binary against the release `SHA256SUMS.txt`.

Tracking latest means a new release reaches your gates without a code change. Where a job
must be reproducible, pin it:

```yaml
    sh "${RUNNER_TEMP}/kyber-weave-install.sh" --no-mcp --version 0.1.1
```

Third-party `uses:` SHAs in these templates are already pinned to full commit SHAs. Bump
them deliberately.

### Smoke test after copying

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
kyber-weave skill validate .agents/skills --format table
kyber-weave agent sync-check . --format table
kyber-weave docs validate . --format table
```

---

## Path conventions (adapt freely)

| Concern | Template default | Notes |
| --- | --- | --- |
| Skills | `.agents/skills` | Templates skip missing directories. |
| Agents | repo root `.` | Matrix default: `codex`, `cursor`, `claude`, `github`, `opencode`, `kilo`. |
| Docs roots | `ontology.docs-root` in config | One directory or several. `--docs-root <dir>`, repeated, overrides for a single run. |
| CodeGraph | `.codegraph/codegraph.db` | Required for `docs drift` only. CodeGraph is **host-owned**, not part of Kyber-Weave. |

`docs drift` is the one gate with an external prerequisite — build or cache a CodeGraph
index before it runs. `docs validate` needs nothing and still enforces the whole ontology.
See the [workflow runbook](../../docs/ci-pipelines/workflows-runbook.md#docs-drift-needs-a-codegraph-index).

## Permissions checklist

| Need | Permission |
| --- | --- |
| Checkout | `contents: read` |
| Upload SARIF | `security-events: write` |

## What these templates intentionally omit

- Host-only path filters and merge-summary aggregation
- Azure / deployment secrets
- Install paths other than the script — see [distribution](../../docs/distribution.md)
