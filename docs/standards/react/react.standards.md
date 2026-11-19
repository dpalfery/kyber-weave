---
id: standards/react
title: React coding standard
doc-type: coding-standard
status: current
technology: react
builds-on: typescript
owner: dpalfery
last-reviewed: 2026-11-19
---

# React coding standard

How React is written in this repository, wherever React code lives. Agents and skills resolve this
document as `<react-coding-standard>`. It builds on the
[TypeScript standard](../typescript/typescript.standards.md), which still governs the language.
A UI displays the data its server provides and holds no business logic of its own.

## Naming and layout

- PascalCase components, one component per file, named after the file.
- Components are grouped by feature area; hooks are `useX.ts`; shared types live beside the API
  client that produces them.

## Coding standards

### Data from the server

- Server data comes through TanStack Query (`useQuery`/`useMutation`) and the shared API client,
  never `fetch` inside a component or an effect.
- Every query-backed view renders four distinct states: loading, error, empty and data. A value
  that has not loaded, or failed to load, is never shown as `0`, `—` or a default; measured zero,
  derived value and missing data look different (see the TypeScript standard's display section).
- A section waits only for its own queries. Never gate a section on unrelated requests.
- Validate each server response's shape at the API client boundary; a response that fails
  validation is an error state, not a crash and not a silent default.
- An error message says only what is known. Network and validation errors reach the user, not
  only `console.error`.

### Components

- One responsibility per component: a component that both fetches and renders a non-trivial tree
  is two components (a container that queries, a view that renders props).
- Repeated logic becomes a custom hook, not a copied block or a seven-argument utility.
- No prop drilling past two levels: use context or restructure.
- Layouts are responsive and match the approved design exactly; a different layout is a plan
  change, not an implementation choice.

### State and effects

- Never mutate state or props; produce a new value.
- Effects are only for synchronizing with something outside React. A value derived from props or
  state is computed during render.
- `useEffect` dependency arrays are complete; a suppressed exhaustive-deps warning needs a comment
  saying why the effect is still correct.
- Keys are stable and unique; an array index is not a key when the list can reorder.

### Rendering cost

- Memoize (`React.memo`, `useMemo`, `useCallback`) when a measurement says to, not by default.
- Heavy transforms do not run in the render path; do them on the server or in a `select` on the
  query.

### Styling

- Tailwind utility classes, composed with `clsx` and `tailwind-merge`. No inline `style` objects
  for anything Tailwind expresses, and no colour the theme already names hardcoded.
- Charts use Recharts; icons use `lucide-react`.

### Accessibility

Semantic HTML first; ARIA only where semantics cannot express it. Every interactive element is
reachable and operable by keyboard, and focus is managed when content appears or moves. This is a
requirement, not a polish step.

### Hygiene

ESLint decides formatting and the mechanical rules; code that fails it is not merged. No
`console.log` or commented-out blocks. No direct DOM queries (`document.getElementById`); a ref
only where accessibility needs to move focus. A new dependency states what it does, why the
existing stack cannot, and who maintains it.

## Testing

- Components and hooks are unit-tested with Vitest and Testing Library over fixtures and mocked
  query results, in memory: the web equivalent of the interface-first rule. Assert what the user
  sees, by role and text, not implementation details.
- Each data state (loading, error, empty, data) of a query-backed view has a test. Hold the
  response back to assert loading; never assert a transient state after it may have passed.
- A mock one test needs is set up inside that test, never in shared setup.
- Playwright end-to-end tests are the exception, for flows that need the real browser and server.

## Commands

From the package root:

```bash
npm ci
npm run typecheck
npm run lint
npx vitest run <paths of the tests covering your change>
npm run build
```

## Security and data handling

- `dangerouslySetInnerHTML` requires sanitized input and a comment naming the source.
- No secrets in client code; anything shipped to the browser is public.

## Done checklist

### Completion

The commands above pass, every changed query-backed view has its four state tests, and a change
to rendered output got a manual look in a browser, keyboard-only for anything interactive.
