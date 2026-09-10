import { describe, expect, it } from 'vitest'
import { getAllProviders, providerDisplayName } from '../src/providers/index.js'

describe('providerDisplayName', () => {
  it('matches every loaded Provider.displayName', async () => {
    const loaded = await getAllProviders()
    expect(loaded.length).toBeGreaterThan(20)
    for (const provider of loaded) {
      expect(providerDisplayName(provider.name)).toBe(provider.displayName)
    }
  })
})
