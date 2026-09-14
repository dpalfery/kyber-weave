import { createHash } from 'node:crypto'

import { getAllProviders } from '../../src/providers/index.js'
import type { SessionSource } from '../../src/providers/types.js'

import type {
  ClassificationInput,
  HarnessId,
  HarnessSourceDescriptor,
  ProviderDisposition,
  SessionClassification,
} from './types.js'

export { getAllProviders }
export type { SessionSource }

const PARSER_CONTRACT_VERSION = '1'

const GEMINI_EXCLUSION_REASON =
  'Gemini represents chat history and model usage, not a coding harness. It must not be a harness id or rollup filter.'

const VERCEL_EXCLUSION_REASON =
  'Vercel Gateway is a network provider source, not a local coding-harness chat-history store.'

function job(harnessIds: readonly HarnessId[]): ProviderDisposition {
  return { kind: 'jobs', harnessIds }
}

function excluded(reason: string): ProviderDisposition {
  return { kind: 'excluded', reason }
}

function descriptor(partial: Omit<HarnessSourceDescriptor, 'parserContractVersion'>): HarnessSourceDescriptor {
  return { ...partial, parserContractVersion: PARSER_CONTRACT_VERSION }
}

export const PROVIDER_DISPOSITIONS: Record<string, ProviderDisposition> = {
  antigravity: job(['antigravity', 'antigravity-cli', 'antigravity-ide']),
  claude: job(['claude-cli', 'claude-desktop', 'claude-unclassified']),
  cline: job(['cline']),
  'cline-cli': job(['cline-cli']),
  codewhale: job(['codewhale']),
  codebuff: job(['codebuff']),
  codex: job(['codex-cli', 'codex-desktop', 'codex-unclassified']),
  copilot: job(['copilot-cli', 'copilot-vscode', 'copilot-jetbrains', 'copilot-agent']),
  devin: job(['devin']),
  droid: job(['droid']),
  dsh: job(['dsh']),
  gemini: excluded(GEMINI_EXCLUSION_REASON),
  hermes: job(['hermes']),
  'ibm-bob': job(['ibm-bob']),
  'kilo-code': job(['kilo-shared-runtime', 'kilo-vscode-legacy']),
  kiro: job(['kiro-cli', 'kiro-ide']),
  kimi: job(['kimi']),
  kimicode: { kind: 'alias-of', harnessId: 'kimi-code' },
  'lingtai-tui': job(['lingtai-tui']),
  'mistral-vibe': job(['mistral-vibe']),
  mux: job(['mux']),
  openclaw: job(['openclaw']),
  openclaude: job(['openclaude']),
  'open-design': job(['open-design']),
  pi: job(['pi']),
  omp: job(['omp']),
  qwen: job(['qwen']),
  quickdesk: job(['quickdesk']),
  'roo-code': job(['roo-code']),
  zerostack: job(['zerostack']),
  grok: job(['grok']),
  forge: job(['forge']),
  goose: job(['goose']),
  cursor: job(['cursor']),
  opencode: job(['opencode']),
  'cursor-agent': job(['cursor-agent']),
  crush: job(['crush']),
  warp: job(['warp']),
  'vercel-gateway': excluded(VERCEL_EXCLUSION_REASON),
  zcode: job(['zcode']),
  zed: job(['zed']),
}

export const HARNESS_DESCRIPTORS: readonly HarnessSourceDescriptor[] = [
  descriptor({
    harnessId: 'antigravity',
    label: 'Antigravity',
    providerName: 'antigravity',
    classifier: 'antigravity-root',
    sourceRootLabel: '~/.gemini/antigravity',
    nativeFormat: 'protobuf-sqlite',
  }),
  descriptor({
    harnessId: 'antigravity-cli',
    label: 'Antigravity CLI',
    providerName: 'antigravity',
    classifier: 'antigravity-root',
    sourceRootLabel: '~/.gemini/antigravity-cli',
    nativeFormat: 'protobuf-sqlite',
  }),
  descriptor({
    harnessId: 'antigravity-ide',
    label: 'Antigravity IDE',
    providerName: 'antigravity',
    classifier: 'antigravity-root',
    sourceRootLabel: '~/.gemini/antigravity-ide',
    nativeFormat: 'protobuf-sqlite',
  }),
  descriptor({
    harnessId: 'copilot-cli',
    label: 'Copilot CLI',
    providerName: 'copilot',
    classifier: 'copilot-source-type',
    sourceRootLabel: '~/.copilot',
    nativeFormat: 'jsonl-session-store',
  }),
  descriptor({
    harnessId: 'copilot-vscode',
    label: 'Copilot for VS Code',
    providerName: 'copilot',
    classifier: 'copilot-source-type',
    sourceRootLabel: 'vscode-family-globalStorage',
    nativeFormat: 'chatsession-transcript',
  }),
  descriptor({
    harnessId: 'copilot-jetbrains',
    label: 'Copilot for JetBrains',
    providerName: 'copilot',
    classifier: 'copilot-source-type',
    sourceRootLabel: '~/.config/github-copilot',
    nativeFormat: 'jetbrains-conversations',
  }),
  descriptor({
    harnessId: 'copilot-agent',
    label: 'Copilot Agent',
    providerName: 'copilot',
    classifier: 'copilot-source-type',
    sourceRootLabel: 'copilot-otel',
    nativeFormat: 'otlp',
  }),
  descriptor({
    harnessId: 'codex-cli',
    label: 'Codex CLI',
    providerName: 'codex',
    classifier: 'codex-originator',
    sourceRootLabel: '~/.codex/sessions',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'codex-desktop',
    label: 'Codex Desktop',
    providerName: 'codex',
    classifier: 'codex-originator',
    sourceRootLabel: '~/.codex/sessions',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'codex-unclassified',
    label: 'Codex (unclassified client)',
    providerName: 'codex',
    classifier: 'codex-originator',
    sourceRootLabel: '~/.codex/sessions',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'claude-cli',
    label: 'Claude Code CLI',
    providerName: 'claude',
    classifier: 'claude-entrypoint',
    sourceRootLabel: '~/.claude/projects',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'claude-desktop',
    label: 'Claude Code Desktop',
    providerName: 'claude',
    classifier: 'claude-entrypoint',
    sourceRootLabel: 'claude-local-agent-sessions',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'claude-unclassified',
    label: 'Claude Code (unclassified client)',
    providerName: 'claude',
    classifier: 'claude-entrypoint',
    sourceRootLabel: '~/.claude/projects',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'cursor',
    label: 'Cursor IDE',
    providerName: 'cursor',
    classifier: 'provider-identity',
    sourceRootLabel: 'cursor-state.vscdb',
    nativeFormat: 'sqlite',
  }),
  descriptor({
    harnessId: 'cursor-agent',
    label: 'Cursor Agent',
    providerName: 'cursor-agent',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.cursor/projects',
    nativeFormat: 'jsonl-sqlite',
  }),
  descriptor({
    harnessId: 'cline',
    label: 'Cline',
    providerName: 'cline',
    classifier: 'provider-identity',
    sourceRootLabel: 'vscode-family-cline-globalStorage',
    nativeFormat: 'task-history-json',
  }),
  descriptor({
    harnessId: 'cline-cli',
    label: 'Cline CLI',
    providerName: 'cline-cli',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.cline/data/sessions',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'kiro-cli',
    label: 'Kiro CLI',
    providerName: 'kiro',
    classifier: 'kiro-path',
    sourceRootLabel: '~/.kiro/sessions/cli',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'kiro-ide',
    label: 'Kiro IDE',
    providerName: 'kiro',
    classifier: 'kiro-path',
    sourceRootLabel: 'kiro-ide-storage',
    nativeFormat: 'ide-storage',
  }),
  descriptor({
    harnessId: 'kilo-shared-runtime',
    label: 'Kilo shared runtime',
    providerName: 'kilo-code',
    classifier: 'kilo-root',
    sourceRootLabel: '~/.local/share/kilo/kilo.db',
    nativeFormat: 'sqlite',
  }),
  descriptor({
    harnessId: 'kilo-vscode-legacy',
    label: 'Kilo for VS Code (legacy)',
    providerName: 'kilo-code',
    classifier: 'kilo-root',
    sourceRootLabel: 'vscode-family-globalStorage/kilocode.kilo-code',
    nativeFormat: 'legacy-task-history',
  }),
  descriptor({
    harnessId: 'pi',
    label: 'Pi',
    providerName: 'pi',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.pi/agent/sessions',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'opencode',
    label: 'OpenCode',
    providerName: 'opencode',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.local/share/opencode',
    nativeFormat: 'sqlite-legacy-files',
  }),
  descriptor({
    harnessId: 'codewhale',
    label: 'CodeWhale',
    providerName: 'codewhale',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.codewhale/sessions',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'codebuff',
    label: 'Codebuff',
    providerName: 'codebuff',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.config/manicode',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'devin',
    label: 'Devin',
    providerName: 'devin',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.local/share/devin/cli',
    nativeFormat: 'transcript-sqlite',
  }),
  descriptor({
    harnessId: 'droid',
    label: 'Droid',
    providerName: 'droid',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.factory/sessions',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'dsh',
    label: 'DSH',
    providerName: 'dsh',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.dsh/sessions',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'hermes',
    label: 'Hermes',
    providerName: 'hermes',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.hermes',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'ibm-bob',
    label: 'IBM Bob',
    providerName: 'ibm-bob',
    classifier: 'provider-identity',
    sourceRootLabel: 'ibm-bob-ide-globalStorage',
    nativeFormat: 'editor-native',
  }),
  descriptor({
    harnessId: 'kimi',
    label: 'Kimi',
    providerName: 'kimi',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.kimi/sessions',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'kimi-code',
    label: 'Kimi Code',
    providerName: 'kimicode',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.kimi-code',
    nativeFormat: 'native-records',
  }),
  descriptor({
    harnessId: 'lingtai-tui',
    label: 'Lingtai TUI',
    providerName: 'lingtai-tui',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.lingtai',
    nativeFormat: 'native-records',
  }),
  descriptor({
    harnessId: 'mistral-vibe',
    label: 'Mistral Vibe',
    providerName: 'mistral-vibe',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.vibe/logs/session',
    nativeFormat: 'session-logs',
  }),
  descriptor({
    harnessId: 'mux',
    label: 'Mux',
    providerName: 'mux',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.mux',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'openclaw',
    label: 'OpenClaw',
    providerName: 'openclaw',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.openclaw',
    nativeFormat: 'native-records',
  }),
  descriptor({
    harnessId: 'openclaude',
    label: 'OpenClaude',
    providerName: 'openclaude',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.openclaude/projects',
    nativeFormat: 'project-session',
  }),
  descriptor({
    harnessId: 'open-design',
    label: 'Open Design',
    providerName: 'open-design',
    classifier: 'provider-identity',
    sourceRootLabel: 'open-design-os-application-data',
    nativeFormat: 'native-records',
  }),
  descriptor({
    harnessId: 'omp',
    label: 'OMP',
    providerName: 'omp',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.omp/agent/sessions',
    nativeFormat: 'jsonl',
  }),
  descriptor({
    harnessId: 'qwen',
    label: 'Qwen',
    providerName: 'qwen',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.qwen/projects',
    nativeFormat: 'project-session',
  }),
  descriptor({
    harnessId: 'quickdesk',
    label: 'QuickDesk',
    providerName: 'quickdesk',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.quickwork',
    nativeFormat: 'native-records',
  }),
  descriptor({
    harnessId: 'roo-code',
    label: 'Roo Code',
    providerName: 'roo-code',
    classifier: 'provider-identity',
    sourceRootLabel: 'vscode-family-roo-code-globalStorage',
    nativeFormat: 'task-history',
  }),
  descriptor({
    harnessId: 'zerostack',
    label: 'ZeroStack',
    providerName: 'zerostack',
    classifier: 'provider-identity',
    sourceRootLabel: 'platform-zerostack-sessions',
    nativeFormat: 'native-records',
  }),
  descriptor({
    harnessId: 'grok',
    label: 'Grok',
    providerName: 'grok',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.grok/sessions',
    nativeFormat: 'native-session',
  }),
  descriptor({
    harnessId: 'forge',
    label: 'Forge',
    providerName: 'forge',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.forge/.forge.db',
    nativeFormat: 'sqlite',
  }),
  descriptor({
    harnessId: 'goose',
    label: 'Goose',
    providerName: 'goose',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.local/share/goose/sessions/sessions.db',
    nativeFormat: 'sqlite',
  }),
  descriptor({
    harnessId: 'crush',
    label: 'Crush',
    providerName: 'crush',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.local/share/crush/projects.json',
    nativeFormat: 'json',
  }),
  descriptor({
    harnessId: 'warp',
    label: 'Warp',
    providerName: 'warp',
    classifier: 'provider-identity',
    sourceRootLabel: 'warp-application-sqlite',
    nativeFormat: 'sqlite',
  }),
  descriptor({
    harnessId: 'zcode',
    label: 'ZCode',
    providerName: 'zcode',
    classifier: 'provider-identity',
    sourceRootLabel: '~/.zcode/cli/db/db.sqlite',
    nativeFormat: 'sqlite',
  }),
  descriptor({
    harnessId: 'zed',
    label: 'Zed',
    providerName: 'zed',
    classifier: 'provider-identity',
    sourceRootLabel: 'zed-threads-sqlite',
    nativeFormat: 'sqlite',
  }),
]

const DESCRIPTORS_BY_ID = new Map(HARNESS_DESCRIPTORS.map(entry => [entry.harnessId, entry]))

const PROVIDER_IDENTITY_HARNESS = new Map<string, HarnessId>(
  HARNESS_DESCRIPTORS
    .filter(entry => entry.classifier === 'provider-identity')
    .map(entry => [entry.providerName, entry.harnessId]),
)

export function descriptorFor(harnessId: string): HarnessSourceDescriptor | undefined {
  return DESCRIPTORS_BY_ID.get(harnessId)
}

export function auditProviderRegistry(providers: readonly { name: string }[]): void {
  const unaccounted = providers
    .map(provider => provider.name)
    .filter(name => PROVIDER_DISPOSITIONS[name] === undefined)
  if (unaccounted.length > 0) {
    throw new Error(
      `Harness-source registry has no job, excluded-with-reason, or alias-of disposition for: ${unaccounted.join(', ')}`,
    )
  }
}

export function sourceKeyFor(harnessId: string, source: SessionSource): string {
  const nativeId = source.sourceId?.trim()
  if (nativeId) return `${harnessId}:${nativeId}`
  const digest = createHash('sha256').update(source.path).digest('hex').slice(0, 16)
  return `${harnessId}:${digest}`
}

export function classifySessionSource(input: ClassificationInput): SessionClassification {
  const provider = input.source.provider
  const disposition = PROVIDER_DISPOSITIONS[provider]
  if (disposition === undefined) {
    return {
      outcome: 'indeterminate',
      provider,
      reason: `unregistered provider '${provider}'`,
    }
  }
  if (disposition.kind === 'excluded') {
    return { outcome: 'excluded', provider, reason: disposition.reason }
  }

  switch (provider) {
    case 'antigravity':
      return classifyAntigravity(input.source)
    case 'copilot':
      return classifyCopilot(input.source)
    case 'codex':
      return classifyCodex(input.originator)
    case 'claude':
      return classifyClaude(input)
    case 'kiro':
      return classifyKiro(input.source)
    case 'kilo-code':
      return classifyKilo(input.source)
    default:
      return classifyProviderIdentity(provider, disposition)
  }
}

function classifyProviderIdentity(provider: string, disposition: ProviderDisposition): SessionClassification {
  if (disposition.kind === 'alias-of') {
    return harness(disposition.harnessId)
  }
  const mapped = PROVIDER_IDENTITY_HARNESS.get(provider)
  if (mapped) return harness(mapped)
  return {
    outcome: 'indeterminate',
    provider,
    reason: `provider '${provider}' has no single-identity mapping`,
  }
}

function classifyAntigravity(session: SessionSource): SessionClassification {
  const path = posixPath(session.path)
  if (session.project === 'antigravity-cli' || path.includes('/.gemini/antigravity-cli/')) {
    return harness('antigravity-cli')
  }
  if (session.project === 'antigravity-ide' || path.includes('/.gemini/antigravity-ide/')) {
    return harness('antigravity-ide')
  }
  if (session.project === 'antigravity' || path.includes('/.gemini/antigravity/')) {
    return harness('antigravity')
  }
  return {
    outcome: 'indeterminate',
    provider: 'antigravity',
    reason: 'Antigravity source has no root or project discriminator',
  }
}

function classifyCopilot(session: SessionSource): SessionClassification {
  switch (session.sourceType) {
    case 'jsonl':
    case 'session-store':
      return harness('copilot-cli')
    case 'chatsession':
    case 'transcript':
      return harness('copilot-vscode')
    case 'jetbrains':
      return harness('copilot-jetbrains')
    case 'otel':
      return harness('copilot-agent')
    default:
      return {
        outcome: 'indeterminate',
        provider: 'copilot',
        reason: 'Copilot sourceType is missing; refusing to guess a client surface',
      }
  }
}

function classifyCodex(originator: string | null | undefined): SessionClassification {
  const normalized = (originator ?? '').trim().toLowerCase()
  if (normalized === 'codex-cli' || normalized === 'codex_cli_rs') {
    return harness('codex-cli')
  }
  if (normalized === 'codex desktop') {
    return harness('codex-desktop')
  }
  return harness('codex-unclassified')
}

function classifyClaude(input: ClassificationInput): SessionClassification {
  const entrypoint = (input.entrypoint ?? '').trim().toLowerCase()
  if (entrypoint === 'cli') return harness('claude-cli')
  if (entrypoint === 'claude-desktop') return harness('claude-desktop')
  if (entrypoint) {
    return harness('claude-unclassified')
  }
  if (input.source.sourceKind === 'claude-desktop') {
    return harness('claude-desktop')
  }
  return harness('claude-unclassified')
}

function classifyKiro(session: SessionSource): SessionClassification {
  const path = posixPath(session.path)
  if (path.includes('/sessions/cli/') || path.endsWith('/sessions/cli')) {
    return harness('kiro-cli')
  }
  if (path.includes('/.kiro/sessions/') || path.includes('kiro.kiroagent')) {
    return harness('kiro-ide')
  }
  return {
    outcome: 'indeterminate',
    provider: 'kiro',
    reason: 'Kiro path does not identify CLI vs IDE',
  }
}

function classifyKilo(session: SessionSource): SessionClassification {
  const path = posixPath(session.path)
  if (path.includes('kilocode.kilo-code')) {
    return harness('kilo-vscode-legacy')
  }
  if (path.includes('.db:') || /(?:^|\/)kilo\.db(?:$|:)/.test(path) || path.includes('/kilo/kilo.db')) {
    return harness('kilo-shared-runtime')
  }
  return {
    outcome: 'indeterminate',
    provider: 'kilo-code',
    reason: 'Kilo source is not the shared runtime DB or the legacy VS Code store',
  }
}

function harness(harnessId: HarnessId): SessionClassification {
  return { outcome: 'harness', harnessId }
}

function posixPath(path: string): string {
  return path.replace(/\\/g, '/')
}
