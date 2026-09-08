---
name: resharper-clt
description: Use when running JetBrains ReSharper Command Line Tools over a .NET solution — the CleanupCode/format fix pass an agent runs before claiming C# work complete, or the once-per-run InspectCode gate whose output a code review triages. Do NOT use for non-.NET languages, for judging design or correctness by reading code, as a substitute for the compiler and test gates, or to run InspectCode from inside a per-task completion gate.
license: MIT
---

# ReSharper Command Line Tools

`InspectCode` runs ReSharper's inspection set over a solution and emits a machine-readable
report. `CleanupCode` applies ReSharper's formatting and cleanup profiles to source files.
Both are mechanical: the same solution at the same revision produces the same output, which
is what makes their result citable as evidence rather than an opinion.

That property is also the boundary. This skill produces and remediates tool output. It does
not judge design, correctness, or intent — those belong to the reviewer and its lenses.

## Setup

The tools ship as a .NET global tool, pinned in the repository's tool manifest at
`.config/dotnet-tools.json`:

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "jetbrains.resharper.globaltools": {
      "version": "2026.2.1",
      "commands": ["jb"],
      "rollForward": false
    }
  }
}
```

ALWAYS restore before the first invocation in a session:

```bash
dotnet tool restore
```

If the repository has no manifest entry, say so and stop rather than installing a tool
version nobody pinned. A floating analyzer version makes every later report incomparable.

## Commands

Resolve the solution path from the repository rather than assuming one, and write reports to
the path declared as **<agent-scratchpad>** where the repository declares one. Substitute
`<solution>` and `<scratchpad>` below.

| Purpose | Command |
|---|---|
| Full-solution inspection | `dotnet jb inspectcode <solution> --output=<scratchpad>/inspectcode.xml --format=Xml` |
| Project-scoped inspection | `dotnet jb inspectcode <solution> --project="<ProjectName>" --output=<scratchpad>/inspect-<project>.xml --format=Xml` |
| SARIF for CI or automated scanning | `dotnet jb inspectcode <solution> --output=<scratchpad>/results.sarif --format=Sarif --severity=WARNING` |
| Formatting and cleanup | `dotnet jb cleanupcode <solution>` |

Scope the run to the affected projects when the solution is large and you are iterating by
hand. The once-per-run review gate takes the full solution: it is the only inspection pass in
the run, so narrowing it drops coverage nothing else replaces.

## Two cadences, and which one you are in

This skill has two commands and they run at different times. Getting them the wrong way round is
the mistake this section exists to prevent.

| | Command | Who runs it | When |
|---|---|---|---|
| **Fix pass** | `dotnet format`, `dotnet format analyzers`, `cleanupcode` | the agent that wrote the code | every task, before `READY_FOR_REVIEW` |
| **Inspection** | `inspectcode` | the review gate runner | once per run, over the whole accumulated change |

The reason is contention and cost. `inspectcode` loads the entire solution; run at baseline and
again at the end by every worker, it was both the slowest part of a completion gate and the part
most likely to collide with a concurrent worker's build output. Run once, on a quiescent tree, it
costs a single solution load and reports against a change that is finished. **A worker that runs
`inspectcode` is doing the council's job at N times the price.**

## Running the fix pass before you claim work is complete

Any agent implementing, modifying, or refactoring C# MUST run this before reporting
`READY_FOR_REVIEW`.

**Fix mechanically, then stop.** A defect a machine can fix is not a defect worth a reviewer's
attention. Every mechanical finding that reaches a reviewer costs a review pass to report, a
rework cycle to fix, and another pass to confirm — to arrive at the same edit `cleanupcode` would
have made for free. Erasing them here is the whole point.

1. **Baseline.** Before the first edit, collect language diagnostics for the files you are
   permitted to change and keep the output. Without it you cannot distinguish a diagnostic you
   introduced from one that was already there, and "pre-existing" is a claim that needs proof.
   Do **not** take an `inspectcode` baseline; that comparison now belongs to the review gate.
2. **Fix deterministically, scoped to the files you changed.** Three commands, in this order,
   each applying rather than verifying:

   ```bash
   dotnet format <solution> --include <changed files>
   dotnet format <solution> analyzers --include <changed files>
   dotnet jb cleanupcode <solution> --profile="<cleanup profile>" --include="<changed files>"
   ```

   `--include` is not optional. Unscoped, these rewrite files the task never touched, and the
   change under review stops being the change you made. The pass is idempotent: running it twice
   produces no second diff, so a re-run after a rework cycle is safe.

   All three load MSBuild and write into `obj/`. Where concurrent workers may run them against the
   same projects, give each an isolated artifacts path — see the host's per-agent completion gate.

3. **Re-collect diagnostics over the files you changed** — whole file, not only the changed
   members. Not workspace-wide: while other workers are in flight, a workspace-wide pass reads
   their half-finished state and attributes it to you.
4. **Fix every finding in your change set that step 2 could not.** What survives the fix pass is
   the genuinely non-mechanical remainder, and that is the part worth your judgement.
5. **Report the result.** The fix pass applied, diagnostics clean on your files, and the isolated
   artifacts path you used — or an explicit list of what remains with baseline proof.

### What the cleanup profile should and should not do

Fix what the repository's own standards and analyzers already require: predefined type keywords,
explicit types where the standard forbids `var`, redundant qualifiers and parentheses, unused
usings, fields that can be `readonly`, formatting. Those are settled decisions, and re-arguing one
in review is waste.

Leave everything the repository has **not** declared. Brace style with no `.editorconfig` rule
behind it, member reordering, and file layout are the tool's opinion rather than the project's
standard — and reordering members in particular buries the actual change under churn a reviewer
then has to read past. A cleanup profile that imposes undeclared preferences trades review cost
for diff noise, which is not the trade this gate exists to make.

A green build is not the same measurement. The compiler and ReSharper's inspection set overlap
partially and disagree at the edges by design — a suggestion-severity inspection is invisible
to `dotnet build` and still a real finding. That is why the review gate pins InspectCode at
`--severity=SUGGESTION` rather than trusting the build.

### Frequently introduced inspections, and what to do

| Inspection | Remediation |
|---|---|
| `PossibleMultipleEnumeration` | Materialize the `IEnumerable` with `.ToList()` or `.ToArray()` before iterating it more than once. |
| `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | Fix the contradictory null check, or correct the nullable annotation on the DTO or API model it disagrees with. |
| `InheritdocInvalidUsage` | Replace `<inheritdoc />` on a type that inherits no documentation with a real `<summary>`. |
| `UseCollectionExpression` | Modernize the array or list initializer to a collection expression `[...]`. |
| `CheckNamespace` | Align the file's namespace to its directory hierarchy. |
| `UnusedParameter.Local`, `PrivateFieldCanBeConvertedToLocalVariable` | Remove the dead parameter, or narrow the single-assignment field to a local. |
| `RedundantSuppressNullableWarningExpression` | Remove the unnecessary `!`. Suppressing a warning the compiler no longer raises hides the next real one. |

**NEVER fix an inspection by breaking a public contract.** Deleting a public API action
method, an interface member, or a DTO serialization property to silence a "never used"
inspection trades a warning for a breaking change. Suppress it with a stated reason instead,
or leave it and say why.

## Running it as a review gate

In a code review this is a **gate**, not a lens: it is a repeatable command whose output the
`static-analysis-triage` lens attributes to the change. Declare it under `review.gates` in
`.kyber-weave/kyber-weave.yml` so the review runner executes it rather than the reviewer
improvising a command:

```yaml
review:
  gates:
    - id: dotnet-tool-restore
      run: [dotnet, tool, restore]
      blocking: true
    - id: inspectcode
      run: [dotnet, jb, inspectcode, <solution>, --output=<scratchpad>/inspectcode.xml, --format=Xml, --severity=SUGGESTION, --caches-home=<scratchpad>/gates/inspectcode-cache]
      blocking: true
```

`dotnet-tool-restore` provisions the pinned `jb` command; without it the inspection gate fails
on a missing tool rather than on a finding. `--severity=SUGGESTION` states the tool's current
default rather than changing it — pinned, because the suggestion-level inspections are exactly
the ones the compiler cannot see, and a default that moves would silently drop them.

`run` is argv, never a command line — the gate runner refuses a shell.

The gate result records the id, exit code, and duration — not the report. The
`static-analysis-triage` lens reads the report itself, from the `--output` path the gate
declaration names, so that path is part of the contract between them: change it in one place
and the lens finds nothing.

**InspectCode exits zero whether or not it finds anything.** `blocking: true` here means the
analyzer must have *run*, not that it found nothing. The judgement about what it found is the
lens's, and it is made against the diff — a solution-wide report is expected to carry
pre-existing findings, and attributing those to the change under review is the specific error
that lens exists to avoid.

The reviewer's own obligations around that output:

- **Do not re-derive it.** The lens attributes diagnostics to the diff and reports them by
  rule identifier. Reading the code to form a second opinion about a rule is a different
  lens's job.
- **Report by rule identifier and location.** "Analyzer warning" is not a finding.
- **A clean run is a recorded result, not silence.** State the gate ran and returned zero
  findings.
- **An unavailable tool is a stated skip.** If the manifest has no entry or the restore
  fails, record the gate as skipped with the reason. A review that quietly omits a gate reads
  as a review that passed one.

## Worked example

A change adds a service that filters an `IEnumerable<Load>` and iterates the result twice.
`dotnet build` is green — nothing here is a compiler error.

**At the task.** The worker runs the fix pass, scoped to its own files and its own build output:

```bash
dotnet format Hauling.sln --include 2-Application/Hauling.Application/Dispatch/LoadPlanner.cs \
  -p:BaseIntermediateOutputPath=.agents-scratchpad/T3/artifacts/obj/
dotnet format Hauling.sln analyzers --include 2-Application/Hauling.Application/Dispatch/LoadPlanner.cs \
  -p:BaseIntermediateOutputPath=.agents-scratchpad/T3/artifacts/obj/
dotnet jb cleanupcode Hauling.sln --profile="<cleanup profile>" --include="2-Application/Hauling.Application/Dispatch/LoadPlanner.cs"
```

Formatting, redundant qualifiers, and unused usings are gone. The multiple enumeration is not —
no formatter can decide that materializing the sequence is the right fix. The worker reports:

```text
DIAGNOSTICS: clean on 2-Application/Hauling.Application/Dispatch/LoadPlanner.cs | fix pass: format, format analyzers, cleanupcode — all applied | artifacts: .agents-scratchpad/T3/artifacts | baseline: .agents-scratchpad/T3/diagnostics-baseline.md | remaining: none
```

**At the end of the run.** The `inspectcode` gate runs once over the whole solution:

```xml
<Issue TypeId="PossibleMultipleEnumeration"
       File="2-Application/Hauling.Application/Dispatch/LoadPlanner.cs" Line="47"
       Message="Possible multiple enumeration" />
```

`static-analysis-triage` checks that line against the accumulated diff, finds it inside T3's
change, and reports it by rule id and location. It becomes a review finding routed back to a
worker of that type — `.ToList()` before the first iteration — rather than something every worker
paid a solution load to look for. Findings on lines the run did not touch are pre-existing and the
lens drops them; that attribution, not the worker's baseline, is what "pre-existing" now means.

## Boundaries

- .NET solutions only. There is no ReSharper CLT for other languages; say so rather than
  substituting a different tool under this skill's name.
- `cleanupcode` rewrites source. It belongs to the completion gate above, run by the agent that
  wrote the code, before any reviewer sees it — and NEVER as part of a review itself. A reviewer
  that edits the code it is judging has stopped being a reviewer.
- `inspectcode` is the mirror image: it belongs to the review gate, once per run, and NEVER to a
  per-task completion gate. A worker that runs it loads the whole solution to analyze a tree its
  peers are still editing, and pays that cost once per task instead of once per run.
- Formatter output is not a review finding. Whitespace differences belong to `cleanupcode`, not
  to the findings list. A reviewer that finds mechanical issues is looking at a change whose
  completion gate did not run: the correct report is that single fact, not a list of nits the
  gate would have erased.
