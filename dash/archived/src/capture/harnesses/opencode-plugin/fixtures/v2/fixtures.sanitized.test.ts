// Guard for the OpenCode V2 fixtures: every captured payload is sanitized before it is committed.
// It fails when a data file carries a credential, a header map, or a personal path. It reads only
// the fixture folder beside this file, so it never touches the developer's home directory.

import { readdirSync, readFileSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const FIXTURE_ROOT = path.dirname(fileURLToPath(import.meta.url))

/** Data files only. This test file is not scanned: it has to name the tokens it forbids. */
const DATA_EXTENSIONS = ['.json', '.jsonl', '.md']

/** Literal substrings no committed fixture may contain. */
const FORBIDDEN_SUBSTRINGS = [
  'Authorization',
  'Bearer',
  'api_key',
  'x-api-key',
  '/Users/',
  '@gmail.com',
  '/private/tmp/claude-501',
]

/**
 * `sk-` counts as a key prefix only at the start of a token. The captured system prompt lists skill
 * ids such as `task-gate`, which contain `sk-` mid-token; a plain substring match would reject real
 * captured data, so the guard matches the prefix form that an API key actually takes.
 */
const KEY_PREFIX = /(?<![A-Za-z0-9])sk-/

function dataFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const full = path.join(dir, entry)
    if (statSync(full).isDirectory()) return dataFiles(full)
    return DATA_EXTENSIONS.some((extension) => entry.endsWith(extension)) ? [full] : []
  })
}

/** Every JSON object key named `headers` (any case), with its location, found anywhere in the value. */
function headerMapKeys(value: unknown, location: string, found: string[]): void {
  if (Array.isArray(value)) {
    value.forEach((item, index) => headerMapKeys(item, `${location}[${index}]`, found))
    return
  }
  if (typeof value === 'object' && value !== null) {
    for (const [key, child] of Object.entries(value)) {
      if (key.toLowerCase() === 'headers') found.push(`${location}.${key}`)
      headerMapKeys(child, `${location}.${key}`, found)
    }
  }
}

function jsonRecords(file: string): unknown[] {
  const text = readFileSync(file, 'utf8')
  if (file.endsWith('.jsonl')) {
    return text
      .split('\n')
      .filter((line) => line.trim() !== '')
      .map((line) => JSON.parse(line) as unknown)
  }
  return [JSON.parse(text) as unknown]
}

describe('OpenCode V2 fixtures are sanitized', () => {
  const files = dataFiles(FIXTURE_ROOT)

  it('the guard finds the committed fixture data files', () => {
    expect(files.length, `no data files under ${FIXTURE_ROOT}`).toBeGreaterThanOrEqual(7)
  })

  it('no fixture carries a credential, a header name, or a personal path', () => {
    const offenders = files.flatMap((file) => {
      const text = readFileSync(file, 'utf8')
      const hits = FORBIDDEN_SUBSTRINGS.filter((token) => text.includes(token))
      if (KEY_PREFIX.test(text)) hits.push('sk- (token start)')
      return hits.map((token) => `${path.basename(file)} contains ${token}`)
    })
    expect(offenders, offenders.join('\n')).toEqual([])
  })

  it('no JSON fixture keeps a header map', () => {
    const offenders = files
      .filter((file) => file.endsWith('.json') || file.endsWith('.jsonl'))
      .flatMap((file) => {
        const found: string[] = []
        jsonRecords(file).forEach((record, index) => headerMapKeys(record, `${path.basename(file)}#${index}`, found))
        return found
      })
    expect(offenders, offenders.join('\n')).toEqual([])
  })
})
