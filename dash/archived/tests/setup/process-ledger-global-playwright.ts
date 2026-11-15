// Playwright globalTeardown entry for the process ledger (storm guard T5). The configs call
// beginLedgerRun() at load, so spec workers inherit the ledger file name; this runs after them.
import { endLedgerRun } from './process-ledger-global.js'

export default async function globalTeardown(): Promise<void> {
  await endLedgerRun()
}
