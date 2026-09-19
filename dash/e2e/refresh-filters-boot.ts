// Boot an isolated dashboard against the T8 temp refreshed DB. Prints T8_READY <url>.

import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { runWebDashboard } from '../src/cli/web.js'
import { refreshTempCanon, USER_CANON } from './refresh-filters-world.js'

const dashDist = join(dirname(fileURLToPath(import.meta.url)), '..', 'dash', 'dist')
process.env.CODEBURN_DASH_DIR ??= dashDist

const world = await refreshTempCanon()
if (world.dbPath === USER_CANON) {
  throw new Error('refusing to serve the user canon.db')
}
world.store.close()
process.env.KYBER_CANON_DB = world.dbPath

const server = await runWebDashboard({
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
process.stdout.write(`T8_READY http://127.0.0.1:${address.port}\n`)
process.stdout.write(`T8_DB ${world.dbPath}\n`)
