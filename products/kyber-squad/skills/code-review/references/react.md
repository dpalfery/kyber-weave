# React (with Material-UI) Code Review Best Practices

## Component Design & Maintainability
- **Single Responsibility:** Check that React components are small, focused, and follow single-responsibility principles. Large components should be split for reusability.
- **State Management:** Ensure state is managed appropriately (lifting state up when necessary). Repeated logic should be encapsulated in custom hooks or utility functions.

## Correctness & State Management
- **State Updates:** Verify state updates are done appropriately using setState or hooks. Do not mutate state or props directly.
- **Hooks:** Ensure correct usage of React hooks. Dependency arrays for `useEffect` must be specified properly to avoid missing updates or infinite loops. No violation of the rules of hooks.

## Performance & Rendering Efficiency
- **Unnecessary Re-renders:** Check usage of keys in lists (each list element must have a stable unique key). Confirm components are not re-rendering due to unchanged props (suggest `React.memo` if applicable).
- **Excessive Computation:** Look for heavy computations or large data transformations happening directly during rendering – these might need memoization (`useMemo`, `useCallback`).
- **Nesting:** Avoid excessively deep component nesting. Use context or splitting instead of deep prop drilling.
- **Trim Dependencies:** Review if new npm packages are necessary, well-maintained, and not duplicating existing functionality.

## Code Consistency & Style
- **Conventions:** Ensure code follows established project coding standards (PascalCase for components, clear directory organization).
- **Linters:** Confirm adherence to linters or formatters (ESLint, Prettier). Remove debugging artifacts (console.log).

## Material-UI (MUI) Best Practices
- **Theming:** Use the `ThemeProvider` and centralized theme configuration instead of scattering style definitions. Avoid mutating theme objects at runtime.
- **Styling:** Prefer MUI's styling solutions (`sx` prop or `styled()` API) over raw inline styles.
- **Avoid Direct DOM Manipulation:** Use React’s state and effect hooks instead of `document.getElementById` or similar.
- **Accessibility:** Ensure UI components are accessible. Provide ARIA attributes, use semantic HTML tags, and verify keyboard navigation and focus management.

## Security & Data Handling
- **Avoid XSS:** Ensure any data inserted into the DOM is properly escaped or sanitized (especially when using `dangerouslySetInnerHTML`).
- **Proper API Usage:** Check that secrets (API keys) are not exposed in client-side code. Ensure no sensitive data is leaked in logs, error messages, or client storage.

## Front-End Observability & Debugging
- **Error Handling:** Ensure significant errors (e.g., API call failures) are handled gracefully and reported.
- **Visual Evidence:** UI changes should ideally be accompanied by relevant screenshots or demo recordings in the PR description.
