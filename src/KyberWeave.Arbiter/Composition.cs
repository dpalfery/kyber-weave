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
    /// The hook path fails open (ADR 0072): an exception in the stdin read or the host
    /// construction logs <c>KW-ARB-HOOK-001</c> to <paramref name="stderr"/> and allows with
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

            try
            {
                string stdinText = await stdin.ReadToEndAsync().ConfigureAwait(false);
                HookCommand command = (commandFactory ?? CreateDefault)(stderr);
                return command.Run(harness, caller, stdinText, stdout, stderr, scope, home);
            }
            catch (Exception ex)
            {
                // Fail open: the stdin read or the host construction broke before the host's own
                // catch existed. The failure is logged and the dispatch continues (ADR 0072).
                return await FailOpenAsync(harness, ex, stdout, stderr).ConfigureAwait(false);
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

            return ConductorGuard.Run(stdinText, stdout, stderr, OptionValue(args, "--allow-mcp"));
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

            return WorkerGuard.Run(harness, stdinText, stdout, stderr, OptionValue(args, "--home"));
        }

        await stderr.WriteLineAsync(
            "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>] | kyber-weave-arbiter guard [--allow-mcp <server>] | " +
            "kyber-weave-arbiter worker-guard --harness <token> | kyber-weave-arbiter --version.")
            .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// Lets the dispatch continue after a fault before <see cref="HookCommand"/> ran: logs
    /// <c>KW-ARB-HOOK-001</c> to stderr and writes the harness's allow document when it has
    /// one. Returns 0, so no harness reads the fault as a block.
    /// </summary>
    private static async Task<int> FailOpenAsync(
        string harness,
        Exception fault,
        TextWriter stdout,
        TextWriter stderr)
    {
        string detail = OneLine(fault.Message);
        string? allow = null;
        try
        {
            HarnessAdapterRegistry registry =
                HarnessAdapterRegistry.CreateDefault(new ArbiterHookDecisionEngine());
            if (registry.TryGet(harness, out IHarnessHookAdapter? adapter) && adapter is not null)
            {
                allow = adapter.DisabledAllowDocument;
            }
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
