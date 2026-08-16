---
id: todo/squad-path-argument-safety
title: squad commands' positional path argument can silently target the wrong directory
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# squad commands' positional path argument can silently target the wrong directory

This is **context for planning the work, not a plan** — what was observed, what needs
verifying, and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

While verifying the Copilot renderer end-to-end, `squad install --target copilot --dry-run
--path <scratch-dir>` was run expecting `--path` to redirect the target root to a scratch
directory. It did not: the deployment root silently defaulted to the current working
directory instead — which, at the time, was this repository's own root — and a **real,
non-dry-run** follow-up call actually wrote 43 Copilot agent/skill files plus
`.kyber-weave/squad.lock.yml` and `.kyber-weave/squad.receipt.json` into this repository.
Caught via `git status` before anything was committed and cleaned up, but the near-miss is
the finding: a plausible, GNU-style flag guess silently installed into the wrong place with
no error and no confirmation prompt.

## What is known

- `Path` on every squad command's settings class (`SquadInstallSettings`,
  `SquadUpdateSettings`, `SquadUninstallSettings`, `SquadStatusSettings`,
  `SquadDoctorSettings` — all in `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`) is a
  **positional** argument: `[CommandArgument(0, "[path]")] public string Path { get; set; }
  = "."`. There is no `--path` option registered on any of them.
- Correct usage is positional: `squad install <path> --target copilot`, not `squad install
  --target copilot --path <path>`.
- What was not verified: whether Spectre.Console.Cli (the CLI framework in use) rejects an
  unrecognized `--path <value>` pair outright, silently drops it while still consuming the
  value as if it were something else, or something in between. The observed behavior was a
  clean success message with the default `.` path in effect — no parse error was surfaced
  anywhere in the captured output.

## What needs deciding

- Whether the fix belongs in Kyber-Squad's settings classes (e.g. adding `--path` as an
  explicit alias option pointing at the same property) or is really a Spectre.Console.Cli
  configuration question (e.g. whether unrecognized options should be a hard parse error
  across this CLI generally, which would be a change with a much larger blast radius than
  Squad alone).
- Whether a mutating command (`install`, `update`, `uninstall` without `--dry-run`) should
  echo the resolved target root before writing, specifically to catch exactly this class of
  mistake — the operator would have seen "Installing to /Users/dave/git/personal/kyber-weave"
  and caught the error before anything was written, not after.

## The code seam

- `SquadCommandComposition.ResolveTargetRoot` (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`)
  is the single place every squad command resolves `settings.Path` into an absolute target
  root — the natural place to add a confirmation echo if that is the chosen fix.
- If Spectre.Console.Cli turns out to silently accept unrecognized long-form options, check
  whether it exposes a stricter parsing mode (Spectre.Console.Cli's `CommandApp` configuration
  is set up in the CLI's `Program`-equivalent entry point) before reaching for a
  Squad-specific workaround.

## How to verify

- Confirm what Spectre.Console.Cli actually does with `squad install --path <dir>
  --target copilot`: parse error, silent drop, or something else — this decides whether the
  fix is in Squad's settings or the CLI's global parser configuration.
- If a confirmation echo is the chosen fix: a mutating squad command run against an
  unexpected root should be visibly wrong *before* any write, not discoverable only via
  `git status` afterward.
