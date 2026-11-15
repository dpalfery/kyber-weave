import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { nodeFs } from './node-deps.js'

// Passthrough wrappers so one test can make the cleanup step fail.
vi.mock('node:fs/promises', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs/promises')>()
  return { ...actual, rmdir: vi.fn(actual.rmdir) }
})

describe('nodeFs.trash', () => {
  let home: string

  beforeEach(async () => {
    home = await mkdtemp(join(tmpdir(), 'kd-trash-'))
  })

  afterEach(async () => {
    vi.restoreAllMocks()
    await rm(home, { recursive: true, force: true })
  })

  async function makeApp(name: string, marker: string): Promise<string> {
    const app = join(home, name)
    await mkdir(app, { recursive: true })
    await writeFile(join(app, 'marker'), marker)
    return app
  }

  it('creates the Trash and keeps the .app extension', async () => {
    const app = await makeApp('KyberDash.app', 'one')
    const moved = await nodeFs.trash(app, home)
    expect(moved.startsWith(join(home, '.Trash'))).toBe(true)
    expect(moved.endsWith('.app')).toBe(true)
    expect(await readFile(join(moved, 'marker'), 'utf-8')).toBe('one')
  })

  it('never overwrites an earlier Trash copy, even within the same millisecond', async () => {
    vi.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000)
    const first = await nodeFs.trash(await makeApp('KyberDash.app', 'one'), home)
    const second = await nodeFs.trash(await makeApp('KyberDash.app', 'two'), home)
    expect(second).not.toBe(first)
    expect(await readFile(join(first, 'marker'), 'utf-8')).toBe('one')
    expect(await readFile(join(second, 'marker'), 'utf-8')).toBe('two')
  })

  it('leaves no placeholder behind when the move fails', async () => {
    await expect(nodeFs.trash(join(home, 'missing.app'), home)).rejects.toThrow()
    expect(await readdir(join(home, '.Trash'))).toEqual([])
  })

  it('reports a placeholder it cannot remove instead of swallowing the error', async () => {
    const fsp = await import('node:fs/promises')
    vi.mocked(fsp.rmdir).mockRejectedValueOnce(
      Object.assign(new Error('EACCES: denied'), { code: 'EACCES' }),
    )
    // A source that is not there makes the real rename fail.
    await expect(nodeFs.trash(join(home, 'gone.app'), home)).rejects.toThrow(
      /ENOENT.*could not be removed.*EACCES/,
    )
  })

  it('refuses an empty home', async () => {
    await expect(nodeFs.trash(join(home, 'x.app'), '')).rejects.toThrow(/no home/)
  })
})
