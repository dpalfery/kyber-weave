using System.Reflection;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Arbiter;

/// <summary>The composition root: parses the hook host's arguments, builds its container on the path that needs one, and calls the use case.</summary>
public static class Composition
{
    /// <summary>Dispatches <c>--version</c> and <c>hook</c>. Streams are parameters so the host contract is testable.</summary>
    /// <remarks>
    /// The hook path fails open (ADR 0072): an exception in the stdin read or the host
    /// construction (the container included) logs <c>KW-ARB-HOOK-001</c> to <paramref name="stderr"/> and allows with
    /// exit 0. Stdout carries only the decision document.
    /// </remarks>
    public static async Task<int> DispatchAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
    {
        return await DispatchAsync(args, stdin, stdout, stderr, commandFactory: null).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches with an injectable host factory. Tests pass a throwing factory to prove
    /// a construction fault still lets the dispatch continue; production passes null for the default host.
    /// </summary>
    internal static async Task<int> DispatchAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        Func<TextWriter, HookCommand>? commandFactory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase)
            || args.Contains("-v", StringComparer.OrdinalIgnoreCase))
        {
            await stdout.WriteLineAsync(VersionLine()).ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "hook", StringComparison.Ordinal))
        {
            string? harness = OptionValue(args, "--harness");
            string? caller = OptionValue(args, "--caller");
            HookScope scope = HookScope.Project;
            string? home = OptionValue(args, "--home");

            string? scopeToken = OptionValue(args, "--scope");
            if (!string.IsNullOrWhiteSpace(scopeToken))
            {
                if (string.Equals(scopeToken, "global", StringComparison.OrdinalIgnoreCase))
                {
                    scope = HookScope.Global;
                }
                else if (!string.Equals(scopeToken, "project", StringComparison.OrdinalIgnoreCase))
                {
                    await stderr.WriteLineAsync(
                        "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>] [--scope project|global] [--home <path>].")
                        .ConfigureAwait(false);
                    return 1;
                }
            }

            if (string.IsNullOrWhiteSpace(harness))
            {
                await stderr.WriteLineAsync(
                    "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>]. " +
                    "The hook event is read on stdin.").ConfigureAwait(false);
                return 1;
            }

            string? stdinText = null;
            try
            {
                stdinText = await stdin.ReadToEndAsync().ConfigureAwait(false);
                if (commandFactory is null && HookFastPath.TryPassThrough(harness, caller, stdinText, out string? allow))
                {
                    // Nothing to decide: no container, no configuration, no engine. A harness that has no silent
                    // allow (Antigravity) still gets its allow document.
                    if (allow is not null)
                    {
                        await stdout.WriteAsync(allow).ConfigureAwait(false);
                    }

                    return 0;
                }

                HookCommand command = (commandFactory ?? CreateDefault)(stderr);
                return command.Run(harness, caller, stdinText, stdout, stderr, scope, home);
            }
            catch (Exception ex)
            {
                // Fail open: the stdin read or the host construction broke before the host's own
                // catch existed. The failure is logged and the dispatch continues (ADR 0072).
                return await FailOpenAsync(harness, ex, stdinText, stdout, stderr).ConfigureAwait(false);
            }
        }

        if (args.Length > 0 && string.Equals(args[0], "guard", StringComparison.Ordinal))
        {
            string stdinText;
            try
            {
                stdinText = await stdin.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An empty event is unreadable, so the guard answers it with an explicit deny.
                stdinText = string.Empty;
                try
                {
                    await stderr.WriteLineAsync($"{HookCommand.FailClosedCode}: conductor guard could not read stdin: {OneLine(ex.Message)}").ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Stderr is best effort; the deny on stdout still fails closed.
                }
            }

            string? allowedMcpServer = OptionValue(args, "--allow-mcp");
            if (ConductorGuard.TryAnswer(stdinText, stdout, stderr, allowedMcpServer, out int answered))
            {
                // Decided from the event alone: no container is built for it.
                return answered;
            }

            IConductorGuard guard;
            try
            {
                guard = ArbiterContainer.CreateGuards().GetRequiredService<IConductorGuard>();
            }
            catch (Exception ex)
            {
                // The guard fails closed: a container that cannot be built answers with an explicit deny.
                await WriteBestEffortAsync(
                    stderr,
                    $"{HookCommand.FailClosedCode}: conductor guard could not start: {OneLine(ex.Message)}").ConfigureAwait(false);
                await stdout.WriteAsync(ConductorGuard.DenyDocument("The conductor guard could not start, so this tool call was denied.")).ConfigureAwait(false);
                return 0;
            }

            return guard.Run(stdinText, stdout, stderr, allowedMcpServer);
        }

        if (args.Length > 0 && string.Equals(args[0], "worker-guard", StringComparison.Ordinal))
        {
            string? harness = OptionValue(args, "--harness");
            if (string.IsNullOrWhiteSpace(harness))
            {
                await stderr.WriteLineAsync(
                    "Usage: kyber-weave-arbiter worker-guard --harness <copilot-cli|claude> [--home <path>]. " +
                    "The tool-call event is read on stdin.").ConfigureAwait(false);
                return 1;
            }

            string stdinText;
            try
            {
                stdinText = await stdin.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An unread event allows: the guard only refuses on a fact it read (ADR 0072).
                await stderr.WriteLineAsync($"{HookCommand.FailClosedCode}: worker guard could not read stdin: {OneLine(ex.Message)}").ConfigureAwait(false);
                return 0;
            }

            if (!WorkerGuard.NeedsDecision(harness, stdinText))
            {
                // A root agent, an agent outside the Squad catalog or a call that writes no file is never refused.
                return 0;
            }

            IWorkerGuard worker;
            try
            {
                worker = ArbiterContainer.CreateGuards().GetRequiredService<IWorkerGuard>();
            }
            catch (Exception ex)
            {
                // The guard only refuses on a fact it read (ADR 0072): a container that cannot be built allows.
                await WriteBestEffortAsync(
                    stderr,
                    $"{HookCommand.FailClosedCode}: worker guard could not start ({OneLine(ex.Message)}); the tool call continues.").ConfigureAwait(false);
                return 0;
            }

            return worker.Run(harness, stdinText, stdout, stderr, OptionValue(args, "--home"));
        }

        await stderr.WriteLineAsync(
            "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>] | kyber-weave-arbiter guard [--allow-mcp <server>] | " +
            "kyber-weave-arbiter worker-guard --harness <token> | kyber-weave-arbiter --version.")
            .ConfigureAwait(false);
        return 1;
    }

    /// <summary>Parses the hook payload when it is JSON, leaving <paramref name="payload"/> unset otherwise.</summary>
    /// <param name="text">The raw stdin text, or null when the read failed.</param>
    /// <param name="payload">The cloned root element when the text parses.</param>
    /// <returns>True when the text is a JSON payload.</returns>
    private static bool TryParsePayload(string? text, out JsonElement payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lets the dispatch continue after a fault before <see cref="HookCommand"/> ran: logs
    /// <c>KW-ARB-HOOK-001</c> to stderr and writes the harness's allow document when it has
    /// one. Returns 0, so no harness reads the fault as a block.
    /// </summary>
    private static async Task<int> FailOpenAsync(
        string harness,
        Exception fault,
        string? stdinText,
        TextWriter stdout,
        TextWriter stderr)
    {
        string detail = OneLine(fault.Message);
        string? allow = null;
        try
        {
            // The event decides which allow shape is legal: Antigravity answers a post-tool event
            // with {} and a pre-tool event with a decision. When the payload is unreadable, fall
            // back to the harness's single allow document.
            allow = TryParsePayload(stdinText, out JsonElement payload)
                ? HarnessAdapterRegistry.AllowDocumentOf(harness, payload)
                : HarnessAdapterRegistry.DisabledAllowDocumentOf(harness);
        }
        catch (Exception)
        {
            // Without an adapter there is no allow document; empty stdout allows on every command harness.
        }

        try
        {
            await stderr.WriteLineAsync(
                $"{HookCommand.FailClosedCode}: hook host failed before evaluation: {detail}. " +
                "The dispatch continues without an Arbiter decision.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stderr is best effort.
        }

        if (allow is not null)
        {
            try
            {
                await stdout.WriteAsync(allow).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Stdout is broken; exit 0 still lets the dispatch continue.
            }
        }

        return 0;
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');

    /// <summary>The <c>--version</c> line: <c>kyber-weave-arbiter &lt;semver&gt;</c>.</summary>
    public static string VersionLine() => $"kyber-weave-arbiter {GetVersion()}";

    /// <summary>Builds the production hook host: a container over the application layer, resolved to the hook command.</summary>
    internal static HookCommand CreateDefault(TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return ArbiterContainer.Create().GetRequiredService<HookCommand>();
    }

    private static async Task WriteBestEffortAsync(TextWriter writer, string line)
    {
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The log is best effort.
        }
    }

    private static string? OptionValue(string[] args, string name)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal) && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string GetVersion()
    {
        Assembly assembly = typeof(Composition).Assembly;
        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIndex = infoVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
        }

        System.Version? nameVersion = assembly.GetName().Version;
        if (nameVersion is not null)
        {
            return nameVersion.ToString();
        }

        return "0.0.0";
    }
}
