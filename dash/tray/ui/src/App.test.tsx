import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'

import { App } from './App'

/**
 * `App` is the shell: it fetches the `ViewState` and hands it to `Popover`.
 * A static render runs no effects, so what it shows is the pre-state view —
 * which is the one thing worth pinning here. Everything the popover renders
 * from a state is covered in `Popover.test.tsx`, against every fixture.
 */
describe('App', () => {
  it('shows a loading view until the first ViewState arrives', () => {
    const html = renderToStaticMarkup(<App />)

    expect(html).toContain('data-testid="popover-loading"')
    // Not an empty frame: a popover that opened before the first state should
    // say why it is blank rather than look broken.
    expect(html).toContain('Reading the store…')
  })
})
