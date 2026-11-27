using KyberWeave.Hooks;

try
{
    return await HooksCli.DispatchAsync(args, Console.In, Console.Out, Console.Error).ConfigureAwait(false);
}
catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
{
    // Hook entry-point boundary: an unexpected internal failure is logged and must not crash the host.
    await Console.Error.WriteLineAsync($"kyber-weave-hooks: skipped after an unexpected failure ({ex.Message}).").ConfigureAwait(false);
    return 0;
}
