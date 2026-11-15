// Config for the isolated self-booting spec (harness-collection): boots its own C# kyberdash
// instance in beforeAll, so there is no webServer here. One worker, so only one boot is alive.
import { defineConfig } from '@playwright/test'

import { beginLedgerRun } from './tests/setup/process-ledger-global.js'

beginLedgerRun()

export default defineConfig({
  testDir: './e2e',
  testMatch: ['harness-collection.spec.ts'],
  timeout: 30_000,
  expect: { timeout: 10_000 },
  workers: 1,
  reporter: 'list',
  globalTeardown: './tests/setup/process-ledger-global-playwright.ts',
})
