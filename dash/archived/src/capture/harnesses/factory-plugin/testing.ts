// Test kit for the Factory (Droid) capture plugin's marketplace route (test-only).
//
// The seam under test is the harness's CLI, `FactoryHarnessCli`. The fake records every argv
// the install and the uninstall would run and answers the two list commands from the state
// it holds, so a test asserts the argv rather than the effect, and no test needs a real
// `droid` on the machine.

import { basename } from 'node:path'

import { FACTORY_MARKETPLACE_NAME, type DroidCommandResult, type FactoryHarnessCli } from './marketplace.js'

/**
 * The source a marketplace the fake was never told about reports.
 *
 * It is NOT KyberDash's own tree, so a marketplace seeded straight into the fake's set is a
 * collision — which is what the real harness would say about a marketplace registered from
 * some other directory (D46).
 */
function defaultSource(name: string): string {
  return `/home/me/.kyberdash/marketplaces/${name}`
}

export type FakeHarness = {
  /** The CLI the install and the uninstall reach. */
  harness: FactoryHarnessCli
  /** Every argv the fake was asked to run, in order. */
  calls: string[][]
  /** The marketplaces the fake believes the harness holds. */
  marketplaces: Set<string>
  /**
   * The source each registered marketplace was added from, as the harness's SOURCE column
   * reports it. A marketplace the fake was never told about reports a path that is NOT
   * KyberDash's own tree, which is what makes it a collision (D46).
   */
  sources: Map<string, string>
  /** The plugin identities the fake believes the harness holds. */
  plugins: Set<string>
  /**
   * The status word each installed plugin carries, as the harness's STATUS column reports
   * it. An installed plugin is `Enabled` unless a test says otherwise.
   */
  statuses: Map<string, string>
  /**
   * Make the next command that changes the harness fail, with the harness's own stderr. The
   * failure is reported once, so a test can pin what the install does with it.
   */
  failNext: (stderr: string) => void
}

export type FakeHarnessOptions = {
  /** The executable the fake reports it used. */
  executable?: string
  /**
   * The named cause the fake reports instead of running anything: the CLI is missing, or its
   * output cannot be understood.
   */
  unavailable?: string
}

/**
 * A fake `droid`: it holds the harness's registry, answers the two list commands from it, and
 * applies the four mutating commands to it.
 */
export function fakeHarness(options: FakeHarnessOptions = {}): FakeHarness {
  const calls: string[][] = []
  const marketplaces = new Set<string>()
  const sources = new Map<string, string>()
  const plugins = new Set<string>()
  const statuses = new Map<string, string>()
  let pendingFailure: string | undefined

  const harness: FactoryHarnessCli = {
    executable: options.unavailable === undefined ? (options.executable ?? '/usr/local/bin/droid') : undefined,
    unavailable: options.unavailable,
    run: (argv: readonly string[]): DroidCommandResult => {
      calls.push([...argv])
      if (options.unavailable !== undefined) {
        return { exitCode: 127, stdout: '', stderr: options.unavailable }
      }
      if (pendingFailure !== undefined) {
        const stderr = pendingFailure
        pendingFailure = undefined
        return { exitCode: 1, stdout: '', stderr }
      }
      const words = [...argv]
      if (words[0] === 'plugin' && words[1] === 'marketplace') {
        if (words[2] === 'list') {
          return {
            exitCode: 0,
            stdout:
              marketplaces.size === 0
                ? 'No marketplaces registered.\n'
                : [...marketplaces]
                    .map((name) => `${name}  local:${sources.get(name) ?? defaultSource(name)}`)
                    .join('\n') + '\n',
            stderr: '',
          }
        }
        if (words[2] === 'add') {
          // The harness registers the marketplace under the SOURCE DIRECTORY'S BASENAME and
          // remembers the source it was given, so the fake does the same.
          const root = words[3] ?? ''
          const name = basename(root)
          marketplaces.add(name)
          sources.set(name, root)
          return { exitCode: 0, stdout: '', stderr: '' }
        }
        if (words[2] === 'remove') {
          marketplaces.delete(words[3] ?? '')
          sources.delete(words[3] ?? '')
          return { exitCode: 0, stdout: '', stderr: '' }
        }
      }
      if (words[0] === 'plugin' && words[1] === 'list') {
        return {
          exitCode: 0,
          stdout:
            plugins.size === 0
              ? 'No plugins installed in user scope.\n'
              : [...plugins].map((identity) => `${identity}  ${statuses.get(identity) ?? 'Enabled'}  1.0.0`).join('\n') + '\n',
          stderr: '',
        }
      }
      if (words[0] === 'plugin' && words[1] === 'install') {
        plugins.add(words[2] ?? '')
        return { exitCode: 0, stdout: '', stderr: '' }
      }
      if (words[0] === 'plugin' && words[1] === 'update') {
        // The harness re-copies the plugin's files into its own cache. The plugin stays
        // installed, and it fails when the marketplace it came from is not registered.
        const identity = words[2]
        if (identity !== undefined && plugins.has(identity)) {
          return { exitCode: 0, stdout: `Successfully updated ${identity}`, stderr: '' }
        }
        return {
          exitCode: 1,
          stdout: '',
          stderr: `Failed to update : Marketplace "${FACTORY_MARKETPLACE_NAME}" not found`,
        }
      }
      if (words[0] === 'plugin' && words[1] === 'uninstall') {
        plugins.delete(words[2] ?? '')
        return { exitCode: 0, stdout: '', stderr: '' }
      }
      return { exitCode: 1, stdout: '', stderr: `unknown command: ${words.join(' ')}` }
    },
  }

  return {
    harness,
    calls,
    marketplaces,
    sources,
    plugins,
    statuses,
    failNext: (stderr: string) => {
      pendingFailure = stderr
    },
  }
}

/** The argv of every command the fake was asked to run, joined for one assertion. */
export function fakeCommandLines(fake: FakeHarness): string[] {
  return fake.calls.map((argv) => argv.join(' '))
}
