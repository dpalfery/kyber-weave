#!/usr/bin/env node
// Exercises the released-shape SEA through its public MCP stdio command.  The
// loopback fixture deliberately returns a contract-v1 analytics envelope, so
// this check proves the bundled MCP SDK can both speak JSON-RPC and reach the
// engine boundary without reading a developer's state or starting an engine.

import { spawn } from 'node:child_process'
import { createServer } from 'node:http'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const EXPECTED_TOOLS = [
  'dash_status',
  'dash_catalog',
  'dash_query',
  'dash_search_sessions',
  'dash_inspect',
  'dash_findings',
  'dash_compare',
  'dash_outcomes',
]
const TIMEOUT_MS = 10_000

function fail(message) {
  throw new Error(`sea-mcp-smoke: ${message}`)
}

function analyticsFixture() {
  return {
    contractVersion: 1,
    scope: {},
    data: { fixture: 'release-smoke' },
    measurement: {},
    coverage: {},
    freshness: {},
    evidence: [],
    pagination: {},
    error: null,
  }
}

async function startFixture() {
  const server = createServer((request, response) => {
    if (request.method === 'GET' && request.url === '/api/kyber/analytics/status') {
      response.writeHead(200, { 'content-type': 'application/json' })
      response.end(JSON.stringify(analyticsFixture()))
      return
    }
    response.writeHead(404, { 'content-type': 'application/json' })
    response.end(JSON.stringify({ error: 'fixture route not found' }))
  })
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  const address = server.address()
  if (address === null || typeof address === 'string') fail('fixture did not bind a loopback port')
  return { server, url: `http://127.0.0.1:${address.port}` }
}

function closeChild(child) {
  if (child.exitCode !== null || child.signalCode !== null) return
  child.stdin.end()
  const grace = setTimeout(() => child.kill('SIGKILL'), 1_000)
  grace.unref()
}

async function main() {
  const [binary] = process.argv.slice(2)
  if (!binary) fail('usage: sea-mcp-smoke.mjs <binary>')

  const tempRoot = await mkdtemp(join(tmpdir(), 'kyberdash-sea-mcp-'))
  let fixture
  let child
  let timer
  try {
    fixture = await startFixture()
    child = spawn(binary, ['mcp', '--server-url', fixture.url, '--state-dir', join(tempRoot, 'state')], {
      env: {
        ...process.env,
        HOME: join(tempRoot, 'home'),
        USERPROFILE: join(tempRoot, 'home'),
        XDG_CONFIG_HOME: join(tempRoot, 'config'),
        XDG_CACHE_HOME: join(tempRoot, 'cache'),
        KYBER_CANON_DB: join(tempRoot, 'state', 'canon.db'),
        KYBERDASH_CACHE_DIR: join(tempRoot, 'cache'),
      },
      stdio: ['pipe', 'pipe', 'pipe'],
    })

    const lines = []
    let stdout = ''
    let stderr = ''
    let closed = false
    let closeError
    child.stdout.on('data', (chunk) => {
      stdout += chunk.toString('utf8')
      for (;;) {
        const newline = stdout.indexOf('\n')
        if (newline === -1) break
        lines.push(stdout.slice(0, newline))
        stdout = stdout.slice(newline + 1)
      }
    })
    child.stderr.on('data', (chunk) => { stderr += chunk.toString('utf8') })
    child.once('error', (error) => { closeError = error; closed = true })
    child.once('close', () => { closed = true })

    timer = setTimeout(() => closeChild(child), TIMEOUT_MS)
    timer.unref()
    let nextId = 1
    async function request(method, params) {
      const id = nextId++
      child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`)
      const deadline = Date.now() + TIMEOUT_MS
      for (;;) {
        const line = lines.shift()
        if (line !== undefined) {
          let reply
          try { reply = JSON.parse(line) } catch { fail('peer wrote malformed JSON-RPC') }
          if (reply?.jsonrpc !== '2.0' || reply.id !== id) fail('peer returned a JSON-RPC response with the wrong version or request id')
          if ('error' in reply) fail(`peer returned JSON-RPC error ${JSON.stringify(reply.error)}`)
          return reply.result
        }
        if (closed) fail(closeError instanceof Error ? closeError.message : `child exited before answering (${stderr.trim() || 'no stderr'})`)
        if (Date.now() >= deadline) fail(`timed out waiting for ${method}`)
        await new Promise((resolve) => setTimeout(resolve, 10))
      }
    }

    const initialized = await request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'kyberdash-sea-smoke', version: '1.0.0' },
    })
    if (typeof initialized?.protocolVersion !== 'string') fail('initialize did not return a protocol version')
    const tools = await request('tools/list', {})
    const names = tools?.tools?.map((tool) => tool?.name)
    if (!Array.isArray(names) || names.length !== EXPECTED_TOOLS.length || new Set(names).size !== EXPECTED_TOOLS.length || EXPECTED_TOOLS.some((name) => !names.includes(name))) {
      fail(`tools/list did not return exactly the expected tools (${EXPECTED_TOOLS.join(', ')})`)
    }
    const result = await request('tools/call', { name: 'dash_status', arguments: {} })
    const text = result?.content?.find((item) => item?.type === 'text')?.text
    let analytics
    try { analytics = JSON.parse(text) } catch { fail('dash_status did not return JSON analytics data') }
    if (analytics?.contractVersion !== 1 || analytics.data === null || analytics.data === undefined) {
      fail('dash_status did not return contractVersion 1 analytics data')
    }
    console.log('sea-mcp-smoke: initialize, eight tools, and dash_status contractVersion 1 succeeded')
  } finally {
    clearTimeout(timer)
    if (child) {
      closeChild(child)
      await new Promise((resolve) => child.once('close', resolve))
    }
    if (fixture) await new Promise((resolve, reject) => fixture.server.close((error) => error ? reject(error) : resolve()))
    await rm(tempRoot, { recursive: true, force: true })
  }
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : String(error))
  process.exitCode = 1
})
