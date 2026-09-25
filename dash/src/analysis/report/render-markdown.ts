// GitHub-flavoured Markdown renderer for a `ContextReport` (R11.9, R14.1–R14.4).
//
// This path never imports chalk. Colour in Markdown would be terminal escapes,
// which a pull-request comment or an agent reading a file cannot interpret and
// which R11.9 forbids. Figures go through `formatMeasured` so an absent value
// cannot become `0` here while the text renderer shows `—`.

import { DIMENSION_KEYS, DIMENSION_NAMES } from '../scorecard.js'
import { BUCKET_KEYS } from './build.js'
import {
  formatMeasured,
  isUnmeasurable,
  type BucketKey,
  type ContextReport,
  type Measured,
  type ReportFinding,
  type ReportHarness,
  type ReportLatestSession,
} from './types.js'

const BUCKET_NAMES: Record<BucketKey, string> = {
  system_prompt: 'System prompt',
  tool_definitions: 'Tool definitions',
  instruction_context: 'Instruction context',
  conversation_history: 'Conversation history',
  tool_result_content: 'Tool result content',
}

function ageLabel(thenIso: string, nowIso: string): string {
  const ms = Date.parse(nowIso) - Date.parse(thenIso)
  if (!Number.isFinite(ms) || ms < 0) return thenIso
  const minutes = Math.round(ms / 60_000)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.round(minutes / 60)
  if (hours < 48) return `${hours}h ago`
  return `${Math.round(hours / 24)}d ago`
}

function dimensionText(figure: Measured<number> & { display?: string }): string {
  if (isUnmeasurable(figure)) return formatMeasured(figure)
  return figure.display ?? formatMeasured(figure)
}

function pressureText(figure: Measured<number>): string {
  return formatMeasured(figure, (value) => `${Math.round(value * 100)}%`)
}

function moneyText(figure: Measured<number>): string {
  return formatMeasured(figure, (value) => `$${value.toFixed(2)}`)
}

function detectedFor(report: ContextReport, harness: string): boolean | undefined {
  return report.detection?.find((row) => row.harness === harness)?.detected
}

export function renderMarkdown(report: ContextReport): string {
  const lines: string[] = []
  const push = (line = ''): void => {
    lines.push(line)
  }

  push('# KyberDash context report')
  push()
  push(`Window: last ${report.scope.days} day${report.scope.days === 1 ? '' : 's'}`)
  if (report.scope.harness !== undefined) push(`Harness: \`${report.scope.harness}\``)
  if (report.scope.sessionId !== undefined) push(`Session: \`${report.scope.sessionId}\``)
  if (report.scope.runId !== undefined) push(`Run: \`${report.scope.runId}\``)

  if (report.coverage !== undefined) {
    push()
    push('## Data coverage')
    push()
    const coverage = report.coverage
    push(`- **Store:** \`${coverage.storePath}\``)
    if (coverage.refresh.lastSuccessAt !== null) {
      push(
        `- **Refresh:** ${coverage.refresh.lastSuccessAt} (${ageLabel(coverage.refresh.lastSuccessAt, report.generatedAt)})`,
      )
    } else {
      push('- **Refresh:** never')
    }
    if (coverage.refresh.lastFailure !== null) {
      push(`- **Last fail:** ${coverage.refresh.lastFailure.at}: ${coverage.refresh.lastFailure.summary}`)
    }
    if (coverage.refresh.inProgress !== null) {
      push(`- **In progress:** pid ${coverage.refresh.inProgress.pid} since ${coverage.refresh.inProgress.since}`)
    }
    push(`- **Quarantine:** ${coverage.quarantineCount === null ? 'unavailable' : coverage.quarantineCount}`)
    push(`- **Problems:** ${coverage.problemCount === null ? 'unavailable' : coverage.problemCount}`)
    if (coverage.harnesses.length === 0) {
      push('- **Harnesses:** none in the window')
    } else {
      push()
      push('| Harness | Detected | Sessions |')
      push('| --- | --- | ---: |')
      for (const row of coverage.harnesses) {
        const detected = detectedFor(report, row.harness)
        const detection = detected === undefined ? '—' : detected ? 'yes' : 'no'
        push(`| ${row.name} | ${detection} | ${row.sessionsInWindow} |`)
      }
    }
    for (const hint of coverage.hints) {
      push()
      push(`> ${hint}`)
    }
  }

  if (report.findings !== undefined) {
    push()
    push('## Findings')
    push()
    if (report.findings.length === 0) {
      push('None in the window.')
    } else {
      report.findings.forEach((finding, index) => pushFinding(push, finding, index))
    }
  }

  if (report.harnesses !== undefined) {
    push()
    push('## Harness dimensions')
    push()
    if (report.harnesses.length === 0) {
      push('None in the window.')
    } else {
      for (const harness of report.harnesses) pushHarness(push, harness)
    }
  }

  if (report.latestSession !== undefined) {
    push()
    push('## Latest session')
    push()
    if (report.latestSession === null) {
      push('No session in the window.')
    } else {
      pushLatest(push, report.latestSession)
    }
  }

  if (report.cost !== undefined) {
    push()
    push('## Cost')
    push()
    push('| Basis | Amount |')
    push('| --- | --- |')
    for (const row of report.cost) {
      push(`| ${row.basis} | ${moneyText(row.amountUsd)} |`)
    }
  }

  push()
  return lines.join('\n')
}

function pushFinding(
  push: (line?: string) => void,
  finding: ReportFinding,
  index: number,
): void {
  push(`### ${index + 1}. ${finding.title}`)
  push()
  push(`- **Class:** ${finding.measurementClass}`)
  const basis = finding.confidenceBasis === null ? '' : ` (${finding.confidenceBasis})`
  push(`- **Confidence:** ${finding.confidence}${basis}`)
  push(`- **Mechanism:** ${finding.mechanism}`)
  push(`- **Evidence:** ${finding.evidenceIds.map((id) => `\`${id}\``).join(', ')}`)
  const recoverable = formatMeasured(finding.recoverableTokens)
  const bar =
    finding.errorBar === null ? '' : ` (${finding.errorBar.low}–${finding.errorBar.high})`
  push(`- **Recoverable:** ${recoverable}${bar}`)
  push(`- **Outcome risk:** ${finding.outcomeRiskCaveat}`)
  push(`- **Recommendation:** ${finding.recommendation}`)
  push(`- \`kyberdash web --view ${finding.view}\``)
  push()
}

function pushHarness(push: (line?: string) => void, harness: ReportHarness): void {
  push(`### ${harness.name}`)
  push()
  push('| Dimension | Value |')
  push('| --- | --- |')
  for (const key of DIMENSION_KEYS) {
    push(`| ${DIMENSION_NAMES[key]} | ${dimensionText(harness.dimensions[key])} |`)
  }
  push()
}

function pushLatest(push: (line?: string) => void, session: ReportLatestSession): void {
  const project = session.project === null ? '' : ` · ${session.project}`
  push(`**${session.sessionId}** · ${session.harness}${project}`)
  push()
  push(`- **Last activity:** ${session.lastActivityAt}`)
  push(`- **Turns:** ${session.turnCount}`)
  push(`- \`kyberdash web --view ${session.view}\``)
  push(`- **Latest turn:** #${session.latestTurn.index}`)
  push(`- **Pressure:** ${pressureText(session.latestTurn.pressure)}`)
  push(`- **Window:** ${formatMeasured(session.latestTurn.contextWindow)}`)
  push()
  push('| Bucket | Tokens |')
  push('| --- | --- |')
  for (const key of BUCKET_KEYS) {
    push(`| ${BUCKET_NAMES[key]} | ${formatMeasured(session.latestTurn.buckets[key])} |`)
  }
  push()
  push(`- **Residual:** ${formatMeasured(session.latestTurn.residual)}`)
  push(`- **Cache break:** ${session.latestTurn.cacheInvalidation ? 'yes' : 'no'}`)
  if (session.toolDefinitionSources.length > 0) {
    push()
    push('| Tool definition source | Tokens |')
    push('| --- | --- |')
    for (const source of session.toolDefinitionSources) {
      push(`| ${source.source} | ${formatMeasured(source.tokens)} |`)
    }
  }
  if (session.cacheInvalidationTurns.length > 0) {
    push()
    push(`Cache-invalidation turns: ${session.cacheInvalidationTurns.join(', ')}`)
  }
}
