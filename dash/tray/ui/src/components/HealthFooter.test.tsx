/**
 * The footer's receiver line, asserted at the component that owns the label map.
 *
 * The popover test covers the DERIVATION (which status the shared settings imply); this
 * covers the rendering, because the two can drift apart: a status with no label renders
 * as `undefined` and reads as a rendering bug rather than a missing state.
 *
 * `off` is the one that matters here. It is the deliberate receiver switch-off, and it
 * must never be phrased as a failure - `not-reachable` means a probe failed, and telling
 * someone their network is broken when they turned the receiver off is the defect.
 */

import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'

import { HealthFooter } from './HealthFooter'
import type { ReceiverStatus } from '../viewState'

const NOW = new Date('2026-09-19T12:00:00.000Z')

function render(receiver: ReceiverStatus): string {
  return renderToStaticMarkup(
    <HealthFooter report={null} refresh={null} receiver={receiver} now={NOW} />,
  )
}

/** Just the receiver line: the refresh line above it has its own vocabulary. */
function receiverLine(receiver: ReceiverStatus): string {
  const html = render(receiver)
  const match = /data-testid="receiver-status">([^<]*)</.exec(html)
  if (match === null) throw new Error(`no receiver line in: ${html}`)
  return match[1] ?? ''
}

describe('the receiver line names the state rather than guessing at a fault', () => {
  it('says "receiver off" for a receiver the user turned off', () => {
    expect(receiverLine('off')).toBe('receiver off')
    // The states it must NOT be phrased as, in either direction.
    expect(render('off')).not.toContain('not reachable')
  })

  it('keeps every other label exactly as it was', () => {
    expect(receiverLine('reachable')).toBe('receiver reachable')
    expect(receiverLine('not-reachable')).toBe('receiver not reachable')
    expect(receiverLine('hosted')).toBe('receiver hosted by KyberDash')
    expect(receiverLine('port-held-by-other')).toBe('receiver port held by another process')
    expect(receiverLine('unknown')).toBe('receiver unknown')
  })
})