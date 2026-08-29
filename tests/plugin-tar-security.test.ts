// Independent review finding: tar entry validation was blind to symlinks
// (tar -tzf hides "-> target"), so a symlink could reach extraction before
// the post-extract symlink check runs. Reject non-regular entries up front.
import { describe, it, expect } from 'vitest'
import { mkdtemp, rm, mkdir, writeFile, symlink } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'
import { execFile } from 'child_process'
import { promisify } from 'util'
import { validateTarEntries } from '../src/plugins/cli.js'

const run = promisify(execFile)

describe('tarball entry-type validation', () => {
  it('rejects a tarball containing a symlink entry', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'tarsec-'))
    try {
      const build = join(dir, 'build')
      await mkdir(build, { recursive: true })
      await writeFile(join(build, 'ok.txt'), 'fine')
      await symlink('/etc/hosts', join(build, 'evil-link'))
      const tarFile = join(dir, 'evil.tgz')
      await run('tar', ['-czf', tarFile, '-C', build, 'ok.txt', 'evil-link'])
      await expect(validateTarEntries(tarFile)).rejects.toThrow(/non-regular entry/)
    } finally {
      await rm(dir, { recursive: true, force: true })
    }
  })

  it('accepts a tarball of plain files and directories', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'tarsec2-'))
    try {
      const build = join(dir, 'build')
      await mkdir(join(build, 'sub'), { recursive: true })
      await writeFile(join(build, 'a.txt'), 'a')
      await writeFile(join(build, 'sub', 'b.txt'), 'b')
      const tarFile = join(dir, 'good.tgz')
      await run('tar', ['-czf', tarFile, '-C', build, '.'])
      await expect(validateTarEntries(tarFile)).resolves.toBeUndefined()
    } finally {
      await rm(dir, { recursive: true, force: true })
    }
  })
})
