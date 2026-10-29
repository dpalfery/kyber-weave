# Todo and Open-Request Intake Path

Use this path for todo/open-request intake and for defect diagnosis before resuming an existing artifact.

## Intent first

Before any assessment, read the user's intent from the request. The assessment exists only to collect a choice the user has not made.

- **Defect first:** if the request reports a concrete failure that has not been diagnosed, use **Defect diagnosis** below, including when the request also names a plan/spec choice or an existing artifact. Preserve both for the handoff; run no plan-versus-spec assessment before diagnosis.
- With no undiagnosed concrete failure, an explicit plan or spec request is the user's choice. Skip the assessment and go to Promotion with that choice.
- With no undiagnosed concrete failure and neither choice, or both choices ("plan or spec?"), run the assessment when the repository publishes **<intake-assessment>**; when that property is absent, skip the assessment and go to Promotion with the plan choice.

Do not call a stated choice an inference; do not treat a missing choice as one.

## Defect diagnosis

Read this contract before dispatching `bug-crusher-investigator`. Pass the concrete failure, exact command or error, existing artifact and task identity if supplied, and `CHOICE: PLAN | SPEC | UNSTATED` preserving the user's explicit choice. The investigator diagnoses only; relay its evidence and `TRIVIAL | NEEDS_ARCHITECT` verdict. If the failure was not reproduced or its root cause is unknown, obtain the missing evidence before proceeding; do not draft a technical fix from symptoms.

- For a failure in an existing Ready plan/spec task, retain its approved scope and mode. Read the execution-and-review contract and route a diagnosed, in-scope `TRIVIAL` fix through its original worker and bounded rework audit. For `NEEDS_ARCHITECT`, dispatch `architect` for a read-only diagnosed-task assessment with the diagnosis, approved task contract, and artifact identity. It returns `STATUS: TASK_ASSESSED`; this is not a new plan/spec choice. With `CONTRACT: UNCHANGED`, no gaps, and precise scoped rework instructions, return those instructions to the original worker under the existing rework limits. With `CONTRACT: REVISION_REQUIRED`, or whenever diagnosis already proves an approved scope, design, or Test/verification contract must change, return the affected artifact to its author and reopen the applicable approval gate before execution. With `CONTRACT: UNDETERMINED` or unresolved gaps, relay the missing evidence and stop that path. Assessment never resets a circuit-breaker tally or bypasses its escalation contract.
- Without existing Ready authority, diagnosis does not authorize implementation. If the user stated PLAN or SPEC, proceed to Promotion with that choice and the diagnosis attached, including when the verdict is `NEEDS_ARCHITECT`. If choice is UNSTATED, send the diagnosis to `architect` for an intake recommendation, then obtain the user's artifact choice.

Never discard an explicit plan/spec choice because diagnosis ran first. For an existing Draft or partial artifact, resume its author with the diagnosis and preserved choice; do not execute it.

## Assessment

The assessment is default-off. It runs only when the repository publishes **<intake-assessment>** in the Config Reg of its root `AGENTS.md`; when that property is absent, the assessment is skipped and the path continues to Promotion with the plan choice.
When you take the plan path because the assessment is off, say so in one line and tell the user they can ask for a specification instead; never route silently.

Only when intent is unstated, send `architect` the request or exact todo path for a read-only intake assessment. It returns:

```text
STATUS: INTAKE_RECOMMENDATION
INPUT: <todo path | open request>
RECOMMENDATION: PLAN | SPEC
RATIONALE: <why the size and uncertainty fit that artifact>
GAPS: <facts still needed, or none>
OPEN_QUESTIONS: <material user choices, or none>
Discovery: codegraph N, docs N, direct reads N
DIRECT_READ_REASONS: <path and reason for each direct read of indexed code, or none>
```

Recommend **plan** for a bounded change or smaller feature in an existing product whose requirements and architecture are established. Recommend **spec** for a new product, large feature, or work whose requirements, interfaces, or architecture still need definition.

The recommendation is not the decision. In supervised mode, present it with its rationale and require the user to choose plan or spec. Do not silently route based on size, wording, or the existence of a todo. In autonomous mode the recommendation is the choice: record it as the autonomy contract directs and go to Promotion.

Handle defensive routing responses without treating them as failures:

- `STATUS: INTENT_STATED` with `CHOICE: PLAN | SPEC`: preserve the choice and go to Promotion without asking the user to choose again.
- `STATUS: DEFECT` with `CHOICE: PLAN | SPEC | UNSTATED`: preserve the choice and artifact, then enter Defect diagnosis. No PLAN/SPEC recommendation is required in that digest.

## Promotion

Before creating or revising a delivery artifact for a concrete failure, require that its diagnosis is attached. Otherwise return to Defect diagnosis. Preserve any existing artifact rather than creating a competing plan/spec for the same task.

- A plan choice (stated in the request or chosen after the assessment) starts the plan path by assigning `architect` a Draft plan under the directory named by **<plan-index>**. On harnesses that withhold file creation from subagents, pre-create the empty destination file before dispatching to `architect`.
- A spec choice starts the spec path by assigning `product-owner` the requirements phase under the directory named by **<specification-index>**. On harnesses that withhold file creation from subagents, pre-create the empty requirements file before dispatching to `product-owner`.
- Default `development-mode` to `test-first`. Record `standard` only after an explicit user opt-out.

When the input is a todo, preserve provenance as the successor is created:

1. Record the successor plan or specification identity in the todo.
2. Mark the todo `Superseded` and update the index named by **<todo-index>**.
3. Keep the todo in the active inventory while the successor remains Draft or partial.
4. Archive the todo only after the successor reaches Ready.

Assign `docs-dev` an explicit promotion task to perform these todo and index writes after the successor author has created the linked artifact. The conductor tracks and verifies the reported status; it does not edit the artifacts itself.
