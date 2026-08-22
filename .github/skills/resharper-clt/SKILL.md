---
name: resharper-clt
description: Use when running JetBrains ReSharper Command Line Tools — InspectCode static analysis or CleanupCode formatting — over a .NET solution, to verify C# work before claiming it complete or to produce the analyzer gate output a code review triages. Do NOT use for non-.NET languages, for judging design or correctness by reading code, or as a substitute for the compiler and test gates.
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

Scope the run to the affected projects when the solution is large. A full-solution pass is
correct for a completion gate and for a review of a broad change; a project-scoped pass is
enough while iterating.

## Running it before you claim work is complete

Any agent implementing, modifying, or refactoring C# MUST run this before reporting
`READY_FOR_REVIEW`.

1. **Baseline.** Before the first edit, inspect the projects you are permitted to change and
   keep the report. Without it you cannot distinguish a diagnostic you introduced from one
   that was already there, and "pre-existing" is a claim that needs proof.
2. **Inspect again after the last edit**, over the same scope.
3. **Fix every ERROR and WARNING the change introduced.** Compare against the baseline; do
   not dismiss a finding merely because its line is untouched.
4. **Report the result.** `dotnet build` and `dotnet jb inspectcode` both at zero errors and
   zero code or logic warnings, or an explicit list of what remains with baseline proof.

A green build does not clear this. The compiler and ReSharper's inspection set overlap
partially and disagree at the edges by design — a suggestion-severity inspection is invisible
to `dotnet build` and still a real finding.

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
    - id: inspectcode
      run: [dotnet, jb, inspectcode, <solution>, --output=<scratchpad>/inspectcode.xml, --format=Xml]
      blocking: true
```

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

```bash
dotnet tool restore
dotnet jb inspectcode Hauling.sln --project="Hauling.Application"   --output=.scratch/inspect-after.xml --format=Xml
```

The report names one inspection the baseline did not have:

```xml
<Issue TypeId="PossibleMultipleEnumeration"
       File="Hauling.Application/Dispatch/LoadPlanner.cs" Line="47"
       Message="Possible multiple enumeration" />
```

Compare it against the baseline report to confirm the change introduced it, materialize the
sequence once with `.ToList()` before the first iteration, and re-run the same scoped
command. Then report it:

```text
DIAGNOSTICS: clean on Hauling.Application/Dispatch/LoadPlanner.cs | inspectcode: 0 errors / 0 warnings | baseline: .scratch/inspect-before.xml | remaining: none
```

Had the finding predated the change, the baseline report is the proof — cite it by path
rather than asserting "pre-existing".

## Boundaries

- .NET solutions only. There is no ReSharper CLT for other languages; say so rather than
  substituting a different tool under this skill's name.
- `cleanupcode` rewrites source. Run it deliberately, and NEVER as part of a review — a
  reviewer that edits the code it is judging has stopped being a reviewer.
- Formatter output is not a review finding. Whitespace differences belong to `cleanupcode`,
  not to the findings list.
