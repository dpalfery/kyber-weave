import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Requirement 3.3 / 3.7: the package carries one KyberDash identity pointing
// at this repository, and no surface renders the upstream name. This file is
// the identity's guard: package metadata, the CLI's own --help, and the web
// dashboard's HTML template.

const DASH_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

const pkg = JSON.parse(readFileSync(path.join(DASH_ROOT, 'package.json'), 'utf8')) as {
  name?: string
  version?: string
  description?: string
  bin?: Record<string, string>
  author?: unknown
  repository?: { url?: string }
  bugs?: { url?: string }
  homepage?: string
}

describe('package identity (R3.3)', () => {
  it('names the package kyberdash with kyberdash as the only bin', () => {
    expect(pkg.name).toBe('kyberdash')
    expect(pkg.bin).toEqual({ kyberdash: 'dist/cli.js' })
  })

  it('points author, repository and issue tracker at dpalfery/kyber-weave', () => {
    expect(JSON.stringify(pkg.author)).toContain('dpalfery')
    expect(pkg.repository?.url).toContain('github.com/dpalfery/kyber-weave')
    expect(pkg.bugs?.url).toContain('github.com/dpalfery/kyber-weave')
    expect(pkg.homepage).toContain('github.com/dpalfery/kyber-weave')
  })

  it('keeps the upstream name out of the package metadata', () => {
    expect(/codeburn/i.test(JSON.stringify({ d: pkg.description, h: pkg.homepage }))).toBe(false)
  })
})

describe('CLI help carries the KyberDash identity (R3.7)', () => {
  it('prints --help without naming CodeBurn', () => {
    const result = spawnSync(
      process.execPath,
      ['--import', 'tsx', 'src/launcher.ts', '--help'],
      { cwd: DASH_ROOT, encoding: 'utf8', timeout: 60_000 },
    )
    expect(result.status).toBe(0)
    const output = `${result.stdout ?? ''}${result.stderr ?? ''}`
    expect(output).toContain('kyberdash')
    expect(/codeburn/i.test(output)).toBe(false)
  })
})

describe('web dashboard HTML (R3.7)', () => {
  it('names the product kyberDash in the served HTML template', () => {
    const html = readFileSync(path.join(DASH_ROOT, 'web', 'index.html'), 'utf8')
    expect(html).toContain('kyberDash')
    expect(/CodeBurn/.test(html)).toBe(false)
  })
})
