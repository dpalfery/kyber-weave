# Bootstrap Templates

Minimal skeletons for a new documentation architecture. Headings name each file's location under **<docs-root>**; inside a skeleton, `[docs-root]` is the chosen root written out literally. Fill in the bracketed parts from the actual repo — don't invent owners, dates, or components you haven't verified.

## <docs-root>/README.md

```markdown
# [Project] Documentation

Canonical home for detailed [Project] documentation. Start with system docs, then follow the component or operational path for your task.

## System
- [Architecture](system/architecture.md)
- [Documentation standard](documentation-standard.md)
- [Component catalog](catalog.md)

## Components
- [Component name](component-folder/)

## Change history
- [Plan index](plans/README.md)
- [Architecture decisions](adr/)
- [Archived material](archive/) — historical only, never current guidance.
```

## <docs-root>/system/architecture.md

```markdown
# [Project] Architecture

## Overview
[High-level summary of the system architecture, boundaries, and key design principles]

## Components
[Core subsystems and their interactions]
```

## <docs-root>/documentation-standard.md

```markdown
# [Project] Documentation Standard

## Locations
- A component README at its source root: purpose, boundaries, links to detailed docs.
- Detailed docs live in `[docs-root]/<component>/`.
- Plans are working documents in `[docs-root]/plans/`; superseded material moves to `[docs-root]/archive/`.

## Required content per component
- `onboarding.md` — setup and debug path.
- `architecture.md` — components, interfaces, data model.
- `requirements.md` — numbered requirements with acceptance criteria.

## Rule
Do not create a second canonical document for a topic. Link to the established source instead.

## Plan lifecycle
`Draft → Ready → In progress → Blocked → Review required → Completed/Superseded → Archived`. A plan is archived only after its acceptance criteria and documentation impact have been verified.
```

## <docs-root>/catalog.md

```markdown
# [Project] Component Catalog

| Component | Type | Source root | Overview | Detailed docs | Owner | Last reviewed | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| [Component] | [Application/Service/Library] | `[path]` | [README](../[path]/README.md) | [docs](component-folder/) | [owner] | [YYYY-MM-DD] | Current |
```

## Config registry (add to root AGENTS.md)

```markdown
## Config Registry

Agents and skills should look up these facts by name rather than hardcoding them:

- **<docs-root>**: `[docs-root]`
- **<documentation-index>**: `[docs-root]/README.md`
- **<documentation-standard>**: `[docs-root]/documentation-standard.md`
- **<component-catalog>**: `[docs-root]/catalog.md`
- **<plan-index>**: `[docs-root]/plans/README.md`

Skills SHALL reference these paths by the property name above (e.g. "the path declared as **<plan-index>** in root AGENTS.md") rather than embedding a relative link that traverses out of the skill's own directory.
```

## <docs-root>/plans/README.md

```markdown
# Plan Index

Authoritative inventory for `[docs-root]/plans/`. Read before opening a plan. Open a plan only when relevant and listed as `Draft`, `Ready`, `In progress`, or `Blocked`.

## Active
| Plan | Status | Goal |
| --- | --- | --- |

## Archive
| Plan | Archived | Outcome / canonical guidance |
| --- | --- | --- |
```

## Plan file skeleton

```markdown
# [YYYY-MM-DD] [Plan title]

**Status:** Draft
**Date:** [YYYY-MM-DD]
**Goal:** [one sentence]

## Investigation findings

## Approved decisions

## Open questions (decision ledger)

## Acceptance criteria
```

## ADR skeleton

```markdown
# ADR-[YYYY-MM-DD]: [Decision title]

**Status:** Accepted
**Date:** [YYYY-MM-DD]

## Decision
[what was decided]

## Rationale
[why]

## Consequences
[trade-offs accepted]
```
