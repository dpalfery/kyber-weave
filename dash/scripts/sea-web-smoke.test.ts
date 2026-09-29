// Tests for the sea-web-smoke script. RED: fails because the script doesn't exist yet.
// Tests against stand-in "binary" Node scripts that simulate different server behaviors.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mkdtemp, rm, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import { spawnSync } from 'child_process'

describe('sea-web-smoke - smoke test for embedded dashboard', () => {
  let tempDir: string
  let stdinDir: string

  beforeEach(async () => {
    tempDir = await mkdtemp(join(tmpdir(), 'smoke-test-'))
    stdinDir = await mkdtemp(join(tmpdir(), 'smoke-stdin-'))
  })

  afterEach(async () => {
    await rm(tempDir, { recursive: true, force: true })
    await rm(stdinDir, { recursive: true, force: true })
  })

  it('exits non-zero when stand-in binary serves the not-built page', async () => {
    // Create a stand-in binary that prints listening line and serves not-built page
    const standinScript = join(tempDir, 'not-built.mjs')
    await writeFile(
      standinScript,
      `
import { createServer } from 'http'
const server = createServer((req, res) => {
  if (req.url === '/') {
    res.writeHead(200, { 'content-type': 'text/html' })
    res.end('<!doctype html><body>Dashboard not built</body>')
  } else {
    res.writeHead(404)
    res.end()
  }
})
server.listen(0, '127.0.0.1', () => {
  const port = server.address().port
  console.log(JSON.stringify({
    event: 'kyberdash.web.listening',
    url: \`http://127.0.0.1:\${port}\`,
    pid: process.pid
  }))
})
`,
    )

    // Run the smoke test against this stand-in
    const DASH_ROOT = process.cwd()
    const result = spawnSync('node', ['scripts/sea-web-smoke.mjs', 'node', standinScript], {
      cwd: DASH_ROOT,
      timeout: 30_000,
      encoding: 'utf8',
    })

    // Should exit non-zero
    expect(result.status).not.toBe(0)
    // Should mention the not-built page in output
    expect(result.stdout + result.stderr).toContain('not built')
  })

  it('exits zero when stand-in binary serves SPA index', async () => {
    // Create a stand-in that serves a valid SPA index
    const standinScript = join(tempDir, 'spa.mjs')
    await writeFile(
      standinScript,
      `
import { createServer } from 'http'
const index = '<!doctype html><html><body><div id="root">App</div></body></html>'
const server = createServer((req, res) => {
  res.writeHead(200, { 'content-type': 'text/html' })
  res.end(index)
})
server.listen(0, '127.0.0.1', () => {
  const port = server.address().port
  console.log(JSON.stringify({
    event: 'kyberdash.web.listening',
    url: \`http://127.0.0.1:\${port}\`,
    pid: process.pid
  }))
})
`,
    )

    const DASH_ROOT = process.cwd()
    const result = spawnSync('node', ['scripts/sea-web-smoke.mjs', 'node', standinScript], {
      cwd: DASH_ROOT,
      timeout: 30_000,
      encoding: 'utf8',
    })

    // Should exit 0
    expect(result.status).toBe(0)
  })

  it('exits non-zero within the 20s bound when binary never prints listening line', async () => {
    // Create a stand-in that never prints the listening line
    const standinScript = join(tempDir, 'silent.mjs')
    await writeFile(
      standinScript,
      `
import { createServer } from 'http'
const server = createServer((req, res) => {
  res.writeHead(200)
  res.end('ok')
})
// Never call listen, so it never prints anything
setTimeout(() => process.exit(0), 60_000)
`,
    )

    const DASH_ROOT = process.cwd()
    const startTime = Date.now()
    const result = spawnSync('node', ['scripts/sea-web-smoke.mjs', 'node', standinScript], {
      cwd: DASH_ROOT,
      timeout: 30_000, // Test timeout must be more than script's 20s bound
      encoding: 'utf8',
    })
    const elapsedMs = Date.now() - startTime

    // Should exit non-zero
    expect(result.status).not.toBe(0)
    // Should complete within a reasonable time (allowing some overhead beyond 20s bound)
    expect(elapsedMs).toBeLessThan(30_000)
  })

  it('always kills the child process', async () => {
    // Create a stand-in that would hang if not killed
    const standinScript = join(tempDir, 'hanging.mjs')
    await writeFile(
      standinScript,
      `
import { createServer } from 'http'
const server = createServer((req, res) => {
  res.writeHead(200, { 'content-type': 'text/html' })
  res.end('<!doctype html><body><div id="root">OK</div></body>')
})
server.listen(0, '127.0.0.1', () => {
  const port = server.address().port
  console.log(JSON.stringify({
    event: 'kyberdash.web.listening',
    url: \`http://127.0.0.1:\${port}\`,
    pid: process.pid
  }))
})
// This would hang if not killed
setTimeout(() => {
  console.log('Would still be running after result')
}, 60_000)
`,
    )

    const DASH_ROOT = process.cwd()
    const result = spawnSync('node', ['scripts/sea-web-smoke.mjs', 'node', standinScript], {
      cwd: DASH_ROOT,
      timeout: 30_000,
      encoding: 'utf8',
    })

    // Should complete quickly (not hang for 60s)
    // The stdout should show it successfully tested and exited
    expect(result.status).toBe(0)
    // The process should not have printed the "still running" line
    expect(result.stdout + result.stderr).not.toContain('Would still be running')
  })

  it('exits non-zero when stand-in binary serves HTML without the SPA root marker', async () => {
    const standinScript = join(tempDir, 'no-root.mjs')
    await writeFile(
      standinScript,
      `
import { createServer } from 'http'
const server = createServer((req, res) => {
  if (req.url === '/') {
    res.writeHead(200, { 'content-type': 'text/html' })
    res.end('<!doctype html><body><h1>Unrelated Page</h1></body>')
  } else {
    res.writeHead(404)
    res.end()
  }
})
server.listen(0, '127.0.0.1', () => {
  const port = server.address().port
  console.log(JSON.stringify({
    event: 'kyberdash.web.listening',
    url: \`http://127.0.0.1:\${port}\`,
    pid: process.pid
  }))
})
`,
    )

    const DASH_ROOT = process.cwd()
    const result = spawnSync('node', ['scripts/sea-web-smoke.mjs', 'node', standinScript], {
      cwd: DASH_ROOT,
      timeout: 30_000,
      encoding: 'utf8',
    })

    expect(result.status).not.toBe(0)
    expect(result.stdout + result.stderr).toContain('does not contain the SPA root marker')
  })
})
