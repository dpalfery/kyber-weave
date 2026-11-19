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
[TypeScript standard](../typescript/typescript.standards.md), which still governs the language, its
"Output and display" rules and its completion gates. A UI displays the data its server provides and
holds no business logic of its own.

## Naming and layout

- PascalCase components, one component per file, named after the file.
- Components are grouped by feature area; hooks are `useX.ts`; shared types live beside the API
  client that produces them.

## Coding standards

### Data from the server

- Server data comes through TanStack Query (`useQuery`/`useMutation`) over the shared API client.
  All network access goes through that client: nothing else calls `fetch`, `axios`,
  `XMLHttpRequest`, `WebSocket` or `EventSource` — not a component, hook, effect, utility or event
  handler. A live channel (SignalR, WebSocket) is wrapped by a client module the same way.
- Every query-backed view renders four distinct states: loading, error, empty and data. A value that
  has not loaded, or failed to load, is never shown as `0`, `—` or a default, and measured zero,
  derived value and missing data look different (the TypeScript standard's "Output and display").
- A section waits only for its own queries. Never gate a section on unrelated requests.
- The API client validates each response's shape. A response that fails validation becomes an error
  state: never a crash, and never a silent default such as an empty list.
- An error message says only what is known. Network and validation errors reach the user, not only
  the console.

### Components

- One responsibility per component: a component that both fetches and renders a non-trivial tree is
  two components (a container that queries, a view that renders props).
- Repeated logic becomes a custom hook, not a copied block or a many-argument utility.
- Prefer context or restructuring over passing a value through components that do not use it; as a
  guide, a value passed through more than two components that only forward it is a sign to change.
- Layouts are responsive and follow the design the plan names; a layout that departs from it is
  raised with the planner, not decided in the implementation.

### State and effects

- Never mutate state or props; produce a new value.
- Effects are only for synchronizing with something outside React. A value derived from props or
  state is computed during render.
- `useEffect` dependency arrays are complete; a suppressed exhaustive-deps warning needs a comment
  saying why the effect is still correct.
- Keys are stable and unique; an array index is not a key when the list can reorder.

### Rendering cost

- Memoize (`React.memo`, `useMemo`, `useCallback`) only with a React DevTools Profiler measurement:
  record the commit duration before and after in the change. Memoize a component that re-renders
  with unchanged props, or a computation (a sort or filter over a large list) that the Profiler shows
  repeating on every render. Otherwise do not memoize.
- Heavy transforms do not run in the render path; do them on the server or in a query `select`.

### Styling

- Tailwind utility classes, composed with `clsx` and `tailwind-merge`. An inline `style` object is
  only for a value the theme cannot express: one computed at runtime (a measured width, a chart
  position) or provided by the server (a status colour, passed as a CSS variable).
- Colours, spacing and fonts come from the Tailwind theme (`@theme` tokens in the app's stylesheet);
  do not hardcode a value the theme defines.
- Charts use Recharts; icons use `lucide-react`.

### Accessibility

Semantic HTML first; ARIA only where semantics cannot express it. Every interactive element is
reachable and operable by keyboard, and focus is managed when content appears or moves. This is a
requirement, not a polish step.

### Hygiene

ESLint decides formatting and the mechanical rules; code that fails it is not merged. No commented-out
blocks, and no `console` calls in merged code except `console.error` for an error that is also shown
to the user. No direct DOM access of any kind (`document.querySelector`, `getElementById`,
`getElementsBy…`, or a ref used to look up nodes React renders). A ref may move focus, measure an
element (`getBoundingClientRect`, `ResizeObserver`) or observe it (`IntersectionObserver`); browser
APIs outside React's tree (`matchMedia`, `document.title`) are reached from an effect. A new dependency states what it does, why the existing stack cannot, and who maintains it.

## Testing

- Components and hooks are tested in memory with Vitest and Testing Library, rendering over fixtures
  and mocked query results. A fixture or mock satisfies the API client's response schema: build it
  through the client's validator or a shared factory, so a mock cannot drift from what production
  accepts. Assert what the user sees, by role and text, not implementation details.
- Each data state (loading, error, empty, data) of a query-backed view has a test. Hold the response
  back to assert loading; never assert a transient state after it may have passed.
- A mock one test needs is set up inside that test, never in shared setup.
- Playwright end-to-end tests are the exception, for behaviour that needs the real browser and
  server, for example keyboard and focus flows, navigation across views, clipboard, downloads and
  printing.

## Commands

React code runs the TypeScript standard's completion gates (typecheck, lint, the related tests and
the reachability check); there is no separate React command set.

## Security and data handling

- Do not use `dangerouslySetInnerHTML`. Render text, or structure the data so React renders it. The
  stack has no approved HTML sanitizer; adding one is a reviewed dependency decision first.
- No secrets in client code; anything shipped to the browser is public.

## Done checklist

### Completion

The TypeScript completion gates pass, every changed query-backed view has its four data-state tests,
and a change to rendered output got a manual look in a browser, keyboard-only for anything
interactive.
