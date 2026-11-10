using System.Reflection;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;

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
                    return 2;
                }
            }

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
                return command.Run(harness, caller, stdinText, stdout, stderr, scope, home);
            }
            catch (Exception ex)
            {
                // Fail closed: the stdin read or the host construction broke before the
                // host's own catch existed. Yield this harness's block, never an
                // empty-stdout error that Claude and Copilot treat as non-blocking.
                return await FailClosedAsync(harness, caller, ex, stdout, stderr).ConfigureAwait(false);
            }
        }

        if (args.Length > 0 && string.Equals(args[0], "guard", StringComparison.Ordinal))
        {
            string stdinText = await stdin.ReadToEndAsync().ConfigureAwait(false);
            return ConductorGuard.Run(stdinText, stdout, stderr, OptionValue(args, "--allow-mcp"));
        }

        await stderr.WriteLineAsync(
            "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>] | kyber-weave-arbiter guard [--allow-mcp <server>] | kyber-weave-arbiter --version.")
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
                    block = LastResortBlock.For(harness);
                }
            }
            else
            {
                block = LastResortBlock.For(harness);
            }
        }
        catch (Exception)
        {
            block = LastResortBlock.For(harness);
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
    internal static KyberWeaveConfig LoadHostConfig(string repoRoot) =>
        LoadHostConfig(repoRoot, HookScope.Project, null);

    internal static KyberWeaveConfig LoadHostConfig(string repoRoot, HookScope scope = HookScope.Project, string? homeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        if (scope == HookScope.Global)
        {
            KyberWeaveConfigLoadResult project = KyberWeaveConfigLoader.TryLoad(repoRoot);
            if (!project.Success)
            {
                throw new InvalidOperationException(
                    $"{HookCommand.FailClosedCode}: failed to load project config '{project.ConfigPath}': {OneLine(project.Error ?? "unknown error")}");
            }

            if (project.Config is not null && project.Config.Arbiter.Enabled)
            {
                return project.Config;
            }

            string effectiveHome = ResolveHomeDirectory(homeDirectory);
            string userConfigPath = UserHostConfig.GetPath(effectiveHome);
            if (!File.Exists(userConfigPath))
            {
                throw new InvalidOperationException(
                    $"{HookCommand.FailClosedCode}: missing user config '{userConfigPath}' for global scope. " +
                    $"Create '{UserHostConfig.GetPath(effectiveHome)}'.");
            }

            try
            {
                string yaml = File.ReadAllText(userConfigPath);
                KyberWeaveConfig config = new() { Arbiter = ArbiterConfigLoader.LoadUserConfig(yaml) };
                return config;
            }
            catch (Exception ex) when (ex is YamlException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"{HookCommand.FailClosedCode}: malformed user config '{userConfigPath}': {OneLine(ex.Message)}",
                    ex);
            }
        }

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

    internal static string ResolveHomeDirectory(string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory) || string.Equals(homeDirectory, "~", StringComparison.Ordinal))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (homeDirectory.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                homeDirectory[2..]);
        }

        return homeDirectory;
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
