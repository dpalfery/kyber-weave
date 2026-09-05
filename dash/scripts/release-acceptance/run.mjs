#!/usr/bin/env node

import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { spawn } from 'node:child_process'
import { performance } from 'node:perf_hooks'
import { basename, isAbsolute, join, resolve } from 'node:path'

const MODES = new Set(['preflight', 'tests', 'package'])

function usage() {
  return `Usage: node scripts/release-acceptance/run.mjs --mode <preflight|tests|package> --output <absolute-dir>

Captures exact candidate/environment provenance. tests adds root, lock, and Swift suites.
package also runs the production build and packages the universal Menu Bar app.
The runner never restores tracked files: any worktree mutation fails the gate.`
}

function parseArgs(argv) {
  const result = { mode: 'preflight', output: '' }
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]
    if (arg === '--help' || arg === '-h') return { help: true }
    if (arg === '--mode') result.mode = argv[++i] ?? ''
    else if (arg === '--output') result.output = argv[++i] ?? ''
    else throw new Error(`Unknown argument: ${arg}`)
  }
  if (!MODES.has(result.mode)) throw new Error(`Invalid mode: ${result.mode}`)
  if (!result.output || !isAbsolute(result.output)) {
    throw new Error('--output must be an absolute directory')
  }
  return result
}

function run(command, args, options = {}) {
  return new Promise(resolveRun => {
    const started = performance.now()
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: process.env,
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    const stdout = []
    const stderr = []
    child.stdout.on('data', chunk => stdout.push(chunk))
    child.stderr.on('data', chunk => stderr.push(chunk))
    child.on('error', error => {
      resolveRun({ command, args, exitCode: null, durationMs: performance.now() - started, stdout: '', stderr: String(error) })
    })
    child.on('close', exitCode => {
      resolveRun({
        command,
        args,
        exitCode,
        durationMs: performance.now() - started,
        stdout: Buffer.concat(stdout).toString('utf8'),
        stderr: Buffer.concat(stderr).toString('utf8'),
      })
    })
  })
}

async function capture(command, args, cwd) {
  const result = await run(command, args, { cwd })
  if (result.exitCode !== 0) throw new Error(`${command} ${args.join(' ')} failed: ${result.stderr.trim()}`)
  return result.stdout.trim()
}

function csv(value) {
  const text = String(value ?? '')
  return /[",\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text
}

async function main() {
  let options
  try {
    options = parseArgs(process.argv.slice(2))
  } catch (error) {
    console.error(String(error))
    console.error(usage())
    process.exitCode = 2
    return
  }
  if (options.help) {
    console.log(usage())
    return
  }

  const root = await capture('git', ['rev-parse', '--show-toplevel'], process.cwd())
  const output = resolve(options.output)
  await mkdir(output, { recursive: true, mode: 0o700 })
  await mkdir(join(output, 'logs'), { recursive: true, mode: 0o700 })

  const dirtyBefore = await capture('git', ['status', '--porcelain=v1'], root)
  if (dirtyBefore) throw new Error('Release acceptance requires a clean worktree before execution.')

  const packageJson = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'))
  const sha = await capture('git', ['rev-parse', 'HEAD'], root)
  const parent = await capture('git', ['rev-parse', 'HEAD^'], root)
  const startedAt = new Date().toISOString()
  const label = `acceptance-${sha.slice(0, 8)}`
  const provenance = {
    started_at: startedAt,
    candidate: {
      sha,
      parent,
      branch: await capture('git', ['branch', '--show-current'], root),
      version: packageJson.version,
      commit: await capture('git', ['show', '-s', '--format=%H%n%P%n%cI%n%s', 'HEAD'], root),
    },
    environment: {
      os: await capture('sw_vers', [], root),
      arch: await capture('uname', ['-m'], root),
      hardware: await capture('sysctl', ['-n', 'hw.model'], root),
      memory_bytes: Number(await capture('sysctl', ['-n', 'hw.memsize'], root)),
      node: process.version,
      npm: await capture('npm', ['--version'], root),
      swift: await capture('swift', ['--version'], root).catch(() => null),
    },
  }
  await writeFile(join(output, 'provenance.json'), `${JSON.stringify(provenance, null, 2)}\n`, { mode: 0o600 })

  const commands = []
  if (options.mode === 'tests' || options.mode === 'package') {
    commands.push(
      { id: 'root-tests', command: 'npm', args: ['test'] },
      { id: 'lock-tests', command: 'npm', args: ['run', 'test:locks'] },
      { id: 'native-tests', command: 'swift', args: ['test', '--package-path', 'mac'] },
    )
  }
  if (options.mode === 'package') {
    commands.push(
      { id: 'production-build', command: 'npm', args: ['run', 'build'] },
      { id: 'menubar-package', command: 'mac/Scripts/package-app.sh', args: [label] },
    )
  }

  const results = []
  for (const spec of commands) {
    const result = await run(spec.command, spec.args, { cwd: root })
    const logName = `${spec.id}.log`
    await writeFile(join(output, 'logs', logName), `${result.stdout}${result.stderr}`, { mode: 0o600 })
    results.push({
      id: spec.id,
      status: result.exitCode === 0 ? 'pass' : 'fail',
      exit_code: result.exitCode,
      duration_ms: Number(result.durationMs.toFixed(3)),
      log: join('logs', logName),
    })
    if (result.exitCode !== 0) break
  }

  const artifacts = []
  if (options.mode === 'package' && results.every(result => result.status === 'pass')) {
    const artifactPath = join(root, 'mac', '.build', 'dist', `CodeBurnMenubar-${label}.zip`)
    const sha256 = await capture('shasum', ['-a', '256', artifactPath], root)
    artifacts.push({ surface: 'menubar', name: basename(artifactPath), sha256: sha256.split(/\s+/)[0] })
  }

  const dirtyAfter = await capture('git', ['status', '--porcelain=v1'], root)
  const status = results.every(result => result.status === 'pass') && !dirtyAfter ? 'pass' : 'fail'
  const summary = {
    ...provenance,
    completed_at: new Date().toISOString(),
    mode: options.mode,
    status,
    tests: results,
    artifacts,
    worktree: {
      clean_before: true,
      clean_after: !dirtyAfter,
      changed_paths_after: dirtyAfter ? dirtyAfter.split('\n') : [],
    },
  }
  await writeFile(join(output, 'automated-results.json'), `${JSON.stringify(summary, null, 2)}\n`, { mode: 0o600 })
  const timingRows = ['id,status,exit_code,duration_ms,log', ...results.map(result => [
    result.id,
    result.status,
    result.exit_code,
    result.duration_ms,
    result.log,
  ].map(csv).join(','))]
  await writeFile(join(output, 'timings.csv'), `${timingRows.join('\n')}\n`, { mode: 0o600 })

  console.log(JSON.stringify({ output, status, candidate: sha, tests: results, artifacts }, null, 2))
  if (status !== 'pass') process.exitCode = 1
}

await main().catch(error => {
  console.error(error instanceof Error ? error.stack : String(error))
  process.exitCode = 1
})
