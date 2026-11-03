// Boot an isolated dashboard against the Factory replay temp DB. Prints FR_READY <url>.

import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { bootIsolatedDashboard } from './boot-dashboard.js'
import { refreshFactoryTempCanon, USER_CANON } from './factory-replay-world.js'

// `build:web` writes the built SPA to `<dash>/dist/dash`, and `web.ts` reads the override from
// `KYBERDASH_DASH_DIR` (see `resolveStaticSource`).
const dashDist = join(dirname(fileURLToPath(import.meta.url)), '..', 'dist', 'dash')
process.env.KYBERDASH_DASH_DIR ??= dashDist

const world = await refreshFactoryTempCanon()
if (world.dbPath === USER_CANON) {
  throw new Error('refusing to serve the user canon.db')
}
world.store.close()
const booted = await bootIsolatedDashboard({
  root: world.root,
  canonDbPath: world.dbPath,
  // The replay projected its own rows before the boot; the JobHost's first maintenance pass would
  // rebuild them from the `records` table, which is what the replay already did.
  maintenance: async () => {},
  period: 'all',
  provider: 'all',
  project: [],
  exclude: [],
  port: 0,
})

process.stdout.write(`FR_READY http://127.0.0.1:${booted.port}\n`)
process.stdout.write(`FR_DB ${world.dbPath}\n`)
