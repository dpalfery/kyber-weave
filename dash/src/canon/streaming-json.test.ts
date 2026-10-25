import { deflateSync } from 'node:zlib'
import { describe, expect, it } from 'vitest'
import { StreamingJsonReader, type ColumnChunkReader, type JsonStringEvent } from './streaming-json.js'

function chunkReader(bytes: Uint8Array, storage: ColumnChunkReader['storage'], reads: number[] = []): ColumnChunkReader {
  return {
    storage,
    read(offset, length) {
      reads.push(length)
      if (offset >= bytes.byteLength) return null
      return bytes.subarray(offset, Math.min(offset + length, bytes.byteLength))
    },
  }
}

function eventPath(path: JsonStringEvent['path']): string {
  return path.map(String).join('.')
}

function collectStringEvents(events: readonly JsonStringEvent[], suffix: string): string {
  let collected = ''
  for (const event of events) {
    if (event.type === 'string-chunk' && eventPath(event.path).endsWith(suffix)) collected += event.text
  }
  return collected
}

async function pumpToEof(
  reader: StreamingJsonReader,
  callback: (event: JsonStringEvent) => boolean | void,
): Promise<JsonStringEvent[]> {
  const events: JsonStringEvent[] = []
  for (let pumpCount = 0; pumpCount < 20_000; pumpCount += 1) {
    const result = await reader.pump((event) => {
      events.push(event)
      return callback(event)
    })
    if (result.kind === 'eof') return events
    expect(result.kind).toBe('yielded')
  }
  throw new Error('Streaming JSON reader did not reach EOF within the bounded pump count.')
}

describe('bounded streaming JSON column reader', () => {
  it('decodes escaped quotes, backslashes and a unicode surrogate pair across tiny source reads', async () => {
    const json = String.raw`{"messages":[{"text":"quote \" slash \\ face \uD83D\uDE42"}],"meta":{"count":5,"enabled":true,"missing":null}}`
    const reads: number[] = []
    const reader = new StreamingJsonReader(chunkReader(Buffer.from(json, 'utf8'), 'text', reads), {
      inputChunkBytes: 7,
      workByteLimit: 64,
      workTimeLimitMs: 1_000,
    })
    try {
      const events = await pumpToEof(reader, () => {})
      expect(collectStringEvents(events, 'messages.0.text')).toBe('quote " slash \\ face 🙂')
      expect(Math.max(...reads)).toBeLessThanOrEqual(7)
      const valueEvents = events.filter((event) => event.type === 'string-chunk' && eventPath(event.path).endsWith('messages.0.text'))
      expect(valueEvents.length).toBeGreaterThan(1)
      expect(events.filter((event) => event.type === 'scalar')).toEqual(expect.arrayContaining([
        expect.objectContaining({ path: ['meta', 'count'], value: 5 }),
        expect.objectContaining({ path: ['meta', 'enabled'], value: true }),
        expect.objectContaining({ path: ['meta', 'missing'], value: null }),
      ]))
    } finally {
      reader.dispose()
    }
  })

  it('preserves nested paths and exact string order when the consumer pauses after each event', async () => {
    const json = JSON.stringify({ turns: [{ parts: [{ text: 'A雪' }, { text: '🙂B' }] }] })
    const reader = new StreamingJsonReader(chunkReader(Buffer.from(json, 'utf8'), 'text'), {
      inputChunkBytes: 5,
      workByteLimit: 19,
      workTimeLimitMs: 1_000,
    })
    const events: JsonStringEvent[] = []
    let pauses = 0
    try {
      for (let pumpCount = 0; pumpCount < 20_000; pumpCount += 1) {
        const result = await reader.pump((event) => {
          events.push(event)
          pauses += 1
          return false
        })
        if (result.kind === 'eof') break
        expect(result.kind).toBe('yielded')
      }
      expect(pauses).toBeGreaterThan(5)
      const values = events
        .filter((event) => event.type === 'string-chunk' && eventPath(event.path).endsWith('.text'))
        .map((event) => event.type === 'string-chunk' ? event.text : '')
      expect(values.join('')).toBe('A雪🙂B')
      const terminal = await reader.pump(() => { throw new Error('EOF must not emit another event.') })
      expect(terminal.kind).toBe('eof')
    } finally {
      reader.dispose()
    }
  })

  it('skips a multi-megabyte unrelated field without buffering it before yielding the later text', async () => {
    let state = 0x10203040
    const prefix = Buffer.allocUnsafe(4_500_000)
    for (let index = 0; index < prefix.length; index += 1) {
      state = (Math.imul(state, 1_664_525) + 1_013_904_223) >>> 0
      prefix[index] = 32 + (state % 95)
    }
    const json = JSON.stringify({ ignored: prefix.toString('ascii'), target: { text: 'late 雪🙂é value' } })
    const compressed = deflateSync(Buffer.from(json, 'utf8'))
    const reads: number[] = []
    const reader = new StreamingJsonReader(chunkReader(compressed, 'blob', reads), {
      inputChunkBytes: 4_096,
      workByteLimit: 65_536,
      workTimeLimitMs: 1_000,
    })
    const workSlices: number[] = []
    try {
      const targetChunks: string[] = []
      for (let pumpCount = 0; pumpCount < 20_000; pumpCount += 1) {
        const result = await reader.pump((event) => {
          if (event.type === 'string-chunk' && eventPath(event.path).endsWith('target.text')) targetChunks.push(event.text)
        })
        workSlices.push(result.decodedBytes)
        if (result.kind === 'eof') break
        expect(result.kind).toBe('yielded')
      }
      expect(targetChunks.join('')).toBe('late 雪🙂é value')
      expect(Math.max(...reads)).toBeLessThanOrEqual(4_096)
      expect(Math.max(...workSlices)).toBeLessThanOrEqual(65_536)
      expect(workSlices.length).toBeGreaterThan(1)
    } finally {
      reader.dispose()
    }
  })

  it('rejects a non-finite exponent as a corrupt column instead of emitting Infinity', async () => {
    for (const json of ['{"v":1e999}', '{"v":-1e999}', '1e999']) {
      const reader = new StreamingJsonReader(chunkReader(Buffer.from(json, 'utf8'), 'text'))
      try {
        let failure: unknown
        try {
          await pumpToEof(reader, () => {})
        } catch (error: unknown) {
          failure = error
        }
        expect(failure, json).toMatchObject({ code: 'corrupt_column' })
      } finally {
        reader.dispose()
      }
    }
  })

  it('reports cancellation and corrupt compressed columns as stable typed failures', async () => {
    const controller = new AbortController()
    controller.abort()
    const cancelled = new StreamingJsonReader(chunkReader(Buffer.from('{}'), 'text'))
    try {
      let cancellation: unknown
      try {
        await cancelled.pump(() => {}, controller.signal)
      } catch (error: unknown) {
        cancellation = error
      }
      expect(cancellation).toMatchObject({ code: 'cancelled' })
    } finally {
      cancelled.dispose()
    }

    const corrupt = new StreamingJsonReader(chunkReader(Buffer.from('not-deflate'), 'blob'))
    try {
      let corruption: unknown
      try {
        await pumpToEof(corrupt, () => {})
      } catch (error: unknown) {
        corruption = error
      }
      expect(corruption).toMatchObject({ code: 'corrupt_column' })
    } finally {
      corrupt.dispose()
    }
  })
})
