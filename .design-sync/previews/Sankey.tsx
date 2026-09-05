import { Panel, Sankey } from 'codeburn-desktop'

type SpendFlow = {
  period: { label: string; start: string; end: string }
  models: Array<{ id: string; label: string; cost: number }>
  projects: Array<{ id: string; label: string; cost: number }>
  links: Array<{ model: string; project: string; cost: number }>
}

const OPUS = 'claude-opus-5'
const SONNET = 'claude-sonnet-5'
const HAIKU = 'claude-haiku-4.5'
const CODEX = 'gpt-5.5-codex'

const KYBER = '/Users/dave/git/kyber-weave'
const CODEBURN = '/Users/dave/git/codeburn'
const AGENTSEAL = '/Users/dave/src/agentseal-site'
const LEDGERLOOM = '/Users/dave/work/ledgerloom-api'
const DOCS = '/Users/dave/src/docs-site'

function node(id: string, cost: number): { id: string; label: string; cost: number } {
  return { id, label: id, cost }
}

/** Every model's outgoing links sum to its node cost, and every project's
 *  incoming links sum to its own — the ribbons only stack cleanly when the
 *  flow balances, exactly as the CLI emits it. */
const MONTH: SpendFlow = {
  period: { label: 'Last 30 days', start: '2026-08-06', end: '2026-09-04' },
  models: [node(OPUS, 186.4), node(SONNET, 98.75), node(CODEX, 41.55), node(HAIKU, 24.3)],
  projects: [node(KYBER, 142.6), node(CODEBURN, 96.2), node(AGENTSEAL, 54.3), node(LEDGERLOOM, 38.4), node(DOCS, 19.5)],
  links: [
    { model: OPUS, project: KYBER, cost: 92.1 },
    { model: OPUS, project: CODEBURN, cost: 48.3 },
    { model: OPUS, project: AGENTSEAL, cost: 26.4 },
    { model: OPUS, project: LEDGERLOOM, cost: 19.6 },
    { model: SONNET, project: KYBER, cost: 34.2 },
    { model: SONNET, project: CODEBURN, cost: 30.05 },
    { model: SONNET, project: AGENTSEAL, cost: 18.6 },
    { model: SONNET, project: DOCS, cost: 15.9 },
    { model: CODEX, project: KYBER, cost: 7.9 },
    { model: CODEX, project: CODEBURN, cost: 11.7 },
    { model: CODEX, project: AGENTSEAL, cost: 4.15 },
    { model: CODEX, project: LEDGERLOOM, cost: 17.8 },
    { model: HAIKU, project: KYBER, cost: 8.4 },
    { model: HAIKU, project: CODEBURN, cost: 6.15 },
    { model: HAIKU, project: AGENTSEAL, cost: 5.15 },
    { model: HAIKU, project: LEDGERLOOM, cost: 1 },
    { model: HAIKU, project: DOCS, cost: 3.6 },
  ],
}

/** The Spend section's flow card: four models across five checkouts. */
export function ModelToProject() {
  return (
    <div style={{ maxWidth: 820 }}>
      <Panel title="Cost flow · model → project" right="Last 30 days" className="scroll-x">
        <Sankey flow={MONTH} />
      </Panel>
    </div>
  )
}

/** The CLI collapses the tail of the project list into `__other__`; that node
 *  drops the model palette and renders neutral on both the bar and the ribbon. */
export function WithOtherBucket() {
  const flow: SpendFlow = {
    period: { label: 'Last 7 days', start: '2026-08-29', end: '2026-09-04' },
    models: [node(OPUS, 92.4), node(SONNET, 48.6), node(CODEX, 21)],
    projects: [node(KYBER, 78.3), node(CODEBURN, 44.2), node(AGENTSEAL, 22.5), node('__other__', 17)],
    links: [
      { model: OPUS, project: KYBER, cost: 52.1 },
      { model: OPUS, project: CODEBURN, cost: 24.3 },
      { model: OPUS, project: AGENTSEAL, cost: 9.6 },
      { model: OPUS, project: '__other__', cost: 6.4 },
      { model: SONNET, project: KYBER, cost: 20.4 },
      { model: SONNET, project: CODEBURN, cost: 13.9 },
      { model: SONNET, project: AGENTSEAL, cost: 8.1 },
      { model: SONNET, project: '__other__', cost: 6.2 },
      { model: CODEX, project: KYBER, cost: 5.8 },
      { model: CODEX, project: CODEBURN, cost: 6 },
      { model: CODEX, project: AGENTSEAL, cost: 4.8 },
      { model: CODEX, project: '__other__', cost: 4.4 },
    ],
  }
  return (
    <div style={{ maxWidth: 820 }}>
      <Panel title="Cost flow · model → project" right="Last 7 days" className="scroll-x">
        <Sankey flow={flow} />
      </Panel>
    </div>
  )
}

/** One model, four checkouts: the single-source fan-out an Opus-only week draws. */
export function SingleModelFanOut() {
  const flow: SpendFlow = {
    period: { label: 'Last 7 days', start: '2026-08-29', end: '2026-09-04' },
    models: [node(OPUS, 118.75)],
    projects: [node(KYBER, 54.2), node(CODEBURN, 31.45), node(LEDGERLOOM, 22.1), node(DOCS, 11)],
    links: [
      { model: OPUS, project: KYBER, cost: 54.2 },
      { model: OPUS, project: CODEBURN, cost: 31.45 },
      { model: OPUS, project: LEDGERLOOM, cost: 22.1 },
      { model: OPUS, project: DOCS, cost: 11 },
    ],
  }
  return (
    <div style={{ maxWidth: 820 }}>
      <Panel title="Cost flow · model → project" right="Last 7 days" className="scroll-x">
        <Sankey flow={flow} />
      </Panel>
    </div>
  )
}

/** Today so far: two models, two checkouts — the thinnest flow the card draws. */
export function QuietDay() {
  const flow: SpendFlow = {
    period: { label: 'Today', start: '2026-09-04', end: '2026-09-04' },
    models: [node(OPUS, 8.4), node(HAIKU, 1.1)],
    projects: [node(KYBER, 6.75), node(CODEBURN, 2.75)],
    links: [
      { model: OPUS, project: KYBER, cost: 6.1 },
      { model: OPUS, project: CODEBURN, cost: 2.3 },
      { model: HAIKU, project: KYBER, cost: 0.65 },
      { model: HAIKU, project: CODEBURN, cost: 0.45 },
    ],
  }
  return (
    <div style={{ maxWidth: 820 }}>
      <Panel title="Cost flow · model → project" right="Today" className="scroll-x">
        <Sankey flow={flow} />
      </Panel>
    </div>
  )
}
