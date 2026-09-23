import { createHash } from 'node:crypto'

import { describe, expect, it } from 'vitest'

import type { SessionSource } from '../providers/types.js'
import { getAllProviders } from '../providers/index.js'

import {
  HARNESS_DESCRIPTORS,
  PROVIDER_DISPOSITIONS,
  auditProviderRegistry,
  classifySessionSource,
  descriptorFor,
  sourceKeyFor,
} from './registry.js'

const REQUIRED_HARNESS_IDS = [
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
] as const

const REMAINING_HARNESS_IDS = [
  'codewhale',
  'codebuff',
  'devin',
  'droid',
  'dsh',
  'hermes',
  'ibm-bob',
  'kimi',
  'kimi-code',
  'lingtai-tui',
  'mistral-vibe',
  'mux',
  'openclaw',
  'openclaude',
  'open-design',
  'omp',
  'qwen',
  'quickdesk',
  'roo-code',
  'zerostack',
  'grok',
  'forge',
  'goose',
  'crush',
  'warp',
  'zcode',
  'zed',
] as const

const INVENTORY_PROVIDER_NAMES = [
  'claude',
  'cline',
  'cline-cli',
  'codewhale',
  'codebuff',
  'codex',
  'copilot',
  'devin',
  'droid',
  'dsh',
  'gemini',
  'hermes',
  'ibm-bob',
  'kilo-code',
  'kiro',
  'kimi',
  'kimicode',
  'lingtai-tui',
  'mistral-vibe',
  'mux',
  'openclaw',
  'openclaude',
  'open-design',
  'pi',
  'omp',
  'qwen',
  'quickdesk',
  'roo-code',
  'zerostack',
  'grok',
  'antigravity',
  'forge',
  'goose',
  'cursor',
  'opencode',
  'cursor-agent',
  'crush',
  'warp',
  'vercel-gateway',
  'zcode',
  'zed',
] as const

function source(partial: Partial<SessionSource> & Pick<SessionSource, 'path' | 'provider'>): SessionSource {
  return {
    project: partial.project ?? 'fixture',
    ...partial,
  }
}

describe('HarnessSourceRegistry', () => {
  it('declares a disposition for every inventory provider name', () => {
    expect(Object.keys(PROVIDER_DISPOSITIONS).sort()).toEqual([...INVENTORY_PROVIDER_NAMES].sort())
  })

  it('accounts for every live getAllProviders() entry', async () => {
    const providers = await getAllProviders()
    expect(providers.length).toBeGreaterThan(0)
    expect(() => auditProviderRegistry(providers)).not.toThrow()

    const unaccounted = providers
      .map(provider => provider.name)
      .filter(name => PROVIDER_DISPOSITIONS[name] === undefined)
    expect(unaccounted).toEqual([])
  })

  it('fails the audit when an unknown provider is registered', () => {
    expect(() => auditProviderRegistry([{ name: 'brand-new-local-bot' }])).toThrow(/brand-new-local-bot/)
  })

  it('excludes Gemini and Vercel Gateway with reasons and never emits Gemini as a harness id', () => {
    expect(PROVIDER_DISPOSITIONS.gemini).toEqual({
      kind: 'excluded',
      reason: expect.stringMatching(/model|chat|not a (coding )?harness/i),
    })
    expect(PROVIDER_DISPOSITIONS['vercel-gateway']).toEqual({
      kind: 'excluded',
      reason: expect.stringMatching(/network/i),
    })

    const harnessIds = HARNESS_DESCRIPTORS.map(descriptor => descriptor.harnessId)
    expect(harnessIds).not.toContain('gemini')
    expect(classifySessionSource({
      source: source({ path: '/tmp/gemini-chat.json', provider: 'gemini' }),
    })).toEqual({
      outcome: 'excluded',
      provider: 'gemini',
      reason: expect.any(String),
    })
    expect(classifySessionSource({
      source: source({ path: 'vercel-ai-gateway:report', provider: 'vercel-gateway' }),
    }).outcome).toBe('excluded')
  })

  it('emits the required split identities with safe labels and parser contract versions', () => {
    const byId = new Map(HARNESS_DESCRIPTORS.map(descriptor => [descriptor.harnessId, descriptor]))
    for (const harnessId of [...REQUIRED_HARNESS_IDS, ...REMAINING_HARNESS_IDS]) {
      const descriptor = byId.get(harnessId)
      expect(descriptor, `missing harness ${harnessId}`).toBeDefined()
      expect(descriptor!.label.length).toBeGreaterThan(0)
      expect(descriptor!.parserContractVersion).toMatch(/^\d+$/)
      expect(descriptor!.sourceRootLabel).not.toMatch(/^\/Users\//)
      expect(descriptor!.sourceRootLabel).not.toMatch(/^\/home\//)
      expect(descriptor!.sourceRootLabel.includes(process.env.HOME ?? '\u0000')).toBe(false)
    }

    expect(descriptorFor('antigravity')?.label).toBe('Antigravity')
    expect(descriptorFor('antigravity-cli')?.label).toBe('Antigravity CLI')
    expect(descriptorFor('antigravity-ide')?.label).toBe('Antigravity IDE')
    expect(descriptorFor('kilo-shared-runtime')?.label).toBe('Kilo shared runtime')
    expect(descriptorFor('kilo-vscode-legacy')?.label).toBe('Kilo for VS Code (legacy)')
    expect(descriptorFor('kimi-code')?.providerName).toBe('kimicode')
    expect(PROVIDER_DISPOSITIONS.kimicode).toEqual({ kind: 'alias-of', harnessId: 'kimi-code' })
  })

  it('classifies Antigravity roots into three jobs and never Gemini', () => {
    expect(classifySessionSource({
      source: source({
        path: '/tmp/.gemini/antigravity/conversations/a.pb',
        provider: 'antigravity',
        project: 'antigravity',
      }),
    })).toEqual({ outcome: 'harness', harnessId: 'antigravity' })
    expect(classifySessionSource({
      source: source({
        path: 'C:\\Users\\dev\\.gemini\\antigravity-cli\\conversations\\b.db',
        provider: 'antigravity',
        project: 'antigravity-cli',
      }),
    })).toEqual({ outcome: 'harness', harnessId: 'antigravity-cli' })
    expect(classifySessionSource({
      source: source({
        path: '/tmp/.gemini/antigravity-ide/implicit/c.pb',
        provider: 'antigravity',
        project: 'antigravity-ide',
      }),
    })).toEqual({ outcome: 'harness', harnessId: 'antigravity-ide' })
  })

  it('classifies Copilot by sourceType without merging surfaces', () => {
    expect(classifySessionSource({
      source: source({ path: '/tmp/.copilot/session.jsonl', provider: 'copilot', sourceType: 'jsonl' }),
    })).toEqual({ outcome: 'harness', harnessId: 'copilot-cli' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/session-store.db', provider: 'copilot', sourceType: 'session-store' }),
    })).toEqual({ outcome: 'harness', harnessId: 'copilot-cli' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/chat.chatSession', provider: 'copilot', sourceType: 'chatsession' }),
    })).toEqual({ outcome: 'harness', harnessId: 'copilot-vscode' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/transcript.json', provider: 'copilot', sourceType: 'transcript' }),
    })).toEqual({ outcome: 'harness', harnessId: 'copilot-vscode' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/.config/github-copilot/x', provider: 'copilot', sourceType: 'jetbrains' }),
    })).toEqual({ outcome: 'harness', harnessId: 'copilot-jetbrains' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/agent-traces.db', provider: 'copilot', sourceType: 'otel' }),
    })).toEqual({ outcome: 'harness', harnessId: 'copilot-agent' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/copilot-unknown', provider: 'copilot' }),
    }).outcome).toBe('indeterminate')
  })

  it('classifies Codex from originator and does not guess unknown clients', () => {
    const path = '/tmp/.codex/sessions/rollout.jsonl'
    expect(classifySessionSource({
      source: source({ path, provider: 'codex' }),
      originator: 'codex-cli',
    })).toEqual({ outcome: 'harness', harnessId: 'codex-cli' })
    expect(classifySessionSource({
      source: source({ path, provider: 'codex' }),
      originator: 'codex_cli_rs',
    })).toEqual({ outcome: 'harness', harnessId: 'codex-cli' })
    expect(classifySessionSource({
      source: source({ path, provider: 'codex' }),
      originator: 'Codex Desktop',
    })).toEqual({ outcome: 'harness', harnessId: 'codex-desktop' })
    expect(classifySessionSource({
      source: source({ path, provider: 'codex' }),
      originator: 't3code_desktop',
    })).toEqual({ outcome: 'harness', harnessId: 'codex-unclassified' })
    expect(classifySessionSource({
      source: source({ path, provider: 'codex' }),
    })).toEqual({ outcome: 'harness', harnessId: 'codex-unclassified' })
  })

  it('classifies Claude from entrypoint/sourceKind and does not silently choose CLI', () => {
    const projects = '/tmp/.claude/projects/my-app'
    expect(classifySessionSource({
      source: source({ path: projects, provider: 'claude', sourceKind: 'claude-config' }),
      entrypoint: 'cli',
    })).toEqual({ outcome: 'harness', harnessId: 'claude-cli' })
    expect(classifySessionSource({
      source: source({ path: projects, provider: 'claude', sourceKind: 'claude-config' }),
      entrypoint: 'claude-desktop',
    })).toEqual({ outcome: 'harness', harnessId: 'claude-desktop' })
    expect(classifySessionSource({
      source: source({
        path: '/tmp/Claude/local-agent-mode-sessions/x',
        provider: 'claude',
        sourceKind: 'claude-desktop',
      }),
    })).toEqual({ outcome: 'harness', harnessId: 'claude-desktop' })
    expect(classifySessionSource({
      source: source({ path: projects, provider: 'claude', sourceKind: 'claude-config' }),
    })).toEqual({ outcome: 'harness', harnessId: 'claude-unclassified' })
  })

  it('splits Cursor from Cursor Agent and Kiro CLI from Kiro IDE', () => {
    expect(classifySessionSource({
      source: source({ path: '/tmp/state.vscdb', provider: 'cursor' }),
    })).toEqual({ outcome: 'harness', harnessId: 'cursor' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/.cursor/projects/a/agent.jsonl', provider: 'cursor-agent' }),
    })).toEqual({ outcome: 'harness', harnessId: 'cursor-agent' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/.kiro/sessions/cli/sess.jsonl', provider: 'kiro' }),
    })).toEqual({ outcome: 'harness', harnessId: 'kiro-cli' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/.kiro/sessions/abc123/sess_1/messages.jsonl', provider: 'kiro' }),
    })).toEqual({ outcome: 'harness', harnessId: 'kiro-ide' })
  })

  it('keeps Kilo shared runtime honest and attributes only the legacy VS Code root', () => {
    expect(classifySessionSource({
      source: source({ path: '/tmp/.local/share/kilo/kilo.db:session-1', provider: 'kilo-code' }),
    })).toEqual({ outcome: 'harness', harnessId: 'kilo-shared-runtime' })
    expect(classifySessionSource({
      source: source({
        path: '/tmp/globalStorage/kilocode.kilo-code/tasks/1',
        provider: 'kilo-code',
      }),
    })).toEqual({ outcome: 'harness', harnessId: 'kilo-vscode-legacy' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/kilo-unknown-root', provider: 'kilo-code' }),
    }).outcome).toBe('indeterminate')
  })

  it('maps remaining one-to-one providers including Pi and OpenCode', () => {
    expect(classifySessionSource({
      source: source({ path: '/tmp/.pi/agent/sessions/a.jsonl', provider: 'pi' }),
    })).toEqual({ outcome: 'harness', harnessId: 'pi' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/.local/share/opencode/opencode.db', provider: 'opencode' }),
    })).toEqual({ outcome: 'harness', harnessId: 'opencode' })
    expect(classifySessionSource({
      source: source({ path: '/tmp/.kimi-code/x', provider: 'kimicode' }),
    })).toEqual({ outcome: 'harness', harnessId: 'kimi-code' })
  })

  it('builds privacy-safe source keys from sourceId or a path digest', () => {
    const native = source({
      path: '/Users/dave/.codex/sessions/secret.jsonl',
      provider: 'codex',
      sourceId: 'codex-config:abc',
    })
    expect(sourceKeyFor('codex-cli', native)).toBe('codex-cli:codex-config:abc')

    const digested = source({ path: '/Users/dave/.pi/agent/sessions/a.jsonl', provider: 'pi' })
    const digest = createHash('sha256').update(digested.path).digest('hex').slice(0, 16)
    expect(sourceKeyFor('pi', digested)).toBe(`pi:${digest}`)
    expect(sourceKeyFor('pi', digested)).not.toContain('/Users/dave')
  })
})
