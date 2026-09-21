import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import { App } from './App'
import './styles.css'

// The shared tokens carry their dark values on `.dark`, which the dashboard's
// index.html sets before first paint. The tray has no theme control of its
// own and no inline script — the webview's CSP is `default-src 'self'` — so it
// follows the system appearance from here, and keeps following it: a menu bar
// app outlives the appearance switches that happen around it.
const darkQuery = window.matchMedia('(prefers-color-scheme: dark)')
const applyScheme = (dark: boolean) => {
  document.documentElement.classList.toggle('dark', dark)
}
applyScheme(darkQuery.matches)
darkQuery.addEventListener('change', (event) => applyScheme(event.matches))

const root = document.getElementById('root')
if (root == null) {
  throw new Error('KyberDash tray UI is missing #root')
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
