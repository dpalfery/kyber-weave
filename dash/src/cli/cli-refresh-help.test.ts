import { spawnSync } from 'node:child_process'

import { describe, expect, it } from 'vitest'

describe('CLI report help', () => {
  it('discloses the context-report flags', () => {
    const result = spawnSync(process.execPath, ['--import', 'tsx', 'src/launcher.ts', 'report', '--help'], {
      cwd: process.cwd(),
      encoding: 'utf8',
    })

    expect(result.status, result.stderr).toBe(0)
    expect(result.stdout).toMatch(/--format/)
    expect(result.stdout).toMatch(/--harness/)
    expect(result.stdout).toMatch(/--days/)
    expect(result.stdout).toMatch(/--limit/)
    expect(result.stdout).toMatch(/--db/)
  })
})
