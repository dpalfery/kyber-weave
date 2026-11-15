// The CLI entry. Everything the program registers lives in program.ts, so tests can build
// the command tree without running it.
import { CommanderError } from 'commander'
import { buildProgram } from './program.js'
import { alreadyPrinted, decodeStderrChunk } from './register.js'

// A downstream reader that closes the pipe early (`| head`, quitting `less`, or
// a missing command) makes stdout writes fail with EPIPE. Exit cleanly rather
// than crashing with an unhandled error event.
process.stdout.on('error', (err: NodeJS.ErrnoException) => {
  if (err.code === 'EPIPE') process.exit(0)
  throw err
})

// Every usage error in the command tree is raised as a CommanderError carrying the exit
// code the contract documents (2 for a refused request). Without this catch the ones our
// own actions throw -- bad --weeks, an unknown settings key, `dash refresh --history-weeks 0`
// -- escape as an uncaught throw: exit 1 and a stack trace in production instead of 2.
//
// Whether to print is decided by observation rather than by inspecting the error's code:
// commander writes the message itself before calling `exitOverride` (Command.error always
// writes to stderr), while an error thrown straight from an action has been printed by
// nobody. Both arrive here as the same `CommanderError` shape, and the `exitOverride` hooks
// in register.ts re-raise commander's own errors with the code preserved, so the signal is
// whether commander's own output already carried this exact message -- not merely whether
// stderr saw any write, which a Node warning or a logging line would satisfy.
const stderr = process.stderr
const originalStderrWrite = stderr.write.bind(stderr)
let stderrTail = ''
stderr.write = ((chunk: unknown, ...rest: unknown[]): boolean => {
  // `rest[0]` is the callback and `rest[1]` the encoding in the three-argument form, so the
  // encoding is looked up by shape rather than by position.
  stderrTail = `${stderrTail}${decodeStderrChunk(chunk, rest[1] ?? rest[0])}`.slice(-8192)
  return (originalStderrWrite as (...args: unknown[]) => boolean)(chunk, ...rest)
}) as typeof stderr.write

try {
  await buildProgram().parseAsync()
} catch (error) {
  if (!(error instanceof CommanderError)) throw error
  // A help or version request exits 0 through the same path (commander has already printed
  // the help text) and must stay silent; anything else the user asked and did not get gets
  // its message exactly once.
  if (error.exitCode !== 0 && !alreadyPrinted(stderrTail, error.message)) {
    stderr.write(`${error.message}\n`)
  }
  process.exitCode = error.exitCode
} finally {
  stderr.write = originalStderrWrite
}
