---
id: kyber-hooks/README
title: Kyber edit hook
doc-type: reference
component: KyberHooks
status: current
owner: dpalfery
last-reviewed: 2026-11-15
---

# Kyber edit hook

**The edit hook lints, formats and optionally tests a file right after a coding agent edits it,
and hands any failure straight back to the agent.** It runs only the checks for that file's
technology and project, inside a time budget, and never blocks the agent because of its own
failure.

The first harness is Claude Code (a `PostToolUse` hook on `Edit|Write|MultiEdit`). The tool is
`kyber-weave-hooks`, a separate small binary so each edit starts fast.

## How it works

1. The hook reads the harness JSON on stdin and takes `tool_input.file_path` and `cwd`.
2. It finds the repository root (the nearest folder with `.git` or `.kyber-weave`) and makes the
   path repository-relative.
3. It merges `.kyber-weave/hooks.yml` over the built-in rules, picks the enabled rules whose
   `include` globs match and `exclude` globs do not, and runs lint rules before test rules.
4. Each command gets its own timeout, capped by what is left of the total time budget.

Install it with `dotnet tool install` for the `KyberWeave.Hooks` package, then add the hook to
`.claude/settings.json`. The command is guarded so it does nothing where the tool is missing and
still passes exit code 2 through:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "if command -v kyber-weave-hooks >/dev/null 2>&1; then kyber-weave-hooks run --harness claude; fi",
            "timeout": 60
          }
        ]
      }
    ]
  }
}
```

`kyber-weave-hooks --version` prints the version.

## Exit codes

| Code | Meaning |
|---|---|
| 0, no output | Every check passed, or nothing applied to the file. |
| 2, feedback on stderr | A blocking check failed. Claude Code shows the feedback to the agent. |
| 1, usage on stderr | The arguments were wrong. Only `run --harness claude` and `--version` exist. |

Feedback is short: one section per failing rule, each trimmed to `max-output-lines` lines and
`max-output-bytes` bytes, with a count of what was cut.

## Fail-open behavior

The hook exits 0 on any problem of its own: an unreadable or malformed `hooks.yml`, a command
that cannot start (for example `dotnet` is not installed), an internal error, or the hook running
past its own time. A command that times out is reported and skipped, not treated as a failure.
When the budget runs out the remaining checks are skipped with a note. Notes and config warnings
go to stderr; Claude Code shows stderr to the agent only on exit 2.

## Safety notes

Path checks compare normalized paths and do not resolve symbolic links, so a symlink under the project is checked by its own path; the effect is that a format check may run on a file the agent could already edit.

## Built-in rules

| Id | Kind | On by default | Runs |
|---|---|---|---|
| `dotnet-format-whitespace` | lint | yes | `dotnet format {target} whitespace --verify-no-changes --include {file} -v minimal` |
| `dotnet-format-style` | lint | yes | `dotnet format {target} style --verify-no-changes --severity warn --include {file} -v minimal` |
| `dotnet-test` | test | no | `dotnet test --project {testProject} -c Release --filter-class {testClasses}` |

The `--filter-class` form needs the Microsoft Testing Platform runner (set `test.runner` in `global.json`); the classic VSTest runner has no such option.

The format rules match `**/*.cs`, `**/*.csproj` and `**/*.props` and skip `bin` and `obj`.
`{target}` is the nearest project file above the file, or the nearest solution when there is none;
formatting one project is much faster than formatting the whole solution. `{marker}` is the nearest
`*.sln` or `*.slnx`, as in the repository's own task gates. A rule that needs a token with no value
for the file (no project or solution, no covering test) is left out.

The test rule is opt-in and never runs a whole suite. For `Foo.cs` it looks for a class `FooTests`
declared in a file `FooTests.cs`, confirms the class exists, and passes only those classes to the
test runner. A file with no covering test class gets no test run.

## Configuration

Everything is optional. `.kyber-weave/hooks.yml` is merged over the built-in rules, and
[`.kyber-weave/hooks.schema.json`](../../.kyber-weave/hooks.schema.json) gives editors validation.

| Key | Default | Meaning |
|---|---|---|
| `time-budget-seconds` | 30 | Total time for all commands for one edit. |
| `stop-on-first-blocking` | false | Stop after the first blocking failure instead of reporting all. |
| `max-output-lines` | 30 | Output lines kept per failing rule. |
| `max-output-bytes` | 3000 | Output bytes kept per failing rule. |
| `rules` | built-ins | Overrides and additions, below. |

A rule has these keys:

| Key | Meaning |
|---|---|
| `id` | Required. The same id as a built-in rule changes only the fields you set. |
| `kind` | `lint` or `test`. Lint runs first. |
| `include`, `exclude` | Globs over the repository-relative path. `**` crosses folders; `*` and `?` do not. |
| `run` | The command as an argument list, never a shell string. |
| `timeout-seconds` | Per-command limit; default 25. |
| `blocking` | Whether failure exits 2; default true. |
| `enabled` | Turn a rule off, or an opt-in rule on. |

Numbers outside their range are clamped with a warning: `time-budget-seconds` 1 to 120, `timeout-seconds` 1 to 300,
`max-output-lines` 1 to 500, `max-output-bytes` 100 to 100000.

Tokens in `run`: `{file}`, `{marker}`, `{project}` and `{target}` (the project, else the marker), all
repository-relative, `{root}` (absolute repository root), `{testProject}` (the test project holding the covering tests), and a standalone
`{testClasses}` argument, which expands to one argument per class. `{testClasses}` is not expanded inside a longer
argument such as `--filter={testClasses}`; a rule written that way never runs. Commands run from the repository
root. A new rule (an id that is not built in) must set `kind`, `include` and `run`.

Unknown keys, wrong value types and incomplete new rules produce a warning in the feedback and are
otherwise ignored.

```yaml
time-budget-seconds: 40
rules:
  - id: dotnet-test            # turn on the built-in opt-in test rule
    enabled: true
  - id: dotnet-format-style    # keep, but report without blocking
    blocking: false
```

## Add a technology

Add a rule that names the tool and the files it covers. The hook has no built-in knowledge of
other stacks. For example, KyberDash TypeScript through the repository's process guard, and the
Rust tray crate:

```yaml
rules:
  - id: dash-eslint
    kind: lint
    include: ["dash/**/*.ts", "dash/**/*.tsx"]
    exclude: ["**/node_modules/**"]
    run: [npx, --prefix, dash, eslint, "{file}"]
    timeout-seconds: 20
  - id: tray-rustfmt
    kind: lint
    include: ["dash/tray/src-tauri/**/*.rs"]
    run: [cargo, fmt, --manifest-path, dash/tray/src-tauri/Cargo.toml, --check]
```

Check each command's real arguments in that area's `AGENTS.md` first; these two are shapes, not
verified recipes.

## A future judge

A future hook step may ask Jev (TypeSafe's decision model, <https://typesafe.ai>; see
[the Jev decision model](../kyber-arbiter/jev-decision-model.md)) to judge ambiguous failures
through the same decision-provider seam the Arbiter uses. Nothing in the hook calls a model today.

## Where the code lives

The hook is the first feature on the layered architecture: `Hooks/` folders in
`src/KyberWeave.Domain` (rules, glob matching, routing, feedback), `src/KyberWeave.Contracts`
(process, file system, config and test-class interfaces), `src/KyberWeave.Data` (adapters and the
YAML reader) and `src/KyberWeave.Application` (the service), with the entry app in
`src/KyberWeave.Hooks`.
