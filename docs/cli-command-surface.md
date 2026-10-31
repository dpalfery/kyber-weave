---
id: cli-command-surface
title: CLI command surface
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-10-29
---

# CLI command surface

The complete command surface of the two binaries this repository ships: `kyber-weave` (the
CLI) and `kyberdash` (the KyberDash engine behind the terminal report, the web dashboard,
and the tray). It carries every registered branch, verb, flag, and exit-code rule, plus the
canonical install, verify, update, and configure flows for both products.

This page is an **aggregation index**. Each entry names the feature page that owns it, and
those pages stay canonical — the by-feature rule in
[`src/KyberWeave.Cli/AGENTS.md`](../src/KyberWeave.Cli/AGENTS.md) is preserved, not
replaced. When you change a verb, a flag, or an exit-code rule, update the owning feature
page **and** this page in the same change.

## Two audiences, one page

The first half is for a person deciding what to run and how to get there. The second half
is the same material as stable command sequences an agent can cite, with this page's own
anchors named so the reference is reachable without prose.

### If you are a person

Read [Canonical flows](#canonical-flows) for the install, verify, update, and configure
path of each product, then the per-verb tables for what a specific command does. Start with
[docs/README.md](README.md) if you are new to the corpus, or
[Installing Kyber-Weave](install.md) for the distribution mechanics.

### If you are an agent

Jump to [Command sequences for agents](#command-sequences-for-agents). Every sequence below
is stable and links back to the reference tables: [`kyber-weave` reference](#kyber-weave-reference),
[`kyberdash` reference](#kyberdash-reference), [Exit codes](#exit-codes), and
[`install.sh` flags](#installsh-flags).

## Canonical flows

### Install

Both binaries install from the same one-liner on macOS and Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
```

It places `kyber-weave`, `kyber-weave-mcp`, and — from releases that publish it — `kyberdash`
as self-contained binaries in `~/.local/bin`. No .NET runtime and no sudo are needed. On
Windows, download the `.zip` archives from
[Releases](https://github.com/dpalfery/kyber-weave/releases), verify the SHA-256 hash, and
extract them onto your `PATH`.

`install.sh` is the only supported user install path: npm, Homebrew, and `dotnet tool` are
not. See [Which product, and which install path](install.md#which-product-and-which-install-path),
[Install channels that are not supported](install.md#install-channels-that-are-not-supported),
and the [`install.sh` flags](#installsh-flags) table on this page. The KyberDash binary's
availability per release is governed by
[the version floor](install.md#kyberdash-and-the-version-floor); the optional macOS/Windows
tray is covered by [the KyberDash tray](install.md#the-kyberdash-tray) and the
[KyberDash adoption guide](dash/onboarding.md#install-the-tray).

### Verify the download

```bash
sha256sum -c SHA256SUMS.txt          # Linux
shasum -a 256 -c SHA256SUMS.txt      # macOS
```

`install.sh` verifies the checksum itself for the one-liner path. For a manual download, see
[Verifying a download](install.md#verifying-a-download). Then confirm the binaries run:

```bash
kyber-weave --version
kyberdash --version
```

### Update

`kyber-weave update` replaces the running Release binaries — the CLI, the sibling
`kyber-weave-mcp`, the installed `kyberdash`, and an installed KyberDash tray — after
SHA-256 verification. It delegates the tray step to `kyberdash menubar --update`. It does not
wrap `install.sh`, and it refuses `dotnet run` and `dotnet tool` installs. After the binaries
commit, it reconciles KyberDash in one run: `kyberdash engine ensure --if-running`, the tray
relaunch when needed, then `kyberdash kyber capture plugin install --harness opencode --preserve
--if-installed`. The reconciliation also runs when the release is unchanged ("already on").
When the plugin file changed, it prints a line saying to restart OpenCode if new OpenCode sessions do not
appear. See [Updating](install.md#updating).

```bash
kyber-weave update                      # latest stable, all components
kyber-weave update --release-candidate  # newest release, including RC and -dev tags
kyber-weave update --no-kyberdash       # leave the KyberDash binary alone
kyber-weave update --no-menubar         # leave the tray alone
kyber-weave update --no-restart         # replace binaries; skip the engine and tray restart (plugin still upgrades if installed)
```

The tray alone updates with `kyberdash menubar --update`. Tray-only details live in the
[KyberDash adoption guide](dash/onboarding.md#update).

### Configure

Kyber-Weave reads `.kyber-weave/kyber-weave.yml` at the repository root; `kyber-weave docs init .`
scaffolds it along with the catalog and the ontology reference, then deploys the authoring
skill through APM. The full key surface is in [Configuration](configuration.md); the
initialization walkthrough is in
[Adopting DocGraph in an existing repository](docgraph/onboarding.md#1-initialize).

```bash
kyber-weave docs init .                       # scaffold config, catalog, ontology; deploy the skill
kyber-weave docs init . --docs-root docs --target agent-skills
```

KyberDash shares one settings store across the CLI, the web dashboard, and the tray; read and
write it with `kyberdash dash settings`. The values are `folder_import.scheduled`,
`jobs.paused`, `jobs.refresh_cadence_minutes`, `receiver.hosted`, and `dashboard.port`. The
configure walkthrough is in the
[KyberDash adoption guide](dash/onboarding.md#configure).

```bash
kyberdash dash settings show
kyberdash dash settings show --json
kyberdash dash settings set folder_import.scheduled on
kyberdash dash settings set dashboard.port 4747
```

## Command sequences for agents

Stable, copy-ready sequences. Each step names the anchor that documents it; the per-verb
tables below carry flags and exit codes.

**Install both products** ([Canonical flows § Install](#install), [`install.sh` flags](#installsh-flags)):

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
```

**Verify a manual download** ([Verify the download](#verify-the-download)):

```bash
sha256sum -c SHA256SUMS.txt
kyber-weave --version
kyberdash --version
```

**Update** ([Update](#update), [Exit codes](#exit-codes)):

```bash
kyber-weave update
kyber-weave update --release-candidate --no-menubar
kyberdash menubar --update
```

**Configure Kyber-Weave** ([Configure](#configure)):

```bash
kyber-weave docs init .
kyber-weave docs validate .
kyber-weave docs drift .
```

**Configure KyberDash** ([Configure](#configure)):

```bash
kyberdash dash settings show
kyberdash dash settings set jobs.refresh_cadence_minutes 30
```

The sections that document these sequences in full are
[Canonical flows](#canonical-flows), [`kyber-weave` reference](#kyber-weave-reference),
[`kyberdash` reference](#kyberdash-reference), [Exit codes](#exit-codes), and
[`install.sh` flags](#installsh-flags).

## `kyber-weave` reference

The CLI is a Spectre.Console.Cli application with six branches — `skill`, `agent`, `docs`,
`squad`, `utilities`, and `review` — plus a top-level `update` verb. Unknown options are a
hard error: strict parsing is on, so a mistyped flag fails rather than being silently
dropped.

### Global options

| Option | Meaning |
|---|---|
| `-h`, `--help` | Show help for the command or branch. |
| `-v`, `--version` | Print the version (`kyber-weave <version>`). |

Most branches and verbs share two settings bases: `AnalysisSettings` supplies `[path]`,
`-f\|--format <FORMAT>` (`table` \| `json` \| `sarif` \| `markdown`; default `table`),
`--no-info`, and `-c\|--config <PATH>`; `DocsSettings` extends it with `--docs-root <DIR>`
(repeatable). `--config` defaults to `<path>/.kyber-weave/kyber-weave.yml`.

### `skill` — Agent Skills governance

Owned by [Skill governance](context-hygiene/skills.md), with the trust-surface engine under
[Instruction-surface scanning](context-hygiene/security-scanning.md).

| Verb | Flags (beyond `[path]`) | What it does |
|---|---|---|
| [`skill validate`](context-hygiene/skills.md#spec-conformance--kw-skill-spec-001-012) | `-f\|--format`, `--no-info`, `-c\|--config` | Check skills against the Agent Skills open-format spec. |
| [`skill lint`](context-hygiene/skills.md#routing-readiness--kw-skill-lint-001-011) | validate flags + `--min-desc-score <SCORE>` (default `70`), `--explain` | Score routing readiness; detect name/description collisions. |
| [`skill scan`](context-hygiene/security-scanning.md) | validate flags + `--fail-on <SEVERITY>` (default `critical`) | Scan skills as a trust surface (injection, secrets, risky scripts). |
| [`skill route`](context-hygiene/skills.md#routing-simulation--skill-route) | `[prompt]`, `-s\|--skills <PATH>` (default `.`), `--eval <FILE>`, `--min-accuracy <VALUE>` (default `0.9`), `--strategy <NAME>` (default `lexical`), `--threshold <VALUE>` (default `0.08`), `--json` | Simulate which skill fires, or run a routing eval set. |
| [`skill catalog`](context-hygiene/skills.md#commands) | `[path]`, `--json` | Inventory all skills under a path. |
| [`skill pack`](context-hygiene/skills.md#commands) | `<path>`, `-o\|--output <FILE>`, `--skip-validation` | Bundle one skill into a Copilot Studio-compatible `.zip`. |
| [`skill new`](context-hygiene/skills.md#commands) | `<name>`, `-o\|--output <DIR>` (default `.`), `-t\|--template <TEMPLATE>` (default `blank`), `--no-license`, `--no-metadata` | Scaffold a spec-correct skill from a template. |

### `agent` — harness agent definition governance

Owned by [Agent harness governance](context-hygiene/agents.md).

| Verb | Flags (beyond `[path]`) | What it does |
|---|---|---|
| [`agent validate`](context-hygiene/agents.md#manifest-conformance--kw-agent-spec-001-004) | validate flags + `--harness <NAME>` | Validate harness agent manifests discovered as `.harness/agents`. |
| [`agent scan`](context-hygiene/security-scanning.md) | validate flags + `--harness <NAME>`, `--fail-on <SEVERITY>` (default `critical`) | Scan agent prompts as a trust surface. |
| [`agent sync-check`](context-hygiene/agents.md#parity-and-drift--kw-agent-sync--kw-agent-lint-) | validate flags + `--harness <NAME>` | Verify role parity and instruction drift across harness folders. |
| [`agent catalog`](context-hygiene/agents.md#commands) | `[path]`, `-c\|--config` | Display the role x harness governance matrix. |

The three shared `validate` flags (`-f|--format`, `--no-info`, `-c|--config`) come from
[`AnalysisSettings`](#global-options).

### `docs` — DocGraph governance

Owned by [Documentation governance gates](docgraph/governance.md), with analysis under
[Documentation analysis and review](docgraph/analysis.md).

| Verb | Flags (beyond `[path]` and `--docs-root`) | What it does |
|---|---|---|
| [`docs init`](docgraph/onboarding.md#1-initialize) | `--owner <OWNER>` (default `unassigned`), `-t\|--target <TARGETS>` (default `agent-skills`), `--no-skill`, `--force`, `--kyber-standards`, `--no-review-gates` | Scaffold host config, the catalog, and the ontology reference; append the catalogue `review:` gates for the declared technologies when none exist (`--no-review-gates` skips this); deploy the authoring skill via APM. |
| [`docs validate`](docgraph/governance.md#schema-conformance--docs-validate) | validate flags + `--docs-root`, `--merge-ready`, `--base <REF>` (default `origin/HEAD`) | Check frontmatter against the ontology schema (`KW-DOC-SPEC-*`); with `--merge-ready` also fail on open work (`KW-DOC-LIFECYCLE-003`) and missing archive closeout (`KW-DOC-LIFECYCLE-004`, scoped to the diff against `--base`). |
| [`docs drift`](docgraph/governance.md#entity-drift--docs-drift) | validate flags + `--docs-root` | Resolve documented symbols against the CodeGraph index (`KW-DOC-DRIFT-*`). |
| [`docs export-graph`](docgraph/architecture.md) | validate flags + `--docs-root`, `-o\|--out <DIR>` (default `./build/doc-graph`) | Export the documentation graph as `nodes.jsonl` and `edges.jsonl`. |
| [`docs catalog`](docgraph/governance.md#coverage--docs-catalog) | validate flags + `--docs-root` | Display doc-type coverage by component. |
| [`docs integrity-check`](docgraph/analysis.md#cli-and-exit-behavior) | validate flags + `--docs-root`, `--fail-on <SEVERITY>` (default `none`) | Find duplicate claims, potential conflicts, and ambiguous terminology. |
| [`docs review export`](docgraph/analysis.md#cli-and-exit-behavior) | integrity flags + `--out <PATH>` | Export pending analysis candidates for agent review. |
| [`docs review import`](docgraph/analysis.md#cli-and-exit-behavior) | integrity flags + `--in <PATH>` | Validate and atomically cache an agent verdict bundle. |
| [`docs glossary`](docgraph/analysis.md#managed-glossary) | validate flags + `--docs-root`, `--write` | Preview or merge managed glossary proposals. |

`docs review export` / `import` are the real review-exchange verbs. There is no
`skill review` branch; the `KW-SKILL-REVIEW-001` rule id has no CLI caller.

### `squad` — Kyber-Squad deployment

Owned by the [Kyber-Squad adoption and usage guide](kyber-squad/onboarding.md), with the
engine under [Kyber-Squad architecture](kyber-squad/architecture.md).

| Verb | Flags | What it does |
|---|---|---|
| [`squad install`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path <PATH>`, `-t\|--target <TARGETS>`, `-x\|--exclude <TARGETS>`, `-g\|--global`, `--dry-run`, `--adopt`, `--yes`, `-v\|--version <VERSION>`, `--no-capture-plugin` | Deploy canonical agents and skills to harness directories. With `--global` and OpenCode resolved, also installs the KyberDash OpenCode capture plugin (preserve mode); `--no-capture-plugin` skips that step. |
| [`squad update`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global`, `-t\|--target`, `-x\|--exclude`, `--dry-run`, `--replace-managed`, `--yes`, `-v\|--version`, `--projects <PROJECTS>`, `--no-capture-plugin` | Update an existing deployment while preserving managed local edits. Same OpenCode plugin step as `squad install`. |
| [`squad uninstall`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global`, `--dry-run`, `--yes` | Remove files recorded in the ownership receipt. |
| [`squad status`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global` | Verify deployed files and report drift against the receipt. |
| [`squad doctor`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global` | Diagnose prerequisites, toolchain availability, and deployment health. |
| [`squad pack`](kyber-squad/onboarding.md#packaging-squad-pack) | `-f\|--format <FORMAT>` (`apm` \| `plugins` \| `all`; default `all`), `-o\|--out <DIR>`, `-v\|--version` | Package agents and skills into distribution archives. |
| [`squad preset use`](kyber-squad/model-profiles.md#apply-switch-and-revert) | `<name>`, `-t\|--target <TARGETS>`, `-g\|--global`, `--dry-run` | Apply a model preset to target harnesses, recording the selection. |
| [`squad preset clear`](kyber-squad/model-profiles.md#apply-switch-and-revert) | `-t\|--target <TARGETS>`, `-g\|--global` | Revert target harnesses to bundled model profiles. |
| [`squad preset list`](kyber-squad/model-profiles.md#apply-switch-and-revert) | — | List available presets and their active deployments. |
| [`squad preset show`](kyber-squad/model-profiles.md#apply-switch-and-revert) | `<name>`, `--target <TARGET>` | Inspect resolved models for a preset and target harness. |

### `utilities` — Kyber-owned utility artifacts

Owned by the [Kyber Utilities adoption and activation guide](kyber-utilities/onboarding.md).

| Verb | Flags | What it does |
|---|---|---|
| [`utilities statusline deploy`](kyber-utilities/onboarding.md#command-reference) | `-t\|--target <TARGET>` (`claude` \| `agy` \| `pi`), `--dry-run` | Stage the Kyber-owned status-line artifacts and print the activation snippet. |
| [`utilities statusline status`](kyber-utilities/onboarding.md#command-reference) | — | Report `ok`, `missing`, or `drift` for each owned file. |
| [`utilities statusline doctor`](kyber-utilities/onboarding.md#command-reference) | — | Check owned files, the executable bit, and runtime prerequisites. |
| [`utilities statusline remove`](kyber-utilities/onboarding.md#command-reference) | — | Delete receipt-owned, unmodified status-line files. |

### `review` — the deterministic review half

Owned by the [Review council overview](code-review/README.md#three-commands), with the
verdict engine under [Review council architecture](code-review/architecture.md#how-the-verdict-is-computed).

| Verb | Flags (beyond `[path]`) | What it does |
|---|---|---|
| [`review gates`](code-review/README.md#three-commands) | `-f\|--format`, `--no-info`, `-c\|--config`, `-o\|--out <PATH>`, `--stop-on-failure`, `--all`, `--agent <NAME>`, `--paths <PATH>` (repeatable), `--base <REF>`, `--task <SLUG>`, `--fix` | Run the deterministic gates declared under `review.gates` (with `--all`, run all declared gates unscoped); with `--agent`, run that developer agent's `review.task-gates` against its change instead. |
| `review catalogue` | `[PATH]`, `--technology <SLUG>` (repeatable) | Print the catalogue `review:` block for the declared (or named) technologies to stdout, without writing any file. Exit `0` on success (including `no gates shipped for <tech>`), `1` on a configuration failure, `2` for an unknown `--technology`. |
| [`review duplicates`](code-review/README.md#three-commands) | `-f\|--format`, `--no-info`, `-c\|--config`, `-o\|--out <PATH>` | Find duplicated symbol bodies from the CodeGraph index. |
| [`review verdict`](code-review/architecture.md#how-the-verdict-is-computed) | `-f\|--format`, `--no-info`, `-c\|--config`, `--findings <PATH>`, `--gates <PATH>` | Compute approve / request-changes / needs-human from findings and gates. |

`review gates --agent <NAME>` selects the [task gates](code-review/architecture.md#task-gates)
declared for that agent under `review.task-gates` and runs them against the files that agent
changed, instead of running the repository-wide `review.gates`:

- `--all` runs all declared repository gates for the current OS unconditionally without
  include path filtering. Combining `--all` with `--agent`, `--base`, or `--paths` is a
  client-input error (exit `2`, `KW-REVIEW-028`).
- `--paths <PATH>` names a changed file, relative to the repository root, and repeats for
  several. Without `--agent` the paths are the council gates' changed set; with `--agent`
  they are the agent's changed files. Without it the changed set is the diff against
  `--base` plus untracked files (`--agent`, default `HEAD`), or the diff from the merge
  base of `--base` to the working tree (council, default `origin/HEAD`). When git cannot
  list the changes, council gates with `include` run unscoped (`KW-REVIEW-034`).
- `--base <REF>` is the ref the working tree is diffed against when `--paths` is absent.
- `--task <SLUG>` is substituted for `{task}` in the gate commands. It must be a lookup name:
  lowercase alphanumerics separated by single hyphens. Requires `--agent`.
- `--fix` runs each gate's `fix` command before its verify command. The verify command still
  decides the result. Requires `--agent`.
- `--task` and `--fix` given without `--agent` are a client-input error (exit `2`).
  `--paths` and `--base` scope the council gates too and are accepted without `--agent`.
- Two tokens expand in the gate's argv: `{changed}`, which must be a whole argv element and
  becomes one argument per changed file the gate's `include` matches, and `{task}`, which may
  sit inside an argument. Neither reaches a shell, and the program (the first element) is always
  a literal, never a token.

### `update` — replace the Release binaries

Owned by [Updating](install.md#updating).

| Verb | Flags | What it does |
|---|---|---|
| [`update`](install.md#updating) | `[version]`, `--release-candidate`, `--no-mcp`, `--no-kyberdash`, `--no-menubar`, `--no-restart` | Replace the installed CLI and MCP binaries (and KyberDash and tray) from a GitHub Release, then restart the KyberDash engine and tray and upgrade an installed OpenCode capture plugin. `--no-restart` skips the engine and tray restart. |

## `kyberdash` reference

KyberDash's CLI is the shared engine behind its three surfaces. The KyberDash binary is a
**display-agnostic engine plus CLI**: the tray and the web dashboard hold no schedulers or
isolated feature logic, and every job they show is executed here or by `kyberdash web`. See
[KyberDash architecture](dash/architecture.md) for the boundary and the
[KyberDash runbook](dash/runbook.md) to run it.

Every command, at the root or below, also accepts the shared options `--verbose` and
`--timezone <zone>`.

### Report, dashboard, tray, and diagnostics

Owned by the [KyberDash overview](dash/README.md) and the
[KyberDash adoption guide](dash/onboarding.md).

| Verb | Flags | What it does |
|---|---|---|
| [`report`](dash/README.md) (default) | `--format text\|markdown\|json` (default `text`), `--harness <id>`, `--session <id>`, `--run <id>`, `--days <n>` (default `7`), `--limit <n>` (default `5`), `--db <path>` | Print a non-interactive context diagnosis of the store. |
| [`web`](dash/onboarding.md#first-run) | `--port <number>` (default: the persisted `dashboard.port`, `4747`), `--no-open`, `--view <path>` | Open the local web dashboard. |
| [`mcp`](dash/runbook.md#2j-conversational-analytics-over-mcp-kyberdash-mcp) | `--server-url <url>` (literal loopback, wins), `--state-dir <dir>` (existing receipt) | Serve the eight read-only analytics tools over MCP stdio; attaches to the running engine, never starts or stops it. |
| [`menubar`](dash/onboarding.md#install-the-tray) | `--force`, `--update`, `--version <version>` | Install and launch the macOS/Windows tray; `--update` updates an existing install. |
| [`engine ensure`](dash/runbook.md) | `--if-running` (required) | Replace a live engine of another version with this binary's version. Starts nothing when no engine runs. Prints one line: `engine: not-running`, `engine: attached <version>`, `engine: replaced <old> -> <new>`, or `engine: started <version>`. |
| [`doctor`](dash/runbook.md) | `--provider <provider>` (default `all`), `--json`, `--no-color` | Per-provider detection status: paths probed, sessions found, parse health. |

### Canonical telemetry commands (`kyber`)

Owned by the [KyberDash runbook](dash/runbook.md) and
[Telemetry inventory](dash/telemetry-inventory.md).

| Verb | Flags | What it does |
|---|---|---|
| [`kyber otel`](dash/runbook.md#1-starting-the-otlp-ingest-receiver) | `--port <number>` (default `4318`), `--host <host>` (default `127.0.0.1`), `--db <path>`, hidden `--serve` | Start the OTLP trace receiver. |
| [`kyber capture status`](dash/runbook.md) | `--harness <id>` (repeatable), `--endpoint <url>` (default `http://127.0.0.1:4318`), `--config-dir <dir>` (OpenCode plugin root: flag, else `$OPENCODE_CONFIG_DIR`, else `~/.config/opencode`) | Report per-harness capture state and receiver liveness. |
| [`kyber capture enable`](dash/runbook.md) | capture flags + `--dry-run` + `--config-dir <dir>` (OpenCode plugin root, same precedence as status) | Write harness OTLP exporter keys and record the receipt. For OpenCode, installs the plugin file, sets consent `content: true`, and restores any `experimental.openTelemetry` key that older builds wrote (no key is written). |
| [`kyber capture disable`](dash/runbook.md) | capture flags + `--dry-run` + `--config-dir <dir>` (OpenCode plugin root, same precedence as status) | Restore pre-enable values per key; refuse on drift. For OpenCode, sets consent `content: false`, removes the plugin file, and runs the same restore of any older `experimental.openTelemetry` key. |
| [`kyber capture plugin install`](dash/runbook.md) | `--harness opencode` (only value), `--preserve`, `--if-installed`, `--config-dir <dir>`, `--dry-run` | Install or upgrade the OpenCode capture plugin file. `--preserve` keeps an edited, foreign, or receipt-unproven older file (exit 0) and creates the consent file with `content: false` only when absent. `--if-installed` acts only when the file is present. `--config-dir` overrides `$OPENCODE_CONFIG_DIR` and `~/.config/opencode`. Never writes `opencode.jsonc`. |
| [`kyber backfill`](dash/runbook.md#3-raw-content-backfill-and-re-normalization) | `--db <path>` | Re-derive canonical content for stored records from their raw payloads. |
| [`kyber renormalize`](dash/runbook.md#3-raw-content-backfill-and-re-normalization) | `--db`, `--source <source>` (repeatable) | Re-derive harness attribution and token conversion. |
| [`kyber build`](dash/runbook.md#2-derived-projection-rebuilding-kyber-build) | `--db <path>` | Build or rebuild derived sessions from canonical records. |
| [`kyber cursor-hook`](dash/runbook.md) | — | POST Cursor hook JSONL from stdin to the local receiver. |
| [`kyber antigravity-statusline`](dash/runbook.md) | — | Record an `agy` status-line payload from stdin. |
| [`otel`](dash/runbook.md#1-starting-the-otlp-ingest-receiver) (top-level alias) | same as `kyber otel` | Backwards-compatible alias for `kyber otel`. |

### Canonical store commands (`dash`)

Owned by the [KyberDash runbook](dash/runbook.md), with configure in the
[KyberDash adoption guide](dash/onboarding.md#configure).

| Verb | Flags | What it does |
|---|---|---|
| [`dash refresh`](dash/runbook.md#2a-local-harness-source-refresh-dash-refresh) | `--db <path>`, `--history-weeks <n>` (default `2`), hidden `--trigger <source>` | Refresh KyberDash from local harness-source history. |
| [`dash clean`](dash/runbook.md#2b-database-clean-dash-clean) | `--db`, `--all`, `--harness <id...>`, `--reingest-weeks <n>` (max `52`), `--no-reingest`, `--yes` | Wipe a harness scope (or all); re-import only when asked. |
| [`dash import-history`](dash/runbook.md#2c-import-folder-history-dash-import-history) (alias `import`) | `--db`, `--weeks <n>` (default `1`), `--harness <id...>` | Import history from local harness folders once. |
| [`dash settings show`](dash/runbook.md#2d-shared-settings-and-pause) (default) | `--db <path>`, `--json` | Print every shared setting and its value. |
| [`dash settings set`](dash/runbook.md#2d-shared-settings-and-pause) | `<key> <value>`, `--db <path>` | Set one shared setting. |

## Exit codes

The CLI's exit codes are deliberately not uniform, because the branches carry different
risk. The three rules below mirror the contract in
[`src/KyberWeave.Cli/AGENTS.md`](../src/KyberWeave.Cli/AGENTS.md).

### `kyber-weave`

| Command kind | Non-zero on |
|---|---|
| `validate`, `lint`, `sync-check`, `drift` | any error |
| `scan` | any critical, raised by `--fail-on warning\|error` |
| `route` | accuracy below `--min-accuracy` |
| `docs integrity-check` | a finding at or above `--fail-on` (default `none` gates nothing) |
| `review verdict` | `0` approve, `1` request-changes, `2` needs-human |
| `review gates`, `review duplicates` | `1` on report errors, else `0` |
| `review gates` | `--base` and `--paths` are accepted without `--agent`; an unsafe value refuses the run (`KW-REVIEW-028`, exit `1`) before any gate starts. `--all` runs all declared gates unscoped; combining `--all` with `--agent`, `--base`, or `--paths` is a client-input error (`KW-REVIEW-028`, exit `2`). |
| `review gates --agent` | `1` on a blocking gate failure or a refused changed set, task slug or token (`KW-REVIEW-028`); `2` for `--task` or `--fix` without `--agent`; else `0` |
| `review catalogue` | `0` on success, `1` on a configuration failure, `2` for an unknown `--technology` |
| `squad install` | `2` for a non-interactive confirmation failure, `1` for other failures |
| `squad preset use`, `squad preset clear` | `2` for a client-input error (unknown preset name, missing `--target` with no receipt roster, undeployed global target); `1` when canonical profiles cannot be resolved (checkout, version snapshot, then newest snapshot all missed) or preset validation fails (unknown category/harness key, reported with file path and line) — before any file or state change in dry-run and apply |
| `squad preset show` | `2` for a client-input error (unknown preset name, missing or invalid `--target`); `1` when canonical profiles cannot be resolved or preset validation fails |
| any command with a config failure | `KW-CONFIG-001`, never an unhandled exception |

### `kyberdash`

| Command | Non-zero on |
|---|---|
| `dash refresh`, `dash clean`, `dash import-history` | `3` when another refresh, clean, or import holds the lock (`REFRESH_BUSY_EXIT_CODE`); the command's own report code otherwise |
| `dash clean` | `2` without `--yes`, or with an invalid or missing scope; `1` on failure |
| `dash settings set` | `2` for an unknown setting key or an invalid value |
| `web` | `2` for a non-port `--port` or an unknown `--view`; `1` when the port is refused; `0` when attaching to the already-running engine |
| `mcp` | `0` on clean EOF/SIGTERM shutdown (the engine keeps running); tool-domain errors are in-band (`isError: true`) with stable codes, not exit codes |
| `kyber otel` | `2` when a custom `--port` or `--host` is supplied while the engine attaches |
| `capture` leaves | `2` for a usage error; otherwise the capture action's own code |
| `engine ensure` | `0` for `not-running`, `attached`, `replaced`, or `started`; `1` for `engine: unresponsive` (the endpoint holder never answers, nothing started) or `engine: failed`; `2` without `--if-running` |
| `kyber capture plugin install` | `0` on success, including every `--preserve` keep and every `--if-installed` skip; `1` on a strict refusal (foreign, edited, or unreadable file) or a failed write; `2` for a missing or unsupported `--harness` |

## `install.sh` flags

Owned by [Installing Kyber-Weave](install.md) and
[Distribution and release flow](distribution.md) (maintainer-facing).

| Flag | Environment variable | Notes |
|---|---|---|
| `--version <v>` | `KYBER_WEAVE_VERSION` | Install a specific release. |
| `--prerelease` | `KYBER_WEAVE_PRERELEASE` | Include pre-release versions. |
| `--install-dir <d>` | `KYBER_WEAVE_INSTALL_DIR` | Default `~/.local/bin`. |
| `--no-mcp` | `KYBER_WEAVE_NO_MCP` | CLI only; skip the MCP server. |
| `--no-kyberdash` | `KYBER_WEAVE_NO_KYBERDASH` | CLI and MCP; skip the KyberDash binary. |
| `--with-menubar` | `KYBER_WEAVE_WITH_MENUBAR` | macOS only; install the signed menubar app. |
| `-h`, `--help` | — | Show help. |

`KYBER_WEAVE_RELEASE_ORIGIN` has no flag and is environment-only.

## Related

- [Installing Kyber-Weave](install.md) — the supported install path and its flags
- [Configuration](configuration.md) — the Kyber-Weave config surface
- [KyberDash adoption guide](dash/onboarding.md) — KyberDash install, update, and configure
- [`src/KyberWeave.Cli/AGENTS.md`](../src/KyberWeave.Cli/AGENTS.md) — the by-feature rule this page indexes
