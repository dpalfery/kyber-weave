import { useRef, useState, type WheelEvent } from 'react'
import type { MenubarPayload } from '../lib/payload'
import type { CurrencyState } from '../lib/currency'
import { formatCompactCurrency, formatCurrency, plural } from '../lib/currency'
import { homePath } from '../lib/platform'

export type Provider = 'all' | 'claude' | 'codex' | 'cursor' | 'copilot' | 'opencode' | 'pi'

/// Same order as the macOS ProviderFilter.allCases.
export const ALL_PROVIDERS: Array<{ id: Provider; label: string; source: string }> = [
  { id: 'all',      label: 'All',      source: 'every detected tool' },
  { id: 'claude',   label: 'Claude',   source: `Claude Code sessions in ${homePath('.claude', 'projects')}` },
  { id: 'codex',    label: 'Codex',    source: `Codex CLI sessions in ${homePath('.codex', 'sessions')}` },
  { id: 'cursor',   label: 'Cursor',   source: 'the Cursor IDE local database' },
  { id: 'copilot',  label: 'Copilot',  source: 'GitHub Copilot session events' },
  { id: 'opencode', label: 'OpenCode', source: 'OpenCode session storage' },
  { id: 'pi',       label: 'Pi',       source: 'Pi session logs' },
]

export const PROVIDER_LABELS: Record<Provider, string> = Object.fromEntries(
  ALL_PROVIDERS.map(p => [p.id, p.label]),
) as Record<Provider, string>

/// Providers the CLI detected on this machine (installed, even with zero spend today).
export function detectedProviders(payload: MenubarPayload | null): Provider[] {
  if (!payload) return []
  const detected = payload.current.providers
  return ALL_PROVIDERS.map(p => p.id).filter(id => id !== 'all' && id in detected)
}

type Props = {
  selected: Provider
  onSelect: (p: Provider) => void
  payload: MenubarPayload | null
  currency: CurrencyState
}

/// Every supported tool is listed so the reader can see at a glance which ones CodeBurn
/// is watching. Tools that are not installed are dimmed and explain themselves on hover;
/// detected tools show today's spend and a hover preview with their share.
export function AgentTabStrip({ selected, onSelect, payload, currency }: Props) {
  const [hovered, setHovered] = useState<Provider | null>(null)
  const scroller = useRef<HTMLDivElement>(null)
  const providers = detectedProviders(payload)
  const costs = payload?.current.providers ?? {}
  const total = providers.reduce((s, id) => s + (costs[id] ?? 0), 0)

  const onWheel = (e: WheelEvent<HTMLDivElement>) => {
    if (scroller.current && e.deltaY !== 0 && e.deltaX === 0) {
      scroller.current.scrollLeft += e.deltaY
    }
  }

  const preview = hovered ? previewFor(hovered, providers, costs, total, currency) : null

  return (
    <div className="agent-tabs-wrap" onMouseLeave={() => setHovered(null)}>
      <nav className="agent-tabs" aria-label="Provider" ref={scroller} onWheel={onWheel}>
        {ALL_PROVIDERS.map(p => {
          const detected = p.id === 'all' ? providers.length > 0 : providers.includes(p.id)
          const cost = p.id === 'all' ? total : (costs[p.id] ?? 0)
          const active = selected === p.id
          return (
            <button
              key={p.id}
              type="button"
              className={`tab ${active ? 'tab-active' : ''} ${detected ? '' : 'tab-muted'}`}
              aria-pressed={active}
              aria-disabled={!detected}
              onMouseEnter={() => setHovered(p.id)}
              onFocus={() => setHovered(p.id)}
              onClick={() => { if (detected) onSelect(p.id) }}
            >
              <span className="tab-label">{p.label}</span>
              {detected && cost > 0 && (
                <span className="tab-cost">{formatCompactCurrency(cost, currency)}</span>
              )}
            </button>
          )
        })}
      </nav>
      {preview && (
        <div className="tab-preview" role="tooltip">
          <div className="tab-preview-title">{preview.title}</div>
          <div className="tab-preview-body">{preview.body}</div>
        </div>
      )}
    </div>
  )
}

function previewFor(
  id: Provider,
  providers: Provider[],
  costs: Record<string, number>,
  total: number,
  currency: CurrencyState,
): { title: string; body: string } {
  const meta = ALL_PROVIDERS.find(p => p.id === id)!
  if (id === 'all') {
    if (providers.length === 0) return { title: 'No tools detected yet', body: 'Run one of the supported tools once, then refresh.' }
    return {
      title: `${formatCurrency(total, currency)} today across ${plural(providers.length, 'tool')}`,
      body: providers.map(p => `${PROVIDER_LABELS[p]} ${formatCompactCurrency(costs[p] ?? 0, currency)}`).join(' · '),
    }
  }
  if (!providers.includes(id)) {
    return { title: `${meta.label} not detected on this machine`, body: `CodeBurn watches ${meta.source}.` }
  }
  const cost = costs[id] ?? 0
  const share = total > 0 ? Math.round((cost / total) * 100) : 0
  return {
    title: `${meta.label} · ${formatCurrency(cost, currency)} today`,
    body: cost > 0 ? `${share}% of today's spend · click to filter every view` : 'No spend yet today · click to filter every view',
  }
}
