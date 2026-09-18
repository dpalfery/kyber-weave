import { Command } from 'commander'
import { findUnpricedModels, loadPricing, setModelAliases, setPriceOverrides, setLocalModelSavings, setFlatRateModels, setFlatRateRemoved, setProxyPaths } from './models.js'
import { allProviderNames } from './providers/index.js'
import { dateKey } from './day-aggregator.js'
import { sessionModelBillableOutputTokens } from './session-output.js'
import { CATEGORY_LABELS, type DateRange, type ProjectSummary, type TaskCategory } from './types.js'
import { aggregateModelEfficiency } from './model-efficiency.js'
import { buildDurablePeriod, type DurablePeriod } from './usage-aggregator.js'
import { renderDashboard } from './dashboard.js'
import { runWebDashboard } from './web-dashboard.js'
import { resolveCliName } from './brand-overlay.js'
import { formatDateRangeLabel, parseDateRangeFlags, parseDayFlag, getDateRange, toPeriod, type Period } from './cli-date.js'
import { registerKyberCommands } from '../kyber/cli/register.js'
import { readConfig } from './config.js'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const { version } = require('../package.json')

function collect(val: string, acc: string[]): string[] {
  acc.push(val)
  return acc
}

function parseInteger(value: string): number {
  return parseInt(value, 10)
}

function assertFormat(value: string, allowed: readonly string[], command: string): void {
  if (!allowed.includes(value)) {
    process.stderr.write(
      `${resolveCliName()} ${command}: unknown format "${value}". Valid values: ${allowed.join(', ')}.\n`
    )
    process.exit(1)
  }
}

function assertProvider(value: string, command: string): void {
  const names = allProviderNames()
  if (value === 'all' || names.includes(value)) return
  process.stderr.write(
    `${resolveCliName()} ${command}: unknown provider "${value}". Valid values: all, ${names.join(', ')}.\n`
  )
  process.exit(1)
}

// Wrapped in a factory because commander option state is sticky across
// parses: `codeburn serve` executes many requests in one process and must
// build a FRESH program per request or one request's --period would leak
// into the next one's defaults. The normal CLI path builds it exactly once.
export function buildProgram(): Command {

async function runJsonReport(period: Period, provider: string, project: string[], exclude: string[]): Promise<void> {
  await loadPricing()
  const { range, label } = getDateRange(period)
  const durable = await buildDurablePeriod({ range, label }, { provider, project, exclude })
  const report = buildJsonReport(durable.liveProjects, label, period, durable)
  console.log(JSON.stringify(report, null, 2))
}

const program = new Command()
  // Display name follows the installed binary (kyberdash). Keep package.json
  // `"bin": { "codeburn": ... }` as the upstream identity for subtree merges.
  .name(resolveCliName())
  .description('See where your AI coding tokens go - by task, tool, model, and project')
  .version(version)
  .option('--verbose', 'print warnings to stderr on read failures and skipped files')
  .option('--timezone <zone>', 'IANA timezone for date grouping (e.g. Asia/Tokyo, America/New_York)')

program.hook('preAction', async (thisCommand) => {
  const tz = thisCommand.opts<{ timezone?: string }>().timezone ?? process.env['CODEBURN_TZ']
  if (tz) {
    try {
      Intl.DateTimeFormat(undefined, { timeZone: tz })
    } catch {
      console.error(`\n  Invalid timezone: "${tz}". Use an IANA timezone like "America/New_York" or "Asia/Tokyo".\n`)
      process.exit(1)
    }
    process.env.TZ = tz
  }
  const config = await readConfig()
  setModelAliases(config.modelAliases ?? {})
  setPriceOverrides(config.priceOverrides ?? {})
  setLocalModelSavings(config.localModelSavings ?? {})
  setFlatRateModels(config.flatRateModels ?? [])
  setFlatRateRemoved(config.flatRateModelsRemoved ?? [])
  setProxyPaths(config.proxyPaths ?? [])
  if (thisCommand.opts<{ verbose?: boolean }>().verbose) {
    process.env['CODEBURN_VERBOSE'] = '1'
  }
})

function buildJsonReport(projects: ProjectSummary[], period: string, periodKey: string, durable: DurablePeriod) {
  const sessions = projects.flatMap(p => p.sessions)

  // Headline totals come from the durable daily cache (carry-forward days whose
  // session files have expired still count), matching the menubar exactly. The
  // proxied/net split is a surviving-session concept (subscription attribution
  // isn't stored per day), so it stays live; net is taken off the durable total.
  const totalCostUSD = durable.data.cost
  const totalSavingsUSD = durable.data.savingsUSD
  const totalEstimatedUSD = durable.data.estimatedCostUSD ?? 0
  // Subscription-covered (proxied) portion of totalCostUSD, and the resulting
  // out-of-pocket figure. `cost` stays the full billable/would-be amount.
  const totalProxiedUSD = projects.reduce((s, p) => s + p.totalProxiedCostUSD, 0)
  const netCostUSD = totalCostUSD - totalProxiedUSD
  const totalCalls = durable.data.calls
  const totalSessions = durable.data.sessions
  const totalInput = durable.data.inputTokens
  const totalOutput = durable.data.outputTokens
  const totalCacheRead = durable.data.cacheReadTokens
  const totalCacheWrite = durable.data.cacheWriteTokens
  // Reads over reads + fresh input. cache_write counts tokens being stored, not
  // served, so it doesn't belong in the denominator. compare-stats.ts computes the
  // same ratio the same way; they have to stay in step.
  const cacheHitDenom = totalInput + totalCacheRead
  const cacheHitPercent = cacheHitDenom > 0 ? Math.round((totalCacheRead / cacheHitDenom) * 1000) / 10 : 0

  // Daily rows come from the same durable day set as the headline so they sum
  // to it, carried days included. Both JSON call sites always pass durable
  // (#1067); the live dailyMap fallback was unreachable and is gone.
  const daily = durable.days.map(d => {
        const turns = Object.values(d.categories).reduce((s, c) => s + c.turns, 0)
        return {
          date: d.date,
          cost: (d.cost),
          savings: (d.savingsUSD),
          calls: d.calls,
          turns,
          editTurns: d.editTurns,
          oneShotTurns: d.oneShotTurns,
          oneShotRate: d.editTurns > 0
            ? Math.round((d.oneShotTurns / d.editTurns) * 1000) / 10
            : null,
        }
      })

  const projectList = projects.map(p => ({
    name: p.project,
    path: p.projectPath,
    cost: (p.totalCostUSD),
    savings: (p.totalSavingsUSD),
    avgCostPerSession: p.sessions.length > 0
      ? (p.totalCostUSD / p.sessions.length)
      : null,
    calls: p.totalApiCalls,
    sessions: p.sessions.length,
  }))

  const modelMap: Record<string, { calls: number; cost: number; savings: number; estimatedCost: number; inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number; baselineModel: string }> = {}
  const modelEfficiency = aggregateModelEfficiency(projects)
  for (const sess of sessions) {
    for (const [model, d] of Object.entries(sess.modelBreakdown)) {
      if (!modelMap[model]) { modelMap[model] = { calls: 0, cost: 0, savings: 0, estimatedCost: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, baselineModel: '' } }
      modelMap[model].calls += d.calls
      modelMap[model].cost += d.costUSD
      modelMap[model].savings += d.savingsUSD
      modelMap[model].estimatedCost += d.estimatedCostUSD ?? 0
      modelMap[model].inputTokens += d.tokens.inputTokens
      modelMap[model].cacheReadTokens += d.tokens.cacheReadInputTokens
      modelMap[model].cacheWriteTokens += d.tokens.cacheCreationInputTokens
    }
    // Output must be billed per call while provider identity is still known.
    // Join on the same key as parser modelBreakdown (getShortModelName), not raw call.model.
    for (const [model, output] of Object.entries(sessionModelBillableOutputTokens(sess))) {
      if (!modelMap[model]) {
        modelMap[model] = { calls: 0, cost: 0, savings: 0, estimatedCost: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, baselineModel: '' }
      }
      modelMap[model].outputTokens += output
    }
  }
  // Pull the active baseline model name out of the savings config so the
  // report can show what the local calls were mapped against without
  // forcing the consumer to cross-reference a separate file. Empty when
  // no savings are configured for this period.
  for (const [model, acc] of Object.entries(modelMap)) {
    if (acc.savings <= 0) continue
    for (const sess of sessions) {
      const bucket = sess.modelBreakdown[model]
      if (!bucket || bucket.savingsUSD <= 0) continue
      for (const turn of sess.turns) {
        for (const call of turn.assistantCalls) {
          if (call.model === model && call.savingsBaselineModel) {
            acc.baselineModel = call.savingsBaselineModel
            break
          }
        }
        if (acc.baselineModel) break
      }
      if (acc.baselineModel) break
    }
  }
  const models = Object.entries(modelMap)
    .sort(([, a], [, b]) => (b.cost + b.savings) - (a.cost + a.savings))
    .map(([name, { cost, savings, estimatedCost, baselineModel, ...rest }]) => {
      const efficiency = modelEfficiency.get(name)
      return {
        name,
        ...rest,
        cost: (cost),
        savings: (savings),
        estimatedCost: (estimatedCost),
        savingsBaselineModel: baselineModel,
        editTurns: efficiency?.editTurns ?? 0,
        oneShotTurns: efficiency?.oneShotTurns ?? 0,
        oneShotRate: efficiency?.oneShotRate ?? null,
        retriesPerEdit: efficiency?.retriesPerEdit ?? null,
        costPerEdit: efficiency?.costPerEditUSD !== null && efficiency?.costPerEditUSD !== undefined
          ? (efficiency.costPerEditUSD)
          : null,
      }
    })

  const catMap: Record<string, { turns: number; cost: number; savings: number; editTurns: number; oneShotTurns: number }> = {}
  for (const sess of sessions) {
    for (const [cat, d] of Object.entries(sess.categoryBreakdown)) {
      if (!catMap[cat]) { catMap[cat] = { turns: 0, cost: 0, savings: 0, editTurns: 0, oneShotTurns: 0 } }
      catMap[cat].turns += d.turns
      catMap[cat].cost += d.costUSD
      catMap[cat].savings += d.savingsUSD
      catMap[cat].editTurns += d.editTurns
      catMap[cat].oneShotTurns += d.oneShotTurns
    }
  }
  const activities = Object.entries(catMap)
    .sort(([, a], [, b]) => (b.cost + b.savings) - (a.cost + a.savings))
    .map(([cat, d]) => ({
      category: CATEGORY_LABELS[cat as TaskCategory] ?? cat,
      cost: (d.cost),
      savings: (d.savings),
      turns: d.turns,
      editTurns: d.editTurns,
      oneShotTurns: d.oneShotTurns,
      oneShotRate: d.editTurns > 0 ? Math.round((d.oneShotTurns / d.editTurns) * 1000) / 10 : null,
    }))

  const toolMap: Record<string, number> = {}
  const mcpMap: Record<string, number> = {}
  const bashMap: Record<string, number> = {}
  const skillMap: Record<string, { turns: number; cost: number; savings: number }> = {}
  const subagentMap: Record<string, { calls: number; cost: number; savings: number }> = {}
  // Claude Code only: real subagent-transcript spend grouped by agentType
  // (workflow-subagent / Explore / general-purpose / …). Distinct from
  // subagentMap, which is Task-tool-input based and never sees workflow agents.
  const agentTypeMap: Record<string, { calls: number; cost: number; savings: number }> = {}
  for (const sess of sessions) {
    for (const [tool, d] of Object.entries(sess.toolBreakdown)) {
      toolMap[tool] = (toolMap[tool] ?? 0) + d.calls
    }
    for (const [server, d] of Object.entries(sess.mcpBreakdown)) {
      mcpMap[server] = (mcpMap[server] ?? 0) + d.calls
    }
    for (const [cmd, d] of Object.entries(sess.bashBreakdown)) {
      bashMap[cmd] = (bashMap[cmd] ?? 0) + d.calls
    }
    for (const [skill, d] of Object.entries(sess.skillBreakdown)) {
      if (!skillMap[skill]) skillMap[skill] = { turns: 0, cost: 0, savings: 0 }
      skillMap[skill].turns += d.turns
      skillMap[skill].cost += d.costUSD
      skillMap[skill].savings += d.savingsUSD
    }
    for (const [sat, d] of Object.entries(sess.subagentBreakdown)) {
      if (!subagentMap[sat]) subagentMap[sat] = { calls: 0, cost: 0, savings: 0 }
      subagentMap[sat].calls += d.calls
      subagentMap[sat].cost += d.costUSD
      subagentMap[sat].savings += d.savingsUSD
    }
    if (sess.agentType) {
      if (!agentTypeMap[sess.agentType]) agentTypeMap[sess.agentType] = { calls: 0, cost: 0, savings: 0 }
      agentTypeMap[sess.agentType].calls += sess.apiCalls
      agentTypeMap[sess.agentType].cost += sess.totalCostUSD
      agentTypeMap[sess.agentType].savings += sess.totalSavingsUSD
    }
  }

  const sortedMap = (m: Record<string, number>) =>
    Object.entries(m).sort(([, a], [, b]) => b - a).map(([name, calls]) => ({ name, calls }))

  const topSessions = projects
    .flatMap(p => p.sessions.map(s => ({
      project: p.project,
      sessionId: s.sessionId,
      date: s.firstTimestamp ? dateKey(s.firstTimestamp) : null,
      cost: (s.totalCostUSD),
      savings: (s.totalSavingsUSD),
      calls: s.apiCalls,
    })))
    .sort((a, b) => (b.cost + b.savings) - (a.cost + a.savings))
    .slice(0, 5)

  return {
    generated: new Date().toISOString(),
    currency: 'USD',
    period,
    periodKey,
    overview: {
      cost: (totalCostUSD),
      // Subscription-covered spend (config `proxyPaths`) and net out-of-pocket.
      // `cost` is the full API-rate figure; `proxiedCost` is the part billed to
      // a subscription; `netCost` = cost - proxiedCost. Both 0 with no proxy
      // paths configured, so existing consumers are unaffected.
      proxiedCost: (totalProxiedUSD),
      netCost: (netCostUSD),
      savings: (totalSavingsUSD),
      // Portion of `cost` priced from estimated tokens (issue #639). Display/
      // metadata only; never subtracted from `cost`. 0 when nothing is estimated.
      estimatedCost: (totalEstimatedUSD),
      calls: totalCalls,
      sessions: totalSessions,
      cacheHitPercent,
      tokens: {
        input: totalInput,
        output: totalOutput,
        cacheRead: totalCacheRead,
        cacheWrite: totalCacheWrite,
      },
    },
    daily,
    projects: projectList,
    models,
    // Models with recorded usage that resolve to no pricing data right now
    // (#638). Their calls contribute $0 to every cost figure above, so
    // consumers can tell "cheap" from "uncounted". Empty when all models
    // priced. Fix entries via `codeburn model-alias` or `price-override`.
    unpricedModels: findUnpricedModels(Object.entries(modelMap).map(([model, d]) => ({
      model,
      calls: d.calls,
      cost: d.cost,
      tokens: d.inputTokens + d.outputTokens + d.cacheReadTokens + d.cacheWriteTokens,
    }))),
    activities,
    tools: sortedMap(toolMap),
    mcpServers: sortedMap(mcpMap),
    shellCommands: sortedMap(bashMap),
    skills: Object.entries(skillMap).sort(([, a], [, b]) => (b.cost + b.savings) - (a.cost + a.savings)).map(([name, d]) => ({ name, turns: d.turns, cost: (d.cost), savings: (d.savings) })),
    subagents: Object.entries(subagentMap).sort(([, a], [, b]) => (b.cost + b.savings) - (a.cost + a.savings)).map(([name, d]) => ({ name, calls: d.calls, cost: (d.cost), savings: (d.savings) })),
    claudeAgentTypes: Object.entries(agentTypeMap).sort(([, a], [, b]) => (b.cost + b.savings) - (a.cost + a.savings)).map(([name, d]) => ({ name, calls: d.calls, cost: (d.cost), savings: (d.savings) })),
    topSessions,
  }
}

program
  .command('report', { isDefault: true })
  .description('Interactive usage dashboard')
  .option('-p, --period <period>', 'Starting period: today, week, 30days, month, all, lifetime (interactive default: today, or week when today is empty)', 'week')
  .option('--day <date>', 'Single day to review (YYYY-MM-DD, today, or yesterday). Overrides --period when set')
  .option('--from <date>', 'Start date (YYYY-MM-DD). Overrides --period when set')
  .option('--to <date>', 'End date (YYYY-MM-DD). Overrides --period when set')
  .option('--provider <provider>', 'Filter by provider (e.g. claude, gemini, cursor, copilot)', 'all')
  .option('--format <format>', 'Output format: tui, json', 'tui')
  .option('--project <name>', 'Show only projects matching name (repeatable)', collect, [])
  .option('--exclude <name>', 'Exclude projects matching name (repeatable)', collect, [])
  .option('--refresh <seconds>', 'Auto-refresh interval in seconds (minimum 60; 0 to disable)', parseInteger, 60)
  .action(async (opts, command) => {
    assertFormat(opts.format, ['tui', 'json'], 'report')
    assertProvider(opts.provider, 'report')
    let customRange: DateRange | null = null
    let daySelection: ReturnType<typeof parseDayFlag> = null
    try {
      if (opts.day && (opts.from || opts.to)) {
        throw new Error('--day cannot be combined with --from or --to')
      }
      daySelection = parseDayFlag(opts.day)
      customRange = parseDateRangeFlags(opts.from, opts.to)
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      console.error(`\n  Error: ${message}\n`)
      process.exit(1)
    }

    const period = toPeriod(opts.period)
    if (opts.format === 'json') {
      await loadPricing()
      if (daySelection || customRange) {
        const range = daySelection?.range ?? customRange!
        const label = daySelection?.label ?? formatDateRangeLabel(opts.from, opts.to)
        const periodKey = daySelection ? 'day' : 'custom'
        const durable = await buildDurablePeriod({ range, label }, { provider: opts.provider, project: opts.project, exclude: opts.exclude })
        console.log(JSON.stringify(buildJsonReport(durable.liveProjects, label, periodKey, durable), null, 2))
      } else {
        await runJsonReport(period, opts.provider, opts.project, opts.exclude)
      }
      return
    }
    const customRangeLabel = customRange ? formatDateRangeLabel(opts.from, opts.to) : undefined
    // #1111: no explicit period of any kind means the interactive dashboard
    // picks its own — Today, or 7 days when today is still empty. Any source
    // other than the option default (a flag, an env value) is the user's
    // choice and is honored as given.
    const autoPeriod = command.getOptionValueSource('period') === 'default' && !daySelection && !customRange
    await renderDashboard(period, opts.provider, opts.refresh, opts.project, opts.exclude, customRange, customRangeLabel, daySelection?.day, autoPeriod)
  })

program
  .command('web')
  .description('Open the local KyberDash web dashboard in your browser')
  .option('--port <number>', 'Port to listen on (falls back to a free port if taken)', parseInteger, 4747)
  .option('--no-open', 'Do not open the browser automatically')
  .action(async (opts) => {
    await runWebDashboard({ port: opts.port, open: opts.open })
  })

program
  .command('menubar')
  .description('Install and launch the KyberDash tray on macOS and Windows')
  .option('--force', 'Reinstall even if a copy is already installed')
  .action(() => {
    // The inherited command installed the upstream CodeBurn menubar app, which shows spend,
    // not context, and is not built from this repository. It refuses until the KyberDash
    // tray is released from here (spec task 9.1), rather than install the wrong app.
    console.error('\n  The KyberDash tray is not released yet; this command will install it once it is.\n')
    process.exit(1)
  })

program
  .command('doctor')
  .description('Per-provider detection status: paths probed, sessions found, parse health (diagnose empty or wrong numbers)')
  .option('--provider <provider>', 'Diagnose a single provider (e.g. claude, codex, opencode)', 'all')
  .option('--json', 'Output machine-readable JSON')
  .option('--no-color', 'Disable ANSI colors')
  .action(async (opts) => {
    assertProvider(opts.provider, 'doctor')
    const { collectDoctorReport, renderDoctorTable, renderDoctorJson } = await import('./doctor.js')
    const report = await collectDoctorReport(opts.provider)
    if (opts.json) {
      process.stdout.write(renderDoctorJson(report) + '\n')
      return
    }
    process.stdout.write(renderDoctorTable(report, { color: opts.color }))
  })

registerKyberCommands(program)

  return program
}
