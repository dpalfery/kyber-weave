# Design Phase

Author the design artifact for one feature specification, grounded in approved requirements, and return a headless digest.

## Inputs

- Feature identity.
- Approved requirements from the active specification directory resolved through **<specification-index>**.
- On revision, the current design and only the user's conductor-relayed feedback.

The portable design identity is `<active-spec-directory>/{feature-name}/design.md`.

## Work

1. Read approved requirements and address every requirement, tracing design sections to requirement ids where useful.
2. Inspect governed documentation and code for the current architecture. Research external constraints when needed and cite authoritative sources in the design.
3. Cover overview, architecture, components and interfaces, data models, error handling, and testing strategy. Use a diagram only when it materially clarifies flow or boundaries.
4. Explain consequential decisions and viable alternatives. Do not invent missing requirements to make the design appear complete.
5. Persist `Phase status: Draft` until the conductor supplies explicit approval. Revisions reopen this phase and tasks.

## Decisions

The design carries a permanent `## Decisions` section, preserved intact across all lifecycle transitions. Record all decisions in a structured table:

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |

## Scale

Declare the design scale at authoring time: `documentation-impact: new-component | extends-component | no-design-change` with `impact-components` naming the affected catalog components (required and non-empty for the first two scales).

## Design harvest

Maintain a `Design harvest` table with one row per design section and one row per requirement-group, keyed by target component and doc type:

| ID | Design item (section) | Target component | Target doc type |

## Gap handling

When approved requirements are contradictory or incomplete enough to block responsible design, persist what is valid and return:

```text
STATUS: REQUIREMENTS_GAP
PHASE: design
ARTIFACT: <resolved design path>
SUMMARY: <work completed>
GAPS: <requirement ids and the blocking contradiction or omission>
OPEN_QUESTIONS: <items for conductor to relay, or none>
```

Otherwise return:

```text
STATUS: READY_FOR_REVIEW
PHASE: design
ARTIFACT: <resolved design path>
SUMMARY: <2-4 sentences on the approach and decisions>
GAPS: none
OPEN_QUESTIONS: <items for conductor to relay, or none>
```

On conductor-supplied explicit approval, record `Phase status: Approved` and return `STATUS: PHASE_APPROVED`. Never prompt the user.
