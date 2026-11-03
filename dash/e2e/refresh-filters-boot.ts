// Boot an isolated dashboard against the T8 temp refreshed DB. Prints T8_READY <url>.

import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { bootIsolatedDashboard } from './boot-dashboard.js'
import { refreshTempCanon, USER_CANON } from './refresh-filters-world.js'

const dashDist = join(dirname(fileURLToPath(import.meta.url)), '..', 'dist', 'dash')
process.env.KYBERDASH_DASH_DIR ??= dashDist

const world = await refreshTempCanon()
if (world.dbPath === USER_CANON) {
  throw new Error('refusing to serve the user canon.db')
}
world.store.close()
const booted = await bootIsolatedDashboard({
  root: world.root,
  canonDbPath: world.dbPath,
  period: 'all',
  provider: 'all',
  project: [],
  exclude: [],
  port: 0,
})

process.stdout.write(`T8_READY http://127.0.0.1:${booted.port}\n`)
process.stdout.write(`T8_DB ${world.dbPath}\n`)
