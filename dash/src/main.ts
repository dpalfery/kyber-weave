// The CLI entry. Everything the program registers lives in program.ts, so tests can build
// the command tree without running it.
import { buildProgram } from './program.js'

// A downstream reader that closes the pipe early (`| head`, quitting `less`, or
// a missing command) makes stdout writes fail with EPIPE. Exit cleanly rather
// than crashing with an unhandled error event.
process.stdout.on('error', (err: NodeJS.ErrnoException) => {
  if (err.code === 'EPIPE') process.exit(0)
  throw err
})

buildProgram().parse()
