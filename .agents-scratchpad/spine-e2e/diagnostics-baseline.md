# Diagnostics baseline — spine e2e (test-dev)

Collected before any edit. Permitted files: Playwright e2e under `dash/` (`dash/e2e/spine.spec.ts`, `dash/e2e/numerals.spec.ts`) plus config read-only (`dash/playwright.config.ts`).

## ReadLints (IDE language diagnostics)

- `dash/e2e/spine.spec.ts`: no linter errors
- `dash/e2e/numerals.spec.ts`: no linter errors
- `dash/playwright.config.ts`: no linter errors

## Host observed before rebuild

- `http://127.0.0.1:4747` served by existing `npm --prefix dash run dev -- web --port 4747 --no-open` (terminal pid 49138)
- Log line: `Dashboard UI is not built. Run: cd dash && npm install && npm run build`
- Playwright `reuseExistingServer: !process.env.CI` would hit this placeholder unless the host is rebuilt/restarted

## ReSharper

Skipped (not C# / .NET).
