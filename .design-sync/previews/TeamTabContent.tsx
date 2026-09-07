import { Panel, TeamTabContent } from 'codeburn-desktop'

/**
 * TeamTabContent reads exactly one place in the payload — `current.plugins[tab]`
 * — and renders whatever the team plugin published there. The rest of a real
 * `MenubarPayload` is the overview totals, which this component never touches.
 */
function payload(plugins: Record<string, unknown>) {
  return { generated: '2024-05-15T09:12:00Z', current: { label: 'Last 7 days', plugins } } as never
}

/** The full `teams.week` shape: two totals plus the ranked work units. */
export function TeamWeek() {
  return (
    <Panel title="Team Week" right="Last 7 days">
      <TeamTabContent
        tab="teams.week"
        payload={payload({
          'teams.week': {
            spend: 1284.55,
            sessions: 412,
            topWorkUnits: [
              { name: 'kyber-weave · retrieval index' },
              { name: 'codeburn · Electron renderer' },
              { name: 'agentseal-site · pricing page' },
            ],
          },
        })}
      />
    </Panel>
  )
}

/** `topWorkUnits` is optional — without it the row falls back to the two totals. */
export function TeamWeekTotalsOnly() {
  return (
    <Panel title="Team Week" right="Last 7 days">
      <TeamTabContent
        tab="teams.week"
        payload={payload({ 'teams.week': { spend: 318.02, sessions: 97 } })}
      />
    </Panel>
  )
}

/** `teams.status` publishes a URL, and the tab renders the hand-off button for it. */
export function TeamStatus() {
  return (
    <Panel title="Team Status">
      <TeamTabContent
        tab="teams.status"
        payload={payload({ 'teams.status': 'https://status.getagentseal.com/codeburn' })}
      />
    </Panel>
  )
}

/** The tab is registered but the CLI published nothing under it this run. */
export function TabWithoutData() {
  return (
    <Panel title="Team Status">
      <TeamTabContent
        tab="teams.status"
        payload={payload({ 'teams.week': { spend: 1284.55, sessions: 412 } })}
      />
    </Panel>
  )
}

/**
 * Forward compatibility: a plugin publishing a schema this desktop build predates
 * asks for an update instead of rendering a half-understood payload.
 */
export function SchemaTooNew() {
  return (
    <Panel title="Team Roadmap">
      <TeamTabContent
        tab="teams.roadmap"
        payload={payload({ 'teams.roadmap': { schemaVersion: 2, quarter: 'Q3 2024', committed: 14 } })}
      />
    </Panel>
  )
}

/** An unrecognised `teams.*` key on a schema this build does understand. */
export function UnknownTab() {
  return (
    <Panel title="Team Retros">
      <TeamTabContent
        tab="teams.retros"
        payload={payload({ 'teams.retros': { schemaVersion: 1, retros: 3 } })}
      />
    </Panel>
  )
}
