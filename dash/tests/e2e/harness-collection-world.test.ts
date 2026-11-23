import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  cleanupIsolatedTempRoot,
  createTempRoot,
  isIsolatedTempRoot,
} from '../../e2e/harness-collection-world.js'

describe('harness-collection-world isolated temp root containment (Issue 1)', () => {
  let sandboxRoot: string
  let fakeTmpDir: string
  let siblingTmpDir: string
  let outsideDir: string

  beforeEach(() => {
    sandboxRoot = realpathSync(mkdtempSync(join(tmpdir(), 'kw-world-test-')))
    fakeTmpDir = join(sandboxRoot, 'tmp')
    siblingTmpDir = join(sandboxRoot, 'tmp-evil')
    outsideDir = join(sandboxRoot, 'outside')
    mkdirSync(fakeTmpDir, { recursive: true })
    mkdirSync(siblingTmpDir, { recursive: true })
    mkdirSync(outsideDir, { recursive: true })
  })

  afterEach(() => {
    rmSync(sandboxRoot, { recursive: true, force: true })
  })

  it('rejects sibling directory sharing tmpdir prefix without separator', () => {
    const siblingSecret = join(siblingTmpDir, 'important.txt')
    writeFileSync(siblingSecret, 'keep me', 'utf8')

    expect(isIsolatedTempRoot(siblingTmpDir, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(siblingTmpDir, fakeTmpDir)).toThrow()
    expect(existsSync(siblingSecret)).toBe(true)
  })

  it('rejects deleting the tmp directory itself or its parent via traversal', () => {
    const childInsideTmp = join(fakeTmpDir, 'keep-me')
    mkdirSync(childInsideTmp, { recursive: true })

    expect(isIsolatedTempRoot(fakeTmpDir, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(fakeTmpDir, fakeTmpDir)).toThrow()
    expect(existsSync(childInsideTmp)).toBe(true)

    const traversalToSibling = join(fakeTmpDir, '..', 'tmp-evil')
    expect(isIsolatedTempRoot(traversalToSibling, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(traversalToSibling, fakeTmpDir)).toThrow()
    expect(existsSync(siblingTmpDir)).toBe(true)
  })

  it('rejects paths inside tmpdir containing .kyberdash or symlinks escaping tmpdir', () => {
    const kyberdashDir = join(fakeTmpDir, '.kyberdash', 'nested')
    mkdirSync(kyberdashDir, { recursive: true })
    expect(isIsolatedTempRoot(kyberdashDir, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(kyberdashDir, fakeTmpDir)).toThrow()
    expect(existsSync(kyberdashDir)).toBe(true)

    const outsideSecretDir = join(outsideDir, 'nested')
    mkdirSync(outsideSecretDir, { recursive: true })
    const escapeLink = join(fakeTmpDir, 'escape-link')
    symlinkSync(outsideDir, escapeLink)

    expect(isIsolatedTempRoot(escapeLink, fakeTmpDir)).toBe(false)
    expect(isIsolatedTempRoot(join(escapeLink, 'nested'), fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(escapeLink, fakeTmpDir)).toThrow()
    expect(() => cleanupIsolatedTempRoot(join(escapeLink, 'nested'), fakeTmpDir)).toThrow()
    expect(existsSync(outsideSecretDir)).toBe(true)
  })

  it('allows and cleans up a genuine isolated directory strictly inside tmpdir', () => {
    const created = createTempRoot(fakeTmpDir)
    expect(resolve(created).startsWith(`${resolve(fakeTmpDir)}/`) || process.platform === 'win32').toBe(true)
    expect(isIsolatedTempRoot(created, fakeTmpDir)).toBe(true)
    writeFileSync(join(created, 'marker.txt'), 'ephemeral', 'utf8')

    cleanupIsolatedTempRoot(created, fakeTmpDir)
    expect(existsSync(created)).toBe(false)
  })
})
