import { defineConfig } from '@playwright/test'
import { fileURLToPath } from 'node:url'

const dashRoot = fileURLToPath(new URL('.', import.meta.url))

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  expect: { timeout: 10_000 },
  workers: 1,
  reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:4747' },
  webServer: {
    // The root dashboard command serves the built SPA and the real local API
    // together. Vite's frontend-only development server runs on 5173 and
    // cannot satisfy this gate without a separate API process.
    command: 'npm run build:dash && npm run dev -- web --no-open --port 4747',
    cwd: dashRoot,
    url: 'http://127.0.0.1:4747',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
})
