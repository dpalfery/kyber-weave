import { defineConfig } from '@playwright/test'
import { fileURLToPath } from 'node:url'

import { beginLedgerRun } from './tests/setup/process-ledger-global.js'

beginLedgerRun()

const dashRoot = fileURLToPath(new URL('.', import.meta.url))

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  expect: { timeout: 10_000 },
  workers: 1,
  globalTeardown: './tests/setup/process-ledger-global-playwright.ts',
  reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:4747' },
  webServer: {
    // Serves the built Vite SPA through the C# kyberdash engine over /api/kyber/*.
    command: 'npm run build:web && dotnet run --project ./server/KyberDash.Cli -c Release -- web --no-open --port 4747',
    cwd: dashRoot,
    url: 'http://127.0.0.1:4747',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
})
