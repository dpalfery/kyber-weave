using KyberWeave.Hooks;

try
{
    return await HooksCli.DispatchAsync(args, Console.In, Console.Out, Console.Error).ConfigureAwait(false);
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"kyber-weave-hooks: skipped after an unexpected failure ({ex.Message}).").ConfigureAwait(false);
    return 0;
}
