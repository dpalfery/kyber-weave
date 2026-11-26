using System.Reflection;
using System.Text.Json;
using KyberWeave.Application;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
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
    /// <param name="stdout">Receives the version text, or the JSON that shows notes from an exit-0 run to the agent.</param>
    /// <param name="stderr">Receives feedback text and usage errors; Claude Code shows it to the agent only on exit 2.</param>
    /// <param name="environment">Reads an environment variable; defaults to the container's process-environment seam.</param>
    /// <param name="configure">Registers services before the application defaults, so they win over them.</param>
    /// <returns>0 to continue, 2 to hand feedback to the agent, 1 for a usage error.</returns>
    public static async Task<int> DispatchAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Action<IServiceCollection>? configure = null,
        Func<string, string?>? environment = null)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            await stdout.WriteLineAsync(Version()).ConfigureAwait(false);
            return 0;
        }

        if (args.Length != 3 || args[0] != "run" || args[1] != "--harness" || args[2] != HarnessRegistry.ClaudeToken)
        {
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 1;
        }

        string json = await ReadBoundedAsync(stdin).ConfigureAwait(false);
        HookParseResult parsed = ClaudeHookEventParser.Parse(json);
        if (parsed.Problem is { } problem)
        {
            string warning = $"kyber-weave-hooks: skipped; {problem}";
            await stderr.WriteLineAsync(warning).ConfigureAwait(false);
            await stdout.WriteLineAsync(AdditionalContext(warning)).ConfigureAwait(false);
            return 0;
        }

        if (parsed.Edit is not { } edit)
        {
            return 0;
        }

        ServiceCollection services = [];
        configure?.Invoke(services);
        _ = services.AddWeaveApplication();
        await using ServiceProvider provider = services.BuildServiceProvider();

        string? projectDirectory = (environment ?? provider.GetRequiredService<IProcessEnvironment>().GetVariable).Invoke("CLAUDE_PROJECT_DIR");
        edit = edit with { ProjectDirectory = string.IsNullOrWhiteSpace(projectDirectory) ? null : projectDirectory };

        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(edit, CancellationToken.None).ConfigureAwait(false);
        if (result.Feedback.Length > 0)
        {
            await stderr.WriteLineAsync(result.Feedback).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                await stdout.WriteLineAsync(AdditionalContext(result.Feedback)).ConfigureAwait(false);
            }
        }

        return result.ExitCode;
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader)
    {
        char[] buffer = new char[ClaudeHookEventParser.MaxInputChars + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return new string(buffer, 0, total);
    }

    private static string AdditionalContext(string feedback) =>
        JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new { hookEventName = "PostToolUse", additionalContext = feedback }
        });

    private static string Version() =>
        typeof(HooksCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
