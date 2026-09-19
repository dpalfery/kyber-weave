import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import { App } from './App'
import './styles.css'

const root = document.getElementById('root')
if (root == null) {
  throw new Error('KyberDash tray UI is missing #root')
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
