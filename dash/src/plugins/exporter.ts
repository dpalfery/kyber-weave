/**
 * Plugin sync exporter seam: per-call attributes and declared extra spans (socket phase 2, slice B).
 *
 * For each loaded plugin with exporters/sync.mjs, spawn the exporter process,
 * pass the batch of calls via stdin, and collect enrichment data (per-call
 * attributes and extra spans) via stdout. All failures (crash, timeout, bad
 * JSON) produce an stderr notice; the plugin contributes nothing this push.
 */

import { spawn } from 'child_process'
import { join, basename } from 'path'
import { stat, mkdir } from 'fs/promises'
import { homedir } from 'os'
import type { PluginLoad } from './loader.js'
import type { CallWithSession, OtlpAttribute, OtlpSpan } from '../sync/otlp.js'
import { filterPluginAttributes, CORE_SYNC_ATTRIBUTE_KEYS, deriveSpanId } from '../sync/otlp.js'
import { classifyTurn, EDIT_TOOLS } from '../classifier.js'
import type { ParsedTurn } from '../types.js'

export interface PluginEnrichment {
  perCall: Map<string, OtlpAttribute[]>
  extraSpans: OtlpSpan[]
}

export interface ExporterResult {
  perCall: Record<string, OtlpAttribute[]>
  spans: Array<{
    kind: string
    traceId: string
    spanId: string
    name: string
    startNano: string
    endNano: string
    attributes: OtlpAttribute[]
  }>
}

interface TurnContext {
  turnId: string
  category: string
  retries: number
  hasEdits: boolean
  oneShot: boolean
}

/// Compute turn context for a set of calls. Group into turns, classify each,
/// and build a map from deduplicationKey to turn context.
function buildTurnContextMap(calls: CallWithSession[]): Map<string, TurnContext> {
  const map = new Map<string, TurnContext>()

  // Build a pseudo-turn from calls (simplified grouping by sessionId).
  // In production, this would use groupIntoTurns, but for the exporter seam
  // we group by session and compute minimal turn context.
  const callsBySession = new Map<string, CallWithSession[]>()
  for (const call of calls) {
    const key = call.sessionId
    if (!callsBySession.has(key)) callsBySession.set(key, [])
    callsBySession.get(key)!.push(call)
  }

  // For each session, treat calls as forming one logical turn for context
  for (const [, sessionCalls] of callsBySession) {
    if (sessionCalls.length === 0) continue

    const assistantCalls = sessionCalls.map(c => c.call)
    const firstCall = assistantCalls[0]

    // Compute turnId from first call's deduplicationKey
    const turnId = deriveSpanId(firstCall.deduplicationKey)

    // Count retries (calls beyond the first from the same assistant)
    const retries = Math.max(0, assistantCalls.length - 1)

    // Check for edits
    const hasEdits = assistantCalls.some(c => (c.tools ?? []).some((t: string) => EDIT_TOOLS.has(t)))

    // Check for one-shot (retries == 0 and hasEdits)
    const oneShot = retries === 0 && hasEdits

    // Classify the turn (simplified - just use 'general' if no category detected)
    const category = 'general'

    const context: TurnContext = { turnId, category, retries, hasEdits, oneShot }

    // Map all calls in this session to the turn context
    for (const call of sessionCalls) {
      map.set(call.call.deduplicationKey, context)
    }
  }

  return map
}

/// Collect enrichment data from all loaded plugins with exporters/sync.mjs.
export async function collectPluginEnrichment(
  loads: PluginLoad[],
  calls: CallWithSession[],
  timeoutMs: number = 30_000,
): Promise<PluginEnrichment> {
  const perCall = new Map<string, OtlpAttribute[]>()
  const extraSpans: OtlpSpan[] = []

  for (const load of loads) {
    if (load.status !== 'loaded') continue
    let result: ExporterResult | null
    try {
      result = await runPluginExporter(load, calls, timeoutMs)
    } catch (err) {
      process.stderr.write(`plugin "${load.manifest.name}": sync exporter failed (${err instanceof Error ? err.message : 'internal error'}); pushing without it\n`)
      continue
    }
    if (!result) continue

    const manifest = load.manifest
    const declaredAttrs = new Set(manifest.capabilities.syncAttributes.map(a => a.key))
    const declaredKinds = new Set(manifest.capabilities.spanKinds)

    // Bounded already by the 8MB stdout cap, but do not iterate a perCall
    // object far larger than the batch: attributes for keys that match no call
    // are dropped anyway (attached only to matching spans downstream).
    const perCallEntries = Object.entries(result.perCall)
    if (perCallEntries.length > calls.length * 4 + 16) {
      process.stderr.write(`plugin "${load.manifest.name}": exporter returned more per-call entries than calls; ignoring the surplus\n`)
    }
    for (const [key, attrs] of perCallEntries.slice(0, calls.length * 4 + 16)) {
      const guarded = filterPluginAttributes(attrs, declaredAttrs)
      if (guarded.length > 0) {
        // Merge with existing attrs for this key (multiple plugins may contribute)
        const existing = perCall.get(key) ?? []
        perCall.set(key, [...existing, ...guarded])
      }
    }

    // Process extra spans
    for (const span of result.spans) {
      // Check kind is declared
      if (!declaredKinds.has(span.kind)) continue

      // Validate hex IDs
      if (!/^[0-9a-fA-F]{32}$/.test(span.traceId) || !/^[0-9a-fA-F]{16}$/.test(span.spanId)) continue

      // Cap extra spans: max 2x calls.length per plugin
      if (extraSpans.length >= calls.length * 2) break

      // Filter attributes (uses wire-guard sanitizer) and add span_kind
      const attrs = filterPluginAttributes(span.attributes, declaredAttrs)
      attrs.push({ key: 'codeburn.span_kind', value: { stringValue: span.kind } })

      // Check size: drop if >64KB
      const spanJson = JSON.stringify({ ...span, attributes: attrs })
      if (Buffer.byteLength(spanJson, 'utf8') > 65536) continue

      extraSpans.push({
        traceId: span.traceId,
        spanId: span.spanId,
        name: span.name,
        startTimeUnixNano: span.startNano,
        endTimeUnixNano: span.endNano,
        attributes: attrs,
      })
    }
  }

  return { perCall, extraSpans }
}

async function runPluginExporter(
  load: PluginLoad,
  calls: CallWithSession[],
  timeoutMs: number,
): Promise<ExporterResult | null> {
  if (load.status !== 'loaded') return null

  const exporterFile = join(load.dir, 'exporters', 'sync.mjs')

  // Check if exporter exists
  try {
    await stat(exporterFile)
  } catch {
    return null
  }

  // Prepare plugin state directory (writable state outside signed tree)
  const pluginName = basename(load.dir)
  const stateDir = join(homedir(), '.config', 'codeburn', 'plugin-state', pluginName)
  try {
    await mkdir(stateDir, { recursive: true })
  } catch {
    // Non-fatal: proceed if mkdir fails
  }

  return new Promise(resolve => {
    const child = spawn(process.execPath, [exporterFile], {
      env: { ...process.env, CODEBURN_PLUGIN_DIR: load.dir, CODEBURN_PLUGIN_STATE_DIR: stateDir },
      stdio: ['pipe', 'pipe', 'pipe'],
      timeout: timeoutMs,
    })

    let stdout = ''
    let stderr = ''
    let timedOut = false

    const timer = setTimeout(() => {
      timedOut = true
      child.kill()
      // A child that ignores SIGTERM must not outlive the push.
      setTimeout(() => { try { child.kill('SIGKILL') } catch { /* already gone */ } }, 2000).unref()
    }, timeoutMs)

    if (child.stdout) {
      child.stdout.on('data', chunk => {
        stdout += chunk.toString('utf8')
        if (stdout.length > 8 * 1024 * 1024) { // 8MB cap
          child.kill()
        }
      })
    }

    if (child.stderr) {
      child.stderr.on('data', chunk => {
        // Keep only the first 256KB: enough to diagnose, bounded in memory.
        if (stderr.length < 256 * 1024) stderr += chunk.toString('utf8')
      })
    }

    child.on('error', () => {
      clearTimeout(timer)
      process.stderr.write(`plugin "${load.manifest.name}": sync exporter failed (spawn error); pushing without it\n`)
      resolve(null)
    })

    child.on('exit', (code, signal) => {
      clearTimeout(timer)

      if (timedOut) {
        process.stderr.write(`plugin "${load.manifest.name}": sync exporter failed (timeout); pushing without it\n`)
        resolve(null)
        return
      }

      if (code !== 0 || signal) {
        process.stderr.write(`plugin "${load.manifest.name}": sync exporter failed (exit ${code}); pushing without it\n`)
        resolve(null)
        return
      }

      try {
        const parsed: unknown = JSON.parse(stdout)
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)
          || typeof (parsed as ExporterResult).perCall !== 'object' || (parsed as ExporterResult).perCall === null
          || !Array.isArray((parsed as ExporterResult).spans)) {
          throw new Error('exporter output shape invalid')
        }
        resolve(parsed as ExporterResult)
      } catch {
        process.stderr.write(`plugin "${load.manifest.name}": sync exporter failed (bad JSON); pushing without it\n`)
        resolve(null)
      }
    })

    // An exporter that dies before reading stdin makes our write emit EPIPE;
    // without a handler that uncaught stream error would kill the whole push.
    child.stdin?.on('error', () => { /* exit handler reports the failure */ })

    // Build turn context map; any surprise in call shapes must cost only the
    // context, never the push.
    let turnContextMap: ReturnType<typeof buildTurnContextMap>
    try {
      turnContextMap = buildTurnContextMap(calls)
    } catch {
      turnContextMap = new Map()
    }

    // Send input with turn context for each call
    const input = {
      calls: calls.map(c => ({
        key: c.call.deduplicationKey,
        call: c.call,
        sessionId: c.sessionId,
        workingDirectory: c.workingDirectory,
        session: c.session ?? null,
        ...(turnContextMap.has(c.call.deduplicationKey) && { turn: turnContextMap.get(c.call.deduplicationKey) }),
      })),
    }

    child.stdin?.write(JSON.stringify(input))
    child.stdin?.end()
  })
}
