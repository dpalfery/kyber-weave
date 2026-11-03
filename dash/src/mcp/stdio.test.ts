import { afterEach, describe, expect, it } from 'vitest'
import type { ChildProcess } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js'

const children: ChildProcess[] = []
const roots: string[] = []

afterEach(async () => {
  for (const child of children.splice(0)) child.kill('SIGTERM')
  await Promise.all(roots.splice(0).map(root => rm(root, { recursive: true, force: true })))
})

async function connectedServer(name: string) {
  const root = await mkdtemp(join(tmpdir(), `kyberdash-mcp-${name}-`))
  roots.push(root)
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [
      fileURLToPath(new URL('../../node_modules/tsx/dist/cli.mjs', import.meta.url)),
      fileURLToPath(new URL('../launcher.ts', import.meta.url)),
      'mcp', '--server-url', 'http://127.0.0.1:1', '--state-dir', join(root, 'state'),
    ],
    env: { ...process.env, HOME: join(root, 'home'), KYBERDASH_STATE_DIR: join(root, 'state') },
    stderr: 'pipe',
  })
  const client = new Client({ name: 'kyberdash-test', version: '1.0.0' })
  await client.connect(transport)
  // The SDK exposes pid and stderr, but not the child handle needed to assert its exit code.
  const child = Reflect.get(transport, '_process') as ChildProcess | undefined
  if (child === undefined) throw new Error('The SDK did not retain its spawned server process after initialize.')
  children.push(child)
  let stdout = ''
  child.stdout?.on('data', (chunk: string | Buffer) => { stdout += chunk.toString() })
  let stderr = ''
  transport.stderr?.on('data', (chunk: string | Buffer) => { stderr += chunk.toString() })
  return { client, transport, child, stdout: () => stdout, stderr: () => stderr }
}

function waitForClose(child: ChildProcess): Promise<number | null> {
  return new Promise(resolve => child.once('close', code => resolve(code)))
}

describe('kyberdash mcp stdio process', () => {
  it('serves initialize and tools/list through the SDK and exits cleanly after client EOF', async () => {
    const { client, child, stdout, stderr } = await connectedServer('client-eof')
    const closed = waitForClose(child)
    const result = await client.listTools().catch((error: unknown) => {
      throw new Error(`tools/list failed (exit=${child.exitCode}, signal=${child.signalCode}): ${stderr()}`, { cause: error })
    })
    expect(result.tools.map(tool => tool.name).sort()).toEqual([
      'dash_catalog', 'dash_compare', 'dash_findings', 'dash_inspect',
      'dash_outcomes', 'dash_query', 'dash_search_sessions', 'dash_status',
    ])
    await client.close()
    expect(await closed, stderr()).toBe(0)
    expect(stdout().split('\n').filter(Boolean).every(line => JSON.parse(line).jsonrpc === '2.0')).toBe(true)
  })

  it('drains on SIGTERM after SDK initialize and does not write diagnostics to stdout', async () => {
    const { client, child, stdout, stderr } = await connectedServer('signal')
    await client.listTools()
    const closed = waitForClose(child)
    child.kill('SIGTERM')
    expect(await closed, stderr()).toBe(0)
    expect(stdout().split('\n').filter(Boolean).every(line => JSON.parse(line).jsonrpc === '2.0')).toBe(true)
  })

  it('handles a closed stdout pipe after SDK initialize without an unhandled process error', async () => {
    const { client, child, stderr } = await connectedServer('epipe')
    await client.listTools()
    const closed = waitForClose(child)
    child.stdout?.destroy()
    child.kill('SIGTERM')
    expect(await closed, stderr()).toBe(0)
    expect(stderr()).not.toMatch(/unhandled|uncaught/i)
  })
})
