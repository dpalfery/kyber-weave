// The Factory (Droid) pairing split.
//
// Factory is ONE harness reached through TWO capture sources, and a session
// both sources observed has to say which source owns which half of the
// picture:
//
//   * the PLUGIN source — the Kyber-owned Factory hook plugin — knows the
//     TURNS: the calls the session made, the spans they hang under, how long
//     they took. It reports no token counter at all;
//   * the FILE source — the Factory transcript files and their companion
//     settings file — knows the COUNTERS: the input, output, cache and
//     thinking tokens the session spent.
//
// The split is decided here, so the rule has one home the session derivation
// can be held to. This module is a LEAF: it imports no behaviour at all,
// only the shape of a record, so the rule cannot drag the engine in with it.

import type { CanonicalRecord } from './types.js'

/**
 * The harness identity Factory's records carry.
 *
 * The same identity `canon/adapters/droid.ts` names as `DROID_HARNESS`. It is
 * spelled out here rather than imported because this module stays a leaf: it
 * imports no behaviour, so the rule cannot drag the engine in with it.
 */
const DROID_HARNESS = 'droid'

/**
 * The telemetry source the Kyber-owned Factory plugin's export arrives under.
 *
 * The plugin names itself in its OTLP resource, and that name is what becomes
 * a record's `source`: the canon seam reads `resource['service.name']` and
 * nothing else. The name is the plugin's own `FACTORY_SERVICE_NAME`
 * (`dash/src/capture/harnesses/factory-plugin/entry.ts`).
 *
 * It is spelled out here rather than imported for the same reason the harness
 * identity above is: this module stays a leaf and imports no behaviour, so the
 * rule cannot drag the engine in with it. The plugin's `factory.*` envelope
 * namespace and its contract strings are repeated the same way by
 * `canon/adapters/droid-envelope.ts`, which states the reason: the canon
 * adapter never depends on the plugin.
 */
const PLUGIN_SOURCE = 'kyberdash-factory-plugin'

/**
 * The source prefix the synthesizer stamps on every file-sourced record.
 *
 * The same namespace `canon/measurability.ts` names as `FILE_SOURCE_PREFIX`:
 * a record synthesized from a harness's own session files carries it, and the
 * plugin source never does.
 */
const FILE_SOURCE_PREFIX = 'codeburn/'

/**
 * One session's records, split into the records that are its turns and the
 * records that are its counters.
 *
 * Each list holds only the records of its own source, in the order they
 * arrived: the split is by SOURCE, never by the order the records happened to
 * come in.
 */
export type DroidSessionSplit = {
  /** The records that are the session's turns. */
  turnRecords: CanonicalRecord[]
  /** The records that are the session's counters. */
  counterRecords: CanonicalRecord[]
}

/**
 * Split one session's records into the records that are its turns and the
 * records that are its counters.
 *
 * The rule is inert outside Factory: a record of another harness belongs to
 * neither list, so every other harness keeps the session derivation it has.
 *
 * A session only the file source observed claims no turn: the file record is
 * its counter source, and the session's own turn is the one the default path
 * finds, with no plugin turn to take it from.
 *
 * The split is TOTAL over Factory's records: every one of them lands in
 * exactly one bucket. The plugin's own source is the turn source, the file
 * source's prefix marks the counter source, and a Factory record of neither
 * source is a TURN record -- it is not a counter source, and a record that
 * fell through both branches would vanish from the session derivation
 * instead of being counted.
 */
export function splitDroidSessionRecords(records: readonly CanonicalRecord[]): DroidSessionSplit {
  const split: DroidSessionSplit = { turnRecords: [], counterRecords: [] }
  for (const record of records) {
    if (record.harness !== DROID_HARNESS) continue
    if (record.source === PLUGIN_SOURCE) {
      split.turnRecords.push(record)
      continue
    }
    if (record.source.startsWith(FILE_SOURCE_PREFIX)) {
      split.counterRecords.push(record)
      continue
    }
    // A Factory record of neither source is a turn record: the split is
    // total, so no record disappears.
    split.turnRecords.push(record)
  }
  return split
}
