# Intake Assessment

Assess a todo or open request without creating a delivery artifact. The conductor uses this digest to help the user choose the path.

## Intent check

Before classifying, check whether the packet already states the user's choice of plan or spec. If it does, do not assess: return `STATUS: BLOCKED` naming that the conductor should dispatch Draft plan creation instead. If it is a defect report, say so in `RATIONALE` and recommend the conductor use `bug-crusher-investigator`.

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
```
