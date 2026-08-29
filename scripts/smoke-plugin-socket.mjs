#!/usr/bin/env node
/**
 * Smoke test for the plugin socket step 9.
 *
 * Exercises:
 *   - `codeburn plugin list` against an empty dir (default state)
 *   - `codeburn plugin list` against a dir with one valid + one rejected plugin
 *   - `codeburn plugin info <name>` for the loaded plugin
 *   - `codeburn plugin verify <name>` (uses CODEBURN_PLUGIN_DEV=1 since signing lands in 9b)
 *   - `codeburn plugin verify <name>` for the rejected plugin
 *   - `codeburn --help` to confirm the subcommand shows up
 *
 * Exits 0 on full success, 1 on any failure.
 */
import { spawn } from 'child_process'
import { mkdtemp, mkdir, writeFile, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

const CLI = join(import.meta.dirname, '..', 'dist', 'main.js')

async function run(args, opts = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn('node', [CLI, ...args], {
      stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, ...opts.env },
    })
    let stdout = ''
    let stderr = ''
    child.stdout.on('data', d => { stdout += d.toString() })
    child.stderr.on('data', d => { stderr += d.toString() })
    child.on('close', code => resolve({ code, stdout, stderr }))
    child.on('error', reject)
  })
}

function assertEq(actual, expected, label) {
  if (actual !== expected) {
    console.error(`FAIL: ${label}\n  expected: ${JSON.stringify(expected)}\n  actual:   ${JSON.stringify(actual)}`)
    process.exitCode = 1
  } else {
    console.log(`PASS: ${label}`)
  }
}

function assertContains(actual, needle, label) {
  if (!actual.includes(needle)) {
    console.error(`FAIL: ${label}\n  needle:   ${JSON.stringify(needle)}\n  actual:   ${JSON.stringify(actual)}`)
    process.exitCode = 1
  } else {
    console.log(`PASS: ${label}`)
  }
}

async function main() {
  // 1) help
  const help = await run(['--help'])
  assertEq(help.code, 0, '`codeburn --help` exits 0')
  assertContains(help.stdout, 'plugin', '`codeburn --help` mentions the plugin subcommand')

  // 2) plugin list — empty
  const emptyDir = await mkdtemp(join(tmpdir(), 'smoke-empty-'))
  try {
    const r = await run(['plugin', 'list', '--dir', emptyDir])
    assertEq(r.code, 0, '`plugin list` against empty dir exits 0')
    assertContains(r.stdout, 'No plugins found', '`plugin list` reports empty')
  } finally {
    await rm(emptyDir, { recursive: true, force: true })
  }

  // 3) plugin list — mixed
  const pluginDir = await mkdtemp(join(tmpdir(), 'smoke-plugins-'))
  try {
    const goodDir = join(pluginDir, 'good-plugin')
    const badDir = join(pluginDir, 'bad-plugin')
    await mkdir(goodDir, { recursive: true })
    await mkdir(badDir, { recursive: true })
    await writeFile(join(goodDir, 'codeburn-plugin.json'), JSON.stringify({
      name: 'good-plugin', version: '0.1.0', cliCompat: '>=0.9.22',
      capabilities: { commands: ['good'], syncAttributes: [], payloadSections: [], spanKinds: [] },
    }))
    await writeFile(join(badDir, 'codeburn-plugin.json'), '{not json')

    const r = await run(['plugin', 'list', '--dir', pluginDir], { env: { CODEBURN_PLUGIN_DEV: '1' } })
    assertEq(r.code, 0, '`plugin list` with one valid + one rejected exits 0')
    assertContains(r.stdout, 'loaded   good-plugin@0.1.0', '`plugin list` shows the loaded plugin')
    assertContains(r.stdout, 'rejected bad-plugin', '`plugin list` shows the rejected plugin')

    // 4) info <name>
    const info = await run(['plugin', 'info', 'good-plugin', '--dir', pluginDir], { env: { CODEBURN_PLUGIN_DEV: '1' } })
    assertEq(info.code, 0, '`plugin info good-plugin` exits 0')
    assertContains(info.stdout, '"name": "good-plugin"', '`plugin info` prints the manifest')

    // 5) verify <name> — good (with dev flag)
    const verify = await run(['plugin', 'verify', 'good-plugin', '--dir', pluginDir], { env: { CODEBURN_PLUGIN_DEV: '1' } })
    assertEq(verify.code, 0, '`plugin verify good-plugin` exits 0 under CODEBURN_PLUGIN_DEV=1')
    assertContains(verify.stdout, 'verified  good-plugin@0.1.0', '`plugin verify` prints the verified line')

    // 6) verify <name> — bad (malformed manifest)
    const verifyBad = await run(['plugin', 'verify', 'bad-plugin', '--dir', pluginDir])
    assertEq(verifyBad.code, 1, '`plugin verify bad-plugin` exits 1 for malformed manifest')
    assertContains(verifyBad.stdout + verifyBad.stderr, 'missing or unreadable', '`plugin verify` reports the read failure')
  } finally {
    await rm(pluginDir, { recursive: true, force: true })
  }

  // 7) add/remove flow with CODEBURN_PLUGIN_DEV=1
  const addRemoveDir = await mkdtemp(join(tmpdir(), 'smoke-add-remove-'))
  try {
    const sourceDir = join(addRemoveDir, 'source')
    const installDir = join(addRemoveDir, 'installed')
    await mkdir(sourceDir, { recursive: true })
    await mkdir(installDir, { recursive: true })
    await writeFile(join(sourceDir, 'codeburn-plugin.json'), JSON.stringify({
      name: 'dev-plugin', version: '0.1.0', cliCompat: '>=0.9.22',
      capabilities: { commands: [], syncAttributes: [], payloadSections: [], spanKinds: [] },
    }))
    await writeFile(join(sourceDir, 'test.txt'), 'test content')

    // Add plugin with dev flag (unsigned)
    const addResult = await run(['plugin', 'add', sourceDir, '--dir', installDir], { env: { CODEBURN_PLUGIN_DEV: '1' } })
    assertEq(addResult.code, 0, '`plugin add` succeeds with CODEBURN_PLUGIN_DEV=1')
    assertContains(addResult.stdout, 'dev-plugin@0.1.0', '`plugin add` confirms installation')

    // List should show the added plugin
    const listAfterAdd = await run(['plugin', 'list', '--dir', installDir], { env: { CODEBURN_PLUGIN_DEV: '1' } })
    assertEq(listAfterAdd.code, 0, '`plugin list` after add exits 0')
    assertContains(listAfterAdd.stdout, 'loaded   dev-plugin@0.1.0', '`plugin list` shows added plugin')

    // Remove without --confirm should fail
    const removeNoConfirm = await run(['plugin', 'remove', 'dev-plugin', '--dir', installDir])
    assertEq(removeNoConfirm.code, 1, '`plugin remove` without --confirm exits 1')
    assertContains(removeNoConfirm.stdout, 'Would remove', '`plugin remove` prints confirmation prompt')

    // Remove with --confirm should succeed
    const removeConfirm = await run(['plugin', 'remove', 'dev-plugin', '--confirm', '--dir', installDir])
    assertEq(removeConfirm.code, 0, '`plugin remove --confirm` succeeds')
    assertContains(removeConfirm.stdout, 'removed', '`plugin remove` confirms removal')

    // List should be empty after removal
    const listAfterRemove = await run(['plugin', 'list', '--dir', installDir])
    assertEq(listAfterRemove.code, 0, '`plugin list` after remove exits 0')
    assertContains(listAfterRemove.stdout, 'No plugins', '`plugin list` shows empty')
  } finally {
    await rm(addRemoveDir, { recursive: true, force: true })
  }

  if (process.exitCode === 1) {
    console.error('\nSMOKE FAILED')
  } else {
    console.log('\nSMOKE PASSED')
  }
}

main().catch(err => { console.error(err); process.exit(1) })