// One-line wording for engine outcomes, shared by `kyberdash engine ensure`, `capture enable`
// and `capture status`, so the tokens an installer parses are written in exactly one place.

import type { EngineEnsureResult } from './ensure.js'
import type { EngineRecord } from './instance.js'

/** What `queryEngine` answers: a record, no engine, or a holder that does not answer. */
export type EngineProbeResult = EngineRecord | null | 'unresponsive'

/** The remedy for an engine that outlived an upgrade. */
export const ENGINE_UPDATE_REMEDY = 'run `kyber-weave update` to restart the engine on the current version'

export type EngineOutcomeLine = { stream: 'stdout' | 'stderr'; line: string; ok: boolean }

/**
 * `engine: attached <version>`, `engine: replaced <old> -> <new>`, `engine: started <version>`
 * (an engine appeared or vanished between the check and the ensure), or `engine: failed <reason>`.
 */
export function formatEnsureOutcome(result: EngineEnsureResult): EngineOutcomeLine {
  switch (result.outcome) {
    case 'attached':
      return { stream: 'stdout', ok: true, line: `engine: attached ${result.record.version}` }
    case 'started':
      return { stream: 'stdout', ok: true, line: `engine: started ${result.record.version}` }
    case 'replaced':
      return { stream: 'stdout', ok: true, line: `engine: replaced ${result.previous.version} -> ${result.record.version}` }
    case 'failed':
      return { stream: 'stderr', ok: false, line: `engine: failed ${result.reason}` }
  }
}

/** `capture status` engine lines: the running engine's version against the CLI's own. */
export function engineStatusLines(probe: EngineProbeResult, cliVersion: string): string[] {
  if (probe === null) return [`engine: not-running (cli ${cliVersion})`]
  if (probe === 'unresponsive') return [`engine: unresponsive (cli ${cliVersion})`]
  if (probe.version === cliVersion) return [`engine: running ${probe.version}, cli ${cliVersion}`]
  return [`engine: running ${probe.version}, cli ${cliVersion} (differs)`, `  ${ENGINE_UPDATE_REMEDY}`]
}
