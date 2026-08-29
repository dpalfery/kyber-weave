// Static guard: every PROVIDER_ENV_VARS entry must be CLEARED or REDIRECTED
// by tests/setup/env-isolation.ts. A data-dir override that is fingerprinted
// for cache invalidation but not isolated in tests leaks the developer's real
// sessions into fixture parses — green on CI (no HERMES_HOME), red on a
// Hermes-shell laptop. The named hole was HERMES_HOME; the class is every
// sibling override that session-cache already knows about.
//
// The lists are imported from the same module applyIsolation() uses. Extra
// High #1064: scraping setup-file source treated a comment containing
// `'HERMES_HOME'` as isolation (false-green). Runtime membership cannot.
import { describe, expect, it } from 'vitest'

import { PROVIDER_ENV_VARS } from '../src/session-cache.js'
import { CLEARED, REDIRECTED } from './setup/env-isolation-vars.js'

describe('env-isolation covers PROVIDER_ENV_VARS', () => {
  it('clears or redirects every provider data-dir override so a developer shell cannot leak real sessions into fixtures', () => {
    const isolated = new Set<string>([...CLEARED, ...REDIRECTED])

    const missing: string[] = []
    for (const [provider, vars] of Object.entries(PROVIDER_ENV_VARS)) {
      for (const varName of vars) {
        if (!isolated.has(varName)) missing.push(`${provider}:${varName}`)
      }
    }

    expect(missing).toEqual([])
  })
})
