import { Panel, FlameMark } from 'codeburn-desktop'

/**
 * The sidebar wordmark: the 20px mark in the `.app` row beside the gradient
 * "CodeBurn" lockup. `live` adds the idle flicker (motion-gated, so it is not
 * visible in a still).
 */
export function Brand() {
  return (
    <div className="app">
      <FlameMark size={20} live />
      <b>CodeBurn</b>
    </div>
  )
}

/** `size` is the only visual axis — the four sizes the app actually ships. */
export function Sizes() {
  return (
    <div style={{ display: 'flex', alignItems: 'flex-end', gap: 'var(--sp-5)' }}>
      <FlameMark size={20} />
      <FlameMark size={40} />
      <FlameMark size={52} />
      <FlameMark size={76} />
    </div>
  )
}

/** The About-dialog hero: the 52px mark stacked over the name and tagline. */
export function AboutCard() {
  return (
    <Panel title="About">
      <div className="app" style={{ flexDirection: 'column', gap: 'var(--sp-2)', padding: 'var(--sp-4) 0 var(--sp-2)' }}>
        <FlameMark size={52} />
        <b>CodeBurn</b>
      </div>
      <p className="empty-note" style={{ textAlign: 'center' }}>
        v0.9.23 · Know where every token goes, across every AI coding tool.
      </p>
    </Panel>
  )
}
