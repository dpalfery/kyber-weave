# First look: read-only detection

Run all of this before the first question. None of it writes a file. Keep the raw output to
yourself; show the user only the summary at the end.

## What to run

| Fact | Command (read-only) |
|---|---|
| CLI and version | `kyber-weave --version` |
| Inside a git repository, not the home directory | `git rev-parse --show-toplevel` |
| Repository configuration | does `.kyber-weave/kyber-weave.yml` exist |
| Machine check | `kyber-weave doctor --check .` (never indexes or installs) |
| Code index | does `.codegraph/` exist; the doctor `codegraph.index` line says whether it is healthy |
| Squad installed for every repository, and its version | `~/.kyber-weave/squad/global/squad.lock.yml`: `targets:` and `squad-version:` |
| Squad installed in this repository only | `kyber-weave squad status . 2>&1 \| grep -oE '\([a-z]+\)$' \| sort \| uniq -c` (each deployed file names its harness in parentheses) |
| Decision gate and its hooks | `kyber-weave arbiter status`; a per-user provider override may sit in `~/.config/kyber-weave/arbiter.yml` |
| Which AI coding tools are on the machine | the folders and commands below |
| Model presets | `kyber-weave squad preset list` |
| Dashboard settings, when `kyberdash` is installed | `kyberdash dash settings show` |
| Leftovers from older releases | below |

**AI coding tools on the machine.** A tool's home folder means it has been run here; a command on
`PATH` means it is installed. These are the folders the squad installs into (an environment
override, where one exists, wins):

| Tool (install name) | Home folder | Command |
|---|---|---|
| Claude Code (`claude`) | `$CLAUDE_CONFIG_DIR` or `~/.claude` | `claude` |
| GitHub Copilot, CLI and VS Code (`copilot`) | `$COPILOT_HOME` or `~/.copilot` | `copilot`; VS Code: `code` |
| Codex (`codex`) | `$CODEX_HOME` or `~/.codex` | `codex` |
| Cursor (`cursor`) | `$CURSOR_CONFIG_DIR` or `~/.cursor` | — |
| OpenCode (`opencode`) | `~/.config/opencode` | `opencode` |
| Kilo (`kilo`) | `~/.config/kilo` | — |
| Pi (`pi`) | `$PI_CODING_AGENT_DIR` or `~/.pi/agent` | — |
| Factory (`factory`) | `~/.factory` | — |
| Antigravity (`antigravity`) | `~/.gemini/config` | — |
| Warp (`warp`) | `~/.warp` | — |
| ZCode (`zcode`) | `$ZCODE_STORAGE_DIR` or `~/.zcode` | — |
| Devin (`devin`) | `~/.config/devin` (Windows: `%APPDATA%\devin`) | — |
| Command Code (`commandcode`) | `~/.commandcode` | — |

A dash means no command name is confirmed. Never guess a command name.

**Which tools count as in use** (the recommended set for steps 2 and 3): every tool that already
has the squad, plus every tool whose command is on `PATH`. A tool with only a home folder may be an
old trial: list it under "other tools found", not in the recommendation. A command missing from
an agent's shell `PATH` while its folder holds recent files is still likely in use; say so.

**Leftovers.** A skill folder under a harness home's `skills/` that `kyber-weave squad status
--global` does not list, and whose name an older squad shipped, is left behind by an update. Known
case: `skills/test-dev/` (the test-writing skill became `testing`). The `test-dev` *agent* is still
current; only the skill folder is a leftover. Note it for the step-8 cleanup, asked with this
context: the harness still loads the old skill, which can give agents outdated testing steps;
options are move it out of the skills folder or leave it; recommend moving it. Per platform:

- macOS: `mv <folder> ~/.Trash/`; restore with Finder's Put Back, or move it back.
- Linux: `gio trash <folder>` when `command -v gio` finds it; restore from the file manager's
  Trash. Otherwise (a bare `mv` into the trash loses its restore data) move it to
  `~/.kyber-weave/removed-skills/<date>/` and tell the user that path.
- Windows: no confirmed recycle-bin command, so in PowerShell
  `Move-Item <folder> "$HOME\.kyber-weave\removed-skills\<date>\"` and tell the user that path.

Where it went to a backup folder, undo by moving it back from there. In every case check the
folder is gone from the skills directory afterwards, and give the restore step.

## The summary to show

Plain words, about eight lines, no rule ids. For example:

```text
Here is what I found (nothing has been changed):
- Kyber-Weave 1.4.2 is installed. This repository has its configuration file.
- Machine check: 9 of 11 checks pass; 2 need a fix (step 1).
- Code index: not built yet for this repository.
- AI coding tools on this machine: Claude Code and GitHub Copilot (CLI and VS Code).
- Kyber-Weave's agents are installed for GitHub Copilot (version 1.4.0) in every repository;
  not yet for Claude Code.
- Decision gate: on for GitHub Copilot; decision model: not set up.
- One leftover folder from an older release (~/.claude/skills/test-dev).
```

Point out mismatches: a tool with the squad but no hooks, hooks for a tool with no squad, or an
installed squad older than the CLI. When `kyber-weave arbiter status` contradicts itself or the
configuration file, trust its per-scope lines and its provider line (they are what it resolved),
and name the contradiction in the summary. A hook for a tool with no squad is reported, not asked
about; the user can ask to remove it.

## Questions this step can raise

**Old CLI version.** The CLI has no read-only "is there a newer version" check, so ask only when
the installed squad's `squad-version` is newer than `kyber-weave --version`, or a step fails for a
missing verb or flag.
- *What*: Kyber-Weave is the command-line tool that installs and checks everything in this setup.
- *Why*: an older version may lack checks and fixes this walkthrough relies on.
- *Options*: update now with `kyber-weave update` (replaces the tool for every repository on
  this machine, about a minute), or continue on the current version.
- *Cost and undo*: writes only the tool's own install folder; reinstall a pinned version with
  the `kyber-weave-cli` skill.
- *Recommend*: update, unless the team pins a version.

**No repository configuration.**
- *What*: `.kyber-weave/kyber-weave.yml` is this repository's Kyber-Weave settings file; the
  agents and checks read it.
- *Why*: without it, the docs tool refuses to answer and runs cannot start.
- *Options*: create it with `kyber-weave docs init .` (writes the settings file, a generated
  paths block in the root `AGENTS.md`, a docs catalog and its reference page, and the docs
  authoring skill; add `--no-skill` when the squad is already installed, since it ships the same
  skills; `--dry-run` is not offered, so show `git status` after), or stop here.
- *Cost and undo*: repository files only, visible in `git status`; undo with `git checkout`/`git
  clean` before committing.
- *Recommend*: create it. Do not answer the documentation-onboarding question here.
