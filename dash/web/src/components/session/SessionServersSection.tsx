import { cn, fmtTokens } from '../../lib/utils.js'
import { Pager } from '../ui/Pager.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionServers } from '../../lib/kyberApi.js'

/**
 * The per-server schema and invocation rollup, one page at a time, as the
 * engine computed it. The view lays the served rows out; it never totals a
 * rollup itself (display layer, ADR 0033). A server that was never invoked is
 * a measured zero and renders as one.
 */
export interface SessionServersSectionProps {
  sessionId: string
  className?: string
}

export function SessionServersSection({ sessionId, className }: SessionServersSectionProps) {
  const servers = useSectionPage(sessionId, 'servers', fetchKyberSessionServers)
  const data = servers.data

  if (servers.failure) {
    return (
      <SectionError
        testId="section-error-servers"
        label="Server rollup"
        message={servers.failure}
        onRetry={servers.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-servers-loading" label="server rollup" />
  }

  const rows = data.entries

  return (
    <div className={cn('space-y-2', className)} data-testid="servers-section">
      <div className="flex flex-wrap items-baseline gap-2">
        <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">Servers</h3>
        <span className="text-[11px] text-tertiary-foreground">
          {data.total === 0 ? 'no servers recorded' : `${data.total} server${data.total === 1 ? '' : 's'} in this session`}
        </span>
      </div>

      {rows.length === 0 ? (
        <p data-testid="servers-empty" className="text-xs text-tertiary-foreground">
          No servers recorded for this session. Tools with no server are the harness's built-ins.
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-border">
          <table className="w-full text-left text-xs">
            <thead>
              <tr className="border-b border-border bg-interactive-secondary/40 text-[10.5px] font-semibold uppercase tracking-wider text-tertiary-foreground">
                <th className="px-2.5 py-1.5 font-mono normal-case tracking-normal">server</th>
                <th className="px-2.5 py-1.5 text-right">tools</th>
                <th className="px-2.5 py-1.5 text-right">schema tokens</th>
                <th className="px-2.5 py-1.5 text-right">invocations</th>
                <th className="px-2.5 py-1.5 text-right">unused tools</th>
                <th className="px-2.5 py-1.5 text-right">unused resident cost</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/40">
              {rows.map((row) => (
                <tr key={row.server} data-testid={`server-row-${row.server}`}>
                  <td className="px-2.5 py-1.5 font-mono text-foreground">
                    {row.server}
                    {row.is_mcp && (
                      <span className="ml-1.5 rounded bg-violet-500/15 px-1.5 py-0.5 text-[9px] font-semibold text-violet-600 dark:text-violet-400">
                        MCP
                      </span>
                    )}
                  </td>
                  <td className="px-2.5 py-1.5 text-right tabular-nums">{row.tools}</td>
                  <td className="px-2.5 py-1.5 text-right tabular-nums">
                    {row.schema_tokens != null ? fmtTokens(row.schema_tokens) : '—'}
                  </td>
                  <td className="px-2.5 py-1.5 text-right tabular-nums">{row.invocations}</td>
                  <td className="px-2.5 py-1.5 text-right tabular-nums">
                    {row.unused_tools > 0 ? `${row.unused_tools} unused` : String(row.unused_tools)}
                  </td>
                  <td className="px-2.5 py-1.5 text-right tabular-nums">
                    {row.unused_cost != null ? fmtTokens(row.unused_cost) : '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <Pager
            {...servers.pagerProps}
            ariaLabel="Server pages"
          />
        </div>
      )}
    </div>
  )
}
