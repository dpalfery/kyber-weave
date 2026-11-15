using System.Reflection;
using KyberWeave.Application;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Hooks;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Hooks;

/// <summary>The command line of <c>kyber-weave-hooks</c>, separated from <c>Main</c> so tests can drive it with in-memory streams.</summary>
internal static class HooksCli
{
    private const string Usage = "usage: kyber-weave-hooks run --harness claude | --version";

    /// <summary>Runs one command.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="stdin">Where the harness writes its hook JSON.</param>
    /// <param name="stdout">Receives only the version text.</param>
    /// <param name="stderr">Receives feedback for the agent and usage errors.</param>
    /// <param name="configure">Registers services before the application defaults, so they win over them.</param>
    /// <returns>0 to continue, 2 to hand feedback to the agent, 1 for a usage error.</returns>
    public static async Task<int> DispatchAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Action<IServiceCollection>? configure = null)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            await stdout.WriteLineAsync(Version()).ConfigureAwait(false);
            return 0;
        }

        if (args.Length != 3 || args[0] != "run" || args[1] != "--harness" || args[2] != "claude")
        {
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 1;
        }

        string json = await stdin.ReadToEndAsync().ConfigureAwait(false);
        if (!ClaudeHookEventParser.TryParse(json, out HookEditEvent? edit) || edit is null)
        {
            return 0;
        }

        ServiceCollection services = [];
        configure?.Invoke(services);
        _ = services.AddWeaveApplication();
        await using ServiceProvider provider = services.BuildServiceProvider();

        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(edit, CancellationToken.None).ConfigureAwait(false);
        if (result.Feedback.Length > 0)
        {
            await stderr.WriteLineAsync(result.Feedback).ConfigureAwait(false);
        }

        return result.ExitCode;
    }

    private static string Version() =>
        typeof(HooksCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
