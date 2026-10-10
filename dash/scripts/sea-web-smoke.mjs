#!/usr/bin/env node
// Smoke-tests a built kyberdash binary's `web` command against the embedded dashboard
// (D1-A, docs/plans/2026-09-28-kyberdash-sea-release-integrity.md). It starts the binary
// with a temporary HOME (so a local run never touches the real ~/.kyberdash) and a
// KYBER_CANON_DB pointed at a path that does not exist, waits for the child's
// `kyberdash.web.listening` stdout line to learn the bound port, then fetches `/`. It
// fails unless the response body is the SPA index rather than the "not built" page. The
// child is always killed, success or failure, and the whole run is bounded to 20 s.
//
// Usage: node scripts/sea-web-smoke.mjs <binary> [extra-args]
//   <binary>      the built kyberdash executable to smoke-test. In tests this is a
//                 stand-in, invoked as `node <stand-in-script.mjs>`: pass `node` as
//                 <binary> and the script path as the sole extra arg.
//   [extra-args]  inserted before the standard `web --no-open --port 0` arguments, so a
//                 stand-in script's own path can ride along with the real binary's args.

import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const OVERALL_TIMEOUT_MS = 20_000
const LISTENING_EVENT = 'kyberdash.web.listening'

/**
 * Resolves with the `url` from the child's `kyberdash.web.listening` stdout line, or
 * `undefined` if the child exits, errors, or is killed before printing one.
 */
function waitForListeningUrl(child) {
  return new Promise((resolve) => {
    let buffer = ''
    let settled = false
    const settle = (value) => {
      if (settled) return
      settled = true
      resolve(value)
    }

    child.stdout.on('data', (chunk) => {
      buffer += chunk.toString('utf8')
      let newlineAt = buffer.indexOf('\n')
      while (newlineAt !== -1) {
        const line = buffer.slice(0, newlineAt)
        buffer = buffer.slice(newlineAt + 1)
        try {
          const parsed = JSON.parse(line)
          if (parsed && parsed.event === LISTENING_EVENT && typeof parsed.url === 'string') {
            settle(parsed.url)
            return
          }
        } catch {
          // Not the JSON listening line: a human-readable banner or unrelated output.
        }
        newlineAt = buffer.indexOf('\n')
      }
    })

    child.once('exit', () => settle(undefined))
    child.once('error', () => settle(undefined))
  })
}

async function main() {
  const [binary, ...extraArgs] = process.argv.slice(2)
  if (!binary) {
    console.error('usage: sea-web-smoke.mjs <binary> [extra-args]')
    process.exitCode = 1
    return
  }

  const tempHome = await mkdtemp(join(tmpdir(), 'kyberdash-smoke-home-'))
  const missingCanonDb = join(tempHome, 'does-not-exist', 'canon.db')

  let child
  let timedOut = false
  const timeout = setTimeout(() => {
    timedOut = true
    if (child && !child.killed) child.kill('SIGKILL')
  }, OVERALL_TIMEOUT_MS)

  try {
    child = spawn(binary, [...extraArgs, 'web', '--no-open', '--port', '0'], {
      env: {
        ...process.env,
        HOME: tempHome,
        KYBER_CANON_DB: missingCanonDb,
      },
    })

    const url = await waitForListeningUrl(child)
    if (!url) {
      console.error(
        timedOut
          ? `sea-web-smoke: timed out after ${OVERALL_TIMEOUT_MS}ms waiting for the ${LISTENING_EVENT} line`
          : `sea-web-smoke: the child exited before printing a ${LISTENING_EVENT} line`,
      )
      process.exitCode = 1
      return
    }

    const res = await fetch(`${url}/`)
    const body = await res.text()
    if (!res.ok || body.toLowerCase().includes('not built')) {
      console.error('sea-web-smoke: served the not built page, not the SPA index')
      process.exitCode = 1
      return
    }
    if (!body.includes('<div id="root">')) {
      console.error('sea-web-smoke: served response does not contain the SPA root marker (<div id="root">)')
      process.exitCode = 1
      return
    }

    console.log(`sea-web-smoke: ${url}/ served the SPA index`)
    process.exitCode = 0
  } catch (err) {
    console.error(`sea-web-smoke: ${err instanceof Error ? err.message : String(err)}`)
    process.exitCode = 1
  } finally {
    clearTimeout(timeout)
    if (child && !child.killed) child.kill('SIGKILL')
    await rm(tempHome, { recursive: true, force: true })
  }
}

main()
