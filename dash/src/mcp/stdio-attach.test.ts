import type { ChildProcess } from 'node:child_process'
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'
import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, describe, expect, it } from 'vitest'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js'

const children: ChildProcess[] = []
const roots: string[] = []

afterEach(async () => {
  for (const child of children.splice(0)) child.kill('SIGTERM')
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })))
})

function envelope(data: unknown): Record<string, unknown> {
  return {
    contractVersion: 1,
    scope: {},
    data,
    measurement: {},
    coverage: {},
    freshness: {},
    evidence: [],
    pagination: {},
    error: null,
  }
}

async function startLoopbackFixture(): Promise<{ url: string; seen: string[]; close: () => Promise<void> }> {
  const seen: string[] = []
  const server: Server = createServer((req: IncomingMessage, res: ServerResponse) => {
    void (async () => {
      const chunks: Buffer[] = []
      for await (const chunk of req) chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : chunk as Buffer)
      seen.push(`${req.method} ${new URL(req.url ?? '/', 'http://127.0.0.1').pathname}`)
      const body = Buffer.concat(chunks).toString('utf8')
      void body
      res.writeHead(200, { 'content-type': 'application/json' })
      res.end(JSON.stringify(envelope({ value: { ok: true } })))
    })().catch(() => {
      res.writeHead(500, { 'content-type': 'application/json' })
      res.end('{}')
    })
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  const address = server.address() as AddressInfo
  return { url: `http://127.0.0.1:${address.port}`, seen, close: () => new Promise<void>((resolve) => server.close(() => resolve())) }
}

async function connectedServer(options: { serverUrl?: string; stateDir?: string; name: string }): Promise<{ client: Client; child: ChildProcess; stdout: () => string; stderr: () => string }> {
  const root = await mkdtemp(join(tmpdir(), `kyberdash-mcp-${options.name}-`))
  roots.push(root)
  const home = join(root, 'home')
  const stateDir = options.stateDir ?? join(root, 'state')
  const args = [
    fileURLToPath(new URL('../../node_modules/tsx/dist/cli.mjs', import.meta.url)),
    fileURLToPath(new URL('../launcher.ts', import.meta.url)),
    'mcp',
    ...(options.serverUrl === undefined ? [] : ['--server-url', options.serverUrl]),
    '--state-dir', stateDir,
  ]
  const transport = new StdioClientTransport({
    command: process.execPath,
    args,
    env: { ...process.env, HOME: home, KYBERDASH_STATE_DIR: join(root, 'env-state') },
    stderr: 'pipe',
  })
  const client = new Client({ name: 'kyberdash-test', version: '1.0.0' })
  await client.connect(transport)
  const child = Reflect.get(transport, '_process') as ChildProcess | undefined
  if (child === undefined) throw new Error('The SDK did not retain its spawned server process after initialize.')
  children.push(child)
  let stdout = ''
  child.stdout?.on('data', (chunk: string | Buffer) => { stdout += chunk.toString() })
  let stderr = ''
  transport.stderr?.on('data', (chunk: string | Buffer) => { stderr += chunk.toString() })
  return { client, child, stdout: () => stdout, stderr: () => stderr }
}

function waitForClose(child: ChildProcess): Promise<number | null> {
  return new Promise((resolve) => child.once('close', (code) => resolve(code)))
}

describe('kyberdash mcp stdio attach and framing', () => {
  it('publishes eight tools over stdio and keeps diagnostics off stdout', async () => {
    const fixture = await startLoopbackFixture()
    try {
      const { client, child, stdout, stderr } = await connectedServer({ serverUrl: fixture.url, name: 'attach-frame' })
      const result = await client.listTools().catch((error: unknown) => {
        throw new Error(`tools/list failed (exit=${child.exitCode}, signal=${child.signalCode}): ${stderr()}`, { cause: error })
      })
      expect(result.tools.map((tool) => tool.name).sort()).toEqual([
        'dash_catalog', 'dash_compare', 'dash_findings', 'dash_inspect',
        'dash_outcomes', 'dash_query', 'dash_search_sessions', 'dash_status',
      ])
      const closed = waitForClose(child)
      await client.close()
      expect(await closed, stderr()).toBe(0)
      const lines = stdout().split('\n').filter(Boolean)
      expect(lines.length).toBeGreaterThan(0)
      for (const line of lines) expect(JSON.parse(line).jsonrpc, line.slice(0, 80)).toBe('2.0')
    } finally {
      await fixture.close()
    }
  })

  it('discovers the state-dir receipt when no explicit server URL is given', async () => {
    const fixture = await startLoopbackFixture()
    try {
      const root = await mkdtemp(join(tmpdir(), 'kyberdash-mcp-receipt-'))
      roots.push(root)
      const stateDir = join(root, 'state')
      await mkdir(stateDir, { recursive: true })
      await writeFile(join(stateDir, 'server.json'), JSON.stringify({ url: fixture.url }))
      const { client, child, stderr } = await connectedServer({ stateDir, name: 'attach-receipt' })
      try {
        const result = await client.callTool({ name: 'dash_status', arguments: {} })
        expect(fixture.seen.join(','), 'state-dir receipt must attach the loopback engine').toContain('/api/kyber/analytics/status')
        expect(JSON.stringify(result.structuredContent)).toContain('"contractVersion":1')
      } finally {
        const closed = waitForClose(child)
        await client.close()
        expect(await closed, stderr()).toBe(0)
      }
    } finally {
      await fixture.close()
    }
  })

  it('prefers the explicit server URL over a stale state-dir receipt', async () => {
    const preferred = await startLoopbackFixture()
    const stale = await startLoopbackFixture()
    try {
      const root = await mkdtemp(join(tmpdir(), 'kyberdash-mcp-override-'))
      roots.push(root)
      const stateDir = join(root, 'state')
      await mkdir(stateDir, { recursive: true })
      await writeFile(join(stateDir, 'server.json'), JSON.stringify({ url: stale.url }))
      const { client, child, stderr } = await connectedServer({ serverUrl: preferred.url, stateDir, name: 'attach-override' })
      try {
        await client.callTool({ name: 'dash_status', arguments: {} })
        expect(preferred.seen.join(','), 'explicit URL must win over the receipt').toContain('/api/kyber/analytics/status')
        expect(stale.seen.join(','), `stale receipt ${stale.url} must stay unused`).not.toContain('/api/kyber/analytics/status')
      } finally {
        const closed = waitForClose(child)
        await client.close()
        expect(await closed, stderr()).toBe(0)
      }
    } finally {
      await preferred.close()
      await stale.close()
    }
  })

  it('surfaces a dead engine behind stdio as a stable error instead of a raw transport failure', async () => {
    const { client, child, stderr } = await connectedServer({ serverUrl: 'http://127.0.0.1:1', name: 'attach-dead' })
    try {
      const result = await client.callTool({ name: 'dash_status', arguments: {} })
      const text = (result.content as Array<{ type: string; text: string }>)[0]?.text ?? ''
      const structured = result.structuredContent as { error: { code: string } | null } | undefined
      const code = structured?.error?.code ?? text.split(':')[0] ?? ''
      expect(code, `dead engine must surface a stable code, got: ${text.slice(0, 300)}`).toMatch(/^(engine_unavailable|incompatible_engine|budget_exceeded|invalid_request|cancelled|internal_error)$/)
      expect(result.isError, text.slice(0, 300)).toBe(true)
    } finally {
      const closed = waitForClose(child)
      await client.close()
      expect(await closed, stderr()).toBe(0)
    }
  })
})
