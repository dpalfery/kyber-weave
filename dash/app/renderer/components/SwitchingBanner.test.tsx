// @vitest-environment jsdom
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { SwitchingBanner } from './SwitchingBanner'

describe('SwitchingBanner', () => {
  it('labels cached values as refreshing while the selected view settles', () => {
    render(<SwitchingBanner />)

    expect(screen.getByRole('status')).toHaveTextContent('Refreshing selected view…')
  })
})
