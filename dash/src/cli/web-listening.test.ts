// The tray supervisor reads the first stdout line of `kyberdash web` as JSON
// and uses that URL; a human banner printed first would make the child look
// like it never started (R5.7). `--view` is how a report and the tray open a
// specific spine location rather than the dashboard root (R5.6).

import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import type { AddressInfo } from 'node:net'
import type { Server } from 'node:http'

import { afterEach, describe, expect, it } from 'vitest'

import { REPORT_SCHEMA_VERSION } from '../analysis/report/types.js'
import { formatValidViewForms } from '../server/view-paths.js'
import { runWebDashboard } from './web.js'

const DASH_ROOT = fileURLToPath(new URL('../..', import.meta.url))
const PACKAGE_VERSION = String(
  (createRequire(import.meta.url)('../../package.json') as { version?: string }).version,
)

const running: ChildProcess[] = []
const tempDirs: string[] = []
const servers: Server[] = []

afterEach(async () => {
  await Promise.all(
    running.splice(0).map(
      (child) =>
        new Promise<void>((resolve) => {
          child.once('exit', () => resolve())
          child.kill('SIGINT')
          setTimeout(() => {
            child.kill('SIGKILL')
            resolve()
          }, 2_000).unref()
        }),
    ),
  )
  await Promise.all(
    servers.splice(0).map(
      (server) =>
        new Promise<void>((resolve) => {
          server.close(() => resolve())
        }),
    ),
  )
  await Promise.all(tempDirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })))
})

async function waitFor(predicate: () => boolean, timeoutMs: number, message: () => string): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (predicate()) return
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  throw new Error(message())
}

describe('kyberdash web listening line (R5.7)', () => {
  it('prints the JSON listening event as the first stdout line', async () => {
    const home = await mkdtemp(join(tmpdir(), 'kyberdash-web-listen-'))
    tempDirs.push(home)
    const dashDir = await mkdtemp(join(tmpdir(), 'kyberdash-web-ui-'))
    tempDirs.push(dashDir)
    await writeFile(join(dashDir, 'index.html'), '<!doctype html><title>kyberDash</title>')

    const child = spawn(
      process.execPath,
      ['--import', 'tsx', 'src/launcher.ts', 'web', '--no-open', '--port', '0'],
      {
        cwd: DASH_ROOT,
        stdio: ['ignore', 'pipe', 'pipe'],
        env: {
          ...process.env,
          HOME: home,
          KYBERDASH_DASH_DIR: dashDir,
          FORCE_COLOR: '0',
        },
      },
    )
    running.push(child)

    let stdout = ''
    let stderr = ''
    child.stdout?.setEncoding('utf8')
    child.stderr?.setEncoding('utf8')
    child.stdout?.on('data', (chunk) => {
      stdout += String(chunk)
    })
    child.stderr?.on('data', (chunk) => {
      stderr += String(chunk)
    })

    await waitFor(
      () => stdout.includes('\n'),
      15_000,
      () => `web never printed a stdout line; stdout=${stdout} stderr=${stderr}`,
    )

    const firstLine = stdout.split('\n')[0] ?? ''
    const payload = JSON.parse(firstLine) as {
      event: string
      url: string
      pid: number
      version: string
      apiVersion: number
    }
    expect(payload).toEqual({
      event: 'kyberdash.web.listening',
      url: expect.stringMatching(/^http:\/\/127\.0\.0\.1:\d+$/),
      pid: child.pid,
      version: PACKAGE_VERSION,
      apiVersion: REPORT_SCHEMA_VERSION,
    })
  })
})

describe('kyberdash web --view (R5.6)', () => {
  it('opens url/finding/<id> when --view finding/<id> is given', async () => {
    const opened: string[] = []
    const server = await runWebDashboard({
      port: 0,
      open: true,
      view: 'finding/fid-9',
      openUrl: (url) => {
        opened.push(url)
      },
      writeStdout: () => {},
    })
    servers.push(server)
    const port = (server.address() as AddressInfo).port
    expect(opened).toEqual([`http://127.0.0.1:${port}/finding/fid-9`])
  })

  it('exits 2 and lists the valid forms for an unknown view', () => {
    const result = spawnSync(
      process.execPath,
      ['--import', 'tsx', 'src/launcher.ts', 'web', '--view', 'not-a-real-view', '--no-open'],
      {
        cwd: DASH_ROOT,
        encoding: 'utf8',
        timeout: 30_000,
        env: {
          ...process.env,
          FORCE_COLOR: '0',
        },
      },
    )
    expect(result.status).toBe(2)
    expect(result.stderr).toContain(formatValidViewForms())
    expect(result.stdout).not.toContain('kyberdash.web.listening')
  })
})
