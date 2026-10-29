// Boot an isolated dashboard against the harness-collection temp DB. Prints HC_READY <url>.

import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { runWebDashboard } from '../src/cli/web.js'
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
process.env.KYBER_CANON_DB = world.dbPath

const server = await runWebDashboard({
  // Keep every server artefact (server.json, jobs.lock) beside the temp store
  // instead of in the operator's home, so the run is isolated end to end.
  stateDir: world.root,
  // The fixture seeds the derived session/run/execution rows directly and has no
  // `records` rows to project from. The JobHost's first tick runs the maintenance
  // pass, finds the projection stale, and rebuilds from that empty table — which
  // deletes every seeded row. A run with a fresh `$HOME` therefore saw an empty
  // dashboard and failed, while a run on a machine whose ~/.kyberdash state made
  // the pass a no-op passed. This boot exists to test rendering, so the pass is
  // off here.
  maintenance: async () => {},
  period: 'all',
  provider: 'all',
  project: [],
  exclude: [],
  port: 0,
  open: false,
})

const address = server.address()
if (address === null || typeof address === 'string') {
  throw new Error('dashboard did not bind a TCP port')
}
process.stdout.write(`HC_READY http://127.0.0.1:${address.port}\n`)
process.stdout.write(`HC_DB ${world.dbPath}\n`)
