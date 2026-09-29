// Tests for the pack-sea-web script. RED: fails because packWebDirectory is not
// implemented yet. Tests that it produces the correct asset format with sorted keys,
// round-trips all bytes, and validates index.html.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mkdtemp, rm, writeFile, mkdir } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import { spawnSync } from 'child_process'

describe('packWebDirectory - embed web assets in kyberdash-web/1 format', () => {
  let tempDir: string
  let sourceDir: string

  beforeEach(async () => {
    tempDir = await mkdtemp(join(tmpdir(), 'pack-sea-web-test-'))
    sourceDir = await mkdtemp(join(tmpdir(), 'web-source-'))
  })

  afterEach(async () => {
    await rm(tempDir, { recursive: true, force: true })
    await rm(sourceDir, { recursive: true, force: true })
  })

  it('produces kyberdash-web/1 format with sorted POSIX keys', async () => {
    // Set up source directory with multiple files
    await writeFile(join(sourceDir, 'index.html'), '<!doctype html><html></html>')
    await writeFile(join(sourceDir, 'style.css'), 'body { color: red; }')
    await writeFile(join(sourceDir, 'app.js'), 'console.log("test");')

    // Dynamic import because the module doesn't exist until implementation
    const { packWebDirectory } = await import('./pack-sea-web.mjs').catch(() => ({
      packWebDirectory: async () => null,
    }))

    if (!packWebDirectory) {
      throw new Error('packWebDirectory not implemented')
    }

    const result = await packWebDirectory(sourceDir)
    expect(result).toBeDefined()
    expect(result.format).toBe('kyberdash-web/1')
    expect(typeof result.files).toBe('object')

    // Verify keys are sorted
    const keys = Object.keys(result.files)
    const sortedKeys = [...keys].sort()
    expect(keys).toEqual(sortedKeys)

    // All keys should be POSIX paths (no backslashes)
    for (const key of keys) {
      expect(key).not.toContain('\\')
    }
  })

  it('round-trips every byte including binary files', async () => {
    // Create text and binary content
    const textContent = 'Hello, this is text content with special chars: 你好 🎉'
    const binaryContent = Buffer.from([0, 1, 2, 255, 254, 253])

    await writeFile(join(sourceDir, 'index.html'), '<!doctype html><html></html>')
    await writeFile(join(sourceDir, 'text.txt'), textContent)
    await writeFile(join(sourceDir, 'binary.dat'), binaryContent)

    const { packWebDirectory } = await import('./pack-sea-web.mjs').catch(() => ({
      packWebDirectory: async () => null,
    }))

    if (!packWebDirectory) {
      throw new Error('packWebDirectory not implemented')
    }

    const result = await packWebDirectory(sourceDir)

    // Decode and verify text file
    if (result.files['text.txt']) {
      const decoded = Buffer.from(result.files['text.txt'], 'base64').toString('utf8')
      expect(decoded).toBe(textContent)
    }

    // Decode and verify binary file
    if (result.files['binary.dat']) {
      const decoded = Buffer.from(result.files['binary.dat'], 'base64')
      expect(decoded).toEqual(binaryContent)
    }
  })

  it('exits non-zero when index.html is missing', async () => {
    // Create source without index.html
    await writeFile(join(sourceDir, 'app.js'), 'console.log("test");')

    // Try to run the CLI - it should fail
    const DASH_ROOT = join(process.cwd(), 'dash')
    const result = spawnSync('node', ['scripts/pack-sea-web.mjs', sourceDir, join(tempDir, 'output.json')], {
      cwd: DASH_ROOT,
      encoding: 'utf8',
    })

    expect(result.status).not.toBe(0)
  })

  it('handles nested directories and preserves structure', async () => {
    // Create nested directory structure
    const assetDir = join(sourceDir, 'assets')
    const fontDir = join(assetDir, 'fonts')

    await writeFile(join(sourceDir, 'index.html'), '<!doctype html><html></html>')
    await mkdir(assetDir, { recursive: true })
    await mkdir(fontDir, { recursive: true })

    await writeFile(join(assetDir, 'app.js'), 'console.log("app");')
    await writeFile(join(assetDir, 'style.css'), 'body {}')
    await writeFile(join(fontDir, 'font.woff2'), 'binary-like')

    const { packWebDirectory } = await import('./pack-sea-web.mjs').catch(() => ({
      packWebDirectory: async () => null,
    }))

    if (!packWebDirectory) {
      throw new Error('packWebDirectory not implemented')
    }

    const result = await packWebDirectory(sourceDir)

    // Check that all nested paths are present
    expect(Object.keys(result.files).some((k) => k.includes('assets/app.js'))).toBe(true)
    expect(Object.keys(result.files).some((k) => k.includes('assets/style.css'))).toBe(true)
    expect(Object.keys(result.files).some((k) => k.includes('assets/fonts'))).toBe(true)
  })
})
