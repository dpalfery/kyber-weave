// Boot an isolated dashboard against the harness-collection temp DB. Prints HC_READY <url>.

import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { bootIsolatedDashboard } from './boot-dashboard.js'
import { refreshTempCanon, USER_CANON } from './harness-collection-world.js'

// `build:web` writes the built SPA to `<dash>/dist/dash`, and `web.ts` reads
// the override from `KYBERDASH_DASH_DIR` (see `resolveStaticSource`). An
// earlier spelling pointed at `dash/dash/dist` and set a dead `CODEBURN_*`
// name, so the server silently fell back to the embedded asset and this
// override was never exercised.
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
  // The fixture seeds the derived session/run/execution rows directly and has no
  // `records` rows to project from. The JobHost's first tick runs the maintenance
  // pass, finds the projection stale, and rebuilds from that empty table, which
  // deletes every seeded row. This boot exists to test rendering, so the pass is off.
  maintenance: async () => {},
  period: 'all',
  provider: 'all',
  project: [],
  exclude: [],
  port: 0,
})

process.stdout.write(`HC_READY http://127.0.0.1:${booted.port}\n`)
process.stdout.write(`HC_DB ${world.dbPath}\n`)
