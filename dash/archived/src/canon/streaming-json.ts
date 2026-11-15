import { createInflate } from 'node:zlib'

export type JsonPathSegment = string | number
export interface ColumnChunkReader { readonly storage: 'blob' | 'text'; read(offset: number, length: number): Uint8Array | null }
export type JsonStringEvent =
  | { type: 'string-start'; path: readonly JsonPathSegment[] }
  | { type: 'string-chunk'; path: readonly JsonPathSegment[]; text: string }
  | { type: 'string-end'; path: readonly JsonPathSegment[] }
  | { type: 'scalar'; path: readonly JsonPathSegment[]; value: number | boolean | null }
export interface StreamingJsonReaderOptions { inputChunkBytes?: number; workByteLimit?: number; workTimeLimitMs?: number }
export class StreamingJsonError extends Error { constructor(readonly code: 'corrupt_column' | 'cancelled', message: string, options?: { cause?: unknown }) { super(message, options); this.name = 'StreamingJsonError' } }
type Frame = { path: readonly JsonPathSegment[]; kind: 'root' | 'object' | 'array'; state: string; key?: string; index?: number; afterComma: boolean }
type Role = { key: true; frame: Frame } | { key: false; path: readonly JsonPathSegment[] }
const numberPattern = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$/

export class StreamingJsonReader {
  private readonly decoder = new TextDecoder('utf-8', { fatal: true })
  private readonly inputBytes: number
  private readonly workBytes: number
  private readonly workMs: number
  private readonly events: JsonStringEvent[] = []
  private readonly chunks: Uint8Array[] = []
  private readonly frames: Frame[] = [{ path: [], kind: 'root', state: 'value', afterComma: false }]
  private readonly inflater = createInflate({ chunkSize: 16 * 1024 })
  private offset = 0
  private ended = false
  private inflateEnded = false
  private inflateError: unknown = null
  private disposed = false
  private text = ''
  private textAt = 0
  private mode: 'normal' | 'string' | 'escape' | 'unicode' | 'number' | 'literal' = 'normal'
  private role: Role | null = null
  private scalarPath: readonly JsonPathSegment[] | null = null
  private string = ''
  private stringBytes = 0
  private chunkBudget = 16
  private unicode = ''
  private token = ''
  private finalised = false

  constructor(private readonly reader: ColumnChunkReader, options: StreamingJsonReaderOptions = {}) {
    this.inputBytes = option(options.inputChunkBytes, 4096, 16384)
    this.workBytes = option(options.workByteLimit, 4 * 1024 * 1024)
    this.workMs = option(options.workTimeLimitMs, 100)
    this.inflater.on('data', (chunk: Buffer) => this.chunks.push(new Uint8Array(chunk.buffer, chunk.byteOffset, chunk.byteLength)))
    this.inflater.once('end', () => { this.inflateEnded = true })
    this.inflater.once('error', (error: unknown) => { this.inflateError = error })
  }

  async pump(onEvent: (event: JsonStringEvent) => boolean | void, signal?: AbortSignal): Promise<{ kind: 'yielded' | 'eof'; decodedBytes: number; elapsedMs: number }> {
    this.check(signal)
    const started = performance.now()
    let decodedBytes = 0
    while (true) {
      this.check(signal)
      const event = this.events.shift()
      if (event !== undefined) { if (onEvent(event) === false) return result('yielded', decodedBytes, started); continue }
      if (this.textAt < this.text.length) {
        // WHY run-sliced strings: consuming ordinary characters one run at
        // a time avoids per-character dispatch and accumulation on megabyte
        // values. Runs stop before quotes, backslashes, and control
        // characters, so escapes, string ends, and the control-character
        // error keep their exact single-step behavior.
        if (this.mode === 'string' && this.consumeStringRun()) {
          if (performance.now() - started >= this.workMs) return result('yielded', decodedBytes, started)
          continue
        }
        const c = this.text[this.textAt++]
        if (c === undefined) throw this.bad('missing decoded character')
        this.character(c)
        // WHY a batched clock: a per-character `performance.now()` dominates
        // the scan cost on megabyte columns. Checking every 256 characters
        // keeps the same work-slice bound within microseconds of overshoot,
        // while byte-based yields and the per-iteration abort check are untouched.
        if ((this.textAt & 255) === 0 && performance.now() - started >= this.workMs) return result('yielded', decodedBytes, started)
        continue
      }
      this.text = ''; this.textAt = 0
      const raw = this.chunks.shift()
      if (raw !== undefined) {
        try { this.text = this.decoder.decode(raw, { stream: true }) } catch (error: unknown) { throw this.bad('invalid UTF-8', error) }
        decodedBytes += raw.byteLength
        if (decodedBytes >= this.workBytes) return result('yielded', decodedBytes, started)
        continue
      }
      if (this.ended) {
        if (this.reader.storage === 'blob' && !this.inflateEnded) { await new Promise<void>((resolve) => setImmediate(resolve)); continue }
        if (!this.finalised) { this.finalised = true; this.finish() }
        if (this.events.length > 0) continue
        return result('eof', decodedBytes, started)
      }
      const input = this.reader.read(this.offset, this.inputBytes)
      if (input === null) {
        this.ended = true
        if (this.reader.storage === 'blob') this.inflater.end()
        else { try { this.text = this.decoder.decode() } catch (error: unknown) { throw this.bad('invalid UTF-8', error) } }
        continue
      }
      if (input.byteLength === 0 || input.byteLength > this.inputBytes) throw this.bad('invalid input chunk length')
      this.offset += input.byteLength
      if (this.reader.storage === 'blob') this.inflater.write(input)
      else { try { this.text = this.decoder.decode(input, { stream: true }) } catch (error: unknown) { throw this.bad('invalid UTF-8', error) }; decodedBytes += input.byteLength }
      if (decodedBytes >= this.workBytes || performance.now() - started >= this.workMs) return result('yielded', decodedBytes, started)
    }
  }

  dispose(): void { if (!this.disposed) { this.disposed = true; this.events.length = 0; this.chunks.length = 0; this.text = ''; this.inflater.removeAllListeners(); this.inflater.destroy() } }
  private character(c: string): void { if (this.mode === 'normal') this.normal(c); else if (this.mode === 'string') this.inString(c); else if (this.mode === 'escape') this.escape(c); else if (this.mode === 'unicode') this.escapeUnicode(c); else if (this.mode === 'number') this.number(c); else this.literal(c) }
  private normal(c: string): void {
    if (/\s/.test(c)) return
    const f = this.top()
    if (f.kind === 'object') {
      // WHY an after-comma marker instead of dropping the early `}` pop: the
      // `key` state serves both a fresh container (`{}`) and the position
      // after a comma (`{"a":1,}`), so only the marker tells a valid empty
      // object from a trailing comma.
      if (f.state === 'key') { if (c === '}') { if (f.afterComma) throw this.bad('trailing comma in object'); this.frames.pop(); return }; if (c !== '"') throw this.bad('object key must be a string'); this.beginString({ key: true, frame: f }); return }
      if (f.state === 'colon') { if (c !== ':') throw this.bad('missing object colon'); f.state = 'value'; return }
      if (f.state === 'comma') { if (c === '}') { this.frames.pop(); return }; if (c !== ',') throw this.bad('object values need commas'); f.state = 'key'; f.afterComma = true; return }
    }
    if (f.kind === 'array') { if (f.state === 'comma') { if (c === ']') { this.frames.pop(); return }; if (c !== ',') throw this.bad('array values need commas'); f.state = 'value'; f.afterComma = true } else if (c === ']') { if (f.afterComma) throw this.bad('trailing comma in array'); this.frames.pop(); return } else { this.value(c) }; return }
    if (f.kind === 'root' && f.state === 'done') throw this.bad('content after root value')
    this.value(c)
  }
  private value(c: string): void {
    const path = this.path(); this.started()
    if (c === '"') this.beginString({ key: false, path })
    else if (c === '{') this.frames.push({ path, kind: 'object', state: 'key', afterComma: false })
    else if (c === '[') this.frames.push({ path, kind: 'array', state: 'value', index: 0, afterComma: false })
    else if (c === '-' || /[0-9]/.test(c)) { this.mode = 'number'; this.token = c; this.scalarPath = path }
    else if (c === 't' || c === 'f' || c === 'n') { this.mode = 'literal'; this.token = c; this.scalarPath = path }
    else throw this.bad('invalid JSON value')
  }
  private beginString(role: Role): void { this.mode = 'string'; this.role = role; this.string = ''; this.stringBytes = 0; this.chunkBudget = 16; if (!role.key) this.events.push({ type: 'string-start', path: role.path }) }
  private inString(c: string): void { if (c === '"') { this.endString(); return }; if (c === '\\') { this.mode = 'escape'; return }; if (c.codePointAt(0)! < 32) throw this.bad('control character in string'); this.append(c) }
  private escape(c: string): void { if (c === 'u') { this.unicode = ''; this.mode = 'unicode'; return }; const map: Record<string, string> = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' }; if (map[c] === undefined) throw this.bad('bad string escape'); this.append(map[c]!); this.mode = 'string' }
  private escapeUnicode(c: string): void { if (!/[0-9a-f]/i.test(c)) throw this.bad('bad unicode escape'); this.unicode += c; if (this.unicode.length === 4) { this.append(String.fromCharCode(Number.parseInt(this.unicode, 16))); this.mode = 'string' } }
  private append(c: string): void {
    const role = this.role
    if (role === null) throw this.bad('string role missing')
    this.string += c
    if (role.key) {
      this.stringBytes += Buffer.byteLength(c)
      if (this.stringBytes > this.workBytes) throw this.bad('object key exceeds metadata budget')
      return
    }
    // WHY char-counted, adaptive value chunks: per-character byte-length
    // calls dominate megabyte scans, and the flush threshold only sets event
    // granularity — downstream joins every chunk back exactly, so counting
    // characters here is invisible past the event stream. The first chunk
    // stays small so short values still stream incrementally; later chunks
    // grow so a multi-megabyte value costs events, not hundreds of thousands.
    this.emitReady()
  }

  /**
   * Consume one run of ordinary string characters. Returns false when the
   * next character needs single-step handling, so the caller falls through
   * to the character state machine with identical behavior.
   */
  private consumeStringRun(): boolean {
    const role = this.role
    if (role === null) return false
    const text = this.text
    const len = text.length
    let end = this.textAt
    while (end < len) {
      const code = text.charCodeAt(end)
      if (code === 34 || code === 92 || code < 32) break
      end += 1
    }
    if (end === this.textAt) return false
    if (end < len) {
      const stop = text.charCodeAt(end)
      if (stop !== 34 && stop !== 92) return false
    }
    const run = text.slice(this.textAt, end)
    this.textAt = end
    this.string += run
    if (role.key) {
      this.stringBytes += Buffer.byteLength(run)
      if (this.stringBytes > this.workBytes) throw this.bad('object key exceeds metadata budget')
      return true
    }
    this.emitReady()
    return true
  }

  /** Emit full budget-sized pieces, keeping the remainder buffered. */
  private emitReady(): void {
    const role = this.role
    if (role === null || role.key) return
    while (this.string.length >= this.chunkBudget) {
      const piece = this.string.slice(0, this.chunkBudget)
      this.events.push({ type: 'string-chunk', path: role.path, text: piece })
      this.string = this.string.slice(this.chunkBudget)
      this.chunkBudget = 65_536
    }
  }
  private endString(): void { const role = this.role; if (role === null) throw this.bad('string role missing'); if (role.key) { role.frame.key = this.string; role.frame.state = 'colon' } else { this.flush(role.path); this.events.push({ type: 'string-end', path: role.path }) }; this.role = null; this.string = ''; this.mode = 'normal' }
  private flush(path: readonly JsonPathSegment[]): void { if (this.string.length > 0) this.events.push({ type: 'string-chunk', path, text: this.string }); this.string = ''; this.stringBytes = 0; this.chunkBudget = 65_536 }
  private number(c: string): void { if (/[0-9eE+.-]/.test(c)) { this.token += c; if (this.token.length > 128) throw this.bad('number too long'); return }; if (!delimiter(c) || !numberPattern.test(this.token)) throw this.bad('invalid JSON number'); const value = Number(this.token); if (!Number.isFinite(value)) throw this.bad('non-finite JSON number'); this.events.push({ type: 'scalar', path: this.scalarPath ?? this.completePath(), value }); this.mode = 'normal'; this.token = ''; this.scalarPath = null; this.normal(c) }
  private literal(c: string): void { if (/[a-z]/.test(c)) { this.token += c; if (this.token.length > 8) throw this.bad('literal too long'); return }; const v = this.token === 'true' ? true : this.token === 'false' ? false : this.token === 'null' ? null : undefined; if (!delimiter(c) || v === undefined) throw this.bad('invalid JSON literal'); this.events.push({ type: 'scalar', path: this.scalarPath ?? this.completePath(), value: v }); this.mode = 'normal'; this.token = ''; this.scalarPath = null; this.normal(c) }
  private path(): readonly JsonPathSegment[] { const f = this.top(); if (f.kind === 'root') return []; if (f.kind === 'array') return [...f.path, f.index!]; if (f.key === undefined) throw this.bad('object missing key'); return [...f.path, f.key] }
  private started(): void { const f = this.top(); if (f.kind === 'root') f.state = 'done'; else if (f.kind === 'array') { f.index!++; f.state = 'comma'; f.afterComma = false } else { f.key = undefined; f.state = 'comma'; f.afterComma = false } }
  private completePath(): readonly JsonPathSegment[] { const f = this.top(); if (f.kind === 'root') return []; if (f.kind === 'array') return [...f.path, f.index! - 1]; throw this.bad('scalar object path lost') }
  private finish(): void { if (this.mode === 'number') { if (!numberPattern.test(this.token)) throw this.bad('invalid final number'); const value = Number(this.token); if (!Number.isFinite(value)) throw this.bad('non-finite final number'); this.events.push({ type: 'scalar', path: this.scalarPath ?? this.completePath(), value }); this.mode = 'normal' } else if (this.mode === 'literal') { const v = this.token === 'true' ? true : this.token === 'false' ? false : this.token === 'null' ? null : undefined; if (v === undefined) throw this.bad('invalid final literal'); this.events.push({ type: 'scalar', path: this.scalarPath ?? this.completePath(), value: v }); this.mode = 'normal' }; this.scalarPath = null; if (this.mode !== 'normal' || this.frames.length !== 1 || this.top().state !== 'done') throw this.bad('incomplete JSON column') }
  private top(): Frame { const f = this.frames.at(-1); if (f === undefined) throw this.bad('nesting underflow'); return f }
  private check(signal?: AbortSignal): void { if (this.disposed || signal?.aborted) throw new StreamingJsonError('cancelled', 'streaming JSON reader was cancelled'); if (this.inflateError !== null) throw this.bad('deflated column cannot be inflated', this.inflateError) }
  private bad(message: string, cause?: unknown): StreamingJsonError { return new StreamingJsonError('corrupt_column', message, cause === undefined ? undefined : { cause }) }
}
function option(value: number | undefined, fallback: number, max = Number.MAX_SAFE_INTEGER): number { if (value === undefined) return fallback; if (!Number.isSafeInteger(value) || value < 1 || value > max) throw new RangeError('streaming JSON options must be positive safe integers'); return value }
function delimiter(c: string): boolean { return /\s/.test(c) || c === ',' || c === ']' || c === '}' }
function result(kind: 'yielded' | 'eof', decodedBytes: number, started: number) { return { kind, decodedBytes, elapsedMs: performance.now() - started } }
