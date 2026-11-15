import { dirname, join } from 'path'
import { fileURLToPath } from 'url'

// Shared with the networked bundler and the #229 regression test so the write
// target and the runtime import directory stay one expression, not two spellings.
const scriptsDir = dirname(fileURLToPath(import.meta.url))
export const pricingDataDir = join(scriptsDir, '..', 'src', 'pricing', 'data')
export const snapshotPath = join(pricingDataDir, 'litellm-snapshot.json')
export const fallbackPath = join(pricingDataDir, 'pricing-fallback.json')
