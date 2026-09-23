// One source tree, with no `kyber/` left in it (R3.1, R3.2).
//
// `dash/kyber/` was the merge zone: the only directory the soft fork let KyberDash
// write, kept apart so a `git subtree pull` from upstream could never conflict with
// it. The one-time fork (ADR 0020) removed the reason for the split, and the two
// trees merged into `dash/src/`.
//
// The name is worth a test rather than a note, because reintroducing it is the
// cheap move: a contributor wanting somewhere to put "our" code next to "theirs"
// would recreate exactly that directory, and the distinction it encodes is one
// this repository no longer makes.
//
// What is checked is the tree and its import graph. The `/api/kyber/*` REST prefix
// is a URL, not a path, and stays — the design names it as the tray's and the web
// dashboard's one data contract.

import { readFileSync, readdirSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const DASH_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const SKIP_DIRS = new Set(['node_modules', 'dist', 'dist-sea', 'build', '.git', 'target'])
const SOURCE_EXT = new Set(['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs'])

function walk(dir: string, onDir: (p: string) => void, onFile: (p: string) => void): void {
  let entries: string[]
  try {
    entries = readdirSync(dir)
  } catch {
    return
  }
  for (const name of entries) {
    if (SKIP_DIRS.has(name) || name.startsWith('.')) continue
    const full = path.join(dir, name)
    if (statSync(full).isDirectory()) {
      onDir(full)
      walk(full, onDir, onFile)
    } else {
      onFile(full)
    }
  }
}

function collect(): { dirs: string[]; files: string[] } {
  const dirs: string[] = []
  const files: string[] = []
  walk(DASH_ROOT, d => dirs.push(d), f => files.push(f))
  return { dirs, files }
}

const rel = (p: string) => path.relative(DASH_ROOT, p).split(path.sep).join('/')

describe('source layout (R3.1, R3.2)', () => {
  const { dirs, files } = collect()

  it('walks a non-empty tree', () => {
    expect(files.length).toBeGreaterThan(200)
  })

  it('has no directory named kyber', () => {
    const offenders = dirs.filter(d => path.basename(d) === 'kyber').map(rel)
    expect(offenders).toEqual([])
  })

  it('has no import path containing a kyber/ segment', () => {
    const offenders: string[] = []
    for (const file of files) {
      if (!SOURCE_EXT.has(path.extname(file))) continue
      const info = ts.preProcessFile(readFileSync(file, 'utf8'), true, true)
      for (const imported of info.importedFiles) {
        if (/(^|\/)kyber\//.test(imported.fileName)) {
          offenders.push(`${rel(file)} imports '${imported.fileName}'`)
        }
      }
    }
    expect(offenders).toEqual([])
  })

  it('keeps the engine directories the requirement names', () => {
    for (const name of ['providers', 'canon', 'analysis', 'otel', 'refresh', 'server', 'cli']) {
      expect(statSync(path.join(DASH_ROOT, 'src', name)).isDirectory()).toBe(true)
    }
  })

  it('keeps the web dashboard and the tray at their own roots', () => {
    expect(statSync(path.join(DASH_ROOT, 'web')).isDirectory()).toBe(true)
    expect(statSync(path.join(DASH_ROOT, 'tray')).isDirectory()).toBe(true)
  })

  it('fails on an import that reaches back into a kyber tree', () => {
    const info = ts.preProcessFile("import { CanonStore } from '../kyber/canon/store.js'", true, true)
    expect(info.importedFiles.some(f => /(^|\/)kyber\//.test(f.fileName))).toBe(true)
  })

  it('leaves the /api/kyber REST prefix alone', () => {
    const info = ts.preProcessFile("const url = '/api/kyber/report'", true, true)
    expect(info.importedFiles).toHaveLength(0)
  })
})
