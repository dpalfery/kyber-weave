---
id: todo/csharp-standards-remediation
title: Bring the C# codebase in line with the 2026-11-26 coding rules, through an opencode inventory and up to three parallel fixers
doc-type: todo
component: CI Pipelines
status: draft
owner: dpalfery
last-reviewed: 2026-11-26
---

# Bring the C# codebase in line with the 2026-11-26 coding rules

## Why this is a todo and not a plan

The owner approved the design and code rules (KW-STD-CS-001 to KW-STD-CS-021 in
[`csharp.rules.md`](../standards/csharp/csharp.rules.md)) and the SQLite rules (KW-STD-SQL-004 to
KW-STD-SQL-007 in [`sqlite.rules.md`](../standards/sqlite/sqlite.rules.md)) on 2026-11-26, and asked
for the analyzers to enforce them. Fixing the codebase takes many pull requests, and
[the plan inventory](../plans/README.md) allows one plan per pull request. So this todo is the
remediation design and the instructions for the opencode workers. Each wave below becomes one plan
under `docs/plans/` on the branch that delivers it, and that plan is archived in its own pull request.

## How the analyzers enforce the rules (already in place)

- `analyzers/rules.globalconfig` turns on the analyzers that `AnalysisMode=all` leaves off (CA1502,
  CA1506). It is always applied.
- `analyzers/ratchet.globalconfig` holds every analyzer that still has violations at `suggestion`,
  so the build stays green. The pull request that brings an analyzer to zero deletes its line, and
  from then on the analyzer fails the build. Never add a line back.
- `-p:KwAnalyzerAudit=true -p:TreatWarningsAsErrors=false` leaves the ratchet out and lists every
  remaining violation as a warning.
- The string comparison and culture analyzers (CA1304, CA1305, CA1307, CA1309, CA1310, CA1311) had
  no violations, so they fail the build now.
- CA1062 does not apply to test code (`.editorconfig`, `[{tests,dash/server.tests}/**.cs]`).

## Baseline (2026-11-26, branch `claude/csharp-practices-review-edb1a8` at `409e1a879` plus this change)

Unique `file(line,col) id` locations from the audit build of `KyberWeave.sln` and
`dash/server/KyberDash.sln`: **699**.

| Analyzer | Rule | `src` | `tests` | `dash/server` | `dash/server.tests` | Total | Kind |
|---|---|---|---|---|---|---|---|
| CA1062 | KW-STD-CS-004 argument guard | 237 | — | 15 | — | 252 | mechanical |
| CA1502 | KW-STD-CS-002 method complexity | 42 | 5 | 51 | 4 | 102 | judgement (split the method) |
| CA1506 | KW-STD-CS-002 class coupling | 39 | 45 | 14 | 20 | 118 | judgement (split the class) |
| CA1031 | KW-STD-CS-006 catch discipline | 68 | 13 | 10 | — | 91 | judgement (name the type, or log and rethrow) |
| CA1063 | KW-STD-CS-012 dispose pattern | — | 29 | 4 | 58 | 91 | mechanical |
| CA1001 | KW-STD-CS-012 owner is disposable | — | 40 | 1 | — | 41 | mechanical |
| CA2227 | KW-STD-CS-003 collection setter | 4 | — | — | — | 4 | mechanical |

Rules with no analyzer, as raw `grep -E` hit counts over `src` and `dash/server` (not yet sorted
into real violations and false positives; the inventory pass does that). A backslash before a
pipe in a pattern cell is the table's escape; the pattern the grep runs uses a plain pipe.

| Rule | Pattern | Hits |
|---|---|---|
| KW-STD-SQL-004 parameterized | `TextExpr\(` | 234 |
| KW-STD-CS-004 no `!` | `[A-Za-z0-9_\)\]]![.;,)\[]` | 327 |
| KW-STD-CS-013 no blocking on async | `\.Wait\(\)\|\.GetAwaiter\(\)\.GetResult\(\)\|\.Result\b[^<]` | 62 |
| clock seam | `DateTime(Offset)?\.(Now\|UtcNow)` | 27 |
| KW-STD-CS-007 no stub members | `NotImplementedException\|NotSupportedException\(\)` | 20 |
| KW-STD-CS-018 HTTP handlers | `new HttpClient\(` | 4 |
| KW-STD-SQL-007 in process | `"sqlite3"` | 3 |

KW-STD-CS-002 beyond the two metrics, KW-STD-CS-005, -008, -010, -011, -014, -016, -017, -019,
-020 and -021 are review rules: the inventory does not search for them, and reviewers apply them to
changed code.

The clock seam has no KW-STD id. It is the clock sentence under "Interface first, unit tested"
in [`csharp.rules.md`](../standards/csharp/csharp.rules.md): the clock is reached only through
its seam. A no-analyzer row takes its `rule` value from the Rule cell of the table above
(`clock seam` for that row). An analyzer row takes the KW-STD id from
`analyzers/ratchet.globalconfig`.

## Roles

- **Orchestrator:** a Claude Code session the owner starts. It runs the opencode instances, checks
  each result, merges, re-runs the audit, deletes ratchet lines and opens the pull requests. It
  never fixes code itself; a fixer that fails gets a smaller slice.
- **Inventory worker:** one opencode instance on the local Qwen model. Read-only on code.
- **Fixers:** up to three opencode instances on the local Qwen model, in parallel, each in its own
  worktree with a file set no other fixer touches.

Every opencode instance runs as:

```bash
opencode run --agent build -m magnitude/qwen3.8-27b "<prompt>"
```

The owner chose the local Qwen model for this work. The standing model rule routes harder work to
`commandcode/deepseek/deepseek-v4.1-flash`. If a judgement slice (CA1031, CA1502, CA1506, KW-STD-SQL)
fails twice on Qwen after being split smaller, the orchestrator asks the owner before switching it.
Never an Anthropic model in opencode.

Worktrees go under `~/git/opencode-worktrees/csharp-remediation/` (Defender skips `~/git`).
The shared work folder, outside every worktree, is
`~/git/opencode-worktrees/csharp-remediation/work/`.

## Step 1: the inventory pass (one Qwen instance)

The orchestrator creates `worktree inventory` from the integration branch, then runs the inventory
worker in it with this prompt:

> You are taking an inventory. Change no source file. Work in this worktree.
>
> 1. Run, from the worktree root:
>    `dotnet build KyberWeave.sln -c Release -p:KwAnalyzerAudit=true -p:TreatWarningsAsErrors=false -clp:NoSummary -v:q > ../work/audit-weave.log 2>&1`
>    and the same for `dash/server/KyberDash.sln` into `../work/audit-dash.log`. Both must exit 0.
> 2. From both logs, keep every line matching `<path>.cs(<line>,<col>): warning CA<digits>`, make the
>    path relative to the worktree root, and drop duplicates. Write one row per location to
>    `../work/inventory.tsv` with these tab-separated columns:
>    `rule` (an analyzer row takes the KW-STD id from `analyzers/ratchet.globalconfig`; a no-analyzer row takes the Rule cell of the table above), `analyzer`, `path`, `line`,
>    `project` (the folder under `src/`, `tests/`, `dash/server/` or `dash/server.tests/`), `kind`
>    (`mechanical` for CA1062, CA1001, CA1063, CA2227; `judgement` for the rest), `note` (empty).
> 3. For each pattern in the "Rules with no analyzer" table of
>    `docs/todo/csharp-standards-remediation.md`, run `grep -rnE --include='*.cs' '<pattern>' src dash/server`,
>    open each hit, and add a row only when it really breaks the rule. Put the reason in `note` for
>    rows you keep. A `.Result` property of our own type, a `!` that is a logical not, and a
>    `NotSupportedException` thrown for an input the method rejects on purpose are not violations.
> 4. Write `../work/slices.md`: group the rows into slices. A slice is one analyzer family
>    (CA1062; CA1001+CA1063; CA2227; CA1031; CA1502+CA1506; one no-analyzer rule) inside one project
>    folder, at most 15 files and at most 40 rows, about 20 minutes of work. No file appears in two
>    slices of the same wave. For each slice list: id (`S01`, `S02`, …), family, project, files,
>    row count, and the test project whose classes cover those files.
> 5. Print the totals per analyzer and per project, and check that the analyzer totals match the
>    audit logs. Stop.

The orchestrator checks the totals against the baseline above (a difference needs a reason), spot
reads five rows per family, then commits nothing: the inventory stays in the work folder.

## Step 2: fan out up to three fixers per round

Waves, in order. Each wave is one plan and one pull request:

1. **Wave A: mechanical.** CA1062 (`src`, `dash/server`), CA2227, CA1001 and CA1063.
2. **Wave B: SQL prerequisite.** Give `BaseSqliteRepository` a parameterized command path and read
   by column name, add `Microsoft.Data.Sqlite.Core` and `SQLitePCLRaw.provider.sqlite3` to the engine
   (same versions as `dash/server/KyberDash.Core`), and move `SqliteAnalysisPersistence` off the CLI.
   One fixer, no parallel slices: every later SQL slice depends on it. Then the `TextExpr` call sites,
   in parallel slices by repository class.
3. **Wave C: catch discipline.** CA1031.
4. **Wave D: complexity and coupling.** CA1502 and CA1506, smallest methods first.
5. **Wave E: the other searched rules.** Blocking on async, the time seam, stub members, HTTP
   handlers, and the null-forgiving `!`.

For each round the orchestrator picks up to three slices of the current wave whose file lists do not
overlap, creates one worktree per slice from the wave branch, and starts one fixer per worktree with
this prompt (fill the `<…>` parts from `slices.md`):

> Fix slice `<id>`: analyzer `<analyzer>` in these files only: `<files>`. The rows are:
> `<rows from inventory.tsv>`.
> First read the rule `<KW-STD id>` in `docs/standards/csharp/csharp.rules.md` (or
> `docs/standards/sqlite/sqlite.rules.md`) and fetch the C# standard with the kw-docs
> `docs_get_coding_standard_for_technology` tool, technology `csharp`.
>
> - Change only the listed files. Change no behaviour: the fix is the smallest one the rule asks for.
> - CA1062: add `ArgumentNullException.ThrowIfNull(<param>);` at the top of the method, or make the
>   type `internal` when nothing outside the assembly uses it.
> - CA1001 / CA1063: make the owning type `IDisposable` (or `IAsyncDisposable`) and dispose the field;
>   a sealed type needs only `Dispose()`, no finalizer and no `Dispose(bool)`.
> - CA2227: remove the setter and expose an `IReadOnly*` collection or an `init` property.
> - CA1031: catch the exception types the call can throw, or log and `throw;`.
> - CA1502 / CA1506: extract private methods or a collaborator behind an interface; keep every public
>   signature.
> - Add no `#pragma`, no `NoWarn`, no `SuppressMessage`, and do not touch either `.globalconfig`.
> - Then run, and fix until all pass:
>   `dotnet build KyberWeave.sln -c Release` (or `dash/server/KyberDash.sln`), 0 warnings;
>   `dotnet test <test project> -c Release --no-build --filter "FullyQualifiedName~<namespace>"` for
>   the classes that cover the files, with a non-zero test count;
>   `dotnet format <sln> whitespace --verify-no-changes -v minimal` and
>   `dotnet format <sln> style --verify-no-changes --severity warn -v minimal`.
> - Never run the whole test suite.
> - Commit once: `refactor(<project>): <analyzer> in <slice id> [opencode]`. Do not push.
> - Report: files changed, rows fixed, rows left with the reason, the four command results.

After each fixer finishes, the orchestrator:

1. Reads the diff. A behaviour change, a suppression, or a file outside the slice sends it back.
2. Re-runs the build and the named test classes in that worktree. A fixer's report is not proof.
3. Merges the slice branch into the wave branch, then deletes the worktree and the slice branch. It
   records the merge commit in the branch ledger.
4. Starts the next slice in the freed seat, so up to three fixers are always running.
5. **Circuit breaker:** a slice that fails its gates twice is split in half and re-queued. The
   orchestrator never finishes it by hand.

When the wave's slices are merged, the orchestrator re-runs the audit build. For every analyzer at
zero, it deletes that analyzer's line in `analyzers/ratchet.globalconfig`, builds both solutions
normally, and runs the wave's touched test classes. Then it opens the wave's pull request, and CI
runs the whole suite and the integration project on Linux.

## Done

`analyzers/ratchet.globalconfig` has no rule line left and is deleted, together with its
`ItemGroup` in `Directory.Build.props`. `TextExpr` and the analysis cache's CLI calls are gone.
The searched rules in the second baseline table have no remaining real violation. Archive this todo.

## Open questions for the owner

1. Do CA1001, CA1063, CA1031, CA1502 and CA1506 apply to test code? Test code holds 214 of the 699
   violations, including 127 of the 132 disposal ones. Recommendation: keep CA1001 and CA1063 for
   tests, since leaked handles make tests flaky, and exempt tests from CA1502 and CA1506 like CA1062.
2. Sonar: none of the Sonar ids in `Directory.Build.props` `NoWarn` do anything, because SonarAnalyzer
   is not referenced. So magic numbers (S109), nesting (S134), method length (S138), cognitive
   complexity (S3776) and concatenation in loops (S1643) have no analyzer. Recommendation: keep them
   review-only for now; adding `SonarAnalyzer.CSharp` as a development-only package would also turn
   on its whole default rule set.
