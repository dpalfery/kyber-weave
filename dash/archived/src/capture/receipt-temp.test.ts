import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { loadCaptureReceipt, receiptPathForHome, saveCaptureReceipt, type CaptureReceipt } from './receipt.js'

const homes: string[] = []
afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})

const receipt: CaptureReceipt = { version: 1, files: [] } as unknown as CaptureReceipt

describe('saveCaptureReceipt temp file', () => {
  it.skipIf(process.platform === 'win32')('never writes through a symlink planted at the pid-named temp path', () => {
    const home = mkdtempSync(join(tmpdir(), 'kyber-receipt-temp-'))
    homes.push(home)
    const victim = join(home, 'victim.txt')
    writeFileSync(victim, 'keep me')
    const path = receiptPathForHome(home)
    mkdirSync(dirname(path), { recursive: true })
    symlinkSync(victim, `${path}.${process.pid}.tmp`)

    saveCaptureReceipt(home, receipt)

    expect(readFileSync(victim, 'utf8')).toBe('keep me')
    expect(loadCaptureReceipt(home)).toEqual(receipt)
    expect(existsSync(path)).toBe(true)
    expect(readdirSync(dirname(path)).filter((name) => name.endsWith('.tmp') && name !== `capture-receipt.json.${process.pid}.tmp`)).toEqual([])
  })
})
