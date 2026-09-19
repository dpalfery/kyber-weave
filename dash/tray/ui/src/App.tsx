import { useEffect } from 'react'

import { hidePopover } from './hidePopover'

export function App() {
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        void hidePopover()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  return (
    <main>
      <h1>KyberDash</h1>
    </main>
  )
}
