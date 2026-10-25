// RED contract tests for the release smoke check of the staged KyberDash MCP SEA binary.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

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
const SMOKE_TIMEOUT_MS = 10_000
const TEST_TIMEOUT_MS = 20_000

const scriptDir = dirname(fileURLToPath(import.meta.url))

describe('sea-mcp-smoke - staged binary MCP release contract', () => {
  let tempDir: string
  let binary: string
  let pidFile: string

  beforeEach(async () => {
    tempDir = await mkdtemp(join(tmpdir(), 'kyberdash-sea-mcp-'))
    binary = join(tempDir, 'kyberdash')
    pidFile = join(tempDir, 'child.pid')
    await writeStandIn('valid')
  })

  afterEach(async () => {
    await rm(tempDir, { recursive: true, force: true })
  })

  it('runs a real MCP initialize, tools/list, and analytics tool request against the staged executable', () => {
    const result = runSmoke()
    expect(result.status, result.stderr).toBe(0)
    expect(result.stdout).toContain('sea-mcp-smoke:')
  }, TEST_TIMEOUT_MS)

  it.each([
    ['malformed JSON-RPC response', 'bad-json'],
    ['wrong JSON-RPC version or request id', 'bad-envelope'],
    ['missing tools', 'missing-tools'],
    ['unexpected tools', 'extra-tools'],
    ['an incorrect analytics contract version', 'bad-contract'],
    ['missing analytics tool data', 'bad-data'],
  ])('fails when the MCP peer returns %s', async (_scenario, mode) => {
    await writeStandIn(mode)
    const result = runSmoke()
    expect(result.status, `${result.stdout}\n${result.stderr}`).not.toBe(0)
  }, TEST_TIMEOUT_MS)

  it('fails within its deadline when the MCP peer hangs and reaps the child', async () => {
    await writeStandIn('hang')
    const startedAt = Date.now()
    const result = runSmoke()
    expect(result.status, `${result.stdout}\n${result.stderr}`).not.toBe(0)
    expect(Date.now() - startedAt).toBeLessThan(SMOKE_TIMEOUT_MS + 5_000)
    await expectChildHasExited()
  }, TEST_TIMEOUT_MS)

  it('reaps the staged executable after a successful request', async () => {
    const result = runSmoke()
    expect(result.status, result.stderr).toBe(0)
    await expectChildHasExited()
  }, TEST_TIMEOUT_MS)

  it('is wired into the local release build after the SEA blob injection', async () => {
    const local = await readFile(join(scriptDir, '..', '..', 'scripts', 'release-local.sh'), 'utf8')
    const code = local.split('\n').filter((line) => !line.trimStart().startsWith('#')).join('\n')
    const blobIndex = code.indexOf('NODE_SEA_BLOB')
    expect(blobIndex, 'release-local.sh has no NODE_SEA_BLOB injection').toBeGreaterThanOrEqual(0)
    const smokeIndex = code.indexOf('scripts/sea-mcp-smoke.mjs')
    expect(smokeIndex, 'release-local.sh does not wire scripts/sea-mcp-smoke.mjs').toBeGreaterThanOrEqual(0)
    expect(smokeIndex, 'sea-mcp-smoke must run after the blob injection').toBeGreaterThan(blobIndex)
  }, TEST_TIMEOUT_MS)

  async function writeStandIn(mode: string): Promise<void> {
    await writeFile(binary, `#!/usr/bin/env node\n${standInSource(mode)}\n`, { mode: 0o755 })
  }

  function runSmoke() {
    return spawnSync('node', [join(scriptDir, 'sea-mcp-smoke.mjs'), binary], {
      cwd: scriptDir,
      encoding: 'utf8',
      timeout: SMOKE_TIMEOUT_MS + 5_000,
      env: {
        ...process.env,
        HOME: join(tempDir, 'home'),
        USERPROFILE: join(tempDir, 'home'),
        KYBER_CANON_DB: join(tempDir, 'state', 'canon.db'),
        KYBERDASH_CACHE_DIR: join(tempDir, 'cache'),
        SEA_MCP_CHILD_PID_FILE: pidFile,
      },
    })
  }

  async function expectChildHasExited(): Promise<void> {
    // The fixture records its pid to disk because the smoke process is a separate process.
    const recordedPid = Number(await readFile(pidFile, 'utf8'))
    expect(recordedPid).toBeGreaterThan(0)
    expect(() => process.kill(recordedPid, 0)).toThrow()
  }
})

function standInSource(mode: string): string {
  return `
import { writeFileSync } from 'node:fs'
import readline from 'node:readline'
const mode = ${JSON.stringify(mode)}
const pidFile = process.env.SEA_MCP_CHILD_PID_FILE
writeFileSync(pidFile, String(process.pid))
const tools = ${JSON.stringify(EXPECTED_TOOLS)}.map(name => ({ name, description: name, inputSchema: { type: 'object', properties: {} } }))
const analytics = { contractVersion: 1, data: { ok: true } }
const rl = readline.createInterface({ input: process.stdin })
rl.on('line', line => {
  if (mode === 'hang') return
  let request
  try { request = JSON.parse(line) } catch { process.stdout.write('{bad json}\\n'); return }
  let result
  if (request.method === 'initialize') result = { protocolVersion: '2025-06-18', capabilities: { tools: {} }, serverInfo: { name: 'kyberdash', version: 'test' } }
  else if (request.method === 'tools/list') result = { tools: mode === 'missing-tools' ? tools.slice(1) : mode === 'extra-tools' ? [...tools, { name: 'surprise' }] : tools }
  else if (request.method === 'tools/call') result = { content: [{ type: 'text', text: JSON.stringify(mode === 'bad-contract' ? { ...analytics, contractVersion: 2 } : mode === 'bad-data' ? { contractVersion: 1, data: null } : analytics) }] }
  else result = {}
  const response = { jsonrpc: '2.0', id: request.id, result }
  if (mode === 'bad-json') process.stdout.write('{bad json}\\n')
  else if (mode === 'bad-envelope') process.stdout.write(JSON.stringify({ ...response, jsonrpc: '1.0', id: 999 }) + '\\n')
  else process.stdout.write(JSON.stringify(response) + '\\n')
})
`
}
