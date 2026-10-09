using System.Reflection;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter;

/// <summary>The composition root: wires the hook host's adapters, configuration loading and versioning.</summary>
public static class Composition
{
    /// <summary>Dispatches <c>--version</c> and <c>hook</c>. Streams are parameters so the host contract is testable.</summary>
    /// <remarks>
    /// The hook path fails closed: the stdin read and the host construction run inside
    /// the fail-closed path, so any exception yields that harness's deny document
    /// carrying <c>KW-ARB-HOOK-001</c> rather than an empty-stdout error. Stdout carries
    /// only the decision document; diagnostics go to <paramref name="stderr"/>.
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
    /// a construction fault still blocks; production passes null for the default host.
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
            if (string.IsNullOrWhiteSpace(harness))
            {
                await stderr.WriteLineAsync(
                    "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>]. " +
                    "The hook event is read on stdin.").ConfigureAwait(false);
                return 2;
            }

            try
            {
                string stdinText = await stdin.ReadToEndAsync().ConfigureAwait(false);
                HookCommand command = (commandFactory ?? CreateDefault)(stderr);
                return command.Run(harness, caller, stdinText, stdout, stderr);
            }
            catch (Exception ex)
            {
                // Fail closed: the stdin read or the host construction broke before the
                // host's own catch existed. Yield this harness's block, never an
                // empty-stdout error that Claude and Copilot treat as non-blocking.
                return await FailClosedAsync(harness, caller, ex, stdout, stderr).ConfigureAwait(false);
            }
        }

        await stderr.WriteLineAsync(
            "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>] | kyber-weave-arbiter --version.")
            .ConfigureAwait(false);
        return 2;
    }

    /// <summary>
    /// Renders the fail-closed block for a fault before <see cref="HookCommand"/> ran.
    /// Returns 0 carrying the block on stdout, or 2 when stdout itself is broken so the
    /// harness still blocks on the exit code. Stdout carries only the block.
    /// </summary>
    private static async Task<int> FailClosedAsync(
        string harness,
        string? caller,
        Exception fault,
        TextWriter stdout,
        TextWriter stderr)
    {
        string detail = OneLine(fault.Message);
        string decisionId;
        try
        {
            decisionId = ArbiterRecordId.New(DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            decisionId = "unknown";
        }

        string block;
        try
        {
            HarnessAdapterRegistry registry =
                HarnessAdapterRegistry.CreateDefault(new ArbiterHookDecisionEngine());
            if (registry.TryGet(harness, out IHarnessHookAdapter? adapter) && adapter is not null)
            {
                try
                {
                    block = adapter.RenderFailClosed(detail, caller, string.Empty, decisionId);
                }
                catch (Exception)
                {
                    block = FallbackBlock;
                }
            }
            else
            {
                block = FallbackBlock;
            }
        }
        catch (Exception)
        {
            block = FallbackBlock;
        }

        try
        {
            await stderr.WriteLineAsync(
                $"{HookCommand.FailClosedCode}: hook host failed before evaluation: {detail}")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stderr is best effort; the block on stdout (or the exit code) still fails closed.
        }

        try
        {
            await stdout.WriteAsync(block).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stdout is broken, so no document can ride it: block on the exit code.
            return 2;
        }

        return 0;
    }

    /// <summary>The last-resort block when even the harness adapter cannot render.</summary>
    private static string FallbackBlock =>
        """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"KW-ARB-HOOK-001: hook host failed."}}""";

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');

    /// <summary>The <c>--version</c> line: <c>kyber-weave-arbiter &lt;semver&gt;</c>.</summary>
    public static string VersionLine() => $"kyber-weave-arbiter {GetVersion()}";

    /// <summary>Builds the production hook host: default adapters, host configuration, stderr logging.</summary>
    internal static HookCommand CreateDefault(TextWriter log) =>
        new(
            HarnessAdapterRegistry.CreateDefault(new ArbiterHookDecisionEngine()),
            LoadHostConfig,
            () => ArbiterRecordId.New(DateTimeOffset.UtcNow));

    /// <summary>
    /// Loads the host configuration for <paramref name="repoRoot"/>. A missing or
    /// malformed <c>.kyber-weave/kyber-weave.yml</c> is an internal error carrying
    /// <c>KW-ARB-HOOK-001</c>: the host fails closed on classified events.
    /// </summary>
    internal static KyberWeaveConfig LoadHostConfig(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        // Mirrors the lookup in KyberWeaveConfigLoader: the default directory path,
        // then the legacy root path. TryLoad alone cannot report "missing" because it
        // answers ProductDefaults when no file exists.
        string defaultPath = Path.Combine(repoRoot, ".kyber-weave", "kyber-weave.yml");
        string legacyPath = Path.Combine(repoRoot, "kyber-weave.yml");
        if (!File.Exists(defaultPath) && !File.Exists(legacyPath))
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: no .kyber-weave/kyber-weave.yml found under " +
                $"'{repoRoot}'. Run kyber-weave docs init to create host configuration.");
        }

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(repoRoot);
        if (!loaded.Success || loaded.Config is null)
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: failed to load " +
                $"'{loaded.ConfigPath ?? defaultPath}': {loaded.Error ?? "unknown error"}.");
        }

        return loaded.Config;
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

        Version? nameVersion = assembly.GetName().Version;
        if (nameVersion is not null)
        {
            return nameVersion.ToString();
        }

        return "0.0.0";
    }
}
