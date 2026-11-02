// Config for the self-booting specs (harness-collection, refresh-filters): each boots its own
// dashboard in beforeAll, so there is no webServer here. One worker, so only one boot is alive.
import { defineConfig } from '@playwright/test'

import { beginLedgerRun } from './tests/setup/process-ledger-global.js'

process.env.KYBERDASH_SPAWN_GUARD = 'deny'
beginLedgerRun()

export default defineConfig({
  testDir: './e2e',
  testMatch: ['harness-collection.spec.ts', 'refresh-filters.spec.ts'],
  timeout: 30_000,
  expect: { timeout: 10_000 },
  workers: 1,
  reporter: 'list',
  globalTeardown: './tests/setup/process-ledger-global-playwright.ts',
})
