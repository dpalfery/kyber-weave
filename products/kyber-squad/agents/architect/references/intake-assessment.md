# Intake Assessment

Assess a todo or open request without creating a delivery artifact. The conductor uses this digest to help the user choose the path.

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
