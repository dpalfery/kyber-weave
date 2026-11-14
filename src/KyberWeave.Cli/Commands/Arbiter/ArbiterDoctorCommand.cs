using System.Diagnostics;
using KyberWeave.Cli;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Diagnoses the host's Arbiter installation without writing anything.</summary>
/// <remarks>
/// Raises the provider and override diagnostics (<c>KW-ARB-CONFIG-006</c>,
/// <c>-007</c>, <c>-009</c>, <c>-010</c>), the missing-key warning
/// (<c>KW-ARB-KEY-001</c>), the unignored-log warning (<c>KW-ARB-LOG-001</c>,
/// through <c>git check-ignore -q artifacts/arbiter</c>), the binary warning
/// (<c>KW-ARB-BIN-001</c>, through <see cref="ArbiterProcessProbe"/>), and the
/// guard warning (<c>KW-ARB-GUARD-001</c>, when none of the plan, spec or todo
/// index properties resolves to a path on disk). It also notes the Copilot CLI
/// post-dispatch advisory limitation as information (no diagnostic id: nothing is
/// wrong, so there is nothing to suppress or baseline).
/// </remarks>
public sealed class ArbiterDoctorCommand : Command<ArbiterSettings>
{
    /// <summary><c>artifacts/arbiter/</c> is not ignored by git.</summary>
    public const string LogNotIgnored = "KW-ARB-LOG-001";

    /// <summary><c>kyber-weave-arbiter</c> is missing from <c>PATH</c>, or its version differs from the CLI's.</summary>
    public const string BinaryMissing = "KW-ARB-BIN-001";

    /// <summary>No plan, spec or todo index is declared, so the Read guard protects nothing.</summary>
    public const string GuardWithoutIndex = "KW-ARB-GUARD-001";

    /// <summary>
    /// Model targeting is on for a hooked target whose model targets file is missing, unreadable, or older
    /// than the Squad lock. Without a current file the model override is dropped without anyone seeing it.
    /// </summary>
    public const string ModelTargetsMissing = "KW-ARB-MODEL-001";

    /// <summary>
    /// The project enables Arbiter, and an installed harness is ungated for it: no project hook gates its
    /// dispatches and no live user-level hook does. The finding names the harness and carries its fix.
    /// </summary>
    public const string ProjectUngated = "KW-ARB-PROJECT-001";

    /// <summary>
    /// The global Squad receipt holds an Arbiter hook for a harness that <c>arbiter.global.targets</c> does
    /// not list, so <c>kyber-weave update</c> never refreshes that hook.
    /// </summary>
    public const string GlobalUnlisted = "KW-ARB-GLOBAL-001";

    private const string ModelTargetingOff = "off";
    private const string EnforcedGlobal = "enforced-global";
    private const string SquadUpdateHint = "Run kyber-weave squad update to rewrite the model targets.";

    /// <summary>
    /// Runs every check and reports findings. A malformed override (<c>-009</c>)
    /// still leaves the product defaults to diagnose, so one bad file never hides
    /// the remaining checks. Writes nothing. Exits 1 only on error findings.
    /// </summary>
    public override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = new();
        CommandHelpers.WriteUnfolded(() => Inspect(settings, report));

        CommandHelpers.Finish(report, settings, "arbiter doctor", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    private static void Inspect(ArbiterSettings settings, DiagnosticReport report)
    {
        _ = ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out string? configPath);
        string home = ArbiterCommandComposition.HomeDirectory();
        IReadOnlyList<string> globalTargets = UserHostConfig.ReadGlobalTargets(home);
        string root = ArbiterCommandComposition.ResolveRepositoryRoot(settings.Path);
        HarnessGatingReport gating = ArbiterHarnessGating.Build(root, home, config, globalTargets);

        AnsiConsole.MarkupLine("[bold]Kyber-Arbiter Doctor[/]");
        AnsiConsole.WriteLine();
        WriteGlobalScope(globalTargets);
        WriteProjectScope(gating, config.Arbiter.Enabled);

        report.AddRange(RuleValidator.Validate(config.Arbiter, configPath));
        report.AddRange(RuleValidator.CheckModelTuning(config.Arbiter));
        CheckProviderTimeout(config, home, report);
        CheckKey(config, report);
        CheckLogIgnored(settings, report);
        CheckBinary(report);
        CheckGuard(config, settings, report);
        CheckProjectHooks(gating, report);
        CheckModelTargets(config, home, globalTargets, report);
        CheckAdvisoryPostDispatchTargets();
    }

    /// <summary>
    /// Warns when model targeting is on and a hooked global target has no current model targets file.
    /// The file lives under the global Arbiter directory, and it must be at least as new as the global
    /// Squad lock, because an older one was written before the last squad update.
    /// </summary>
    /// <summary>
    /// Warns (<c>KW-ARB-CONFIG-016</c>) when the user override's provider timeout cannot finish inside the hook
    /// timeout the repository renders. A malformed override is reported by the hook and validate paths, not here.
    /// </summary>
    private static void CheckProviderTimeout(KyberWeaveConfig config, string home, DiagnosticReport report)
    {
        ArbiterProviderConfig effective;
        try
        {
            effective = ArbiterUserSettings.ApplyTo(config.Arbiter.Provider, home);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return;
        }

        report.AddRange(RuleValidator.CheckProviderTimeout(config.Arbiter.Provider, effective));
    }

    private static void CheckModelTargets(KyberWeaveConfig config, string home, IReadOnlyList<string> globalTargets, DiagnosticReport report)
    {
        if (string.Equals(config.Arbiter.ModelTargeting.Mode, ModelTargetingOff, StringComparison.Ordinal))
        {
            return;
        }

        string arbiterDirectory = Path.Combine(home, ".kyber-weave", "arbiter");
        string lockPath = Path.Combine(home, ".kyber-weave", "squad", "global", "squad.lock.yml");
        foreach (string token in globalTargets)
        {
            if (!IsHooked(token))
            {
                continue;
            }

            string targetsPath = Path.Combine(arbiterDirectory, $"model-targets.{token}.json");
            DateTime targetsWritten = default;
            bool readable = ArbiterModelTargets.TryRead(arbiterDirectory, token) is not null
                && TryGetWriteTimeUtc(targetsPath, out targetsWritten);
            bool stale = readable && TryGetWriteTimeUtc(lockPath, out DateTime lockWritten)
                && targetsWritten < lockWritten;
            if (readable && !stale)
            {
                continue;
            }

            string reason = readable ? "is older than the Squad lock" : "is missing or unreadable";
            AnsiConsole.MarkupLine(
                $"  [yellow]warn[/] Model targets [bold]{ModelTargetsMissing}[/]: the {Markup.Escape(TargetDisplayName(token))} targets file {reason}. {SquadUpdateHint}");
            report.Add(new Diagnostic(
                ModelTargetsMissing,
                Severity.Warning,
                $"Model targeting is '{config.Arbiter.ModelTargeting.Mode}' but the {TargetDisplayName(token)} model targets file {reason}.",
                targetsPath,
                Hint: SquadUpdateHint));
        }
    }

    /// <summary>
    /// Reads a file's last write time in UTC, or reports that there is none. A missing file reads as the
    /// file-time epoch rather than an error, and an unreadable one throws, so both come back as false and
    /// the check degrades to its warning instead of crashing <c>doctor</c>.
    /// </summary>
    /// <param name="path">The file to stat.</param>
    /// <param name="writtenUtc">The last write time in UTC when the file exists and can be stat'ed.</param>
    /// <returns>True when a real write time was read; false when the file is missing or cannot be stat'ed.</returns>
    internal static bool TryGetWriteTimeUtc(string path, out DateTime writtenUtc)
    {
        writtenUtc = default;
        try
        {
            writtenUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return writtenUtc != DateTime.FromFileTimeUtc(0);
    }

    /// <summary>
    /// A target is hooked when the catalogue knows it and its global reason is the enforced one. A
    /// fallback-only target carries its own degradation reason and is not hooked, so its missing
    /// targets file is not a warning: no hook would have honoured the override anyway.
    /// </summary>
    private static bool IsHooked(string token) =>
        SquadTargetCatalog.TryResolve(token, out _)
        && string.Equals(GlobalReason(token), EnforcedGlobal, StringComparison.Ordinal);

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private static void WriteGlobalScope(IReadOnlyList<string> targets)
    {
        AnsiConsole.MarkupLine("[bold]GLOBAL scope[/]");
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]no global targets configured.[/]");
            return;
        }

        foreach (string token in targets)
        {
            string reason = GlobalReason(token);
            string label = TargetDisplayName(token);
            AnsiConsole.MarkupLine($"  {Markup.Escape(label)}: {Markup.Escape(reason)}");
        }
    }

    private static void WriteProjectScope(HarnessGatingReport gating, bool enabled)
    {
        AnsiConsole.MarkupLine("[bold]PROJECT scope[/]");
        if (enabled)
        {
            AnsiConsole.MarkupLine("  arbiter.enabled: true");
        }

        ArbiterHarnessGating.WriteRows(gating, TargetDisplayName);
    }

    private static string GlobalReason(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return "enforced-global";
        }

        IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [target],
            SquadDeploymentScope.Global);
        return records.Count == 0 ? EnforcedGlobal : (records[0].Details ?? EnforcedGlobal);
    }

    private static string TargetDisplayName(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return token;
        }

        return target switch
        {
            SquadTarget.Claude => "Claude",
            SquadTarget.Codex => "Codex",
            SquadTarget.Copilot => "Copilot",
            SquadTarget.Cursor => "Cursor",
            SquadTarget.OpenCode => "OpenCode",
            SquadTarget.Pi => "Pi",
            SquadTarget.Warp => "Warp",
            SquadTarget.ZCode => "ZCode",
            SquadTarget.Kilo => "Kilo",
            SquadTarget.Antigravity => "Antigravity",
            SquadTarget.Factory => "Factory",
            SquadTarget.Devin => "Devin",
            SquadTarget.CommandCode => "CommandCode",
            _ => token,
        };
    }

    /// <summary>
    /// Raises one <see cref="ProjectUngated"/> warning per ungated harness, and one
    /// <see cref="GlobalUnlisted"/> warning per harness the global receipt holds a hook for but the global
    /// targets do not list. Each finding prints its line here and is recorded for the report.
    /// </summary>
    private static void CheckProjectHooks(HarnessGatingReport gating, DiagnosticReport report)
    {
        foreach (HarnessGatingRow row in gating.Rows.Where(row => row.Ungated))
        {
            string label = TargetDisplayName(row.Token);
            AnsiConsole.MarkupLine(
                $"  [yellow]warn[/] [bold]{ProjectUngated}[/]: {Markup.Escape(label)} is ungated; run {Markup.Escape(row.FixCommand ?? string.Empty)}.");
            report.Add(new Diagnostic(
                ProjectUngated,
                Severity.Warning,
                $"Project enables Arbiter, and {label} is ungated: no project hook and no live global hook gates its dispatches.",
                "arbiter.enabled",
                Hint: row.FixCommand));
        }

        foreach (string token in gating.UnlistedGlobalTokens)
        {
            string label = TargetDisplayName(token);
            string fix = $"kyber-weave arbiter install --target {token}";
            AnsiConsole.MarkupLine(
                $"  [yellow]warn[/] [bold]{GlobalUnlisted}[/]: the global Squad receipt holds an Arbiter hook for {Markup.Escape(label)}, which arbiter.global.targets does not list; run {Markup.Escape(fix)}.");
            report.Add(new Diagnostic(
                GlobalUnlisted,
                Severity.Warning,
                $"The global Squad receipt holds an Arbiter hook for {label}, but {label} is not in arbiter.global.targets, so kyber-weave update never refreshes it.",
                "arbiter.global.targets",
                Hint: fix));
        }
    }

    private static void CheckKey(KyberWeaveConfig config, DiagnosticReport report)
    {
        ArbiterProviderConfig provider = config.Arbiter.Provider;
        if (provider.Kind == ArbiterProviderKind.None || ArbiterKeyResolver.IsLoopbackEndpoint(provider.Endpoint))
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Key: none needed (none or loopback provider).");
            return;
        }

        string? origin = ArbiterKeyResolver.GetOrigin(provider.Endpoint);
        string? key;
        try
        {
            key = ArbiterCommandComposition.ResolveKey(config, provider.Endpoint);
        }
        catch (SecretServiceUnavailableException exception)
        {
            // The store cannot run on this host: the finding carries the remedy, not a generic "no key".
            AnsiConsole.MarkupLine(
                $"  [yellow]warn[/] Key [bold]{ArbiterStatusCommand.KeyMissing}[/]: {Markup.Escape(exception.Message)}");
            report.Add(new Diagnostic(
                ArbiterStatusCommand.KeyMissing,
                Severity.Warning,
                exception.Message,
                "arbiter.provider"));
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            key = null;
        }

        if (key is not null)
        {
            AnsiConsole.MarkupLine($"  [green]ok[/] Key: found for {Markup.Escape(origin ?? provider.Endpoint)}.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"  [yellow]warn[/] Key [bold]{ArbiterStatusCommand.KeyMissing}[/]: no key resolves for {Markup.Escape(origin ?? provider.Endpoint)}.");
        report.Add(new Diagnostic(
            ArbiterStatusCommand.KeyMissing,
            Severity.Warning,
            $"A remote provider is configured, and no key resolves for origin '{origin}'.",
            "arbiter.provider",
            Hint: $"Run 'arbiter setup' or export {ArbiterKeyResolver.EnvVarName}."));
    }

    private static void CheckLogIgnored(ArbiterSettings settings, DiagnosticReport report)
    {
        string root = ArbiterCommandComposition.ResolveRepositoryRoot(settings.Path);
        if (IsIgnoredByGit(root))
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Log: artifacts/arbiter/ is ignored by git.");
            return;
        }

        AnsiConsole.MarkupLine($"  [yellow]warn[/] Log [bold]{LogNotIgnored}[/]: artifacts/arbiter/ is not ignored by git.");
        report.Add(new Diagnostic(
            LogNotIgnored,
            Severity.Warning,
            "artifacts/arbiter/ is not ignored by git; the ledger and decision log must not be committed.",
            "artifacts/arbiter",
            Hint: "Add artifacts/arbiter/ to .gitignore."));
    }

    private static bool IsIgnoredByGit(string root)
    {
        try
        {
            ProcessStartInfo startInfo = new("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("check-ignore");
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("artifacts/arbiter");
            ProcessResult result = ArbiterCommandComposition.RunProcess(startInfo, string.Empty);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            // Without git there is no evidence the log is ignored: warn, the safe direction.
            return false;
        }
    }

    private static void CheckBinary(DiagnosticReport report)
    {
        ArbiterProcessProbe probe = new(ArbiterCommandComposition.ArbiterProbeExecutor());
        ToolProbeResult result = probe.Probe();
        string cliVersion = CliAssemblyVersion.ForProbe(typeof(ArbiterDoctorCommand).Assembly);

        if (result is { IsAvailable: true, Version: not null }
            && string.Equals(result.Version, cliVersion, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"  [green]ok[/] Binary: kyber-weave-arbiter {Markup.Escape(result.Version)}.");
            return;
        }

        string reason = result.FailureReason
            ?? (result.Version is null
                ? "The 'kyber-weave-arbiter' executable is not available on PATH."
                : $"The 'kyber-weave-arbiter' version '{result.Version}' differs from the CLI version '{cliVersion}'.");
        AnsiConsole.MarkupLine($"  [yellow]warn[/] Binary [bold]{BinaryMissing}[/]: {Markup.Escape(reason)}");
        report.Add(new Diagnostic(
            BinaryMissing,
            Severity.Warning,
            $"Kyber-Weave Arbiter binary: {reason}",
            "kyber-weave-arbiter",
            Hint: "Install the kyber-weave-arbiter binary matching this CLI version."));
    }

    private static void CheckGuard(KyberWeaveConfig config, ArbiterSettings settings, DiagnosticReport report)
    {
        string root = ArbiterCommandComposition.ResolveRepositoryRoot(settings.Path);
        IReadOnlyList<ConfigRegEntry> entries = config.ConfigReg.Resolve(config.Ontology);
        string[] wanted =
        [
            ConfigRegConfig.PlanIndexProperty,
            ConfigRegConfig.SpecificationIndexProperty,
            ConfigRegConfig.TodoIndexProperty,
        ];
        bool anyResolves = wanted.Any(name => entries.Any(entry =>
            string.Equals(entry.Name, name, StringComparison.Ordinal) && IndexExists(root, entry.Path)));

        if (anyResolves)
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Guard: a plan, spec or todo index resolves.");
            return;
        }

        AnsiConsole.MarkupLine($"  [yellow]warn[/] Guard [bold]{GuardWithoutIndex}[/]: no plan, spec or todo index resolves.");
        report.Add(new Diagnostic(
            GuardWithoutIndex,
            Severity.Warning,
            "No plan, spec or todo index is declared, so the Read guard protects nothing.",
            "arbiter",
            Hint: "Declare the plan, spec and todo indexes so the Read guard has something to protect."));
    }

    /// <summary>
    /// Notes, as information, the targets whose post-dispatch findings are advisory:
    /// on <c>copilot-cli</c>, <c>codex</c> and <c>cursor</c> a post-dispatch
    /// <c>Deny</c>, <c>PostBlock</c> or <c>PostAnnotation</c> renders only as additional
    /// context, so the conductor can ignore it. Pre-dispatch denies still block on all
    /// three. Informational only, hence no diagnostic id.
    /// </summary>
    private static void CheckAdvisoryPostDispatchTargets()
    {
        foreach ((string label, string target, string channel) in AdvisoryPostDispatchTargets)
        {
            AnsiConsole.MarkupLine(
                $"  [blue]info[/] {label} ({target}): post-dispatch Deny/PostBlock/PostAnnotation render as {channel} (advisory); pre-dispatch denies still block.");
        }
    }

    private static readonly (string Label, string Target, string Channel)[] AdvisoryPostDispatchTargets =
    [
        ("Copilot CLI", "copilot-cli", "additionalContext"),
        ("Codex", "codex", "additionalContext"),
        ("Cursor", "cursor", "additional_context"),
    ];

    private static bool IndexExists(string root, string path)
    {
        try
        {
            string full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
            return File.Exists(full) || Directory.Exists(full);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }
}
