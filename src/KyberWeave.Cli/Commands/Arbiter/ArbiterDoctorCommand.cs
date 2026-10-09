using System.Diagnostics;
using System.Reflection;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Release;
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
    /// Runs every check and reports findings. A malformed override (<c>-009</c>)
    /// still leaves the product defaults to diagnose, so one bad file never hides
    /// the remaining checks. Writes nothing. Exits 1 only on error findings.
    /// </summary>
    protected override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = new();
        _ = ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out string? configPath);

        AnsiConsole.MarkupLine("[bold]Kyber-Arbiter Doctor[/]");
        AnsiConsole.WriteLine();

        report.AddRange(RuleValidator.Validate(config.Arbiter, configPath));
        report.AddRange(RuleValidator.CheckModelTuning(config.Arbiter));
        CheckKey(config, report);
        CheckLogIgnored(settings, report);
        CheckBinary(report);
        CheckGuard(config, settings, report);
        CheckCopilotCliLimitation();

        CommandHelpers.Finish(report, settings, "arbiter doctor", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private static void CheckKey(KyberWeaveConfig config, DiagnosticReport report)
    {
        ArbiterProviderConfig provider = config.Arbiter.Provider;
        if (provider.Kind == ArbiterProviderKind.None || ArbiterKeyResolver.IsLoopbackEndpoint(provider.Endpoint))
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Key: none needed (none or loopback provider).");
            return;
        }

        string? origin = ArbiterKeyResolver.GetOrigin(provider.Endpoint);
        if (ArbiterCommandComposition.TryResolveKey(config, provider.Endpoint) is not null)
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
        string cliVersion = NormalizeCliVersion(GetCliVersion());

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
    /// Notes the Copilot CLI post-dispatch limitation as information: on the
    /// <c>copilot-cli</c> target <c>Deny</c>, <c>PostBlock</c> and <c>PostAnnotation</c>
    /// render only as <c>additionalContext</c>, so post-dispatch findings are advisory.
    /// Informational only, hence no diagnostic id.
    /// </summary>
    private static void CheckCopilotCliLimitation()
    {
        AnsiConsole.MarkupLine(
            "  [blue]info[/] Copilot CLI (copilot-cli): post-dispatch Deny/PostBlock/PostAnnotation render as additionalContext (advisory); pre-dispatch denies still block.");
    }

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

    private static string GetCliVersion()
    {
        Assembly assembly = typeof(ArbiterDoctorCommand).Assembly;
        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
            return infoVersion;

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>
    /// Normalizes the CLI version for comparison against the probe's semver: the
    /// assembly informational version may carry <c>+build</c> metadata the probe
    /// never emits. Mirrors the Squad doctor comparison so both cite the same version.
    /// </summary>
    private static string NormalizeCliVersion(string version)
    {
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string normalized = plus > 0 ? version[..plus] : version;
        return normalized.Trim().TrimStart('v');
    }
}
