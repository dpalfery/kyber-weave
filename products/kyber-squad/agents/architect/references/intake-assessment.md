# Intake Assessment

Assess a todo or open request without creating a delivery artifact. The conductor uses this digest to help the user choose the path, or to assess a diagnosed defect in an existing approved task.

## Diagnosed Ready-task assessment

When assigned a read-only diagnosed-task assessment for an existing Ready plan/spec task, inspect the supplied diagnosis and approved contract before the intent check below. Determine whether the scoped fix fits the approved scope, design, and Test/verification contract. Do not change the Ready artifact, create a competing artifact, implement the fix, or approve a contract change; do not ask for a new plan/spec choice.

Return exactly:

```text
STATUS: TASK_ASSESSED
ARTIFACT: <existing plan/spec identity>
TASK: <existing task identity>
CONTRACT: UNCHANGED | REVISION_REQUIRED | UNDETERMINED
RATIONALE: <why the diagnosed fix fits or requires revision, or what is unknown>
REWORK: <precise scoped fix instructions for UNCHANGED, or none>
GAPS: <missing evidence, or none>
Discovery: codegraph N, docs N, direct reads N
DIRECT_READ_REASONS: <path and reason for each direct read of indexed code, or none>
```

`UNCHANGED` requires sufficient evidence and actionable scoped rework instructions. `REVISION_REQUIRED` names the affected contract and its required revision; the conductor returns it to the artifact author and reopens approval before execution. If evidence cannot establish either outcome, return `UNDETERMINED` with the missing evidence in `GAPS`; the conductor relays it and stops that path. This assessment grants no new implementation authority and does not reset rework limits.

## Intent check

Before classifying, apply the conductor's defect-first precedence. For an undiagnosed concrete failure, return this routing digest and stop; do not manufacture a PLAN/SPEC recommendation:

```text
STATUS: DEFECT
INPUT: <todo path | open request>
CHOICE: PLAN | SPEC | UNSTATED
ARTIFACT: <existing artifact and task identity, or none>
RATIONALE: <concrete failure requiring diagnosis>
Discovery: codegraph N, docs N, direct reads N
DIRECT_READ_REASONS: <path and reason for each direct read of indexed code, or none>
```

Preserve an explicit PLAN or SPEC choice; neither choice or both choices means UNSTATED. The conductor reads its defect-diagnosis contract and dispatches `bug-crusher-investigator`. A packet already carrying the completed diagnosis may continue below.

With no undiagnosed concrete failure, if the packet already states the user's choice, return this digest and stop:

```text
STATUS: INTENT_STATED
INPUT: <todo path | open request>
CHOICE: PLAN | SPEC
ARTIFACT: <existing artifact, or none>
Discovery: codegraph N, docs N, direct reads N
DIRECT_READ_REASONS: <path and reason for each direct read of indexed code, or none>
```

The conductor resumes the named artifact or promotes the preserved choice. These are routing statuses, not blockers; neither creates an artifact or requires the user to choose again.

## Classification

Recommend **PLAN** when the work is a bounded change or smaller feature in an existing product and the requirements and architecture are established enough to decompose directly.

Recommend **SPEC** when the work is a new product, a large feature, or the requirements, interfaces, data model, or architecture need definition before implementation can be decomposed safely.

Size alone is not decisive. A broad mechanical change with settled behavior may still be a plan; a small request with unresolved product semantics may need a spec.

Inspect enough governed documentation and code to support the recommendation. Surface unknowns rather than designing them away. Do not create a plan, specification, or todo mutation until the conductor returns the user's chosen path.

Return exactly:

```text
STATUS: INTAKE_RECOMMENDATION
INPUT: <todo path | open request>
RECOMMENDATION: PLAN | SPEC
RATIONALE: <bounded explanation grounded in discovered facts>
GAPS: <facts missing from the input, or none>
OPEN_QUESTIONS: <material choices the conductor must relay, or none>
Discovery: codegraph N, docs N, direct reads N
DIRECT_READ_REASONS: <path and reason for each direct read of indexed code, or none>
```
