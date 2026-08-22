---
id: standards/react
title: react coding standard
doc-type: coding-standard
status: draft
technology: react
owner: unassigned
last-reviewed: 2026-08-16
---

# react coding standard

How React code is written in this repository. Agents and skills resolve this document as
`<react-coding-standard>`.

## Authority & status

When this standard is in `status: current`, what it says here outranks whatever defaults a
portable agent shipped with. While in `status: draft`, it serves as a non-authoritative
template/proposal and does NOT override portable agent defaults until reviewed and promoted
to `current`.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`.

## Components

- **One responsibility per component.** A component that both fetches and renders a
  non-trivial tree is two components.
- **Repeated logic becomes a custom hook**, not a copied block or a utility that takes seven
  arguments.
- **PascalCase for components**, one component per file, named after the file.
- **No prop drilling past two levels.** Use context or restructure — passing a value through
  components that do not use it makes every one of them a false dependency.

## State and effects

- **Never mutate state or props.** Produce a new value.
- **`useEffect` dependency arrays are complete.** A lint suppression on an exhaustive-deps
  warning needs a comment saying why the effect is correct without the missing dependency.
- **Effects are for synchronizing with something outside React.** Deriving a value from props
  or state is a calculation during render, not an effect that sets state.
- **Keys are stable and unique.** An array index is not a key when the list reorders.

## Rendering cost

- Memoize (`React.memo`, `useMemo`, `useCallback`) when a measurement says to, not
  pre-emptively. Memoization has a cost of its own, and one applied everywhere hides the
  component that actually re-renders.
- Heavy transforms do not belong in the render path.

## Styling

- Use the design system's own mechanism — the `sx` prop or `styled()` under MUI — rather than
  raw inline styles.
- Theme values come from a central `ThemeProvider`. Do not mutate the theme at runtime, and do
  not hardcode a colour that the theme already names.

## Accessibility

Semantic HTML first; ARIA only where semantics cannot express it. Interactive elements are
reachable and operable by keyboard, and focus is managed when content appears or moves. This
is a requirement, not a polish step.

## Safety

- `dangerouslySetInnerHTML` requires sanitized input and a comment naming the source.
- No secrets in client code. Anything shipped to a browser is public, including values in a
  bundled environment variable.
- Errors from network calls surface to the user and to telemetry. A caught error that only
  reaches `console.error` is invisible in production.

## Hygiene

ESLint and Prettier decide formatting and the mechanical rules; do not restate them here, and
do not merge code that fails them. No `console.log` or commented-out blocks in merged code.

New dependencies need a reason: what it does, why the existing stack cannot, and who
maintains it.
