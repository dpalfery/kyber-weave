---
id: cli-command-surface
title: CLI command surface
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-11-07
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
`kyber-weave-mcp`, an installed `kyber-weave-arbiter`, the installed `kyberdash`, and an installed KyberDash tray — after
SHA-256 verification. It does not wrap `install.sh`, and it refuses `dotnet run` and `dotnet tool` installs.
The updating process swaps the binaries and then does nothing else: it starts the installed
kyber-weave as `kyber-weave update <version> --finish-update` and relays that process's exit code.
The finishing process runs everything after the swap, so the steps below run in the new release.
Its wait is 15 minutes, and the process tree is killed on expiry. See
[distribution](distribution.md#the-self-update-sequence) for the full sequence.

`--finish-update` is a hidden option. Only the updater passes it, with the owner's `--no-kyberdash`,
`--no-menubar`, `--no-arbiter` and `--no-restart` forwarded. A person does not run it. It refuses, before
any work, in three cases:

- no version named: `usage: kyber-weave update <version> --finish-update. Name the version the update installed.`
- a version other than the one running: `--finish-update finishes the update to the version that runs it. This kyber-weave is <current>, but <version> was asked for.`
- combined with `--release-candidate`: `--finish-update cannot be combined with --release-candidate. It finishes one named version.`

It downloads nothing and commits nothing. On success it prints no outcome line; the updating process
prints the single line `updated kyber-weave <version> -> <path>`.

The finishing process runs the KyberDash steps as a changed release. It runs
`kyberdash engine ensure --if-running`, relaunches the tray when needed, then runs `kyberdash kyber
capture plugin install --harness opencode --preserve --if-installed` and `kyberdash kyber capture
plugin install --harness factory --preserve --if-installed`. These steps also run when the release is
unchanged ("already on"). It then re-renders the arbiter hooks/scripts for global targets registered
under `arbiter.global.targets` and each registered project with `arbiter.enabled`, using the Squad
update path and this release's hook source. `--no-arbiter` or an absent arbiter skips that.
A failed refresh is a warning with exit 0: `warning: arbiter hooks were not refreshed: <cause>. The
binaries are already updated; run kyber-weave update <version> to retry.`

A failure after the install exits 1 with `kyber-weave <version> is installed, but the last steps did not
finish: <cause>. Run kyber-weave update <version> to finish them.` When the finishing process exits
non-zero, the message reads `kyber-weave <version> is installed, but the post-update steps did not finish:
the new kyber-weave exited <code>, and its output above says why. The binaries are already updated. Run
kyber-weave update <version> to finish them.` A pinned older release that does not know `--finish-update`
fails the hand-off, and the retry it names is `kyber-weave update <version>`.

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

Then answer the onboarding question once, so `run start` stops asking it. The skill walks each
declared technology's default gates and `## Completion` text and records what you accept or change;
the verb below records only the answer.

```bash
kyber-weave docs onboarding done      # the skill was applied
kyber-weave docs onboarding declined  # this repository chose not to onboard
```

The setting, its two values and what the stop is for are in
[Configuration § Onboarding](configuration.md#onboarding).

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

**Start a run on its branch** ([`run start`](#run--run-start),
[Run controls](configuration.md#run-controls)):

```bash
kyber-weave run start --harness claude --type fix --slug arbiter-global-block-identity
kyber-weave run start --harness claude --type fix --slug arbiter-global-block-identity --json
kyber-weave run start --harness claude --type feat --slug new-gate --resume <branch>
```

The sections that document these sequences in full are
[Canonical flows](#canonical-flows), [`kyber-weave` reference](#kyber-weave-reference),
[`kyberdash` reference](#kyberdash-reference), [Exit codes](#exit-codes), and
[`install.sh` flags](#installsh-flags).

## `kyber-weave` reference

The CLI is a Spectre.Console.Cli application with eight branches — `skill`, `agent`, `docs`,
`squad`, `run`, `arbiter`, `utilities`, and `review` — plus the top-level `update` and `doctor`
verbs. Unknown options are a
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
| [`docs init`](docgraph/onboarding.md#1-initialize) | `--owner <OWNER>` (default `unassigned`), `-t\|--target <TARGETS>` (default `agent-skills`), `--no-skill`, `--force`, `--kyber-standards`, `--no-review-gates`, `--no-mcp-check`, `--provision` | Scaffold host config, the catalog, and the ontology reference; append the catalogue `review:` gates for the declared technologies when none exist (`--no-review-gates` skips this); deploy the authoring skill via APM; check the declared MCP servers against each harness's repository-scope configuration and offer the repair (`--no-mcp-check` skips the check, the prompt, and the repair). `--provision` runs [`doctor --provision`](#doctor--repository-documentation-tooling) against the git root of the scaffolded path after scaffolding succeeds. `--target` names APM harnesses, not a path, so provisioning always goes to the scaffolded repository. Off by default: scaffolding alone starts no process. |
| [`docs mcp-wire`](docgraph/mcp-runbook.md#docs-mcp-wire) | `-t\|--target <TARGETS>` (comma list of harness slugs: `factory`, `claude-code`, `cursor`, `vscode`, `opencode`), `--dry-run` | Write the declared `mcp.required-servers` entries into the named harnesses' repository-scope configuration. A declared command that names a path is resolved against the repository under inspection, not the working directory. No `--target` previews and writes nothing; `--dry-run` prints the per-file change — file, harness, and the servers it would add — and writes nothing including the receipt. A named target already holding every declared server is reported as `nothing to write` at exit 0. A configuration that is a symbolic link, one this process may not inspect, and a run whose receipt cannot be persisted are refused rather than followed or half-written. |
| [`docs mcp-unwire`](docgraph/mcp-runbook.md#docs-mcp-unwire) | `-t\|--target <TARGETS>` | Restore the files a receipt records as `inserted` and delete the ones it records as `created`. No `--target` lists the recorded files and removes nothing. Refuses when the file changed since the write, when it is a symbolic link, and when it may not be read. A record for a write that never landed is recognised from the bytes on disk and dropped instead of being refused or restored. |
| [`docs mcp-migrate`](docgraph/mcp-runbook.md#docs-mcp-migrate) | `-t\|--target <TARGETS>` (harness slugs, plus `declaration` for a host with no harness files), `--dry-run` | Rename a proven old MCP server key to its new name — `kyber-weave` to `kw-docs`, `kyberdash` to `kw-dash`, `kyber-weave-arbiter` to `kw-arbiter` — in the named harnesses' repository-scope configuration, and migrate the host declaration's `mcp:` section with them. No `--target` previews every old key found with its proof result and the writable targets that would take it, and writes nothing. A key is renamed only where the entry also proves the launch: `argv[0]`'s file name equals the shipped binary, with the next argument `serve` for the arbiter and `mcp` for the dash. An unproven entry is reported with the manual edit and never touched; Codex, ZCode, and the Copilot repository settings are reported with the snippet and never written. `--dry-run` prints the same report and writes nothing, not even the receipt. |
| [`docs onboarding`](configuration.md#onboarding) | `<done\|declined>` (required), `[path]` | Record that this repository has answered the onboarding question, in `.kyber-weave/kyber-weave.yml`, and re-splice the Config Reg block in `AGENTS.md` that publishes it. The value is written in place, so the operator's comments and every other key survive; the block is re-spliced because an answer no agent instruction file carries is one no run can honour. `done` means the onboarding skill was applied; `declined` means the repository chose not to onboard, and it ends the question exactly as `done` does. Exit `0` recorded (an existing identical answer is a no-op at `0`), `2` an answer that is not one of the two — nothing is written, the refusal names both — `1` an operational failure: no configuration to edit, or a file that could not be read or written. |
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
| [`squad install`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path <PATH>`, `-t\|--target <TARGETS>`, `-x\|--exclude <TARGETS>`, `-g\|--global`, `--dry-run`, `--adopt`, `--yes`, `-v\|--version <VERSION>`, `--no-capture-plugin` | Deploy canonical agents and skills to harness directories. With `--global` and a capture-plugin harness resolved (OpenCode or Factory), also installs that harness's KyberDash capture plugin (preserve mode); `--no-capture-plugin` skips that step. |
| [`squad update`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global`, `-t\|--target`, `-x\|--exclude`, `--dry-run`, `--replace-managed`, `--yes`, `-v\|--version`, `--projects <PROJECTS>`, `--no-capture-plugin` | Update an existing deployment while preserving managed local edits. Same capture-plugin step as `squad install`. |
| [`squad uninstall`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global`, `--dry-run`, `--yes` | Remove files recorded in the ownership receipt. |
| [`squad status`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global` | Verify deployed files and report drift against the receipt. |
| [`squad doctor`](kyber-squad/onboarding.md#command-reference) | `[path]`, `--path`, `-g\|--global` | Diagnose prerequisites, toolchain availability, and deployment health. |
| [`squad pack`](kyber-squad/onboarding.md#packaging-squad-pack) | `-f\|--format <FORMAT>` (`apm` \| `plugins` \| `all`; default `all`), `-o\|--out <DIR>`, `-v\|--version` | Package agents and skills into distribution archives. |
| [`squad preset use`](kyber-squad/model-profiles.md#apply-switch-and-revert) | `<name>`, `-t\|--target <TARGETS>`, `-g\|--global`, `--dry-run` | Apply a model preset to target harnesses, recording the selection. |
| [`squad preset clear`](kyber-squad/model-profiles.md#apply-switch-and-revert) | `-t\|--target <TARGETS>`, `-g\|--global` | Revert target harnesses to bundled model profiles. |
| [`squad preset list`](kyber-squad/model-profiles.md#apply-switch-and-revert) | — | List available presets and their active deployments. |
| [`squad preset show`](kyber-squad/model-profiles.md#apply-switch-and-revert) | `<name>`, `--target <TARGET>` | Inspect resolved models for a preset and target harness. |

### `run` — run start

Owned by [Run start: fetch and branch first](kyber-squad/architecture.md#run-start-fetch-and-branch-first),
with the settings it reads in [Configuration § Onboarding](configuration.md#onboarding) and
[§ Run controls](configuration.md#run-controls).

| Verb | Flags | What it does |
|---|---|---|
| [`run start`](kyber-squad/architecture.md#run-start-fetch-and-branch-first) | `--harness <TOKEN>` (required), `--type <TYPE>` (required), `--slug <SLUG>` (required), `--resume <BRANCH>`, `--failing-version <VERSION>`, `--json` | The step before anyone investigates or plans: fetch `origin`, resolve the base, compare the failing build's version against `HEAD` and the base, and put the work on the branch it belongs on — in the checkout or in a worktree of its own. |

```bash
kyber-weave run start --harness <token> --type <type> --slug <slug> \
  [--resume <branch-or-pr>] [--failing-version <version>] [--json]
```

**What it does, in order.**

1. Names the branch from the three arguments, refusing input it cannot act on before it touches git
   (exit `2`).
2. Requires the current directory to be inside a git repository.
3. Loads the repository's configuration; a configuration failure is exit `1`, with the diagnostics
   printed the way every other command prints them.
4. **Stops if the onboarding question is unanswered** (exit `1`). A repository whose configuration
   sets no `onboarding:` value has never been asked which technologies, gates and standards it
   wants, so the run names the `kyber-docs-onboarding-and-assessment` skill and
   [`docs onboarding`](configuration.md#onboarding), and creates no branch. This is the first check
   after the configuration, ahead of the tool checks and the fetch, because the gates the run will
   be held to are exactly what onboarding chooses; an autonomous run stops here too, asking nothing
   and writing nothing. See [Configuration § Onboarding](configuration.md#onboarding).
5. Requires the `codegraph` binary to resolve on `PATH`; absent is exit `3`, whose remedy is an
   install rather than a repair.
6. Fetches with `git fetch origin --prune`.
7. Resolves the base: `run.base-branch` when set, `origin/HEAD` otherwise. **`main` is never
   assumed**, so a repository whose default branch is `develop` is compared against `develop`.
8. Refuses `--resume` naming neither a local branch nor `origin/<branch>` (exit `2`).
9. Reads the versions and the divergence *before* switching anything, so the report describes the
   checkout as the operator left it. `--failing-version` is what makes "the defect is not the
   commit you are looking at" visible.
10. Creates the branch with `--no-track`, in the checkout when it is clean and carries no other
    worktree, and in a worktree of its own otherwise.

**The branch and title shapes.** Both name the harness, because the diff does not:

| Part | Shape | Example |
|---|---|---|
| Branch | `[<owner>.<agent>.]<harness>/<type>/<slug>` | `claude/fix/arbiter-global-block-identity` |
| Title | `<type>(<scope>): <summary> [<harness>]` | `fix(arbiter): qualify the global block identity [claude]` |

`<type>` is a conventional-commit type (`build`, `chore`, `ci`, `docs`, `feat`, `fix`, `perf`,
`refactor`, `revert`, `style`, `test`). `<slug>` is lowercase hyphen-separated words. `<harness>`
is one of the thirteen catalogue tokens — `codex`, `cursor`, `claude`, `copilot`, `opencode`,
`kilo`, `antigravity`, `warp`, `factory`, `pi`, `zcode`, `devin`, `commandcode` — or `manual` for
work named after no harness. The branch and title shapes deliberately share one vocabulary with
`scripts/check-pr-name.sh`, which CI runs as the `pr-naming` job:
a branch the check rejects is a branch whose pull request cannot reach the default branch, so
creating one only moves the discovery to the end of the work.

**Where the branch lands.** A clean checkout with no other worktree attached is branched in place.
A dirty one, or one a second worktree already shares, gets a worktree at
`<repository parent>/<repo name>.worktrees/<branch with / replaced by ->` — for example
`…/kyber-weave.worktrees/claude-fix-arbiter-global-block-identity` — so the operator's edits stay
on the branch they were made for, and no branch moves under another worker. A resumed branch keeps
its own branch and is never renamed.

**`--json` keys.** One object on stdout, for a caller that dispatches the step and reads its result:

| Key | Meaning |
|---|---|
| `base` | The base branch as reported, e.g. `develop` (resolved through `origin/HEAD` when unset). |
| `baseRef` | The ref the branch was created from, e.g. `origin/develop`. |
| `branch` | The branch now in place — the resumed one when `--resume` was given. |
| `worktree` | The worktree path, or `null` when it branched in place. |
| `location` | `in-place` or `worktree`. |
| `revision` | The commit the base resolved to, or `null` when it did not resolve. |
| `resumed` | Whether this run continued an existing branch. |
| `versions.failing` | `--failing-version` as given, or `null`. |
| `versions.head` | The nearest tag at `HEAD`, else its abbreviated commit. |
| `versions.base` | The nearest tag at the base ref, else its abbreviated commit. |
| `serverAlignment.remote` | The remote name every run starts from: `origin`. |
| `serverAlignment.host` | The host `origin` names, or `null` for a remote that is a local path. |
| `serverAlignment.remoteUrl` | What `git remote get-url origin` returned, or `null`. |
| `serverAlignment.head` | The commit of the compared ref — `HEAD`, or the resumed branch. |
| `serverAlignment.ahead` | Commits on the compared ref that the base lacks, or `null` when git cannot compare. |
| `serverAlignment.behind` | Commits on the base the compared ref lacks, or `null` when git cannot compare. |
| `serverAlignment.aligned` | Whether both counts are zero. |

**Exit codes.**

| Exit | Meaning |
|---|---|
| `0` | Ready: the branch, or the resumed branch, is in place. |
| `1` | A tool failure while `stop-on-tool-failure` is on — the fetch, the base, or the branch creation. With the setting off, a failed fetch only warns and the run continues from the base this checkout last fetched. |
| `2` | Refused input: an unknown harness token, a type that is not conventional, a slug that is not lowercase hyphen-separated words, a `--resume` naming neither a local nor an `origin` branch, or a `--resume` naming a pull request number. |
| `3` | The `codegraph` binary is not on `PATH`. Its remedy is an install, so it is not reported as a failure (exit `1`). |

**Known limit.** `--resume` accepts a local branch or `origin/<branch>`. A numeric pull request
reference is **refused with exit `2`**, not resolved: no pull request is fetched or looked up.
Pass the branch the pull request already owns.

**The no-shell route.** A harness that gives the conductor no shell cannot run the command, so the
conductor dispatches `github-devops` with the exact command and a `RUN-START: true` marker. The
marker exists because `KW-ARB-PLAN-001` escalates a `delegate` dispatch carrying no `PLAN_FILE`,
which would refuse the very route the marker exists for: the Arbiter's `TriggerClassifier`
classifies `RUN-START: true` as `investigate`, never `delegate`, so the roster check and
`KW-ARB-READONLY-001` still apply to the dispatch. See
[the three-step decision gates](adr/0053-kyber-arbiter-three-step-decision-gates.md).

### `arbiter` — Kyber Arbiter decision gates

Owned by the [Kyber Arbiter runbook](kyber-arbiter/runbook.md), with the engine under
[Kyber Arbiter architecture](kyber-arbiter/architecture.md) and the decision record in
[ADR 0053](adr/0053-kyber-arbiter-three-step-decision-gates.md).

| Verb | Flags (beyond `[path]`) | What it does |
|---|---|---|
| [`arbiter install`](kyber-arbiter/runbook.md#setup-doctor-audit) | `--target <TARGET>` (required), `--provider <KIND>` (`none` \| `systemone`; empty resolves to `none`), `--arbiter-key <KEY>`, `--key-stdin`, `--dry-run`, `--yes`, `-g\|--global`, `--project`, `--path <PATH>` (project only) | One-command install: defaults to global scope (`~/.kyber-weave/kyber-weave.yml` and `arbiter.global.targets`). It sets `arbiter.enabled: true` non-destructively, renders the target through the existing Squad install pipeline, applies the provider, stores the key in the OS credential store, verifies, and prints the target's trust step. `--project` opts into repo scope, `--path` is valid only with it, and `--global --project` exits `2` with `KW-ARB-INSTALL-001`. `--arbiter-key` prints an argv/shell-history warning; `--key-stdin` and `TYPESAFE_API_KEY` stay the safer paths. Idempotent: a second run is a no-op (exit `0`). At global scope one `--target` per run, and the run merges that target into the global receipt the earlier runs wrote: `--target cursor`, then `--target codex`, then `--target factory` leaves three owned blocks in one receipt, in any order, each exit `0`, because a global block is claimed by target and path (several formats record the same bare `hooks.json`). A genuine duplicate — the same target claiming one path twice — still exits `1`. |
| [`arbiter uninstall`](kyber-arbiter/runbook.md#setup-doctor-audit) | `--target <TARGET>` (required), `-g\|--global`, `--project`, `--path <PATH>` (project only), `--remove-key` | Remove only Squad-owned arbiter files/blocks for the target (owned plugin/extension files, owned hook blocks). Defaults to user/global scope (removes only global-receipt-owned files/blocks and target from `arbiter.global.targets`); `--project` opts into repo scope (`--path` valid only with it). At global scope it removes that target's block from the receipt and leaves every other target's block and files in place, deleting the receipt only when it owns nothing any more. `--remove-key` deletes the stored key and warns when another scope still shares the key. |
| [`arbiter setup`](kyber-arbiter/runbook.md#setup-doctor-audit) | provider choice; `--key-stdin` | Choose provider (`none`, TypeSafe cloud, local Ollama); stores the key in the OS credential store. |
| [`arbiter status`](kyber-arbiter/runbook.md#setup-doctor-audit) | — | Show provider, model, endpoint origin, whether a key resolves — never the value. |
| [`arbiter doctor`](kyber-arbiter/runbook.md#setup-doctor-audit) | — | Warn on switched-off model rules, untuned models, missing keys, un-ignored log dirs, missing binaries. |
| [`arbiter validate`](kyber-arbiter/runbook.md#setup-doctor-audit) | `[path]` | Validate the `arbiter:` section, the user override, and the shipped rules. |
| [`arbiter rules`](kyber-arbiter/runbook.md#setup-doctor-audit) | `[--trigger <t>]` | List each rule's id, trigger, step, question, answers, effects, and facts. |
| [`arbiter plan`](kyber-arbiter/runbook.md#setup-doctor-audit) | `<file>` | Print what the parser understood, with `KW-ARB-PLAN-001` / `KW-ARB-PARSE-00x`. |
| [`arbiter eval`](kyber-arbiter/runbook.md#setup-doctor-audit) | `--trigger <t> --event <file> [--provider none]` | Evaluate a recorded event offline; writes nothing. |
| [`arbiter audit`](kyber-arbiter/runbook.md#setup-doctor-audit) | `[--plan <file>] [--session <id>] [--since <ISO-8601>]` | Report decision-less events, unmarked dispatches, unpaired pre/post events, and implementation packets naming planning paths (`KW-ARB-AUDIT-001`…`-004`). |

### `utilities` — Kyber-owned utility artifacts

Owned by the [Kyber Utilities adoption and activation guide](kyber-utilities/onboarding.md).

| Verb | Flags | What it does |
|---|---|---|
| [`utilities statusline deploy`](kyber-utilities/onboarding.md#command-reference) | `-t\|--target <TARGET>` (`claude` \| `agy` \| `pi`), `--dry-run` | Stage the Kyber-owned status-line artifacts and print the activation snippet. |
| [`utilities statusline status`](kyber-utilities/onboarding.md#command-reference) | — | Report `ok`, `missing`, or `drift` for each owned file. |
| [`utilities statusline doctor`](kyber-utilities/onboarding.md#command-reference) | — | Check owned files, the executable bit, and runtime prerequisites. |
| [`utilities statusline remove`](kyber-utilities/onboarding.md#command-reference) | — | Delete receipt-owned, unmodified status-line files. |

### `review` — the deterministic review half

Owned by the [Review council overview](code-review/README.md#the-review-commands), with the
verdict engine under [Review council architecture](code-review/architecture.md#how-the-verdict-is-computed).

| Verb | Flags (beyond `[path]`) | What it does |
|---|---|---|
| [`review gates`](code-review/README.md#the-review-commands) | `-f\|--format`, `--no-info`, `-c\|--config`, `-o\|--out <PATH>`, `--stop-on-failure`, `--all`, `--gate <ID>` (repeatable), `--agent <NAME>`, `--paths <PATH>` (repeatable), `--base <REF>`, `--task <SLUG>`, `--fix`, `--council` | Run the deterministic gates declared under `review.gates` (with `--all`, run all declared gates unscoped; with `--gate`, run only the gates named); with `--agent`, run that developer agent's `review.task-gates` against its change instead. |
| `review catalogue` | `[PATH]`, `--technology <SLUG>` (repeatable) | Print the catalogue `review:` block for the declared (or named) technologies to stdout, without writing any file. Exit `0` on success (including `no gates shipped for <tech>`), `1` on a configuration failure, `2` for an unknown `--technology`. |
| [`review duplicates`](code-review/README.md#the-review-commands) | `-f\|--format`, `--no-info`, `-c\|--config`, `-o\|--out <PATH>` | Find duplicated symbol bodies from the CodeGraph index. |
| [`review suppressions`](code-review/architecture.md#the-suppression-gate) | `-f\|--format`, `--no-info`, `-c\|--config`, `--base <REF>` | Fail a change that adds a warning suppression without a `reason:` on the same or the immediately preceding added line. History is never re-litigated; an unreadable change fails rather than passing. |
| [`review verdict`](code-review/architecture.md#how-the-verdict-is-computed) | `-f\|--format`, `--no-info`, `-c\|--config`, `--findings <PATH>`, `--gates <PATH>` | Compute approve / request-changes from findings and gates. |

`review gates --agent <NAME>` selects the [task gates](code-review/architecture.md#task-gates)
declared for that agent under `review.task-gates` and runs them against the files that agent
changed, instead of running the repository-wide `review.gates`:

- `--all` runs all declared repository gates for the current OS unconditionally without
  include path filtering. Combining `--all` with `--agent`, `--base`, `--paths` or `--gate`
  is a client-input error (exit `2`, `KW-REVIEW-028`). Under `--all` a full-suite gate
  refused for lack of `--council` ends the run at once: no gate starts and the command
  exits `1`.
- `--gate <ID>` runs only the gates named, by the id the host declares under `review.gates`.
  It repeats. The named list replaces the declared one before include matching, applicability,
  the full-suite admission rule and the report all read it, so a full-suite gate named without
  `--council` is refused exactly as it would be anywhere else. An id that names no declared
  gate is refused (`KW-REVIEW-028`, exit `1`) rather than reported as zero gates, which a
  reader could take as a pass. Exclusive with `--all` and with `--agent` (both exit `2`);
  combines with `--paths`, `--base` and `--council`.
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
- `--council` declares the run to be the review council or CI, the only callers that may run gates marked
  `full-suite: true` (`test`, `ts-test`, `cargo-test`). Without it, when stdin is not a terminal, each
  full-suite gate is not run: it is reported as a refused blocking failure (`KW-REVIEW-033`, severity Error, "a gate never started")
  that names `kyber-weave review gates . --agent <name> --paths <files>` and
  `scripts/fast-loop.sh --class 'KyberWeave.Tests.<Area>*'`. A refusal means "not run here", not a code
  finding. The other gates still run, unless the run is `--all` (see above), and `--agent` task-gate runs
  are unaffected. The command exits `1`.
  A pre-push self-review run never passes `--council`; CI runs the refused gates.
- Three tokens expand in the gate's argv: `{changed}`, which must be a whole argv element and
  becomes one argument per changed file the gate's `include` matches; `{task}`, which may sit
  inside an argument; and `{tests}`, which becomes the fully qualified test classes covering the
  change, resolved from `review.test-map` first and the `<Stem>Tests` naming convention second. A
  change no class covers skips the gate with `KW-REVIEW-026` rather than running a filter over
  nothing. None reaches a shell, and the program (the first element) is always a literal, never a
  token.

### `update` — replace the Release binaries

Owned by [Updating](install.md#updating).

| Verb | Flags | What it does |
|---|---|---|
| [`update`](install.md#updating) | `[version]`, `--release-candidate`, `--no-mcp`, `--no-arbiter`, `--no-kyberdash`, `--no-menubar`, `--no-restart` | Replace the installed CLI and MCP binaries (and an installed `kyber-weave-arbiter`, KyberDash and tray) from a GitHub Release, then hand the post-swap steps to the new binary: restart the KyberDash engine and tray, upgrade an installed OpenCode or Factory capture plugin, and re-render the arbiter hooks/scripts for global targets registered under `arbiter.global.targets` and registered projects with `arbiter.enabled`, using the new release's hook source (`--no-arbiter` or an absent arbiter skips the re-render). `--no-restart` skips the engine and tray restart. A hidden `--finish-update` option, passed only by the updater, runs those steps in the finishing process; it refuses a version other than its own, `--release-candidate`, and a missing version, and the failure messages and the pinned retry `kyber-weave update <version>` are in [Update](#update). |

### `doctor` — repository documentation tooling

A top-level verb scoped to one repository. It checks the CodeGraph index that `docs drift`
needs, and it proves that the Kyber-Weave docs MCP server answers for that checkout. It does
not diagnose a squad deployment; use [`squad doctor`](kyber-squad/onboarding.md#command-reference)
for that. The `kyberdash` binary has its own `doctor` verb, which reports provider detection
and is unrelated.

| Verb | Flags (beyond `[path]`) | What it does |
|---|---|---|
| [`doctor`](docgraph/mcp-runbook.md#the-live-check-doctor---check) | `[path]`, `--check`, `--provision`, `--ensure`, `--index-timeout <SECONDS>` (default `600`), `--mcp-timeout <SECONDS>` (default `20`), `--json` | Check CodeGraph and the docs MCP server for the git repository that holds `[path]`, and provision or repair the CodeGraph index when asked. `--check`, `--provision` and `--ensure` are mutually exclusive (exit `2`). |

Mode rule:

- `--check` is always read-only. It detects the `codegraph` binary and the index and runs the
  MCP proof. It never runs `codegraph init`, `codegraph sync` or `codegraph index`.
- `--provision` always provisions: `codegraph init .` when `.codegraph/` is absent,
  `codegraph sync .` otherwise, both at the git root.
- `--ensure` is the one command a worker runs first, in the tree it will work in. It runs
  `sync` (or `init`), restores any tracked file that preparation rewrote — never a file that was
  already dirty — proves the index with one `codegraph explore` call and the docs server with one
  call, and repairs **once** with `codegraph index .` and retries once when a step fails. It
  reads `stop-on-tool-failure` itself: under the default a failed check exits `1`; under
  `stop-on-tool-failure: false` the same checks are reported as warnings and the exit is `0`,
  except a missing binary, which stays `3`. Its steps and check ids are in the
  [MCP runbook](docgraph/mcp-runbook.md#the-workers-first-command-doctor---ensure).
- A bare run provisions only when stdin is a terminal and `CI` is unset. Otherwise it behaves
  as `--check` and prints `provisioning skipped: non-interactive; rerun with --provision`. The
  hint is suppressed with `--json`, so stdout stays valid JSON.

Exit codes for `doctor` (the `kyber-weave` binary):

| Exit | Meaning |
|---|---|
| `0` | Every check passed, or `--ensure` ran with `stop-on-tool-failure: false` and only reported failures. |
| `1` | A check failed: the index failed or timed out, the index lock is busy, `.codegraph/` is damaged or not writable, `--check` found no index, the `--ensure` explore proof still failed after its one repair, or the MCP server did not start, handshake, answer `docs_answer_question_from_repo_documentation` (check id `mcp.docs_explore`, which predates the rename), or answer for this repository. |
| `2` | A client-input error: two modes at once, a non-positive timeout, a path that is not usable or not inside a git repository, or a root that is the home directory. |
| `3` | A prerequisite is missing and installing it needs the user's approval: the `codegraph` executable is not on `PATH`. Nothing is installed, in any mode. |

The failure output contract, the check ids, and the provisioning rules are in the
[MCP runbook](docgraph/mcp-runbook.md#the-live-check-doctor---check).

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
| [`kyber capture status`](dash/runbook.md) | `--harness <id>` (repeatable), `--endpoint <url>` (default `http://127.0.0.1:4318`), `--config-dir <dir>` (OpenCode plugin root: flag, else `$OPENCODE_CONFIG_DIR`, else `~/.config/opencode`) | Report per-harness capture state and receiver liveness. The registered harnesses are `copilot`, `claude-code`, `codex`, `opencode`, `pi`, `antigravity`, `kilo` and `factory`. |
| [`kyber capture enable`](dash/runbook.md) | capture flags + `--dry-run` + `--config-dir <dir>` (OpenCode plugin root, same precedence as status) | Write harness OTLP exporter keys and record the receipt. For OpenCode, installs the plugin file, sets consent `content: true`, and restores any `experimental.openTelemetry` key that older builds wrote (no key is written). For Factory, publishes the Kyber-owned marketplace tree and asks the harness to install the plugin from it (`droid plugin marketplace add`, then `droid plugin install kyberdash-factory@kyberdash --scope user`), and turns the content consent on with it — the confirmed switch is the consent, and the confirm states what the switch does to the owner's prompt text. |
| [`kyber capture disable`](dash/runbook.md) | capture flags + `--dry-run` + `--config-dir <dir>` (OpenCode plugin root, same precedence as status) | Restore pre-enable values per key; refuse on drift. For OpenCode, sets consent `content: false`, removes the plugin file, and runs the same restore of any older `experimental.openTelemetry` key. For Factory, sets the content consent off, asks the harness to uninstall the plugin, and removes the tree KyberDash published, provided every recorded digest still matches. |
| [`kyber capture plugin install`](dash/runbook.md) | `--harness opencode\|factory`, `--preserve`, `--if-installed`, `--config-dir <dir>`, `--dry-run` | Install or upgrade a harness capture plugin. For OpenCode, the plugin file. For Factory, a Kyber-owned **marketplace tree** published under KyberDash's own state directory and installed by the harness itself (`droid plugin marketplace add`, then `droid plugin install kyberdash-factory@kyberdash --scope user`). `--preserve` keeps an edited, foreign, or receipt-unproven older tree (exit 0) and creates the consent file with `content: false` only when absent. `--if-installed` acts only when the plugin is already present: for OpenCode, the plugin file; for Factory, the **harness's own registry** reporting the plugin installed. The Factory verdict line is `factory plugin: <token> <tree>`, and its tokens are `installed`, `upgraded`, `unchanged`, `kept-conflict`, `kept-edited`, `kept-foreign`, `kept-unreadable`, `skipped-not-installed`, `would-install` and `would-upgrade`: `upgraded` means the published tree was rewritten with the current bundle and the harness's cached copy was refreshed with `droid plugin update`, and `unchanged` means the published tree is current and the harness reports the plugin installed, so nothing ran and nothing changed: the harness's cached copy is not inspected. The refresh rides along with the verdicts that refresh — the tree's content changed (the `upgraded` verdict), or the tree was written fresh while the harness already held the plugin (an `installed` verdict) — and never with `unchanged`. A refusal exits `1` and names its cause — a name the harness holds that KyberDash does not own (`conflict`), a tree the owner edited (`drift`), or a state that could not be read (`unreadable`). `--config-dir` is the **OpenCode** config root, and the Factory plugin-install verb **refuses** it with a usage error (exit `2`): the Factory tree lands under **KyberDash's own marketplace root** (`~/.kyberdash/marketplaces/kyberdash/`), never under a Factory root, and `$FACTORY_DIR` is honoured only by the Factory **file source**, for the sessions tree. A residue the harness's own uninstall leaves behind is **reported with the exact command that removes it** and is never patched by hand. Never writes `opencode.jsonc`, `~/.factory/settings.json` or `~/.factory/hooks.json`. |
| [`kyber capture plugin consent`](dash/runbook.md#1c-factory-droid-capture-enable-and-verify) | `--harness factory`, `--on`, `--off` | Turn the content consent of a harness capture plugin on or off. For Factory, this turns the prompt-text capture on or off without touching the capture itself — the Settings switch sets the consent as it confirms the capture, and this verb is the way to turn the prompt text back off while the capture stays on. `--on` and `--off` are mutually exclusive and exactly one is required. |
| [`kyber backfill`](dash/runbook.md#3-raw-content-backfill-and-re-normalization) | `--db <path>` | Re-derive canonical content for stored records from their raw payloads. |
| [`kyber renormalize`](dash/runbook.md#3-raw-content-backfill-and-re-normalization) | `--db`, `--source <source>` (repeatable) | Re-derive harness attribution and token conversion, and keep the content a record already collected while gaining the content the adapter derives from the same raw evidence for the buckets the collected content left empty. |
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
| `docs init` | `1` on a scaffold failure or a configuration failure (`KW-CONFIG-001`). MCP wiring findings — missing, differing, unlaunchable, undetectable, declined, or refused — never fail the run |
| `docs mcp-wire` | `0` when every named target was written, already held every declared server and was reported as `nothing to write`, or nothing was named; `1` when any named target was refused or the configuration could not load. A refusal includes a configuration that is a symbolic link, which is refused before it is read, a configuration this process may not inspect, and a receipt that could not be persisted — the record lands before the file, so a write it cannot record is a write it does not make. `--dry-run` exits what the real run would have exited |
| `docs mcp-unwire` | `0` when every named file was restored or deleted, or when no target was named; `1` on any refusal — no receipt record, a file that is gone, a digest that differs from the recorded one, a file that is a symbolic link, or a file this process may not read. A record whose file still holds the pre-write bytes is an interrupted write, not drift, and is reported at `0` |
| `docs mcp-migrate` | `0` for a preview, a completed migration, or nothing to do; `1` when any named target was refused — a target slug the catalog does not know, a configuration that is a symbolic link, one this process may not inspect, a malformed or foreign document, an old and a new entry whose launches differ, or a receipt that could not be persisted. A refusal writes zero bytes to that file and the other named targets still proceed. `--dry-run` exits what the real run would have exited |
| `review verdict` | `0` approve, `1` request-changes |
| `review gates`, `review duplicates` | `1` on report errors, else `0` |
| `review gates` | `--base` and `--paths` are accepted without `--agent`; an unsafe value refuses the run (`KW-REVIEW-028`, exit `1`) before any gate starts. `--all` runs all declared gates unscoped; combining `--all` with `--agent`, `--base`, `--paths` or `--gate` is a client-input error (`KW-REVIEW-028`, exit `2`). `--gate` naming no declared gate, or combining with `--agent`, is a client-input error (`KW-REVIEW-028`); an unknown id exits `1`, a bad combination exits `2`. A full-suite gate refused for lack of `--council` (`KW-REVIEW-033`, a gate that never started) is a blocking failure: exit `1`. Under `--all` that refusal ends the run with no gate started; in a scoped run the other gates still run. A gate whose `requires-paths` entry is absent is also `KW-REVIEW-033`, exitCode `-1`: exit `1` when blocking. |
| `review gates --agent` | `1` on a blocking gate failure or a refused changed set, task slug or token (`KW-REVIEW-028`); `2` for `--task` or `--fix` without `--agent`; else `0` |
| `review catalogue` | `0` on success, `1` on a configuration failure, `2` for an unknown `--technology` |
| [`review suppressions`](code-review/architecture.md#the-suppression-gate) | `0` when every suppression the change added states a reason, or the change adds none; `1` when one does not (`KW-REVIEW-036`) or the change against `--base` could not be read (`KW-REVIEW-037`), because an unreadable change is no evidence either way; `2` for a `--base` ref or a path refused before anything was read (`KW-REVIEW-028`) |
| `squad install` | `2` for a non-interactive confirmation failure, `1` for other failures |
| `arbiter install`, `arbiter uninstall` | `2` for a client-input error (missing or unknown `--target`, combining `--global` and `--project` or `--path` without `--project` — `KW-ARB-INSTALL-001`, a keyed provider with no key — `KW-ARB-KEY-001`, an invalid `--provider`, a declined confirmation), `1` for an operational failure (unreadable/unwritable config or state, failed Squad render, failed verification, a receipt that refuses a block claim such as one target claiming one path twice); `0` for success or an idempotent no-op. The codes are the same at both scopes, and at global scope a second block-bearing target is neither a client-input error nor a duplicate: its block is claimed by target beside path, so each target installs and uninstalls on its own (`0`) |
| `squad preset use`, `squad preset clear` | `2` for a client-input error (unknown preset name, missing `--target` with no receipt roster, undeployed global target); `1` when canonical profiles cannot be resolved (checkout, version snapshot, then newest snapshot all missed) or preset validation fails (unknown category/harness key, reported with file path and line) — before any file or state change in dry-run and apply |
| `squad preset show` | `2` for a client-input error (unknown preset name, missing or invalid `--target`); `1` when canonical profiles cannot be resolved or preset validation fails |
| `doctor` | `0` all checks pass; `1` a check failed; `2` a client-input error or a refused root; `3` the `codegraph` prerequisite is missing and installing it needs user approval. Exit `3` is checked before `1`. See [`doctor`](#doctor--repository-documentation-tooling). |
| [`run start`](#run--run-start) | `0` the branch is in place; `1` a configuration failure, an unanswered onboarding setting, or a tool failure while `stop-on-tool-failure` is on; `2` refused input (harness token, type, slug, or a `--resume` naming no local or `origin` branch); `3` the `codegraph` prerequisite is missing. Initial argument validation runs before the configuration is loaded, configuration before the onboarding check, and onboarding before the binary is probed, so initial bad input (`2`) outranks configuration and onboarding (`1`), which outrank missing codegraph (`3`); `--resume` validation runs after fetch on the branch resolution path. |
| `docs onboarding` | `0` the answer is recorded (an answer already holding that value is a no-op at `0`); `2` an answer that is neither `done` nor `declined`, with nothing written; `1` no configuration to edit, or a file that could not be read or written |
| any command with a config failure | `KW-CONFIG-001`, never an unhandled exception |

### Repository scripts

The two test-running scripts in `scripts/` share one full-suite refusal. Exit `89` means the
command is a full test-suite run and the caller is not the council or CI: `scripts/proc-guard.sh run`
(no `--council` and stdin not a terminal) and `scripts/fast-loop.sh --full`, `--scoped` or `--regenerate`
(stdin not a terminal). Both exit `89` before any build or process starts, and print the targeted
alternatives. `proc-guard.sh run` takes `--council` on its command line only; no environment variable lifts
the refusal. `scripts/proc-guard.sh classify -- <argv>` prints `full` or `partial` for a command and starts
nothing, so you can check a command before running it; shell strings such as `bash -c` are not parsed and count as `partial`. The launcher list, the per-tool full-run rules and the remaining limits are in the [test standard, limits of the guard](standards/test/README.md#limits-of-the-guard). `scripts/fast-loop.sh --class` and `--method` are unchanged.

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
| `kyber capture plugin install` | `0` on success, including every `--preserve` keep and every `--if-installed` skip; `1` on a strict refusal (foreign, edited, or unreadable file or tree) or a failed write; `2` for a missing or unsupported `--harness` |
| `kyber capture plugin consent` | `0` when the consent file was written; `1` when it could not be written; `2` for a usage error — a missing or unsupported `--harness`, or both or neither of `--on` and `--off` |

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
