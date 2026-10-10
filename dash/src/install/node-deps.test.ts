/**
 * Contract test for T1: `nodeInstallDeps().kyberdashPath` resolves to an absolute path,
 * returning `process.execPath` when running as a SEA, and resolving an absolute path argument
 * when not.
 *
 * Prevents regression: a bare argv[1] (e.g., 'kyberdash') or relative argv[1] (e.g., './kyberdash')
 * must not be recorded as the SEA CLI path. Also ensures `isSea()` is read from `node:sea` module,
 * not from `process`.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { isAbsolute } from 'node:path'

import { isSea } from 'node:sea'

import { nodeInstallDeps } from './node-deps.js'

/**
 * Mock the node:sea builtin module. The real implementation will import
 * `isSea` from `node:sea` and use it to determine if running as a SEA.
 */
vi.mock('node:sea', () => ({
  isSea: vi.fn(() => false),
  getAsset: vi.fn(),
}))

describe('nodeInstallDeps().kyberdashPath', () => {
  const originalArgv = process.argv.slice()

  beforeEach(() => {
    // Reset process.argv to a known state
    process.argv = originalArgv.slice()
  })

  afterEach(() => {
    // Restore process.argv after each test
    process.argv = originalArgv.slice()
    // Reset the isSea mock to its default
    vi.mocked(isSea).mockReset()
  })

  describe('case (a): isSea() true with argv[1] = "kyberdash"', () => {
    it('returns process.execPath (the absolute binary path)', () => {
      vi.mocked(isSea).mockReturnValue(true)

      process.argv = [process.execPath, 'kyberdash', 'menubar']

      const deps = nodeInstallDeps()

      expect(deps.kyberdashPath).toBe(process.execPath)
    })

    it('kyberdashPath is absolute', () => {
      vi.mocked(isSea).mockReturnValue(true)

      process.argv = [process.execPath, 'kyberdash', 'menubar']

      const deps = nodeInstallDeps()

      expect(isAbsolute(deps.kyberdashPath)).toBe(true)
    })
  })

  describe('case (b): isSea() true with argv[1] = "./kyberdash"', () => {
    it('returns process.execPath (the absolute binary path)', () => {
      vi.mocked(isSea).mockReturnValue(true)

      process.argv = [process.execPath, './kyberdash', 'menubar']

      const deps = nodeInstallDeps()

      expect(deps.kyberdashPath).toBe(process.execPath)
    })

    it('kyberdashPath is absolute', () => {
      vi.mocked(isSea).mockReturnValue(true)

      process.argv = [process.execPath, './kyberdash', 'menubar']

      const deps = nodeInstallDeps()

      expect(isAbsolute(deps.kyberdashPath)).toBe(true)
    })
  })

  describe('case (c): isSea() false with an absolute argv[1]', () => {
    it('resolves and returns that absolute path', () => {
      vi.mocked(isSea).mockReturnValue(false)

      const absolutePath = '/absolute/path/to/kyberdash'
      process.argv = [process.execPath, absolutePath, 'menubar']

      const deps = nodeInstallDeps()

      expect(deps.kyberdashPath).toBe(absolutePath)
    })

    it('kyberdashPath is absolute', () => {
      vi.mocked(isSea).mockReturnValue(false)

      const absolutePath = '/absolute/path/to/kyberdash'
      process.argv = [process.execPath, absolutePath, 'menubar']

      const deps = nodeInstallDeps()

      expect(isAbsolute(deps.kyberdashPath)).toBe(true)
    })
  })

  describe('case (d): kyberdashPath is absolute in every case', () => {
    it('is absolute when isSea() is true and argv[1] is "kyberdash"', () => {
      vi.mocked(isSea).mockReturnValue(true)

      process.argv = [process.execPath, 'kyberdash']

      const deps = nodeInstallDeps()

      expect(isAbsolute(deps.kyberdashPath)).toBe(true)
    })

    it('is absolute when isSea() is true and argv[1] is "./kyberdash"', () => {
      vi.mocked(isSea).mockReturnValue(true)

      process.argv = [process.execPath, './kyberdash']

      const deps = nodeInstallDeps()

      expect(isAbsolute(deps.kyberdashPath)).toBe(true)
    })

    it('is absolute when isSea() is false and argv[1] is absolute', () => {
      vi.mocked(isSea).mockReturnValue(false)

      process.argv = [process.execPath, '/absolute/path/to/kyberdash']

      const deps = nodeInstallDeps()

      expect(isAbsolute(deps.kyberdashPath)).toBe(true)
    })

    it('is absolute when argv[1] is missing and falls back to process.execPath', () => {
      vi.mocked(isSea).mockReturnValue(false)

      process.argv = [process.execPath]

      const deps = nodeInstallDeps()

      expect(isAbsolute(deps.kyberdashPath)).toBe(true)
    })
  })
})
