# Todo and Open-Request Intake Path

Use this path for a supplied todo or an open request with no active plan or specification.

## Assessment

Send `architect` the request or exact todo path for a read-only intake assessment. It returns:

```text
STATUS: INTAKE_RECOMMENDATION
INPUT: <todo path | open request>
RECOMMENDATION: PLAN | SPEC
RATIONALE: <why the size and uncertainty fit that artifact>
GAPS: <facts still needed, or none>
OPEN_QUESTIONS: <material user choices, or none>
```

Recommend **plan** for a bounded change or smaller feature in an existing product whose requirements and architecture are established. Recommend **spec** for a new product, large feature, or work whose requirements, interfaces, or architecture still need definition.

The recommendation is not the decision. Present it with its rationale and require the user to choose plan or spec. Do not silently route based on size, wording, or the existence of a todo.

## Promotion

- A plan choice starts the plan path by assigning `architect` a Draft plan under the directory named by **<plan-index>**.
- A spec choice starts the spec path by assigning `product-owner` the requirements phase under the directory named by **<specification-index>**.
- Default `development-mode` to `test-first`. Record `standard` only after an explicit user opt-out.

When the input is a todo, preserve provenance as the successor is created:

1. Record the successor plan or specification identity in the todo.
2. Mark the todo `Superseded` and update the index named by **<todo-index>**.
3. Keep the todo in the active inventory while the successor remains Draft or partial.
4. Archive the todo only after the successor reaches Ready.

Assign `docs-dev` an explicit promotion task to perform these todo and index writes after the successor author has created the linked artifact. The conductor tracks and verifies the reported status; it does not edit the artifacts itself.
