import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const FIXTURE_DIR = dirname(fileURLToPath(import.meta.url))

export function readModelCatalogFixture(name: string): unknown {
  const raw = readFileSync(join(FIXTURE_DIR, name), 'utf8')
  return JSON.parse(raw) as unknown
}
