// Which executable a job child is spawned from (issue #319 T12 rework, defect 1).
//
// The hosted hosts spawn this CLI again (`JobHost` appends `['dash', 'refresh', ...]`), so
// the target must be the CLI root. Getting the *wrapper* right is what this covers: in a
// SEA the binary takes no arguments at all, in a tsx checkout the loader flags in
// `process.execArgv` must survive into the child, and a plain `node dist/cli.js` needs only
// the entry. The last case here runs for real, with no mocks: it composes the argv the
// resolver produces for the shape an actual `tsx src/launcher.ts` process reports and
// executes it.
import { spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import {
  PATH_FALLBACK_PROGRAM,
  filterInspectorFlags,
  resolveJobSpawnTarget,
  type SpawnTarget,
  type SpawnRuntime,
} from './spawn-target.js'

const DASH_ROOT = fileURLToPath(new URL('../../', import.meta.url))
const TSX_BIN = join(DASH_ROOT, 'node_modules', '.bin', 'tsx')
const LAUNCHER = join(DASH_ROOT, 'src', 'launcher.ts')

describe('resolveJobSpawnTarget', () => {
  it('gives a SEA the binary alone: argv[1] there is the unexpanded argv0', () => {
    const runtime: SpawnRuntime = {
      execPath: '/opt/kyberdash/bin/kyberdash',
      execArgv: [],
      argv: ['kyberdash', 'kyberdash', 'web'],
      isSea: true,
    }
    expect(resolveJobSpawnTarget(runtime)).toEqual({
      program: '/opt/kyberdash/bin/kyberdash',
      programArgs: [],
    })
  })

  it('keeps the tsx loader flags ahead of a TypeScript entry', () => {
    const execArgv = [
      '--require',
      join(DASH_ROOT, 'node_modules/tsx/dist/preflight.cjs'),
      '--import',
      `file://${join(DASH_ROOT, 'node_modules/tsx/dist/loader.mjs')}`,
    ]
    const runtime: SpawnRuntime = {
      execPath: '/usr/local/bin/node',
      execArgv,
      argv: ['/usr/local/bin/node', '/repo/dash/src/launcher.ts', 'web'],
      isSea: false,
    }
    expect(resolveJobSpawnTarget(runtime)).toEqual({
      program: '/usr/local/bin/node',
      programArgs: [...execArgv, '/repo/dash/src/launcher.ts'],
    })
  })

  it('runs a plain node JS entry with no wrapper flags', () => {
    const runtime: SpawnRuntime = {
      execPath: '/usr/local/bin/node',
      execArgv: ['--no-deprecation'],
      argv: ['/usr/local/bin/node', '/repo/dash/dist/cli.js', 'web'],
      isSea: false,
    }
    expect(resolveJobSpawnTarget(runtime)).toEqual({
      program: '/usr/local/bin/node',
      programArgs: ['--no-deprecation', '/repo/dash/dist/cli.js'],
    })
  })

  it('falls back to PATH when there is no entry script to re-run', () => {
    for (const argv of [['/usr/local/bin/node'], ['/usr/local/bin/node', '']]) {
      expect(resolveJobSpawnTarget({ execPath: '/usr/local/bin/node', execArgv: [], argv, isSea: false })).toEqual({
        program: PATH_FALLBACK_PROGRAM,
        programArgs: [],
      })
    }
  })
})

// An inspector flag in execArgv is a debugger PORT held by the parent, not a loader. A
// child that inherits it cannot bind the same port and dies with EADDRINUSE before it runs,
// so `node --inspect src/launcher.ts web` is the shape that breaks job spawning.
describe('resolveJobSpawnTarget: inspector flags are not inherited by the child', () => {
  function targetFor(execArgv: string[]): SpawnTarget['programArgs'] {
    return resolveJobSpawnTarget({
      execPath: '/usr/local/bin/node',
      execArgv,
      argv: ['/usr/local/bin/node', '/repo/dash/src/launcher.ts', 'web'],
      isSea: false,
    }).programArgs
  }

  it.each([
    ['--inspect'],
    ['--inspect-brk'],
    ['--inspect-wait'],
    ['--inspect=9229'],
    ['--inspect-brk=9229'],
    ['--debug'],
    ['--debug-brk'],
  ])('drops the valueless inspector flag %s and keeps the loader flags', (flag) => {
    expect(targetFor(['--no-deprecation', flag, '--import', 'loader.mjs'])).toEqual([
      '--no-deprecation',
      '--import',
      'loader.mjs',
      '/repo/dash/src/launcher.ts',
    ])
  })

  it.each([
    ['--inspect-port', '9229'],
    ['--debug-port', '9230'],
  ])('drops %s together with its separate value', (flag, port) => {
    expect(targetFor([flag, port, '--import', 'loader.mjs'])).toEqual([
      '--import',
      'loader.mjs',
      '/repo/dash/src/launcher.ts',
    ])
  })

  it.each(['--inspect-port=9229', '--debug-port=9230', '--debug-port='])(
    'drops the inspector flag written with an inline value: %s',
    (flag) => {
      expect(targetFor([flag, '--require', 'preflight.cjs'])).toEqual([
        '--require',
        'preflight.cjs',
        '/repo/dash/src/launcher.ts',
      ])
    },
  )

  it('keeps the loader flags the TypeScript entry needs, values included', () => {
    // Only the two port flags consume a separate value, so a `--import`/`--require` value
    // that happens to sit next to an inspector flag survives with its own flag - and the
    // `--experimental-*` family, which is load-bearing too, is left alone.
    const execArgv = [
      '--inspect',
      '--require',
      join(DASH_ROOT, 'node_modules/tsx/dist/preflight.cjs'),
      '--import',
      `file://${join(DASH_ROOT, 'node_modules/tsx/dist/loader.mjs')}`,
      '--experimental-strip-types',
      '-r',
      './register.js',
      '--no-warnings',
    ]
    expect(targetFor(execArgv)).toEqual([
      '--require',
      join(DASH_ROOT, 'node_modules/tsx/dist/preflight.cjs'),
      '--import',
      `file://${join(DASH_ROOT, 'node_modules/tsx/dist/loader.mjs')}`,
      '--experimental-strip-types',
      '-r',
      './register.js',
      '--no-warnings',
      '/repo/dash/src/launcher.ts',
    ])
  })

  it('filters execArgv on its own: an all-inspector list filters to empty', () => {
    expect(filterInspectorFlags(['--inspect', '--inspect-port', '9229'])).toEqual([])
    expect(filterInspectorFlags(['--import', 'loader.mjs'])).toEqual(['--import', 'loader.mjs'])
    expect(filterInspectorFlags([])).toEqual([])
  })
})

// The end-to-end checks shell out to the real CLI, so they are skipped rather than failed
// when the checkout has no tsx installed (`npm ci` has not been run).
const describeCheckout = existsSync(TSX_BIN) ? describe : describe.skip

if (!existsSync(TSX_BIN)) {
  describe.skip('spawned CLI, end to end (tsx not installed)', () => {
    it('is skipped: dash/node_modules/.bin/tsx does not exist, so no TypeScript entry can be spawned', () => {})
  })
}

describeCheckout('spawned CLI, end to end', () => {
  /** A temp HOME and a temp canon.db, so nothing here reads or writes the real ones. */
  function sandbox(): { env: NodeJS.ProcessEnv } {
    const home = mkdtempSync(join(tmpdir(), 'kyber-spawn-home-'))
    const db = join(home, 'canon.db')
    return { env: { ...process.env, HOME: home, USERPROFILE: home, KYBER_CANON_DB: db } }
  }

  it('runs `tsx src/launcher.ts dash settings show` from dash/', () => {
    const { env } = sandbox()
    const result = spawnSync(TSX_BIN, [LAUNCHER, 'dash', 'settings', 'show'], {
      cwd: DASH_ROOT,
      env,
      encoding: 'utf8',
      timeout: 120_000,
    })
    expect(result.status, result.stderr).toBe(0)
    expect(result.stdout).toContain('jobs.refresh_cadence_minutes')
  })

  it('resolves the real checkout runtime shape to that same command', () => {
    const { env } = sandbox()
    // What an actual `tsx` process reports about itself. Observed rather than hand-written,
    // so the execArgv the resolver sees is the loader flags tsx really installs.
    const probe = join(mkdtempSync(join(tmpdir(), 'kyber-spawn-probe-')), 'probe.ts')
    writeFileSync(
      probe,
      'console.log(JSON.stringify({ execPath: process.execPath, execArgv: process.execArgv }))\n',
    )
    const probed = spawnSync(TSX_BIN, [probe], { cwd: DASH_ROOT, env, encoding: 'utf8', timeout: 120_000 })
    expect(probed.status, probed.stderr).toBe(0)
    const observed = JSON.parse(probed.stdout.trim()) as { execPath: string; execArgv: string[] }

    // The probe ran the entry tsx was given; the CLI runs this one. Everything the resolver
    // decides comes from execPath/execArgv/isSea, so substituting the entry argv is the
    // shape a real `tsx src/launcher.ts` process has, with no flag invented.
    const runtime: SpawnRuntime = {
      execPath: observed.execPath,
      execArgv: observed.execArgv,
      argv: [observed.execPath, LAUNCHER],
      isSea: false,
    }
    const target = resolveJobSpawnTarget(runtime)
    const result = spawnSync(target.program, [...target.programArgs, 'dash', 'settings', 'show'], {
      cwd: DASH_ROOT,
      env,
      encoding: 'utf8',
      timeout: 120_000,
    })
    expect(result.status, `stderr:\n${result.stderr}`).toBe(0)
    // The four settings a job child would read on its way through the CLI root.
    for (const key of [
      'folder_import.scheduled',
      'jobs.paused',
      'jobs.refresh_cadence_minutes',
      'receiver.hosted',
    ]) {
      expect(result.stdout).toContain(key)
    }
  })
})