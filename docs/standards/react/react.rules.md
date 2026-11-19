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
- Server data comes through TanStack Query over the shared API client. All network access goes
  through that client (or the client module wrapping a live channel): no `fetch`, `axios`,
  `XMLHttpRequest`, `WebSocket` or `EventSource` anywhere else.
- Every query-backed view renders distinct loading, error, empty and data states. Never show `0`, `—`
  or a default for a value that has not loaded or failed to load, and make measured zero, derived
  value and missing data look different.
- A section waits only for its own queries.
- The API client validates each response's shape; a response that fails validation is an error
  state, never a crash and never a silent default.
- Each data state of a query-backed view has a Vitest + Testing Library test. Hold the response back
  to assert loading, never assert a transient state after it may have passed, and keep mocks inside
  the test that needs them.
- No `dangerouslySetInnerHTML`. No `console` calls except `console.error` for an error the user also
  sees. No looking up DOM nodes React renders; a ref only moves focus, measures or observes.
- Semantic, keyboard-operable markup; Tailwind with theme tokens for styling.
