// Kilo Code reader boundary.
//
// The surveyed Kilo global store is empty and its OTel format is undocumented.
// Empty storage is absence of evidence, never a measured all-zero session.

import type { ContentReader } from './types.js'

/** Why Kilo Code cannot currently contribute collectable session evidence. */
export const KILO_NOT_COLLECTABLE_REASON =
  'Kilo Code has no session data in its empty store, and its OTel format is undocumented.'

/** Public reader shape for the currently non-collectable Kilo Code surface. */
export const kiloReader: ContentReader = {
  async *read(): AsyncGenerator<never> {
    // No documented, populated source exists to parse.
  },
}

export default kiloReader
