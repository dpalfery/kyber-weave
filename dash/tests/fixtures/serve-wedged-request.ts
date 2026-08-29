// A `codeburn serve --stdio` whose one allowed command never settles: the
// smallest honest async wedge. Used by serve-stdio.test.ts to prove the
// stdin-EOF drain is BOUNDED — that a request which can never finish releases
// the child at the bound instead of turning it into the orphan the drain was
// added to prevent. Nothing production-side knows this file exists; it only
// supplies its own buildProgram to the real runStdioServe.
import { Command } from 'commander'

import { runStdioServe } from '../../src/serve.js'

const buildProgram = (): Command => {
  const program = new Command()
  program.exitOverride()
  program
    .command('status')
    .option('--format <format>', 'output format')
    .action(() => new Promise<never>(() => { /* never settles */ }))
  return program
}

await runStdioServe(buildProgram)
process.exit(0)
