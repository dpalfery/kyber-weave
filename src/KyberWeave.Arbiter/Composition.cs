using System.Reflection;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter;

/// <summary>The composition root: wires the hook host's adapters, configuration loading and versioning.</summary>
public static class Composition
{
    /// <summary>Dispatches <c>--version</c> and <c>hook</c>. Streams are parameters so the host contract is testable.</summary>
    public static async Task<int> DispatchAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
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

            string stdinText = await stdin.ReadToEndAsync().ConfigureAwait(false);
            HookCommand command = CreateDefault(stderr);
            return command.Run(harness, caller, stdinText, stdout, stderr);
        }

        await stderr.WriteLineAsync(
            "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>] | kyber-weave-arbiter --version.")
            .ConfigureAwait(false);
        return 2;
    }

    /// <summary>The <c>--version</c> line: <c>kyber-weave-arbiter &lt;semver&gt;</c>.</summary>
    public static string VersionLine() => $"kyber-weave-arbiter {GetVersion()}";

    /// <summary>Builds the production hook host: default adapters, host configuration, stderr logging.</summary>
    internal static HookCommand CreateDefault(TextWriter log) =>
        new(
            HarnessAdapterRegistry.CreateDefault(new AllowAllHookDecisionEngine()),
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

/// <summary>
/// The task 4.1 dispatch-gating decision: allow. Full step-0/step-1 evaluation arrives
/// with the <c>serve</c>/<c>eval</c> entry points; until then the adapter still strips
/// routing headers for implementation specialists, while the Read guard and the
/// fail-closed paths enforce.
/// </summary>
public sealed class AllowAllHookDecisionEngine : IHookDecisionEngine
{
    /// <inheritdoc/>
    public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
        new(HookOutcomeKind.Allow);

    /// <inheritdoc/>
    public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
        new(HookOutcomeKind.Allow);
}
