// The Factory (Droid) capture replay: a recorded Factory hook sequence, replayed through the
// production plugin runner, the production OTLP receiver, the droid canon adapter, the canonical
// projection and the dashboard.
//
// REPLAY, NOT LIVE CAPTURE. The payloads under `fixtures/factory/` are recordings of Factory hook
// events, shaped by the field names the wire contract's mapper reads. No Droid agent runs here, so
// nothing in this file is evidence that a live Factory turn is captured; the live check is a
// separate, owner-run procedure.
//
// TWO SESSIONS, ONE STORE. The content consent is a per-run decision, so the scenario has to show
// both sides of it:
//
//   - the content-off session is replayed with the consent file saying content off, so the prompt
//     text never leaves the plugin;
//   - the consent file is then flipped and the content-on session is replayed, so the prompt text
//     travels.
//
// The consent FILE is what decides, so the runner is given no environment override: the file is the
// owner's decision and the override would answer for it.
//
// No companion settings file is written beside either session, so nothing pairs with them: both
// sessions keep the plugin path's own token declaration (not measurable, with a reason). The paired
// case — a Factory transcript's companion settings file naming the same session id — is pinned by
// the canon pairing rules, not by this world.
//
// The recorded payloads carry the fields Factory's own hook payloads carry (session_id,
// transcript_path, cwd, permission_mode, hook_event_name and the per-event fields), with two
// deliberate exceptions, both of which the mapper reads:
//
//   - `model` on the prompt payload. Factory's documented UserPromptSubmit payload carries no
//     model field, so this recording exercises the mapper's model path rather than reproducing a
//     live payload verbatim;
//   - the Stop payload's `message_id` names the assistant message the turn ended, not the user
//     message the prompt named. Factory reports the session's last message id at Stop time, which
//     by then is the assistant's reply, and that keeps the prompt's turn span and the finished
//     turn span two spans instead of one.
//
// The plugin runner is the production one (`runHook`), the delivery is a real OTLP/HTTP POST to the
// production loopback receiver, and the receiver's store port is the production ingest seam, so
// every batch is normalized by the droid canon adapter and persisted the way a live batch is.

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

import { buildHarnessRollup } from '../src/canon/harnesses.js'
import { ingestBatch } from '../src/canon/ingest.js'
import { projectCanonicalStore } from '../src/canon/projection.js'
import { CanonStore } from '../src/canon/store.js'
import { FACTORY_CONSENT_FILE_NAME, KYBERDASH_FACTORY_CONTEXT_ENV } from '../src/capture/harnesses/factory-plugin/consent.js'
import { runHook } from '../src/capture/harnesses/factory-plugin/entry.js'
import { readSpoolEvents } from '../src/capture/harnesses/factory-plugin/spool.js'
import { OtlpReceiver, type OtlpSpan } from '../src/otel/receiver.js'

import { USER_CANON, createTempRoot } from './harness-collection-world.js'

/** The operator's own canon store: never a write target and never served. */
export { USER_CANON }

/** The session replayed with the consent file saying content off. */
export const FACTORY_CONTENT_OFF_SESSION_ID = 'factory-replay-content-off'
/** The session replayed after the consent file was flipped to content on. */
export const FACTORY_CONTENT_ON_SESSION_ID = 'factory-replay-content-on'
/** The model the recorded prompt payload names. */
export const FACTORY_REPLAY_MODEL = 'gpt-5-codex'
/** The tool the recorded tool payloads name. */
export const FACTORY_REPLAY_TOOL = 'Read'
/** The prompt text the consent decides about. */
export const FACTORY_REPLAY_PROMPT = 'the recorded factory prompt that must not travel without consent'

/** One recorded Factory hook payload. */
export type RecordedFactoryHook = {
  hook_event_name: string
  session_id: string
  [field: string]: unknown
}

export type FactoryReplayWorld = {
  root: string
  dbPath: string
  store: CanonStore
}

/**
 * Replay the recorded Factory sessions into a fresh temp canon store.
 *
 * The store is a temp file, never the operator's own `~/.kyberdash/canon.db`: this world writes
 * rows, and a row written into the operator's own corpus would be fiction in their dashboard.
 */
export async function refreshFactoryTempCanon(root = createTempRoot()): Promise<FactoryReplayWorld> {
  const dbPath = join(root, 'canon.db')
  if (dbPath === USER_CANON) {
    throw new Error('refusing to use the user canon.db as a Factory replay write target')
  }

  const store = new CanonStore(dbPath)
  try {
    await replayRecordedFactorySessions(store, root)
    // The harness rollup is what the harness page reads; without it the `droid` harness renders
    // the missing-rollup state instead of the sessions the replay produced.
    buildHarnessRollup(store)
    return { root, dbPath, store }
  } catch (error) {
    store.close()
    throw error
  }
}

/** The recorded payloads of one session, in the order Factory fires the hooks. */
export function recordedFactoryHooks(which: 'content-off' | 'content-on'): RecordedFactoryHook[] {
  const path = new URL(`./fixtures/factory/${which}.json`, import.meta.url)
  const parsed: unknown = JSON.parse(readFileSync(path, 'utf8'))
  if (!Array.isArray(parsed)) {
    throw new Error(`the recorded Factory fixture ${which}.json is not an array of hook payloads`)
  }
  return parsed as RecordedFactoryHook[]
}

/**
 * Replay both recorded sessions through the production plugin runner and the production receiver.
 *
 * Every hook event is one short-lived runner process in production and one delivery here: the
 * runner reads the payload the way Factory writes it (JSON on stdin) and POSTs the mapped span to
 * the loopback receiver, which decodes it and hands the batch to the production ingest seam.
 */
async function replayRecordedFactorySessions(store: CanonStore, root: string): Promise<void> {
  const stateDir = join(root, 'state')
  const consentPath = join(stateDir, FACTORY_CONSENT_FILE_NAME)
  mkdirSync(stateDir, { recursive: true })

  const accepted: OtlpSpan[] = []
  // The consent the file holds for the phase being replayed, mirrored to the store gate.
  let storeConsent = false
  let ingestFailure: unknown
  const receiver = new OtlpReceiver({
    port: 0,
    host: '127.0.0.1',
    // The receiver's store port is the ingest seam: every decoded batch is normalized by the
    // droid canon adapter and persisted as its own transaction, the way a live batch is.
    store: {
      write(spans: readonly OtlpSpan[]): void {
        accepted.push(...spans)
        // The store-side content gate reads the owner's consent from the owner's HOME
        // (`~/.kyberdash/factory-plugin.json`), which in production is the very file the hook
        // runner reads. This world keeps its consent file under a temp state dir instead, so
        // the store gate is told the same decision the file holds for this phase; without it
        // the gate reads the CI runner's home (no file, so off) and strips the consent-on prompt.
        const previous = process.env[KYBERDASH_FACTORY_CONTEXT_ENV]
        process.env[KYBERDASH_FACTORY_CONTEXT_ENV] = storeConsent ? '1' : '0'
        try {
          ingestBatch(spans, store)
        } catch (error) {
          // The receiver answers 500 when its sink throws, and the hook runner would park the
          // event in its spool instead of failing. The failure is kept here so the replay stops
          // at the stage that broke, with the reason, instead of quietly losing a hook event.
          ingestFailure = ingestFailure ?? error
          throw error
        } finally {
          if (previous === undefined) delete process.env[KYBERDASH_FACTORY_CONTEXT_ENV]
          else process.env[KYBERDASH_FACTORY_CONTEXT_ENV] = previous
        }
      },
    },
  })
  await receiver.start()
  try {
    writeConsent(consentPath, false)
    storeConsent = false
    await replaySession(receiver, stateDir, consentPath, recordedFactoryHooks('content-off'))
    writeConsent(consentPath, true)
    storeConsent = true
    await replaySession(receiver, stateDir, consentPath, recordedFactoryHooks('content-on'))
  } finally {
    await receiver.stop()
  }

  if (ingestFailure !== undefined) {
    throw new Error('the Factory replay stopped at the ingest stage', { cause: ingestFailure })
  }
  if (accepted.length === 0) {
    throw new Error('the Factory replay stopped at the receiver stage: no OTLP batch was accepted')
  }
  // A parked event is a delivery the receiver did not take: the scenario would read a corpus
  // that is missing a hook event, so the replay stops here instead.
  const parked = readSpoolEvents(stateDir)
  if (parked.length > 0) {
    throw new Error(
      `the Factory replay stopped at the delivery stage: ${parked.length} hook events were parked in the spool`,
    )
  }
  await projectCanonicalStore(store)
}

/** Run every recorded hook event of one session through the production hook runner. */
async function replaySession(
  receiver: OtlpReceiver,
  stateDir: string,
  consentPath: string,
  payloads: readonly RecordedFactoryHook[],
): Promise<void> {
  for (const payload of payloads) {
    const exitCode = await runHook({
      endpoint: `${receiver.endpoint}/v1/traces`,
      stateDir,
      consentPath,
      // No environment override: the consent FILE is the decision, and an override would answer
      // for the owner who wrote it.
      env: {},
      readStdin: async () => JSON.stringify(payload),
      stdout: () => {},
      stderr: () => {},
    })
    if (exitCode !== 0) {
      throw new Error(
        `the Factory hook runner exited ${exitCode} for the ${payload.hook_event_name} hook`,
      )
    }
  }
}

/** The consent file the owner writes: `{ "version": 1, "content": <boolean> }`. */
function writeConsent(path: string, content: boolean): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, JSON.stringify({ version: 1, content }))
}
