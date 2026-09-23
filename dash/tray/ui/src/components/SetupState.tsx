/**
 * Shown when `kyberdash` is missing or too old (R6.7).
 *
 * The probed locations are listed because "not found" without them leaves the
 * user guessing where to put the binary, and the remedy is the command that
 * fixes it. Requirement 6.7 also forbids presenting earlier data as current, so
 * this state replaces the panels rather than sitting above them.
 */

import type { SetupInfo } from '../viewState'

type Props = {
  setup: SetupInfo
  onQuit: () => void
}

const HEADLINE: Record<SetupInfo['reason'], string> = {
  'not-found': 'KyberDash cannot find the kyberdash command',
  'too-old': 'The kyberdash command is older than this tray understands',
}

export function SetupState({ setup, onQuit }: Props) {
  return (
    <section className="setup" data-testid="setup-state">
      <h1 className="setup__headline">{HEADLINE[setup.reason]}</h1>

      <p className="setup__remedy" data-testid="setup-remedy">
        {setup.remedy}
      </p>

      <details className="setup__probed">
        <summary>Where it looked</summary>
        <ul data-testid="setup-probed">
          {setup.probed.map((location) => (
            <li key={location}>
              <code>{location}</code>
            </li>
          ))}
        </ul>
      </details>

      <nav className="actions">
        <button type="button" onClick={onQuit} data-testid="quit">
          Quit
        </button>
      </nav>
    </section>
  )
}
