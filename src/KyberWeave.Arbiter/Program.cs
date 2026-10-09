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
    // here means the streams themselves broke. Nothing may go to stdout.
    await Console.Error.WriteLineAsync($"KW-ARB-HOOK-001: hook host failed: {ex.Message}")
        .ConfigureAwait(false);
    exitCode = 1;
}

return exitCode;
