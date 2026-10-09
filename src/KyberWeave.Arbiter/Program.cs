using KyberWeave.Arbiter;

// The hook host is a separate executable rather than a `kyber-weave hook` subcommand.
// Stdout carries the harness's decision document, and the CLI is built on
// Spectre.Console, which writes there. A separate entry point makes stream corruption
// structurally impossible instead of a matter of discipline.
int exitCode;
try
{
    exitCode = await Composition.DispatchAsync(args, Console.In, Console.Out, Console.Error)
        .ConfigureAwait(false);
}
catch (Exception ex)
{
    // Last resort: the hook path already fails closed with a harness block, so reaching
    // here means the streams themselves broke. Nothing may go to stdout. Exit 2, not 1:
    // on Claude a crash without exit code 2 is non-blocking, and on Copilot exit 1 is a
    // non-blocking hook error, so exit 1 would let a gated dispatch proceed.
    await Console.Error.WriteLineAsync($"KW-ARB-HOOK-001: hook host failed: {ex.Message}")
        .ConfigureAwait(false);
    exitCode = 2;
}

return exitCode;
