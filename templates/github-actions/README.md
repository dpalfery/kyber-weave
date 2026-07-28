# Kyber-Weave GitHub Actions templates

Sample workflows for host repositories. Copy into `.github/workflows/`, then pin versions and adapt paths.

These are **product templates**, not live host CI.

| Template | Gates |
| --- | --- |
| [`kyber-weave-skill-gate.yml`](kyber-weave-skill-gate.yml) | `skill validate` · `skill lint` · `skill scan` (SARIF) |
| [`kyber-weave-agent-gate.yml`](kyber-weave-agent-gate.yml) | `agent validate` · `agent scan` (per harness) · `agent sync-check` |
| [`kyber-weave-docs-gate.yml`](kyber-weave-docs-gate.yml) | `docs validate` · `docs drift` (needs a CodeGraph index) |

---

## How to copy

1. Copy one or more YAML files into `.github/workflows/`.
2. Set **version pins** (see below). Never leave placeholders in production CI.
3. Adjust env paths:
   - Skills: `SKILL_DIRS`
   - Agents: `PROJECT_ROOT` / `HARNESS` matrix
   - Docs: `DOCS_ROOT` (CLI default is host-dependent; templates use `docs`)
4. Optionally add a root `kyber-weave.yml` for ontology / harness overrides.
5. Wire job names into branch protection if they should block merges.
6. Grant `security-events: write` if you keep SARIF upload steps.

### Minimal smoke after copy

```bash
npm i -g @dpalfery/kyber-weave@<PINNED_VERSION>
kyber-weave skill validate .agents/skills --format table
kyber-weave agent sync-check . --format table
kyber-weave docs validate . --docs-root docs --format table
```

---

## Version pins (required)

| Placeholder | Where | Purpose |
| --- | --- | --- |
| `KYBER_WEAVE_VERSION` | workflow `env` | Exact SemVer of npm package `@dpalfery/kyber-weave` (e.g. `0.1.0`), **or** leave empty and install from a Release asset instead |
| `NODE_VERSION` | workflow `env` | Node used only to run `npm i -g` (binaries are self-contained; Node is not required at CLI runtime) |
| Third-party `uses:` SHAs | each step | Already pinned to full commit SHAs. Bump deliberately. |

```yaml
env:
  # PIN: published @dpalfery/kyber-weave SemVer — replace before production use
  KYBER_WEAVE_VERSION: "0.1.0"
  NODE_VERSION: "22"
```

### Install options

**Preferred — npm:**

```yaml
- uses: actions/setup-node@820762786026740c76f36085b0efc47a31fe5020 # v7.0.0
  with:
    node-version: ${{ env.NODE_VERSION }}
- run: npm i -g "@dpalfery/kyber-weave@${{ env.KYBER_WEAVE_VERSION }}"
```

The npm package downloads the matching GitHub Release binaries for the runner RID at install time (or on first CLI invocation). Ensure the runner can reach `github.com` releases, or pre-stage binaries and set `KYBER_WEAVE_BINARY_DIR`.

**Also first-class — GitHub Release asset** for the runner RID:

```yaml
- name: Install Kyber-Weave from Release
  env:
    TAG: v${{ env.KYBER_WEAVE_VERSION }}
  run: |
    set -euo pipefail
    RID=linux-x64
    curl -fsSL -o kw.tgz \
      "https://github.com/dpalfery/kyber-weave/releases/download/${TAG}/kyber-weave-${RID}.tar.gz"
    tar -xzf kw.tgz
    sudo install -m 755 kyber-weave /usr/local/bin/kyber-weave
```

**Homebrew** for macOS runners / local developer machines:

```bash
brew tap dpalfery/kyber-weave
brew install kyber-weave
```

**Advanced only:** GitHub Packages `dotnet tool` for .NET specialists. **nuget.org is not used.**

---

## Path conventions (adapt freely)

| Concern | Template default | Notes |
| --- | --- | --- |
| Skills | `.agents/skills` | Templates skip missing directories. |
| Agents | repo root `.` | Matrix default: `codex`, `cursor`, `claude`, `github`, `opencode`, `kilo`. |
| Docs root | `docs` | Hosts that use `6-Docs` should set `DOCS_ROOT` explicitly. |
| CodeGraph | `.codegraph/codegraph.db` | Required for `docs drift`. CodeGraph is **not** part of the Kyber-Weave product kernel. |

---

## Permissions checklist

| Need | Permission |
| --- | --- |
| Checkout | `contents: read` |
| Upload SARIF | `security-events: write` |

---

## What these templates intentionally omit

- Host-only path filters and merge-summary aggregation
- NVIDIA SkillSpector (host-owned)
- Azure / deployment secrets
- nuget.org install paths
