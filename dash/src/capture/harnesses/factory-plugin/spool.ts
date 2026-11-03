// The Factory capture plugin's file-backed spool: the events a delivery could not hand over.
//
// A Factory hook is a one-shot process: one short-lived process per hook event. An in-memory
// queue would lose the span when the process exits, so the spool is a file under KyberDash's own
// state directory (`<stateDir>/capture/factory`), never in the owner's Factory tree.
//
// The spool is the file-backed analogue of the OpenCode outbox's policy: bounded, drop-oldest,
// and drained by the next run before that run sends its own event.
//
// This module is a LEAF: it imports only `node:` built-ins and the shared consent writer's
// atomic file write, so the plugin never drags engine code into the process it runs in.

import { closeSync, fstatSync, mkdirSync, openSync, readFileSync, rmSync } from 'node:fs'
import { join } from 'node:path'

import { writeFileAtomic } from '../../consent-file.js'

/** The most events the spool holds; the oldest is dropped when it is full. */
export const SPOOL_MAX_EVENTS = 500
/** The most bytes the spool file holds; the oldest events are dropped when it is full. */
export const SPOOL_MAX_BYTES = 4 * 1024 * 1024

/** The spool file inside the spool directory: one JSON event per line. */
const SPOOL_FILE_NAME = 'spool.jsonl'

/** The spool directory under the KyberDash state root. */
export function factorySpoolDir(stateDir: string): string {
  return join(stateDir, 'capture', 'factory')
}

/** The spool file under the KyberDash state root. */
function spoolPath(stateDir: string): string {
  return join(factorySpoolDir(stateDir), SPOOL_FILE_NAME)
}

/** Every event the spool holds, oldest first. A missing or unreadable spool holds nothing. */
export function readSpoolEvents(stateDir: string): unknown[] {
  let handle: number
  try {
    handle = openSync(spoolPath(stateDir), 'r')
  } catch {
    // A spool that was never written holds no events; so does one that cannot be opened.
    return []
  }
  let text: string
  try {
    // The spool file is measured through the descriptor BEFORE its content is read, so a
    // file that is already over the bound is refused without ever being materialised: the
    // bound holds before the read, not after it.
    const stat = fstatSync(handle)
    if (!stat.isFile() || stat.size > SPOOL_MAX_BYTES) return []
    text = readFileSync(handle, 'utf8')
  } catch {
    return []
  } finally {
    closeSync(handle)
  }
  const events: unknown[] = []
  for (const line of text.split('\n')) {
    if (line.trim() === '') continue
    try {
      events.push(JSON.parse(line))
    } catch {
      // A line that is not JSON is not an event: it is skipped, not fatal. The spool is a
      // delivery aid, and one damaged line must not cost the events around it.
    }
  }
  return events
}

/** Park one event. The spool stays bounded: the oldest event is dropped when it is full. */
export function appendSpoolEvent(stateDir: string, event: unknown): void {
  const events = [...readSpoolEvents(stateDir), event]
  writeSpoolEvents(stateDir, boundEvents(events))
}

/** Empty the spool: the events it held were delivered, or were given up on. */
export function clearSpool(stateDir: string): void {
  rmSync(spoolPath(stateDir), { force: true })
}

/**
 * The events the spool keeps, oldest first: at most `SPOOL_MAX_EVENTS` of them, and at most
 * `SPOOL_MAX_BYTES` bytes. The oldest event is dropped first, because the newest event is the
 * one the next run is most likely to still care about.
 *
 * The bound is weighed in BYTES, because the spool file is written as UTF-8: a line of
 * characters can be up to three times as many bytes, and a bound that counted characters
 * would let the file grow to three times the ceiling it documents.
 */
function boundEvents(events: readonly unknown[]): string[] {
  const kept: string[] = []
  let bytes = 0
  for (let index = events.length - 1; index >= 0; index -= 1) {
    const line = serializeEvent(events[index])
    if (line === undefined) continue
    if (kept.length >= SPOOL_MAX_EVENTS) break
    // The newline the line is written with is a byte of the file too.
    const size = Buffer.byteLength(line, 'utf8') + 1
    if (bytes + size > SPOOL_MAX_BYTES) break
    kept.unshift(line)
    bytes += size
  }
  return kept
}

/** The event as one JSON line, or undefined when the event has no JSON form at all. */
function serializeEvent(event: unknown): string | undefined {
  const line = JSON.stringify(event)
  return line === undefined ? undefined : line
}

/** Write the spool file. The write is atomic, so a reader never sees half a spool. */
function writeSpoolEvents(stateDir: string, lines: readonly string[]): void {
  mkdirSync(factorySpoolDir(stateDir), { recursive: true })
  const text = lines.map((line) => `${line}\n`).join('')
  writeFileAtomic(spoolPath(stateDir), text)
}
