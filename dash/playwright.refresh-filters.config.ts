import { defineConfig } from '@playwright/test'

/**
 * Isolated T8 browser gate. No webServer on 4747 — the spec boots a temp-DB
 * dashboard itself so spine.spec.ts can keep using the live host.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: 'refresh-filters.spec.ts',
  timeout: 180_000,
  expect: { timeout: 20_000 },
  workers: 1,
  reporter: 'list',
})
