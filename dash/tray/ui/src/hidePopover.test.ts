import { describe, expect, it } from 'vitest'

import { hidePopover } from './hidePopover'

describe('hidePopover', () => {
  it('is a no-op when the Tauri runtime is absent', async () => {
    await expect(hidePopover()).resolves.toBeUndefined()
  })
})
