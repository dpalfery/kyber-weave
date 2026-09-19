import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'

import { App } from './App'

describe('App', () => {
  it('renders the KyberDash heading', () => {
    expect(renderToStaticMarkup(<App />)).toContain('KyberDash')
  })
})
