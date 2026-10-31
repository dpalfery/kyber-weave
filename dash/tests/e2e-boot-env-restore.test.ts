// Storm guard follow-up: bootIsolatedDashboard points KYBER_CANON_DB at the world's temp
// canon.db for the life of the boot. A vitest worker is reused across test files, so the
// override must not outlive the handle: a leaked path would aim the next test at a deleted
// temp DB.
import { rmSync } from 'node:fs'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { bootIsolatedDashboard } from '../e2e/boot-dashboard.js'
import { createTempRoot, refreshTempCanon } from '../e2e/harness-collection-world.js'

const KEY = 'KYBER_CANON_DB'
const createdRoots: string[] = []
let original: string | undefined

beforeEach(() => {
  original = process.env[KEY]
})

afterEach(() => {
  if (original === undefined) delete process.env[KEY]
  else process.env[KEY] = original
  for (const root of createdRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

async function bootAndClose(): Promise<void> {
  const root = createTempRoot()
  createdRoots.push(root)
  const world = await refreshTempCanon(root)
  world.store.close()
  const handle = await bootIsolatedDashboard({
    root: world.root,
    canonDbPath: world.dbPath,
    port: 0,
    period: 'all',
    provider: 'all',
    project: [],
    exclude: [],
    maintenance: async () => {},
  })
  expect(process.env[KEY], 'the boot must point at its own canon.db while running').toBe(world.dbPath)
  await handle.close()
}

describe('bootIsolatedDashboard restores KYBER_CANON_DB', () => {
  it('restores a previously set value after close', async () => {
    process.env[KEY] = '/previous/canon.db'
    await bootAndClose()
    expect(process.env[KEY]).toBe('/previous/canon.db')
  })

  it('leaves the variable unset after close when it was unset before', async () => {
    delete process.env[KEY]
    await bootAndClose()
    expect(KEY in process.env).toBe(false)
  })

  it('restores the value when the boot fails', async () => {
    process.env[KEY] = '/previous/canon.db'
    const root = createTempRoot()
    createdRoots.push(root)
    await expect(
      bootIsolatedDashboard({
        root,
        canonDbPath: '/nonexistent-dir/never/canon.db',
        port: -1,
        period: 'all',
        provider: 'all',
        project: [],
        exclude: [],
      }),
    ).rejects.toBeDefined()
    expect(process.env[KEY]).toBe('/previous/canon.db')
  })
})
