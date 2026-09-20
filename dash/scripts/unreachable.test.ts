import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

// @ts-expect-error -- plain ESM script without type declarations
import { findUnreachable, isTestOnly, isTestSupport } from './unreachable.mjs'

const dirs: string[] = []

function tree(files: Record<string, string>): string {
  const root = mkdtempSync(join(tmpdir(), 'unreachable-'))
  dirs.push(root)
  for (const [path, body] of Object.entries(files)) {
    const full = join(root, path)
    mkdirSync(join(full, '..'), { recursive: true })
    writeFileSync(full, body)
  }
  return root
}

afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

describe('findUnreachable', () => {
  it('reports a module no entry reaches and nothing that one does', async () => {
    const root = tree({
      'src/main.ts': "import { a } from './a.js'\nconsole.log(a)\n",
      'src/a.ts': "import { b } from './nested/b.js'\nexport const a = b\n",
      'src/nested/b.ts': 'export const b = 1\n',
      'src/orphan.ts': 'export const orphan = 1\n',
    })
    const unreachable = await findUnreachable({ root, entries: ['src/main.ts'], scanRoots: ['src'] })
    expect(unreachable).toEqual(['src/orphan.ts'])
  })

  it('never reports test-only files', async () => {
    const root = tree({
      'src/main.ts': 'export {}\n',
      'src/main.test.ts': "import './main.js'\n",
      'src/fixtures/seed.ts': 'export const seed = 1\n',
      'src/__snapshots__/x.ts': 'export {}\n',
    })
    expect(await findUnreachable({ root, entries: ['src/main.ts'], scanRoots: ['src'] })).toEqual([])
  })

  it('counts a module imported only for its types as reached', async () => {
    const root = tree({
      'src/main.ts': "import type { Shape } from './types.js'\nexport const s: Shape = { n: 1 }\n",
      'src/types.ts': 'export type Shape = { n: number }\n',
    })
    expect(await findUnreachable({ root, entries: ['src/main.ts'], scanRoots: ['src'] })).toEqual([])
  })

  it('follows dynamic imports and path aliases', async () => {
    const root = tree({
      'src/main.ts': "export const load = () => import('./lazy.js')\n",
      'src/lazy.ts': "import { w } from '@/widget'\nexport default w\n",
      'web/widget.tsx': 'export const w = 1\n',
    })
    expect(
      await findUnreachable({ root, entries: ['src/main.ts'], scanRoots: ['src', 'web'], aliases: { '@': 'web' } }),
    ).toEqual([])
  })

  it('treats a bare package import as external rather than failing', async () => {
    const root = tree({ 'src/main.ts': "import 'not-installed-anywhere'\n" })
    expect(await findUnreachable({ root, entries: ['src/main.ts'], scanRoots: ['src'] })).toEqual([])
  })
})

describe('isTestOnly', () => {
  it.each([
    ['src/a.test.ts', true],
    ['src/a.spec.tsx', true],
    ['src/types.d.ts', true],
    ['kyber/canon/fixtures/x.ts', true],
    ['src/a.ts', false],
    ['kyber/canon/adapters/testing.ts', true],
    ['src/test-harness.ts', false],
  ])('%s → %s', (path, expected) => {
    expect(isTestOnly(path)).toBe(expected)
  })
})

describe('the test-support pass', () => {
  // The production pass cannot see these files: its scan roots exclude tests/, and
  // isTestOnly filters out anything under fixtures/. That gap let a mock identity
  // provider outlive the sign-in feature it existed for.
  it('reports a fixture no test imports, and spares one a test does', async () => {
    const root = tree({
      'src/thing.test.ts': "import { help } from '../tests/fixtures/used.js'\nhelp()\n",
      'tests/fixtures/used.ts': 'export function help() {}\n',
      'tests/fixtures/orphan.ts': 'export function stale() {}\n',
    })
    const unreachable = await findUnreachable({
      root,
      entries: ['src/thing.test.ts'],
      scanRoots: ['src', 'tests'],
      subject: isTestSupport,
    })
    expect(unreachable).toEqual(['tests/fixtures/orphan.ts'])
  })

  it('does not report the test files themselves, which are the entries', async () => {
    const root = tree({
      'src/a.test.ts': 'export {}\n',
      'tests/fixtures/helper.ts': 'export const h = 1\n',
    })
    const unreachable = await findUnreachable({
      root,
      entries: ['src/a.test.ts'],
      scanRoots: ['src', 'tests'],
      subject: isTestSupport,
    })
    expect(unreachable).toEqual(['tests/fixtures/helper.ts'])
    expect(unreachable).not.toContain('src/a.test.ts')
  })

  it('reports a fixture spawned by a path string unless it is declared an entry', async () => {
    // preProcessFile reads specifiers, not string literals, so a worker spawned by path is
    // invisible to the walk. This is why TEST_ENTRIES exists and has to name each one.
    const files = {
      'src/spawn.test.ts': "import { spawn } from 'node:child_process'\nspawn('node', ['tests/fixtures/worker.ts'])\n",
      'tests/fixtures/worker.ts': 'process.exit(0)\n',
    }
    const root = tree(files)
    const opts = { root, scanRoots: ['src', 'tests'], subject: isTestSupport }

    expect(await findUnreachable({ ...opts, entries: ['src/spawn.test.ts'] })).toEqual([
      'tests/fixtures/worker.ts',
    ])
    expect(
      await findUnreachable({ ...opts, entries: ['src/spawn.test.ts', 'tests/fixtures/worker.ts'] }),
    ).toEqual([])
  })
})

describe('isTestSupport', () => {
  it('claims fixtures, test kits and setup', () => {
    expect(isTestSupport('tests/fixtures/mock-idp.ts')).toBe(true)
    expect(isTestSupport('src/canon/adapters/testing.ts')).toBe(true)
    expect(isTestSupport('tests/setup/env-isolation.ts')).toBe(true)
    expect(isTestSupport('src/__fixtures__/seed.ts')).toBe(true)
  })

  it('does not claim tests, declarations or ordinary source', () => {
    expect(isTestSupport('tests/fixtures/thing.test.ts')).toBe(false)
    expect(isTestSupport('src/ingest/parser.test.ts')).toBe(false)
    expect(isTestSupport('src/types.d.ts')).toBe(false)
    expect(isTestSupport('src/ingest/parser.ts')).toBe(false)
  })
})
