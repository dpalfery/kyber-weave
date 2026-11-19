---
id: standards/react/rules
title: "react rules"
doc-type: rule
status: current
technology: react
owner: dpalfery
last-reviewed: 2026-11-19
---

# react rules

- A UI displays the data its server provides and holds no business logic of its own.
- Server data comes through TanStack Query and the shared API client, never `fetch` in a component.
- Every query-backed view renders distinct loading, error, empty and data states. Never show `0`,
  `—` or a default for a value that has not loaded or failed to load.
- A section waits only for its own queries.
- Validate each server response at the API client; a response that fails validation is an error
  state.
- Each data state of a query-backed view has a Vitest + Testing Library test; mocks live inside the
  test that needs them.
- Semantic, keyboard-operable markup; Tailwind for styling; no `console.log`, no direct DOM queries.
