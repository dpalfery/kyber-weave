// The spool's byte bound: the spool file holds UTF-8 bytes, so the bound must count bytes.
//
// The spool is the file-backed queue of the events a delivery could not hand over. Its size is
// bounded by `SPOOL_MAX_BYTES`, and that bound is the only thing keeping the file from growing
// with an outage: the events are parked until the next run delivers them.
//
// A Factory event carries up to 64 KiB of UTF-8 prompt text, and UTF-8 spends up to three bytes
// on a character. A bound that counts CHARACTERS is therefore not a bound on the bytes the file
// holds, and the ceiling the constant documents is not the ceiling the file respects.

import { mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { FACTORY_KEYS } from './contract.js'
import { SPOOL_MAX_BYTES, appendSpoolEvent, factorySpoolDir } from './spool.js'

/** One character that costs three UTF-8 bytes and one UTF-16 code unit. */
const CJK = '漢'

/**
 * The prompt of one event, in characters: 21,845 of them are 65,535 UTF-8 bytes, which is the
 * most prompt text an event carries.
 */
const PROMPT_CHARS = 21845

/** The spool file inside the spool directory: one JSON event per line. */
const SPOOL_FILE_NAME = 'spool.jsonl'

const dirs: string[] = []
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
  vi.doUnmock('node:fs')
  vi.resetModules()
})

function temp(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  dirs.push(dir)
  return dir
}

/** One spooled span: the shape the hook runner parks, with a prompt of 64 KiB of UTF-8. */
function span(index: number): unknown {
  return {
    traceId: '0'.repeat(32),
    spanId: '1'.repeat(16),
    name: 'factory.turn',
    kind: 'SPAN_KIND_INTERNAL',
    attributes: [
      { key: FACTORY_KEYS.sessionId, value: { stringValue: `session-${index}` } },
      { key: FACTORY_KEYS.promptText, value: { stringValue: CJK.repeat(PROMPT_CHARS) } },
    ],
  }
}

describe('the spool byte bound', () => {
  it('bounds the spool file by its UTF-8 bytes, not by its characters', () => {
    const stateDir = temp('factory-spool-bytes-')
    const spoolFile = join(factorySpoolDir(stateDir), SPOOL_FILE_NAME)

    // The events are offered one at a time, and the run stops as soon as the file passes the
    // ceiling: the fixture is as small as the discrimination allows, and the ceiling is
    // 4 MiB, so nothing smaller can show a byte bound that is not one.
    const offered: string[] = []
    for (let index = 0; index < 80; index += 1) {
      const event = span(index)
      appendSpoolEvent(stateDir, event)
      offered.push(JSON.stringify(event))
      if (statSync(spoolFile).size > SPOOL_MAX_BYTES) break
    }

    const fixtureChars = offered.reduce((total, line) => total + line.length + 1, 0)
    const fixtureBytes = offered.reduce((total, line) => total + Buffer.byteLength(line) + 1, 0)

    // The fixture is the discriminating case: the events it offers fit inside the bound as
    // CHARACTERS and do not fit as BYTES, so a bound that counts characters keeps every one of
    // them and the file grows past the ceiling. Without this the assertion below could pass
    // for the wrong reason — a fixture so large that any bound drops everything.
    expect(
      fixtureChars,
      'the offered events fit inside the bound as characters',
    ).toBeLessThan(SPOOL_MAX_BYTES)
    expect(
      fixtureBytes,
      'the offered events do not fit inside the bound as bytes',
    ).toBeGreaterThan(SPOOL_MAX_BYTES)

    const text = readFileSync(spoolFile, 'utf8')
    // The kept events are under the bound as characters ...
    expect(text.length, 'the kept events fit inside the bound as characters').toBeLessThan(
      SPOOL_MAX_BYTES,
    )
    // ... and the file they are written as is under the byte ceiling the constant documents.
    expect(
      statSync(spoolFile).size,
      'the spool file holds at most SPOOL_MAX_BYTES bytes',
    ).toBeLessThanOrEqual(SPOOL_MAX_BYTES)
  })

  it('does not read a spool file that is already over the byte bound', async () => {
    const stateDir = temp('factory-spool-over-')
    const spoolFile = join(factorySpoolDir(stateDir), SPOOL_FILE_NAME)
    mkdirSync(factorySpoolDir(stateDir), { recursive: true })
    // A spool over the bound: one event bigger than the whole ceiling, which a spool
    // file can only hold if it was replaced by something that is not a spool.
    writeFileSync(spoolFile, `${JSON.stringify({ blob: 'x'.repeat(SPOOL_MAX_BYTES + 1024) })}\n`)

    // The reads the reader makes are recorded: the bound has to hold BEFORE the file is
    // read, or the whole file is allocated first and the bound only refuses it afterwards.
    const reads: string[] = []
    vi.doMock('node:fs', async (importOriginal) => {
      const actual = (await importOriginal()) as Record<string, unknown>
      const real = actual['readFileSync'] as (...args: unknown[]) => unknown
      return {
        ...actual,
        readFileSync: (...args: unknown[]) => {
          reads.push(`readFileSync ${String(args[0])}`)
          return real(...args)
        },
        // The bounded read goes through a descriptor, so it is recorded too: an assertion
        // on `reads` cannot pass just because the read was not handed the path.
        readSync: (...args: unknown[]) => {
          reads.push(`readSync ${String(args[0])}`)
          return (actual['readSync'] as (...a: unknown[]) => number)(...args)
        },
      }
    })
    vi.resetModules()
    const spool = await import('./spool.js')

    const events = spool.readSpoolEvents(stateDir)

    expect(reads, 'a spool file that is already over the bound is never read').toEqual([])
    // Whatever the reader hands back is inside the bound: a reader that refuses the file
    // holds no events at all, and a reader that keeps the newest ones holds fewer than
    // the ceiling. Either way the bound is what the reader answers to.
    const held = events.reduce<number>(
      (total, event) => total + Buffer.byteLength(JSON.stringify(event), 'utf8'),
      0,
    )
    expect(held, 'the events the reader hands back fit inside the byte bound').toBeLessThanOrEqual(
      SPOOL_MAX_BYTES,
    )
  })
  it('never reads past the bound when the spool grows after it was measured', async () => {
    const stateDir = temp('factory-spool-grown-')
    const spoolFile = join(factorySpoolDir(stateDir), SPOOL_FILE_NAME)
    mkdirSync(factorySpoolDir(stateDir), { recursive: true })
    // Twice the ceiling on disk, while the measure below reports a 100-byte file: the
    // file grew between the fstat and the read.
    writeFileSync(spoolFile, `${JSON.stringify({ blob: 'x'.repeat(2 * SPOOL_MAX_BYTES) })}\n`)

    const reads: string[] = []
    let bytesRead = 0
    vi.doMock('node:fs', async (importOriginal) => {
      const actual = (await importOriginal()) as Record<string, unknown>
      const realReadFile = actual['readFileSync'] as (...args: unknown[]) => unknown
      const realRead = actual['readSync'] as (...args: unknown[]) => number
      const realFstat = actual['fstatSync'] as (...args: unknown[]) => { size: number }
      return {
        ...actual,
        readFileSync: (...args: unknown[]) => {
          reads.push(`readFileSync ${String(args[0])}`)
          return realReadFile(...args)
        },
        readSync: (...args: unknown[]) => {
          const got = realRead(...args)
          bytesRead += got
          return got
        },
        fstatSync: (...args: unknown[]) => {
          const stat = realFstat(...args)
          Object.defineProperty(stat, 'size', { value: 100 })
          return stat
        },
      }
    })
    vi.resetModules()
    const spool = await import('./spool.js')

    const events = spool.readSpoolEvents(stateDir)

    expect(reads, 'the spool file is never read whole').toEqual([])
    expect(bytesRead, 'the read stops one byte past the bound').toBeLessThanOrEqual(SPOOL_MAX_BYTES + 1)
    expect(events, 'a spool that grew past the bound holds no events').toEqual([])
  })
})
