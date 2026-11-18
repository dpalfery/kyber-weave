---
id: fixtures/arbiter-plans/spec-tasks
title: Spec task grammar
doc-type: spec
status: draft
---

# Spec task grammar

## Tasks

- [ ] 1. Ship the thing
- [ ] 1.1 Write the failing test
  - Edit `src/KyberWeave.Core/Arbiter/engine.ts`, then `helpers.ts` beside it.
  - Depends on: none.
- [x] 1.2 Green path with a label
  - **Files:** `src/one.ts`, `two.ts`
  - Depends on: 1.1
- [x] 2. Parent with no body of its own
- [ ] 3. Broken dependency
  - Edit `src/fix.ts`.
  - Depends on: 9.9
