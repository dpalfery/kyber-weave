// Plan T3 (D3, R1): the committed plugin text module is a fresh bundle of the
// entry, the bundle carries only the 2.1.4 `{ id, setup }` default, and it
// loads from a data URL (no file asset, as inside the SEA).

import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { describe, expect, it } from 'vitest'
import { OPENCODE_PLUGIN_MARKER, OPENCODE_PLUGIN_SOURCE } from './generated/plugin-source.js'

const here = dirname(fileURLToPath(import.meta.url))
const generatedFile = join(here, 'generated', 'plugin-source.ts')
const scriptUrl = pathToFileURL(join(here, '..', '..', '..', '..', 'scripts', 'bundle-opencode-plugin.mjs')).href

type BundleScript = {
  OPENCODE_PLUGIN_MARKER: string
  bundleOpencodePlugin: () => Promise<string>
  renderPluginSourceModule: (source: string) => string
}

async function loadScript(): Promise<BundleScript> {
  return (await import(/* @vite-ignore */ scriptUrl)) as BundleScript
}

/**
 * Load `source` the way OpenCode would: a real Node process doing `import()` of a data URL.
 * Vitest's module runner rejects data URLs, and a child process also proves the text needs no
 * file beside it. `probe` runs in the child with the namespace and prints one JSON result.
 */
function probeInNode(source: string, probe: string): unknown {
  const url = `data:text/javascript;base64,${Buffer.from(source, 'utf8').toString('base64')}`
  const program = `const ns = await import(${JSON.stringify(url)})\n${probe}`
  // The program goes on stdin, not in `-e`: the data URL alone can exceed the Windows
  // command-line limit (32,767 characters), which makes spawn fail with ENAMETOOLONG.
  const out = execFileSync(process.execPath, ['--input-type=module'], {
    input: program,
    encoding: 'utf8',
    timeout: 20_000,
    env: { PATH: process.env['PATH'] ?? '' },
  })
  return JSON.parse(out)
}

/** Specifiers of every static `import … from`, bare `import 'x'`, and dynamic `import('x')`. */
function importSpecifiers(source: string): string[] {
  const found: string[] = []
  const patterns = [/\bfrom\s*["']([^"']+)["']/g, /\bimport\s*["']([^"']+)["']/g, /\bimport\(\s*["']([^"']+)["']\s*\)/g]
  for (const pattern of patterns) {
    for (const match of source.matchAll(pattern)) found.push(match[1] ?? '')
  }
  return found
}

describe('committed OpenCode plugin source (drift guard)', () => {
  it('equals a fresh bundle of the entry', async () => {
    const script = await loadScript()
    expect(OPENCODE_PLUGIN_SOURCE).toBe(await script.bundleOpencodePlugin())
  })

  it('is the exact module text the build script renders', async () => {
    const script = await loadScript()
    expect(readFileSync(generatedFile, 'utf8')).toBe(script.renderPluginSourceModule(OPENCODE_PLUGIN_SOURCE))
  })

  it('starts with the ownership marker on its first line', async () => {
    const script = await loadScript()
    expect(OPENCODE_PLUGIN_MARKER).toBe(script.OPENCODE_PLUGIN_MARKER)
    expect(OPENCODE_PLUGIN_SOURCE.split('\n')[0]).toBe(OPENCODE_PLUGIN_MARKER)
  })

  it('imports only node: built-ins and never requires', () => {
    const specifiers = importSpecifiers(OPENCODE_PLUGIN_SOURCE)
    expect(specifiers.length).toBeGreaterThan(0)
    expect(specifiers.filter((specifier) => !specifier.startsWith('node:'))).toEqual([])
    expect(OPENCODE_PLUGIN_SOURCE).not.toMatch(/\brequire\(/)
  })
})

describe('bundled plugin loaded from a data URL', () => {
  it('exposes only a default export with exactly the keys id and setup', () => {
    const shape = probeInNode(
      OPENCODE_PLUGIN_SOURCE,
      `console.log(JSON.stringify({
        exports: Object.keys(ns),
        keys: Object.keys(ns.default).sort(),
        id: ns.default.id,
        setupType: typeof ns.default.setup,
      }))`,
    )
    expect(shape).toEqual({ exports: ['default'], keys: ['id', 'setup'], id: 'kyberdash-opencode', setupType: 'function' })
  })

  it('setup registers the V2 session and tool hooks, subscribes to events, returns a cleanup, and starts no fetch', () => {
    // The probe ends through stdout's write callback, so the output is flushed before the process exits.
    const probe = [
      'let fetches = 0',
      'globalThis.fetch = () => { fetches += 1; return Promise.reject(new Error("no network in the load test")) }',
      'const session = []',
      'const tool = []',
      'let subscriptions = 0',
      'const ctx = {',
      "  location: { directory: '/HOME/scratch/oc-probe' },",
      '  session: { hook: (name) => { session.push(name); return Promise.resolve({}) } },',
      '  tool: { hook: (name) => { tool.push(name); return Promise.resolve({}) } },',
      '  event: { subscribe: () => { subscriptions += 1; return { [Symbol.asyncIterator]: () => ({ next: () => new Promise(() => {}), return: () => Promise.resolve({ done: true, value: undefined }) }) } } },',
      '}',
      'const cleanup = await ns.default.setup(ctx)',
      'process.stdout.write(JSON.stringify({ session: session.sort(), tool: tool.sort(), subscriptions, fetches, cleanup: typeof cleanup }) + "\\n", () => process.exit(0))',
    ].join('\n')
    const result = probeInNode(OPENCODE_PLUGIN_SOURCE, probe)
    expect(result).toEqual({
      session: ['context', 'model.request', 'prompt'],
      tool: ['execute.after', 'execute.before'],
      subscriptions: 1,
      fetches: 0,
      cleanup: 'function',
    })
  })
})
