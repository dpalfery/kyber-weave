import { existsSync } from 'fs'
import { writeFile } from 'fs/promises'
import { join } from 'path'

import { saveStatusSnapshot } from '../../src/session-cache.js'

const [cacheDir, barriers, role, roundsRaw] = process.argv.slice(2)
if (!cacheDir || !barriers || (role !== 'fresh' && role !== 'stale')) {
  throw new Error('usage: status-snapshot-writer <cacheDir> <barriers> <fresh|stale> <rounds>')
}
const rounds = Number(roundsRaw)
if (!Number.isInteger(rounds) || rounds < 1) throw new Error('rounds must be a positive integer')

process.env['CODEBURN_CACHE_DIR'] = cacheDir
const semanticKey = 'child-process-render-v1'
const delay = (ms: number): Promise<void> => new Promise(resolve => { setTimeout(resolve, ms) })

for (let round = 0; round < rounds; round++) {
  await writeFile(join(barriers, `${role}.${round}.ready`), '')
  const go = join(barriers, `${round}.go`)
  while (!existsSync(go)) await delay(2)

  const fresh = role === 'fresh'
  const published = await saveStatusSnapshot(
    `${role}-${round}`,
    fresh ? 3_000 : 2_000,
    fresh ? 3_000 : 2_000,
    `child-query-${round}`,
    semanticKey,
    { role, round },
  )
  await writeFile(join(barriers, `${role}.${round}.done`), String(published))
}
