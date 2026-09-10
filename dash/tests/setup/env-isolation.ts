// Vitest setup file: isolates every test from the developer's shell environment.
//
// codeburn discovers sessions through a long list of provider-specific env
// vars (CLAUDE_CONFIG_DIR, CODEX_HOME, HERMES_HOME, CRUSH_GLOBAL_DATA, …) and
// via HOME / XDG_* / APPDATA / LOCALAPPDATA. Without this file, any value set
// in the developer's shell (e.g. HERMES_HOME=/Users/me/.hermes) bleeds into
// fixture-based tests: the parser reads the developer's REAL sessions instead
// of the temp-dir fixture, producing nonsense totals and false failures that
// pass on a clean CI runner. tests/env-isolation-declarations.test.ts fails
// closed if PROVIDER_ENV_VARS grows a data-dir override that is not listed.
//
// What this file does:
//   1. Mints an empty sandbox temp dir once per worker.
//   2. REDIRECTED vars (HOME / XDG_* / APPDATA / LOCALAPPDATA) point at the
//      sandbox so any fallback to homedir() / platform defaults lands in an
//      empty filesystem.
//   3. CLEARED vars (every provider's explicit override) are deleted so a test
//      that does NOT set one gets "unconfigured" rather than the dev's value.
//   4. PRESERVED vars (PATH, COLUMNS, …) are snapshotted from the dev's shell
//      and restored every test. We can't wipe them - Node uses PATH for spawn
//      and module resolution, terminal code uses COLUMNS - but a test that
//      mutates them shouldn't leak the change into the next test.
//   5. Re-asserts the above before EVERY test (global beforeEach), so a test
//      that mutates an env var doesn't leak its value into the next test.
//      Tests can freely set process.env['HOME'] = customDir without saving the
//      previous value - the next test gets a fresh sandbox baseline.
//
// CAVEAT: env vars set in a test file's beforeAll() get overwritten by this
// file's beforeEach before each test runs. Use beforeEach (not beforeAll) when
// the test body depends on a specific env var value.

import { mkdtempSync } from 'fs'
import { tmpdir } from 'os'
import { join } from 'path'
import { beforeEach } from 'vitest'

import { CLEARED, PRESERVED, REDIRECTED } from './env-isolation-vars.js'

const sandbox = mkdtempSync(join(tmpdir(), 'codeburn-test-env-'))

const preservedSnapshot = new Map<string, string | undefined>()
for (const key of PRESERVED) preservedSnapshot.set(key, process.env[key])

function applyIsolation(): void {
  for (const key of REDIRECTED) process.env[key] = sandbox
  for (const key of CLEARED) delete process.env[key]
  for (const key of PRESERVED) {
    const original = preservedSnapshot.get(key)
    if (original === undefined) delete process.env[key]
    else process.env[key] = original
  }
  // Price off the bundled LiteLLM snapshot only. Without this, loadPricing()
  // fetches the live upstream table, so a mid-week reprice by a provider turns
  // pricing assertions red with no local change.
  process.env['CODEBURN_PRICING_SNAPSHOT_ONLY'] = '1'
  // Same for FX: skip the live Frankfurter fetch and fall back to the
  // USD-equivalent rate unless a test seeds its own exchange-rate.json cache.
  process.env['CODEBURN_FX_NO_FETCH'] = '1'
  // Pin the timezone so date grouping is deterministic regardless of the dev's
  // shell TZ. Clearing it is not enough (Node falls back to the OS zone); a
  // non-UTC TZ would otherwise shift day buckets versus a clean CI runner. A
  // test that needs a specific zone can still set process.env.TZ in beforeEach.
  process.env.TZ = 'UTC'
}

applyIsolation()
beforeEach(applyIsolation)
