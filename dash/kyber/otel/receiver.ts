// OTLP/HTTP trace and log receiver for KyberDash (spec: docs/specs/kyberdash,
// design.md "Ingest layer"). One `node:http` server on the OTLP-standard
// port 4318 accepting `POST /v1/traces` in both OTLP encodings: JSON,
// because the existing collectors hand-roll OTLP JSON to that exact port
// (R2.2), and protobuf, because an OTel SDK exporter emits protobuf by
// default (R2.3).
//
// The receiver owns exactly the decode step. Both encodings normalize to
// one `OtlpSpan` shape — a decoded span, not a canonical record: harness
// attribution, token decomposition and cost are the normalization layer's
// job (task 5), so nothing here fabricates them. Decoded spans are handed
// to an `OtlpSpanStore` port; the batching, backpressure-owning
// `IngestWriter` (task 6.2) implements that port against `CanonStore`.
// The default in-memory store exists so the receiver is complete and
// testable on its own.
//
// Malformed payloads are rejected whole with a diagnostic and nothing is
// ingested (design.md, "Error Handling"): decode runs to completion before
// the store is touched, so a bad span in the middle of a batch writes
// nothing.
//
// Protobuf is decoded by hand — a wire-format reader plus the OTLP trace
// schema's field numbers — because the schema is small and stable, and
// because the merge zone carries no protobuf library the subtree could not
// merge cleanly. JSON is decoded to the same span shape while accepting
// both the canonical proto3 JSON encoding (base64 ids, enums as symbol
// names, int64 as strings) and the looser hand-rolled forms already
// posting to this port (hex ids, plain numbers, `INTERNAL`-style enums).

import { execFile } from 'node:child_process'
import { createHash } from 'node:crypto'
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import { promisify } from 'node:util'

const execFileAsync = promisify(execFile)

/** The OTLP/HTTP standard port (R2.1). */
export const DEFAULT_OTLP_PORT = 4318

/** The only endpoint this receiver serves (R2.1). */
export const OTLP_TRACES_PATH = '/v1/traces'
export const OTLP_LOGS_PATH = '/v1/logs'

/**
 * Request bodies are whole `ExportTraceServiceRequest`s; collectors batch.
 * The cap exists so a runaway or hostile client cannot balloon memory —
 * a legitimate OTLP batch of spans fits comfortably below it.
 */
export const DEFAULT_MAX_BODY_BYTES = 64 * 1024 * 1024

export type OtlpStatusCode = 'unset' | 'ok' | 'error'

/** A span's OTLP status; `message` is present only when the source set one. */
export type OtlpStatus = {
  code: OtlpStatusCode
  message?: string
}

/** The instrumentation scope that produced a span. */
export type OtlpScope = {
  name?: string
  version?: string
}

/**
 * The common span shape both encodings decode to. Ids are lower-case hex
 * (32 chars for trace, 16 for span) regardless of how the wire encoded
 * them; timestamps are ISO strings derived from the nanosecond fields,
 * which are preserved verbatim as strings so sub-millisecond ordering
 * survives for the analysis layer.
 */
export type OtlpSpan = {
  traceId: string
  spanId: string
  /** Null for a root span. */
  parentSpanId: string | null
  name: string
  /** OTLP kind name, lower-case: `unspecified`, `internal`, `server`, … */
  kind: string
  startTimeUnixNano: string
  endTimeUnixNano: string
  timestamp: string
  durationMs: number
  status: OtlpStatus
  /** OTLP attributes as a plain map, with AnyValue oneofs resolved. */
  attributes: Record<string, unknown>
  /** Resource attributes of the emitting process (e.g. `service.name`). */
  resource: Record<string, unknown>
  scope: OtlpScope
}

export type OtlpLog = {
  logId: string
  traceId: string | null
  spanId: string | null
  sessionId: string | null
  timestamp: string
  body: string | null
  attributes: Record<string, unknown>
  resource: Record<string, unknown>
  scope: OtlpScope
}

/**
 * Globally unique log identity for enrichment idempotency.
 *
 * Event class names (`claude_code.api_request_body`) repeat every turn; using
 * one as `logId` made later records of the same class look like duplicates.
 * Correlation identity + timestamp + a payload digest distinguishes them.
 */
export function deriveLogId(input: {
  traceId: string | null
  spanId: string | null
  sessionId: string | null
  timeUnixNano: bigint | string
  body: unknown
  attributes: Record<string, unknown>
  eventName?: string
}): string {
  const keys = Object.keys(input.attributes).sort()
  const attributes: Record<string, unknown> = {}
  for (const key of keys) attributes[key] = input.attributes[key]
  const digest = createHash('sha256')
    .update(
      JSON.stringify({
        event: input.eventName ?? '',
        body: input.body ?? null,
        attributes,
      }),
    )
    .digest('hex')
    .slice(0, 16)
  return `${input.traceId ?? input.sessionId ?? 'log'}:${input.spanId ?? 'none'}:${String(input.timeUnixNano)}:${digest}`
}

/**
 * Where decoded spans go. A port, not a store: persistence strategy —
 * batching, backpressure, canonical mapping — belongs to `IngestWriter`
 * (task 6.2), which implements this against `CanonStore`.
 */
export interface OtlpSpanStore {
  write(spans: readonly OtlpSpan[]): void
}

export interface OtlpSignalStore extends OtlpSpanStore {
  writeLogs(logs: readonly OtlpLog[]): void
}

export type OtlpReceiverStore =
  | OtlpSpanStore
  | OtlpSignalStore
  | { spans: OtlpSpanStore; logs: { writeLogs(logs: readonly OtlpLog[]): void } }

/** Default sink: keeps every decoded span in memory. */
export class InMemorySpanStore implements OtlpSpanStore {
  readonly spans: OtlpSpan[] = []

  write(spans: readonly OtlpSpan[]): void {
    this.spans.push(...spans)
  }
}

export class InMemoryLogStore {
  readonly logs: OtlpLog[] = []

  writeLogs(logs: readonly OtlpLog[]): void {
    this.logs.push(...logs)
  }
}

/** A payload that cannot be decoded as OTLP; the message is the diagnostic. */
export class OtlpDecodeError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'OtlpDecodeError'
  }
}

// ---------------------------------------------------------------------------
// Port-conflict diagnostics (R2.4)
// ---------------------------------------------------------------------------

/** One process found holding a port; identity is best-effort. */
export type PortOccupant = {
  /** OS process id, when the discovery tool reported one. */
  pid?: string
  /** Process name as the discovery tool printed it. */
  name?: string
  /** The tool's own line, kept verbatim for the diagnostic message. */
  detail: string
}

/** How a bound port's occupants are looked up; injectable so tests can pin it. */
export type PortOccupantDiscovery = (port: number, host: string) => Promise<PortOccupant[]>

function describeOccupant(occupant: PortOccupant): string {
  const identity =
    occupant.pid === undefined
      ? (occupant.name ?? 'unknown process')
      : `${occupant.name ?? 'process'} (PID ${occupant.pid})`
  return `${identity} — ${occupant.detail}`
}

function portConflictMessage(
  port: number,
  host: string,
  occupants: readonly PortOccupant[],
  cause: Error | undefined,
): string {
  const causeText = cause === undefined ? 'EADDRINUSE' : cause.message
  const occupantText =
    occupants.length > 0
      ? `Occupying process: ${occupants.map(describeOccupant).join('; ')}.`
      : 'The occupying process could not be identified (tried: lsof, ss, netstat).'
  return (
    `cannot listen on ${host}:${port}: the port is already bound (${causeText}). ` +
    `${occupantText} ` +
    'The receiver did not rebind to a different port; stop the occupying process or configure another port explicitly.'
  )
}

/**
 * The configured port is already bound (R2.4). Carries the port and the
 * best-effort occupying processes so the report names both the conflict
 * and its cause; the receiver never rebinds in response to one.
 */
export class PortConflictError extends Error {
  readonly code = 'PORT_CONFLICT'
  readonly port: number
  readonly host: string
  readonly occupants: readonly PortOccupant[]

  constructor(port: number, host: string, occupants: readonly PortOccupant[], cause?: Error) {
    super(portConflictMessage(port, host, occupants, cause), { cause })
    this.name = 'PortConflictError'
    this.port = port
    this.host = host
    this.occupants = occupants
  }
}

/** Parse `lsof -nP -iTCP:<port> -sTCP:LISTEN` output (macOS, Linux lsof). */
export function parseLsofOutput(stdout: string): PortOccupant[] {
  const occupants: PortOccupant[] = []
  for (const line of stdout.split('\n')) {
    const columns = line.trim().split(/\s+/)
    if (columns.length < 2 || columns[0] === 'COMMAND') continue
    const [name, pid] = columns
    if (!/^\d+$/.test(pid)) continue
    occupants.push({ pid, name, detail: line.trim() })
  }
  return occupants
}

/** Parse `ss -lptn 'sport = :<port>'` output (Linux): the `users:(...)` column names the process. */
export function parseSsOutput(stdout: string): PortOccupant[] {
  const occupants: PortOccupant[] = []
  for (const line of stdout.split('\n')) {
    const match = /users:\(\("([^"]+)",pid=(\d+)/.exec(line)
    if (match === null) continue
    occupants.push({ pid: match[2], name: match[1], detail: line.trim() })
  }
  return occupants
}

/**
 * Parse `netstat` output filtered to listening sockets on `port`. The last
 * column is the PID on `netstat -ano` (Windows, some Linux); Linux
 * `netstat -lptn` prints a `users:(("name",pid=N,...))` column instead.
 */
export function parseNetstatOutput(stdout: string, port: number): PortOccupant[] {
  const occupants: PortOccupant[] = []
  const onPort = new RegExp(`:${port}\\s`)
  for (const line of stdout.split('\n')) {
    if (!onPort.test(line) || !/LISTEN/i.test(line)) continue
    const trimmed = line.trim()
    const named = /users:\(\("([^"]+)",pid=(\d+)/.exec(line)
    if (named !== null) {
      occupants.push({ pid: named[2], name: named[1], detail: trimmed })
      continue
    }
    const columns = trimmed.split(/\s+/)
    const last = columns[columns.length - 1]
    if (/^\d+$/.test(last)) occupants.push({ pid: last, detail: trimmed })
  }
  return occupants
}

const DISCOVERY_TOOL_TIMEOUT_MS = 2_000

function dedupeOccupants(occupants: readonly PortOccupant[]): PortOccupant[] {
  const seen = new Set<string>()
  const unique: PortOccupant[] = []
  for (const occupant of occupants) {
    const key = `${occupant.pid ?? '?'}:${occupant.name ?? '?'}:${occupant.detail}`
    if (seen.has(key)) continue
    seen.add(key)
    unique.push(occupant)
  }
  return unique
}

/**
 * Best-effort discovery of what holds a bound port (R2.4): try the
 * standard listening-socket tools in order and keep the first that names
 * something. A missing or failing tool is not an error — an empty result
 * means "not discoverable on this machine", which the conflict report
 * says explicitly rather than guessing.
 */
export async function discoverPortOccupants(port: number, host: string): Promise<PortOccupant[]> {
  void host // the OS tools report by port; the host is carried in the report
  const attempts: Array<() => Promise<PortOccupant[]>> = [
    async () =>
      parseLsofOutput(
        (await execFileAsync('lsof', ['-nP', `-iTCP:${port}`, '-sTCP:LISTEN'], { timeout: DISCOVERY_TOOL_TIMEOUT_MS })).stdout,
      ),
    async () =>
      parseSsOutput(
        (await execFileAsync('ss', ['-lptn', `sport = :${port}`], { timeout: DISCOVERY_TOOL_TIMEOUT_MS })).stdout,
      ),
    async () =>
      parseNetstatOutput(
        (await execFileAsync('netstat', ['-ano'], { timeout: DISCOVERY_TOOL_TIMEOUT_MS })).stdout,
        port,
      ),
  ]
  for (const attempt of attempts) {
    try {
      const occupants = await attempt()
      if (occupants.length > 0) return dedupeOccupants(occupants)
    } catch {
      // Tool absent or unusable on this platform — try the next.
    }
  }
  return []
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

const SPAN_KINDS = ['unspecified', 'internal', 'server', 'client', 'producer', 'consumer'] as const
const STATUS_CODES = ['unset', 'ok', 'error'] as const

export function bytesToHex(bytes: Uint8Array): string {
  return Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString('hex')
}

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err)
}

function isErrnoException(err: unknown): err is NodeJS.ErrnoException {
  return err instanceof Error && typeof (err as NodeJS.ErrnoException).code === 'string'
}

function expectObject(value: unknown, where: string): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new OtlpDecodeError(`${where} must be a JSON object`)
  }
  return value as Record<string, unknown>
}

function requiredString(value: unknown, where: string): string {
  if (typeof value !== 'string' || value.length === 0) {
    throw new OtlpDecodeError(`${where} must be a non-empty string`)
  }
  return value
}

/**
 * Decode a span id to lower-case hex. Accepts the canonical proto3 JSON
 * base64 (standard or URL-safe, padded or not) and the hex form hand-rolled
 * collectors emit; the two are distinguishable because a `byteLength`-byte
 * id is 2·byteLength hex chars but never that many base64 chars.
 */
function idToHex(value: unknown, byteLength: number, where: string): string {
  if (value instanceof Uint8Array) {
    if (value.length !== byteLength) {
      throw new OtlpDecodeError(`${where} must be ${byteLength} bytes, got ${value.length}`)
    }
    return bytesToHex(value)
  }
  if (typeof value !== 'string' || value.length === 0) {
    throw new OtlpDecodeError(
      `${where} is required: expected a ${byteLength}-byte id as hex or base64`,
    )
  }
  if (value.length === byteLength * 2 && /^[0-9a-fA-F]+$/.test(value)) {
    return value.toLowerCase()
  }
  const decoded = base64ToBytes(value, where)
  if (decoded.length !== byteLength) {
    throw new OtlpDecodeError(
      `${where} decoded to ${decoded.length} bytes, expected ${byteLength}`,
    )
  }
  return bytesToHex(decoded)
}

function base64ToBytes(value: string, where: string): Uint8Array {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/')
  const padded = normalized + '='.repeat((4 - (normalized.length % 4)) % 4)
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(padded)) {
    throw new OtlpDecodeError(`${where} is neither hex nor base64`)
  }
  return Buffer.from(padded, 'base64')
}

function nanosToBigInt(value: unknown, where: string): bigint {
  if (typeof value === 'bigint') return value
  if (typeof value === 'number' && Number.isFinite(value)) return BigInt(Math.trunc(value))
  if (typeof value === 'string' && /^-?\d+$/.test(value)) return BigInt(value)
  throw new OtlpDecodeError(
    `${where} must be an integer nanosecond count (proto3 JSON encodes int64 as a string)`,
  )
}

function nanosToIsoTimestamp(ns: bigint): string {
  return new Date(Number(ns / 1_000_000n)).toISOString()
}

function nanosDeltaToMs(from: bigint, to: bigint): number {
  return Number(to - from) / 1_000_000
}

/**
 * Map an OTLP enum to its name. Accepts the canonical proto3 JSON symbol
 * (`SPAN_KIND_INTERNAL`, `STATUS_CODE_OK`), the bare name hand-rolled
 * collectors emit (`INTERNAL`, `ok`), and the numeric form. An
 * unrecognized value collapses to the zero value rather than rejecting the
 * whole payload over a field no analysis attributes harness evidence to.
 */
function enumName<T extends string>(value: unknown, names: readonly T[], prefix: string): T {
  if (typeof value === 'number' && Number.isInteger(value) && value >= 0 && value < names.length) {
    return names[value]
  }
  if (typeof value === 'string') {
    const normalized = value.toLowerCase()
    const stripped = normalized.startsWith(prefix) ? normalized.slice(prefix.length) : normalized
    const index = (names as readonly string[]).indexOf(stripped)
    if (index >= 0) return names[index]
  }
  return names[0]
}

// ---------------------------------------------------------------------------
// JSON decoding (R2.2)
// ---------------------------------------------------------------------------

/**
 * Decode an OTLP/JSON `ExportTraceServiceRequest` body into spans. Anything
 * that parses but does not match the OTLP shape throws `OtlpDecodeError`
 * with a path to the offending field.
 */
export function decodeOtlpJson(body: string): OtlpSpan[] {
  let parsed: unknown
  try {
    parsed = JSON.parse(body)
  } catch (err) {
    throw new OtlpDecodeError(`OTLP JSON body does not parse: ${message(err)}`)
  }

  const request = expectObject(parsed, 'OTLP payload')
  const resourceSpans = request.resourceSpans
  if (resourceSpans === undefined || resourceSpans === null) return []
  if (!Array.isArray(resourceSpans)) {
    throw new OtlpDecodeError('resourceSpans must be an array')
  }

  const spans: OtlpSpan[] = []
  for (const [rsIndex, rsRaw] of resourceSpans.entries()) {
    const rsWhere = `resourceSpans[${rsIndex}]`
    const resourceSpan = expectObject(rsRaw, rsWhere)

    const resourceRaw = resourceSpan.resource
    const resource =
      resourceRaw === undefined || resourceRaw === null
        ? {}
        : attributesFromJson(
            expectObject(resourceRaw, `${rsWhere}.resource`).attributes,
            `${rsWhere}.resource.attributes`,
          )

    const scopeSpansRaw = resourceSpan.scopeSpans
    if (scopeSpansRaw === undefined || scopeSpansRaw === null) continue
    if (!Array.isArray(scopeSpansRaw)) {
      throw new OtlpDecodeError(`${rsWhere}.scopeSpans must be an array`)
    }

    for (const [ssIndex, ssRaw] of scopeSpansRaw.entries()) {
      const ssWhere = `${rsWhere}.scopeSpans[${ssIndex}]`
      const scopeSpans = expectObject(ssRaw, ssWhere)
      const scope = scopeFromJson(scopeSpans.scope, `${ssWhere}.scope`)

      const spansRaw = scopeSpans.spans
      if (spansRaw === undefined || spansRaw === null) continue
      if (!Array.isArray(spansRaw)) {
        throw new OtlpDecodeError(`${ssWhere}.spans must be an array`)
      }
      for (const [sIndex, sRaw] of spansRaw.entries()) {
        spans.push(spanFromJson(sRaw, resource, scope, `${ssWhere}.spans[${sIndex}]`))
      }
    }
  }
  return spans
}

export function decodeOtlpLogJson(body: string): OtlpLog[] {
  let parsed: unknown
  try {
    parsed = JSON.parse(body)
  } catch (err) {
    throw new OtlpDecodeError(`OTLP JSON body does not parse: ${message(err)}`)
  }
  const request = expectObject(parsed, 'OTLP payload')
  const resources = request.resourceLogs
  if (resources === undefined || resources === null) return []
  if (!Array.isArray(resources)) throw new OtlpDecodeError('resourceLogs must be an array')
  const logs: OtlpLog[] = []
  for (const [ri, raw] of resources.entries()) {
    const where = `resourceLogs[${ri}]`
    const resourceLog = expectObject(raw, where)
    const resourceRaw = resourceLog.resource
    const resource =
      resourceRaw === undefined || resourceRaw === null
        ? {}
        : attributesFromJson(expectObject(resourceRaw, `${where}.resource`).attributes, `${where}.resource.attributes`)
    const scopes = resourceLog.scopeLogs
    if (scopes === undefined || scopes === null) continue
    if (!Array.isArray(scopes)) throw new OtlpDecodeError(`${where}.scopeLogs must be an array`)
    for (const [si, scopeRaw] of scopes.entries()) {
      const scopeWhere = `${where}.scopeLogs[${si}]`
      const scopeLog = expectObject(scopeRaw, scopeWhere)
      const scope = scopeFromJson(scopeLog.scope, `${scopeWhere}.scope`)
      const records = scopeLog.logRecords
      if (records === undefined || records === null) continue
      if (!Array.isArray(records)) throw new OtlpDecodeError(`${scopeWhere}.logRecords must be an array`)
      for (const [li, recordRaw] of records.entries()) {
        const recordWhere = `${scopeWhere}.logRecords[${li}]`
        const record = expectObject(recordRaw, recordWhere)
        const time = nanosToBigInt(record.timeUnixNano, `${recordWhere}.timeUnixNano`)
        const attributes = attributesFromJson(record.attributes, `${recordWhere}.attributes`)
        const session = attributes.session_id
        const sessionId = typeof session === 'string' ? session : null
        const traceId = record.traceId === undefined || record.traceId === null || record.traceId === ''
          ? null : idToHex(record.traceId, 16, `${recordWhere}.traceId`)
        const spanId = record.spanId === undefined || record.spanId === null || record.spanId === ''
          ? null : idToHex(record.spanId, 8, `${recordWhere}.spanId`)
        const body = record.body === undefined || record.body === null ? null : String(anyValueFromJson(record.body, `${recordWhere}.body`))
        const eventName = typeof record.eventName === 'string' ? record.eventName : undefined
        logs.push({
          logId: deriveLogId({
            traceId,
            spanId,
            sessionId,
            timeUnixNano: time,
            body,
            attributes,
            eventName,
          }),
          traceId,
          spanId,
          sessionId,
          timestamp: nanosToIsoTimestamp(time),
          body,
          attributes,
          resource,
          scope,
        })
      }
    }
  }
  return logs
}

function scopeFromJson(value: unknown, where: string): OtlpScope {
  if (value === undefined || value === null) return {}
  const scope = expectObject(value, where)
  const result: OtlpScope = {}
  if (scope.name !== undefined && scope.name !== null) {
    result.name = requiredString(scope.name, `${where}.name`)
  }
  if (scope.version !== undefined && scope.version !== null) {
    result.version = requiredString(scope.version, `${where}.version`)
  }
  return result
}

function spanFromJson(
  raw: unknown,
  resource: Record<string, unknown>,
  scope: OtlpScope,
  where: string,
): OtlpSpan {
  const span = expectObject(raw, where)
  const start = nanosToBigInt(span.startTimeUnixNano, `${where}.startTimeUnixNano`)
  const end = nanosToBigInt(span.endTimeUnixNano, `${where}.endTimeUnixNano`)
  const parent =
    span.parentSpanId === undefined || span.parentSpanId === null || span.parentSpanId === ''
      ? null
      : idToHex(span.parentSpanId, 8, `${where}.parentSpanId`)
  return {
    traceId: idToHex(span.traceId, 16, `${where}.traceId`),
    spanId: idToHex(span.spanId, 8, `${where}.spanId`),
    parentSpanId: parent,
    name: requiredString(span.name, `${where}.name`),
    kind: enumName(span.kind, SPAN_KINDS, 'span_kind_'),
    startTimeUnixNano: start.toString(),
    endTimeUnixNano: end.toString(),
    timestamp: nanosToIsoTimestamp(start),
    durationMs: nanosDeltaToMs(start, end),
    status: statusFromJson(span.status, `${where}.status`),
    attributes: attributesFromJson(span.attributes, `${where}.attributes`),
    resource,
    scope,
  }
}

function statusFromJson(value: unknown, where: string): OtlpStatus {
  if (value === undefined || value === null) return { code: 'unset' }
  const status = expectObject(value, where)
  const result: OtlpStatus = { code: enumName(status.code, STATUS_CODES, 'status_code_') }
  // The proto field is `message`; `description` is accepted because some
  // hand-rolled collectors send it.
  const text = status.message ?? status.description
  if (text !== undefined && text !== null && text !== '') {
    result.message = requiredString(text, `${where}.message`)
  }
  return result
}

function attributesFromJson(raw: unknown, where: string): Record<string, unknown> {
  if (raw === undefined || raw === null) return {}
  if (!Array.isArray(raw)) {
    throw new OtlpDecodeError(`${where} must be an array of key/value pairs`)
  }
  const result: Record<string, unknown> = {}
  for (const [index, entryRaw] of raw.entries()) {
    const entryWhere = `${where}[${index}]`
    const entry = expectObject(entryRaw, entryWhere)
    const key = requiredString(entry.key, `${entryWhere}.key`)
    result[key] = anyValueFromJson(entry.value, `${entryWhere}.value`)
  }
  return result
}

/**
 * Resolve an OTLP JSON `AnyValue`. The canonical oneof is required — a
 * value object with none of its fields set throws rather than silently
 * becoming null, because a mistyped key (`stringvalue`) in a hand-rolled
 * collector must surface, not vanish. Bare primitives are accepted for the
 * same hand-rolled payloads.
 */
function anyValueFromJson(value: unknown, where: string): unknown {
  if (value === undefined || value === null) return null
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
    return value
  }
  const any = expectObject(value, where)
  if ('stringValue' in any) return requiredString(any.stringValue, `${where}.stringValue`)
  if ('boolValue' in any) {
    if (typeof any.boolValue !== 'boolean') {
      throw new OtlpDecodeError(`${where}.boolValue must be a boolean`)
    }
    return any.boolValue
  }
  if ('intValue' in any) return Number(nanosToBigInt(any.intValue, `${where}.intValue`))
  if ('doubleValue' in any) {
    if (typeof any.doubleValue !== 'number') {
      throw new OtlpDecodeError(`${where}.doubleValue must be a number`)
    }
    return any.doubleValue
  }
  if ('arrayValue' in any) {
    const values = expectObject(any.arrayValue, `${where}.arrayValue`).values
    if (!Array.isArray(values)) {
      throw new OtlpDecodeError(`${where}.arrayValue.values must be an array`)
    }
    return values.map((item, index) => anyValueFromJson(item, `${where}.arrayValue.values[${index}]`))
  }
  if ('kvlistValue' in any) {
    const values = expectObject(any.kvlistValue, `${where}.kvlistValue`).values
    return attributesFromJson(values, `${where}.kvlistValue.values`)
  }
  if ('bytesValue' in any) {
    if (typeof any.bytesValue !== 'string') {
      throw new OtlpDecodeError(`${where}.bytesValue must be a base64 string`)
    }
    return bytesValueToHex(any.bytesValue, where)
  }
  throw new OtlpDecodeError(
    `${where} is an AnyValue with none of its oneof fields set ` +
      `(expected stringValue, boolValue, intValue, doubleValue, arrayValue, kvlistValue or bytesValue)`,
  )
}

/** OTLP JSON carries `bytes` as base64; hex is accepted as a hand-rolled form. */
function bytesValueToHex(value: string, where: string): string {
  if (value.length % 2 === 0 && /^[0-9a-fA-F]*$/.test(value)) return value.toLowerCase()
  return bytesToHex(base64ToBytes(value, where))
}

// ---------------------------------------------------------------------------
// Protobuf decoding (R2.3)
// ---------------------------------------------------------------------------
// The OTLP trace schema as field numbers, from opentelemetry-proto
// trace/v1/trace.proto and common/v1/common.proto:
//
//   ExportTraceServiceRequest { repeated ResourceSpans resource_spans = 1 }
//   ResourceSpans { Resource resource = 1; repeated ScopeSpans scope_spans = 2 }
//   Resource { repeated KeyValue attributes = 1 }
//   ScopeSpans { InstrumentationScope scope = 1; repeated Span spans = 2 }
//   InstrumentationScope { string name = 1; string version = 2 }
//   Span { bytes trace_id = 1; bytes span_id = 2; bytes parent_span_id = 4;
//          string name = 5; SpanKind kind = 6;
//          fixed64 start_time_unix_nano = 7; fixed64 end_time_unix_nano = 8;
//          repeated KeyValue attributes = 9; Status status = 15 }
//   KeyValue { string key = 1; AnyValue value = 2 }
//   AnyValue oneof { string string_value = 1; bool bool_value = 2;
//                    int64 int_value = 3; double double_value = 4;
//                    ArrayValue array_value = 5; KeyValueList kvlist_value = 6;
//                    bytes bytes_value = 7 }
//   ArrayValue { repeated AnyValue values = 1 }
//   KeyValueList { repeated KeyValue values = 1 }
//   Status { string message = 2; StatusCode code = 3 }
//
// The reader below decodes the wire format into a generic field map;
// unknown fields (a newer OTLP revision) are skipped, which keeps the
// receiver forward-compatible without guessing at their meaning.

type PbValue = bigint | number | Uint8Array
/** A protobuf message as decoded: field number → values in wire order. */
type PbMessage = Map<number, PbValue[]>

class PbReader {
  private offset = 0

  constructor(private readonly buf: Uint8Array) {}

  get atEnd(): boolean {
    return this.offset === this.buf.length
  }

  take(count: number): Uint8Array {
    if (this.offset + count > this.buf.length) {
      throw new OtlpDecodeError('protobuf message is truncated')
    }
    const slice = this.buf.subarray(this.offset, this.offset + count)
    this.offset += count
    return slice
  }

  varint(): bigint {
    let value = 0n
    let shift = 0n
    for (;;) {
      const [byte] = this.take(1)
      value |= BigInt(byte & 0x7f) << shift
      if ((byte & 0x80) === 0) return value
      shift += 7n
      if (shift >= 64n) throw new OtlpDecodeError('protobuf varint exceeds 64 bits')
    }
  }

  /** Eight bytes, little-endian — the wire form of `fixed64` nanoseconds. */
  fixed64(): bigint {
    const bytes = this.take(8)
    let value = 0n
    for (let i = 7; i >= 0; i--) value = (value << 8n) | BigInt(bytes[i])
    return value
  }

  fixed32(): number {
    const bytes = this.take(4)
    let value = 0
    for (const byte of bytes) value = value * 256 + byte
    return value
  }

  tag(): { field: number; wire: number } {
    const key = this.varint()
    const field = Number(key >> 3n)
    const wire = Number(key & 7n)
    if (field < 1) throw new OtlpDecodeError('protobuf field number 0 is reserved')
    return { field, wire }
  }
}

function readMessage(buf: Uint8Array): PbMessage {
  const reader = new PbReader(buf)
  const fields: PbMessage = new Map()
  while (!reader.atEnd) {
    const { field, wire } = reader.tag()
    let value: PbValue
    if (wire === 0) value = reader.varint()
    else if (wire === 1) value = reader.fixed64()
    else if (wire === 2) value = reader.take(Number(reader.varint()))
    else if (wire === 5) value = reader.fixed32()
    else throw new OtlpDecodeError(`unsupported protobuf wire type ${wire}: OTLP never uses groups`)
    const existing = fields.get(field)
    if (existing === undefined) fields.set(field, [value])
    else existing.push(value)
  }
  return fields
}

function pbBytes(message: PbMessage | undefined, field: number): Uint8Array | undefined {
  const first = message?.get(field)?.[0]
  return first instanceof Uint8Array ? first : undefined
}

function pbString(message: PbMessage | undefined, field: number): string | undefined {
  const bytes = pbBytes(message, field)
  return bytes === undefined ? undefined : Buffer.from(bytes).toString('utf8')
}

function pbBigInt(message: PbMessage | undefined, field: number): bigint | undefined {
  const first = message?.get(field)?.[0]
  return typeof first === 'bigint' ? first : undefined
}

function pbNumber(message: PbMessage | undefined, field: number): number | undefined {
  const value = pbBigInt(message, field)
  return value === undefined ? undefined : Number(value)
}

function pbMessage(message: PbMessage | undefined, field: number): PbMessage | undefined {
  const bytes = pbBytes(message, field)
  return bytes === undefined ? undefined : readMessage(bytes)
}

function pbMessages(message: PbMessage | undefined, field: number, where: string): PbMessage[] {
  return (message?.get(field) ?? []).map((value, index) => {
    if (!(value instanceof Uint8Array)) {
      throw new OtlpDecodeError(`${where}: field ${field} item ${index} is not length-delimited`)
    }
    return readMessage(value)
  })
}

/** Reinterpret the little-endian `fixed64` bits as the double they carry. */
function pbFixed64ToDouble(bits: bigint): number {
  const view = new DataView(new ArrayBuffer(8))
  for (let byte = 0; byte < 8; byte++) {
    view.setUint8(byte, Number((bits >> BigInt(8 * (7 - byte))) & 0xffn))
  }
  return view.getFloat64(0)
}

function pbAnyValue(message: PbMessage): unknown {
  const string = pbString(message, 1)
  if (string !== undefined) return string
  const bool = pbBigInt(message, 2)
  if (bool !== undefined) return bool !== 0n
  const int = pbBigInt(message, 3)
  if (int !== undefined) return Number(int)
  const double = pbBigInt(message, 4)
  if (double !== undefined) return pbFixed64ToDouble(double)
  const array = pbMessage(message, 5)
  if (array !== undefined) {
    return pbMessages(array, 1, 'ArrayValue.values').map((item) => pbAnyValue(item))
  }
  const kvlist = pbMessage(message, 6)
  if (kvlist !== undefined) return pbKeyValues(kvlist, 1)
  const bytes = pbBytes(message, 7)
  if (bytes !== undefined) return bytesToHex(bytes)
  return null
}

function pbKeyValues(message: PbMessage, field: number): Record<string, unknown> {
  const result: Record<string, unknown> = {}
  for (const pair of pbMessages(message, field, 'KeyValue list')) {
    const key = pbString(pair, 1)
    if (key === undefined || key.length === 0) continue
    const value = pbMessage(pair, 2)
    result[key] = value === undefined ? null : pbAnyValue(value)
  }
  return result
}

function requiredId(bytes: Uint8Array | undefined, byteLength: number, where: string): string {
  if (bytes === undefined || bytes.length !== byteLength) {
    throw new OtlpDecodeError(`${where} must be exactly ${byteLength} bytes`)
  }
  return bytesToHex(bytes)
}

/**
 * Decode an OTLP protobuf `ExportTraceServiceRequest` body into spans.
 * Structural damage — truncation, an impossible wire type, a short id —
 * throws `OtlpDecodeError`; unknown fields are skipped.
 */
export function decodeOtlpProtobuf(body: Uint8Array): OtlpSpan[] {
  const request = readMessage(body)
  const spans: OtlpSpan[] = []

  for (const [rsIndex, resourceSpan] of pbMessages(request, 1, 'resource_spans').entries()) {
    const rsWhere = `resource_spans[${rsIndex}]`
    const resourceMessage = pbMessage(resourceSpan, 1)
    const resource = resourceMessage === undefined ? {} : pbKeyValues(resourceMessage, 1)

    for (const [ssIndex, scopeSpans] of pbMessages(resourceSpan, 2, `${rsWhere}.scope_spans`).entries()) {
      const ssWhere = `${rsWhere}.scope_spans[${ssIndex}]`
      const scopeMessage = pbMessage(scopeSpans, 1)
      const scope: OtlpScope = {}
      if (scopeMessage !== undefined) {
        const name = pbString(scopeMessage, 1)
        const version = pbString(scopeMessage, 2)
        if (name !== undefined && name.length > 0) scope.name = name
        if (version !== undefined && version.length > 0) scope.version = version
      }

      for (const [sIndex, spanMessage] of pbMessages(scopeSpans, 2, `${ssWhere}.spans`).entries()) {
        spans.push(spanFromProtobuf(spanMessage, resource, scope, `${ssWhere}.spans[${sIndex}]`))
      }
    }
  }
  return spans
}

export function decodeOtlpLogProtobuf(body: Uint8Array): OtlpLog[] {
  const request = readMessage(body)
  const logs: OtlpLog[] = []
  for (const [ri, resourceLogs] of pbMessages(request, 1, 'resource_logs').entries()) {
    const where = `resource_logs[${ri}]`
    const resourceMessage = pbMessage(resourceLogs, 1)
    const resource = resourceMessage === undefined ? {} : pbKeyValues(resourceMessage, 1)
    for (const [, scopeLogs] of pbMessages(resourceLogs, 2, `${where}.scope_logs`).entries()) {
      const scopeMessage = pbMessage(scopeLogs, 1)
      const scope: OtlpScope = {}
      const name = pbString(scopeMessage, 1)
      const version = pbString(scopeMessage, 2)
      if (name) scope.name = name
      if (version) scope.version = version
      for (const [, logMessage] of pbMessages(scopeLogs, 2, `${where}.scope_logs.log_records`).entries()) {
        const time = pbBigInt(logMessage, 1)
        if (time === undefined) throw new OtlpDecodeError(`${where}.time_unix_nano is required`)
        const trace = pbBytes(logMessage, 9)
        const span = pbBytes(logMessage, 10)
        const attributes = pbKeyValues(logMessage, 6)
        const session = attributes.session_id
        const sessionId = typeof session === 'string' ? session : null
        const bodyMessage = pbMessage(logMessage, 5)
        const body = bodyMessage === undefined ? null : String(pbAnyValue(bodyMessage))
        const eventName = pbString(logMessage, 12)
        const traceId = trace === undefined ? null : requiredId(trace, 16, `${where}.trace_id`)
        const spanId = span === undefined ? null : requiredId(span, 8, `${where}.span_id`)
        logs.push({
          logId: deriveLogId({
            traceId,
            spanId,
            sessionId,
            timeUnixNano: time,
            body,
            attributes,
            eventName,
          }),
          traceId,
          spanId,
          sessionId,
          timestamp: nanosToIsoTimestamp(time),
          body,
          attributes,
          resource,
          scope,
        })
      }
    }
  }
  return logs
}

function spanFromProtobuf(
  message: PbMessage,
  resource: Record<string, unknown>,
  scope: OtlpScope,
  where: string,
): OtlpSpan {
  const name = pbString(message, 5)
  if (name === undefined || name.length === 0) {
    throw new OtlpDecodeError(`${where}.name is required`)
  }
  const start = pbBigInt(message, 7)
  if (start === undefined) throw new OtlpDecodeError(`${where}.start_time_unix_nano is required`)
  const end = pbBigInt(message, 8)
  if (end === undefined) throw new OtlpDecodeError(`${where}.end_time_unix_nano is required`)

  const parent = pbBytes(message, 4)
  const statusMessage = pbMessage(message, 15)
  const status: OtlpStatus = {
    code: enumName(pbNumber(statusMessage, 3), STATUS_CODES, 'status_code_'),
  }
  const statusText = pbString(statusMessage, 2)
  if (statusText !== undefined && statusText.length > 0) status.message = statusText

  return {
    traceId: requiredId(pbBytes(message, 1), 16, `${where}.trace_id`),
    spanId: requiredId(pbBytes(message, 2), 8, `${where}.span_id`),
    parentSpanId:
      parent !== undefined && parent.length > 0
        ? requiredId(parent, 8, `${where}.parent_span_id`)
        : null,
    name,
    kind: enumName(pbNumber(message, 6), SPAN_KINDS, 'span_kind_'),
    startTimeUnixNano: start.toString(),
    endTimeUnixNano: end.toString(),
    timestamp: nanosToIsoTimestamp(start),
    durationMs: nanosDeltaToMs(start, end),
    status,
    attributes: pbKeyValues(message, 9),
    resource,
    scope,
  }
}

// ---------------------------------------------------------------------------
// The receiver
// ---------------------------------------------------------------------------

export type OtlpReceiverOptions = {
  /** OTLP standard 4318 unless configured otherwise (R2.1). */
  port?: number
  /**
   * Bound interface. Localhost by default so nothing but on-machine
   * collectors can reach it — telemetry must not leave the machine (R12.1).
   */
  host?: string
  store?: OtlpReceiverStore
  maxBodyBytes?: number
  /** Override port-occupant discovery; tests pin this to make the report deterministic. */
  discoverOccupants?: PortOccupantDiscovery
}

class BodyTooLargeError extends Error {}

/**
 * Decodes OTLP trace payloads and writes the resulting spans to a store.
 * Batching and backpressure belong to `IngestWriter` (task 6.2), which
 * implements the store port; a `start()` on an occupied port rejects
 * with a `PortConflictError` naming the occupying process where
 * discoverable (R2.4) — never a silent failure, never a quiet rebind.
 */
export class OtlpReceiver {
  readonly store: OtlpReceiverStore

  private readonly listenPort: number
  private readonly hostname: string
  private readonly maxBodyBytes: number
  private readonly discoverOccupants: PortOccupantDiscovery
  private readonly server: Server

  constructor(opts: OtlpReceiverOptions = {}) {
    this.listenPort = opts.port ?? DEFAULT_OTLP_PORT
    this.hostname = opts.host ?? '127.0.0.1'
    this.store = opts.store ?? new InMemorySpanStore()
    this.maxBodyBytes = opts.maxBodyBytes ?? DEFAULT_MAX_BODY_BYTES
    this.discoverOccupants = opts.discoverOccupants ?? discoverPortOccupants
    this.server = createServer((request, response) => {
      void this.handleRequest(request, response)
    })
  }

  /** The port served once started; with `port: 0` this is the assigned ephemeral port. */
  get port(): number {
    const address = this.server.address()
    return typeof address === 'object' && address !== null ? address.port : this.listenPort
  }

  /** True while the server is bound; a failed `start()` leaves this false. */
  get listening(): boolean {
    return this.server.listening
  }

  async start(): Promise<void> {
    if (this.server.listening) return
    try {
      await new Promise<void>((resolve, reject) => {
        const onListening = () => {
          this.server.off('error', onError)
          resolve()
        }
        const onError = (err: Error) => {
          this.server.off('listening', onListening)
          reject(err)
        }
        this.server.once('listening', onListening)
        this.server.once('error', onError)
        this.server.listen(this.listenPort, this.hostname)
      })
    } catch (err) {
      // R2.4: an already-bound configured port is a conflict to report —
      // with the occupying process where discoverable — never a silent
      // failure, and never a quiet rebind to some other port.
      if (isErrnoException(err) && err.code === 'EADDRINUSE') {
        const occupants = await this.discoverOccupants(this.listenPort, this.hostname)
        throw new PortConflictError(this.listenPort, this.hostname, occupants, err)
      }
      throw err
    }
  }

  async stop(): Promise<void> {
    if (!this.server.listening) return
    const closed = new Promise<void>((resolve, reject) => {
      const onClose = () => {
        this.server.off('error', onError)
        resolve()
      }
      const onError = (err: Error) => {
        this.server.off('close', onClose)
        reject(err)
      }
      this.server.once('close', onClose)
      this.server.once('error', onError)
    })
    this.server.close()
    // A keep-alive client (fetch's default pool) would otherwise hold
    // close() open indefinitely.
    this.server.closeAllConnections()
    await closed
  }

  /**
   * The server's request handler. Responses are JSON regardless of request
   * encoding: `{ acceptedSpans }` on success, `{ error: { code, message } }`
   * with an HTTP diagnostic status on failure.
   */
  async handleRequest(req: IncomingMessage, res: ServerResponse): Promise<void> {
    try {
      await this.dispatch(req, res)
    } catch (err) {
      if (err instanceof BodyTooLargeError) {
        respondJson(res, 413, {
          error: {
            code: 'OTLP_PAYLOAD_TOO_LARGE',
            message: `body exceeds the ${this.maxBodyBytes}-byte limit`,
          },
        })
      } else if (err instanceof OtlpDecodeError) {
        // Malformed OTLP: reject with a diagnostic; nothing was written.
        respondJson(res, 400, { error: { code: 'OTLP_MALFORMED_PAYLOAD', message: err.message } })
      } else {
        respondJson(res, 500, { error: { code: 'OTLP_INTERNAL', message: message(err) } })
      }
    }
  }

  private async dispatch(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const path = (req.url ?? '/').split('?')[0]
    if (path !== OTLP_TRACES_PATH && path !== OTLP_LOGS_PATH) {
      respondJson(res, 404, {
        error: {
          code: 'OTLP_NOT_FOUND',
            message: `unknown endpoint ${req.method} ${path}: this receiver serves POST ${OTLP_TRACES_PATH} and ${OTLP_LOGS_PATH}`,
        },
      })
      return
    }
    if (req.method !== 'POST') {
      res.setHeader('Allow', 'POST')
      respondJson(res, 405, {
        error: { code: 'OTLP_METHOD_NOT_ALLOWED', message: `${req.method} is not supported: use POST` },
      })
      return
    }
    const encoding = encodingForContentType(req.headers['content-type'])
    if (encoding === undefined) {
      respondJson(res, 415, {
        error: {
          code: 'OTLP_UNSUPPORTED_MEDIA_TYPE',
          message: `content type ${headerValue(req.headers['content-type']) || '(none)'} is not supported: use application/json or application/x-protobuf`,
        },
      })
      return
    }

    const body = await this.readBody(req)
    if (path === OTLP_LOGS_PATH) {
      const logs = encoding === 'json' ? decodeOtlpLogJson(body.toString('utf8')) : decodeOtlpLogProtobuf(body)
      const nested = this.store as unknown as { logs?: { writeLogs(logs: readonly OtlpLog[]): void } }
      const sink = nested.logs ?? (this.store as Partial<OtlpSignalStore>)
      if (typeof sink.writeLogs !== 'function') {
        throw new Error('OTLP logs require a signal-aware receiver store')
      }
      sink.writeLogs(logs)
      respondJson(res, 200, { acceptedLogs: logs.length })
      return
    }
    const spans = encoding === 'json' ? decodeOtlpJson(body.toString('utf8')) : decodeOtlpProtobuf(body)
    const nested = this.store as unknown as { spans?: OtlpSpanStore }
    const spanSink = nested.spans !== undefined && typeof nested.spans.write === 'function'
      ? nested.spans
      : this.store as OtlpSpanStore
    spanSink.write(spans)
    respondJson(res, 200, { acceptedSpans: spans.length })
  }

  private readBody(req: IncomingMessage): Promise<Buffer> {
    return new Promise<Buffer>((resolve, reject) => {
      const chunks: Buffer[] = []
      let received = 0
      let settled = false
      const fail = (err: unknown): void => {
        if (settled) return
        settled = true
        reject(err instanceof Error ? err : new Error(message(err)))
      }
      req.on('data', (chunk: Buffer) => {
        received += chunk.length
        if (received > this.maxBodyBytes) {
          // Keep draining so the client can finish and read the 413, but
          // buffer nothing further.
          fail(new BodyTooLargeError())
          return
        }
        chunks.push(chunk)
      })
      req.on('end', () => {
        if (settled) return
        settled = true
        resolve(Buffer.concat(chunks))
      })
      req.on('error', fail)
    })
  }
}

function respondJson(res: ServerResponse, status: number, body: Record<string, unknown>): void {
  res.writeHead(status, { 'content-type': 'application/json' })
  res.end(JSON.stringify(body))
}

function headerValue(value: string | string[] | undefined): string {
  return Array.isArray(value) ? value.join(', ') : (value ?? '')
}

/**
 * Media-type detection for the two encodings this receiver accepts (R2.2,
 * R2.3). Parameters (`; charset=utf-8`) are tolerated; `application/protobuf`
 * is accepted alongside the OTLP-canonical `application/x-protobuf`.
 */
function encodingForContentType(value: string | string[] | undefined): 'json' | 'protobuf' | undefined {
  const mediaType = headerValue(value).split(';')[0].trim().toLowerCase()
  if (mediaType === 'application/json') return 'json'
  if (mediaType === 'application/x-protobuf' || mediaType === 'application/protobuf') {
    return 'protobuf'
  }
  return undefined
}
