// Shared T8 fixture: refresh into a temp canon.db (never ~/.kyberdash/canon.db).

import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { CanonStore } from '../src/canon/store.js'
import { refreshHarnessSources } from '../src/refresh/orchestrator.js'
import { descriptorFor } from '../src/refresh/registry.js'
import {
  COMMAND_STARTED_AT,
  copyFixture,
  fixtureProvider,
  jsonlLoader,
  sqliteLoader,
  writeCursorVirtualDb,
} from '../src/refresh/fixtures/integration-harness.js'
import type { Provider, SessionSource } from '../src/providers/types.js'

export const USER_CANON = join(homedir(), '.kyberdash', 'canon.db')

export const JOB_IDS = [
  'antigravity',
  'antigravity-cli',
  'antigravity-ide',
  'copilot-cli',
  'copilot-vscode',
  'copilot-jetbrains',
  'copilot-agent',
  'codex-cli',
  'codex-desktop',
  'codex-unclassified',
  'claude-cli',
  'claude-desktop',
  'claude-unclassified',
  'cursor',
  'cursor-agent',
  'cline',
  'cline-cli',
  'kiro-cli',
  'kiro-ide',
  'kilo-shared-runtime',
  'kilo-vscode-legacy',
  'pi',
  'opencode',
  'droid',
] as const

/** Surfaces this fixture actually ingests (others remain registered-empty). */
export const POPULATED_HARNESSES = [
  'antigravity',
  'antigravity-cli',
  'antigravity-ide',
  'copilot-cli',
  'cursor',
  'cursor-agent',
  'kilo-shared-runtime',
  'kilo-vscode-legacy',
  'pi',
  'opencode',
] as const

export type RefreshedWorld = {
  root: string
  dbPath: string
  store: CanonStore
}

function source(path: string, provider: string, extras: Partial<SessionSource> = {}): SessionSource {
  return { path, project: extras.project ?? provider, provider, ...extras }
}

function descriptorsFor(ids: readonly string[]) {
  return ids.map((id) => {
    const descriptor = descriptorFor(id)
    if (descriptor === undefined) throw new Error(`missing descriptor ${id}`)
    return descriptor
  })
}

function writeTurnJsonl(
  destination: string,
  turns: Array<{ sessionId: string; turnId: string; timestamp: string; provider: string }>,
): string {
  mkdirSync(dirname(destination), { recursive: true })
  const lines = turns.map((turn) =>
    JSON.stringify({
      type: 'message',
      timestamp: turn.timestamp,
      sessionId: turn.sessionId,
      turnId: turn.turnId,
      provider: turn.provider,
      model: 'test-model',
      message: { role: 'assistant', content: `${turn.provider} fixture ${turn.turnId}` },
    }),
  )
  writeFileSync(destination, `${lines.join('\n')}\n`)
  return destination
}

export function createTempRoot(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-refresh-t8-'))
  if (!root.startsWith(tmpdir())) {
    throw new Error(`T8 temp root escaped tmpdir: ${root}`)
  }
  if (root.includes('.kyberdash')) {
    throw new Error(`T8 temp root must not sit under .kyberdash: ${root}`)
  }
  return root
}

export async function refreshTempCanon(root = createTempRoot()): Promise<RefreshedWorld> {
  const dbPath = join(root, 'canon.db')
  if (dbPath === USER_CANON) {
    throw new Error('refusing to use the user canon.db as a T8 write target')
  }

  const piPath = copyFixture('pi-cross-cutoff.jsonl', join(root, 'pi', 'pi-cross-cutoff.jsonl'))
  const antigravityPath = copyFixture(
    'antigravity-conversation.jsonl',
    join(root, '.gemini', 'antigravity', 'conversation.jsonl'),
  )
  const antigravityCliPath = copyFixture(
    'antigravity-cli-conversation.jsonl',
    join(root, '.gemini', 'antigravity-cli', 'conversation.jsonl'),
  )
  const antigravityIdePath = copyFixture(
    'antigravity-conversation.jsonl',
    join(root, '.gemini', 'antigravity-ide', 'conversation.jsonl'),
  )
  const copilotPath = copyFixture('copilot-cli-session.jsonl', join(root, '.copilot', 'session.jsonl'))
  const cursorDbPath = writeCursorVirtualDb(join(root, 'cursor', 'state.vscdb'))
  const cursorAgentPath = writeTurnJsonl(join(root, '.cursor', 'projects', 'agent.jsonl'), [
    {
      sessionId: 'cursor-agent-1',
      turnId: 'agent-turn-1',
      timestamp: '2026-09-06T14:00:00.000Z',
      provider: 'cursor-agent',
    },
  ])
  const opencodePath = writeTurnJsonl(join(root, '.local', 'share', 'opencode', 'session.jsonl'), [
    {
      sessionId: 'opencode-1',
      turnId: 'oc-1',
      timestamp: '2026-09-06T15:00:00.000Z',
      provider: 'opencode',
    },
  ])
  const kiloSharedPath = writeTurnJsonl(join(root, 'kilo', 'kilo.db'), [
    {
      sessionId: 'kilo-shared-1',
      turnId: 'ks-1',
      timestamp: '2026-09-06T16:00:00.000Z',
      provider: 'kilo-code',
    },
  ])
  const kiloLegacyPath = writeTurnJsonl(join(root, 'kilocode.kilo-code', 'session.jsonl'), [
    {
      sessionId: 'kilo-legacy-1',
      turnId: 'kl-1',
      timestamp: '2026-09-06T16:30:00.000Z',
      provider: 'kilo-code',
    },
  ])

  const store = new CanonStore(dbPath)
  const providers = async (): Promise<Provider[]> => [
    fixtureProvider('pi', [source(piPath, 'pi', { sourceId: 'pi-cross' })], jsonlLoader),
    fixtureProvider(
      'antigravity',
      [
        source(antigravityPath, 'antigravity', { project: 'antigravity', sourceId: 'agy-root' }),
        source(antigravityCliPath, 'antigravity', { project: 'antigravity-cli', sourceId: 'agy-cli' }),
        source(antigravityIdePath, 'antigravity', { project: 'antigravity-ide', sourceId: 'agy-ide' }),
      ],
      jsonlLoader,
    ),
    fixtureProvider(
      'copilot',
      [source(copilotPath, 'copilot', { sourceType: 'jsonl', sourceId: 'copilot-cli-1' })],
      jsonlLoader,
    ),
    fixtureProvider('cursor', [source(cursorDbPath, 'cursor', { sourceId: 'cursor-virtual' })], sqliteLoader),
    fixtureProvider(
      'cursor-agent',
      [source(cursorAgentPath, 'cursor-agent', { sourceId: 'cursor-agent-1' })],
      jsonlLoader,
    ),
    fixtureProvider('opencode', [source(opencodePath, 'opencode', { sourceId: 'opencode-1' })], jsonlLoader),
    fixtureProvider(
      'kilo-code',
      [
        source(kiloSharedPath, 'kilo-code', { sourceId: 'kilo-shared' }),
        source(kiloLegacyPath, 'kilo-code', { sourceId: 'kilo-legacy' }),
      ],
      jsonlLoader,
    ),
  ]

  await refreshHarnessSources(
    store,
    {
      getAllProviders: providers,
      descriptors: descriptorsFor(JOB_IDS),
      jobConcurrency: 2,
      writerCapacity: 2,
      commandStartedAt: COMMAND_STARTED_AT,
      parseAllSessions: async () => undefined,
    },
    { historyWeeks: 6 },
  )

  return { root, dbPath, store }
}
