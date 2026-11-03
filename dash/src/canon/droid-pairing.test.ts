// The Droid pairing split.
//
// Factory (Droid) is ONE harness observed through TWO capture sources, and a
// session both sources observed has to say which source owns which half of
// the picture:
//
//   * the PLUGIN source — the Kyber-owned Factory hook plugin — knows the
//     TURNS: the calls the session made, the spans they hang under, how
//     long they took. It reports no token counter at all;
//   * the FILE source — the Factory transcript files and their companion
//     settings file — knows the COUNTERS: the input, output, cache and
//     thinking tokens the session spent.
//
// The split helper is where that ownership is decided, so it is pinned here as
// a unit: given the records of one session, it names the records that are the
// session's turns and the records that are the session's counters.
//
// The contract this file pins:
//
//   * a session BOTH sources observed splits: the plugin records are the
//     turns, the file records are the counters, and neither list holds a
//     record of the other source;
//   * a session ONLY THE PLUGIN observed keeps its records as the turns and
//     names no counter record — the plugin reports no counter, so there is
//     nothing to count;
//   * a session ONLY THE FILE SOURCE observed claims no turn: the file
//     record is the counter source, and the session's own turn is the one
//     the default path finds, with no plugin turn to take it from;
//   * a session of ANOTHER HARNESS is not split at all;
//   * the plugin source is the name the plugin ITSELF reports — the
//     `service.name` its OTLP resource carries — so the pairing fires for
//     the source that really arrives, and the name is read from the
//     plugin's own source rather than repeated here;
//   * the split is TOTAL: every droid record lands in exactly one bucket,
//     and a record of neither source is a turn record rather than a record
//     that vanishes.
//
// The helper belongs in `./droid-pairing.ts`. It is loaded through a runtime
// specifier on purpose: a literal import would fail the typecheck while the
// helper is still missing, and the failure this file exists to state is a
// runtime one — the module is absent, or it does not answer with the split
// this contract describes.

import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

import type { CanonicalRecord } from './types.js'

/** The split the helper answers with: the session's turns and its counters. */
type DroidSessionSplit = {
  turnRecords: CanonicalRecord[]
  counterRecords: CanonicalRecord[]
}

/** The helper this file pins. */
type SplitDroidSessionRecords = (records: readonly CanonicalRecord[]) => DroidSessionSplit

/** The module the helper belongs in, and the specifier forms it may take. */
const HELPER_SPECIFIERS = ['./droid-pairing.js', './droid-pairing.ts']

/** Whether a loaded value answers with the split this contract describes. */
function isSplitHelper(value: unknown): value is SplitDroidSessionRecords {
  return typeof value === 'function'
}

/**
 * Load the split helper, or say why it could not be loaded.
 *
 * The specifier is a runtime value so that this file states the contract
 * without a build-time dependency on a module that does not exist yet: the
 * case fails with the reason, and once the module exists the same case pins
 * its behaviour.
 */
async function loadSplitHelper(): Promise<SplitDroidSessionRecords> {
  const failures: string[] = []
  for (const specifier of HELPER_SPECIFIERS) {
    let loaded: unknown
    try {
      loaded = await import(specifier)
    } catch (error) {
      failures.push(
        `${specifier} could not be loaded: ${error instanceof Error ? error.message : String(error)}`,
      )
      continue
    }
    const helper = (loaded as { splitDroidSessionRecords?: unknown }).splitDroidSessionRecords
    if (isSplitHelper(helper)) return helper
    failures.push(`${specifier} exports no splitDroidSessionRecords function`)
  }
  throw new Error(`the Droid pairing split helper is missing: ${failures.join('; ')}`)
}

/** A canonical record, built whole so no field is left to a cast. */
function canonicalRecord(over: {
  spanId: string
  name: string
  source: string
  harness?: string
}): CanonicalRecord {
  return {
    spanId: over.spanId,
    traceId: `trace-${over.spanId}`,
    parentSpanId: null,
    source: over.source,
    harness: over.harness ?? 'droid',
    sessionId: 'factory-session-1',
    name: over.name,
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-04-20T10:00:02.000Z',
    durationMs: 0,
    status: 'unset',
    tokens: {
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
      output: 0,
      reportedInput: 0,
      reportedOutput: 0,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

/** The plugin's own turn record — the source that knows the turns. */
const PLUGIN_RECORD = canonicalRecord({
  spanId: 'plugin-turn-factory-session-1',
  name: 'factory.turn',
  source: 'kyberdash-factory-plugin',
})

/** The file source's record — the source that knows the counters. */
const FILE_RECORD = canonicalRecord({
  spanId: 'synth:droid:factory-session-1:a1',
  name: 'droid:gpt-5',
  source: 'codeburn/droid',
})

/** A record of another harness, which the Droid rule does not touch. */
const OTHER_HARNESS_RECORD = canonicalRecord({
  spanId: 'claude-turn-1',
  name: 'claude.turn',
  source: 'codeburn/claude',
  harness: 'claude',
})

/** A droid record of NEITHER source: the plugin's is not it, and it is not file-sourced. */
const OTHER_SOURCE_RECORD = canonicalRecord({
  spanId: 'native-turn-factory-session-1',
  name: 'gen_ai.chat',
  source: 'droid-native-export',
})

// ---------------------------------------------------------------------------
// The plugin's own service name
// ---------------------------------------------------------------------------

/** The plugin bundle's own source directory, where its service name lives. */
const PLUGIN_SOURCE_DIR = fileURLToPath(
  new URL('../capture/harnesses/factory-plugin/', import.meta.url),
)

/**
 * The `service.name` the Factory plugin's own export reports.
 *
 * The name is the plugin's own constant, and it becomes the canon record's
 * source — so the pairing has to recognise THIS name and no other. It is read
 * from the plugin's own source rather than repeated here, so this file cannot
 * drift away from the plugin: rename the plugin's service and this file
 * follows it, and the pairing has to follow it too.
 *
 * The name must be ONE constant: two spellings of it in the plugin bundle is
 * itself the drift this guard exists to catch.
 */
function pluginServiceName(): string {
  const found = new Set<string>()
  for (const entry of readdirSync(PLUGIN_SOURCE_DIR, { withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.endsWith('.ts')) continue
    if (entry.name.endsWith('.test.ts')) continue
    const text = readFileSync(join(PLUGIN_SOURCE_DIR, entry.name), 'utf8')
    for (const match of text.matchAll(/SERVICE_NAME\s*=\s*'([^']+)'/g)) found.add(match[1]!)
  }
  if (found.size !== 1) {
    throw new Error(
      `the Factory plugin's service name must be ONE constant; found ${
        [...found].join(', ') || 'none'
      } in ${PLUGIN_SOURCE_DIR}`,
    )
  }
  return [...found][0]!
}

describe('the Droid pairing split', () => {
  it('splits a session both sources observed into the plugin turns and the file counters', async () => {
    const split = await loadSplitHelper()

    // The file record comes first: the split is by source, never by the
    // order the records happened to arrive in.
    const result = split([FILE_RECORD, PLUGIN_RECORD])

    expect(result.turnRecords.map((record) => record.spanId)).toEqual([PLUGIN_RECORD.spanId])
    expect(result.counterRecords.map((record) => record.spanId)).toEqual([FILE_RECORD.spanId])
  })

  it('keeps a session only the plugin observed as its turns and names no counter record', async () => {
    const split = await loadSplitHelper()

    const result = split([PLUGIN_RECORD])

    expect(result.turnRecords.map((record) => record.spanId)).toEqual([PLUGIN_RECORD.spanId])
    expect(result.counterRecords).toEqual([])
  })

  it('claims no turn for a session only the file source observed', async () => {
    const split = await loadSplitHelper()

    const result = split([FILE_RECORD])

    // The file record is the counter source here. It is not claimed as a
    // turn: the plugin is the source that reports turns, and this session
    // has none.
    expect(result.turnRecords).toEqual([])
  })

  it('splits nothing for a session of another harness', async () => {
    const split = await loadSplitHelper()

    const result = split([OTHER_HARNESS_RECORD])

    expect(result.turnRecords).toEqual([])
    expect(result.counterRecords).toEqual([])
  })

  it('treats the plugin\'s own service name as the plugin source, so the pairing fires for the name the plugin really reports', async () => {
    const split = await loadSplitHelper()
    const serviceName = pluginServiceName()

    // The record the plugin's own export produces: its source IS the name the
    // plugin reports, and the pairing has to recognise that name.
    const result = split([
      canonicalRecord({
        spanId: 'plugin-turn-real-source',
        name: 'factory.turn',
        source: serviceName,
      }),
    ])

    expect(result.turnRecords.map((record) => record.spanId)).toEqual(['plugin-turn-real-source'])
    expect(result.counterRecords).toEqual([])
  })

  it('splits a session the plugin\'s own service name and the file source both observed into the plugin turns and the file counters', async () => {
    const split = await loadSplitHelper()
    const serviceName = pluginServiceName()

    const pluginRecord = canonicalRecord({
      spanId: 'plugin-turn-real-source',
      name: 'factory.turn',
      source: serviceName,
    })

    const result = split([FILE_RECORD, pluginRecord])

    expect(result.turnRecords.map((record) => record.spanId)).toEqual([pluginRecord.spanId])
    expect(result.counterRecords.map((record) => record.spanId)).toEqual([FILE_RECORD.spanId])
  })

  it('keeps a droid record of neither source as a turn record, so no record vanishes', async () => {
    const split = await loadSplitHelper()

    // The record is Factory's, but it came from neither the plugin nor the
    // file source. It belongs to a bucket: the split is total, so a record
    // never falls through both branches and disappears.
    const result = split([OTHER_SOURCE_RECORD])

    expect(result.turnRecords.map((record) => record.spanId)).toEqual([OTHER_SOURCE_RECORD.spanId])
    expect(result.counterRecords).toEqual([])
  })
})
