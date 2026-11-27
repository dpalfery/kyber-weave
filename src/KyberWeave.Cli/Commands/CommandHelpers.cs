using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;

namespace KyberWeave.Cli.Commands;

public static class CommandHelpers
{
    /// <summary>
    /// Loads <c>.kyber-weave/kyber-weave.yml</c> for CLI commands. On failure, adds
    /// <see cref="KyberWeaveConfigDiagnosticCodes.LoadError"/> and returns false.
    /// </summary>
    public static bool TryLoadConfig(
        IKyberWeaveConfigRepository configs,
        string repoRoot,
        string? configPath,
        DiagnosticReport report,
        out KyberWeaveConfig config) =>
        TryLoadConfig(configs, repoRoot, configPath, report, out config, out _);

    /// <summary>
    /// Loads <c>.kyber-weave/kyber-weave.yml</c> for CLI commands, reporting the file it read.
    /// </summary>
    /// <param name="configs">The repository the configuration is read through.</param>
    /// <param name="repoRoot">The repository root the configuration is resolved under.</param>
    /// <param name="configPath">An explicit <c>--config</c> path, or null for the repository's own.</param>
    /// <param name="report">The report a load failure is recorded in.</param>
    /// <param name="config">The loaded configuration, or the product defaults on failure.</param>
    /// <param name="resolvedConfigPath">
    /// The configuration file that was read, or null when the repository has none. A command that reads a
    /// setting the file itself decides whether the repository opted into — onboarding is one — needs the
    /// difference between "unset" and "not configured".
    /// </param>
    public static bool TryLoadConfig(
        IKyberWeaveConfigRepository configs,
        string repoRoot,
        string? configPath,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out string? resolvedConfigPath)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(report);

        KyberWeaveConfigLoadResult result = configs.TryLoad(repoRoot, configPath);
        resolvedConfigPath = result.ConfigPath;
        if (!result.Success || result.Config is not { } loadedConfig)
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigDiagnosticCodes.LoadError,
                Severity.Error,
                result.Error ?? "Failed to load kyber-weave.yml.",
                "kyber-weave.yml",
                result.ConfigPath));
            config = KyberWeaveConfigDefaults.Product;
            return false;
        }

        config = loadedConfig;

        // Unknown keys load but do nothing, so each one is surfaced rather than left to be noticed later.
        foreach (string warning in config.Review.Warnings)
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigDiagnosticCodes.Warning,
                Severity.Warning,
                warning,
                "kyber-weave.yml",
                result.ConfigPath,
                Hint: "Check the spelling against the gate keys, or remove the key."));
        }

        return true;
    }

    /// <summary>
    /// Applies <c>--no-info</c>, renders <paramref name="report"/> in the requested format through
    /// <paramref name="console"/>, and prints the summary, so a command that writes to an injected console renders its
    /// findings there too.
    /// </summary>
    /// <param name="console">The console the findings are written to.</param>
    /// <param name="report">The findings to render.</param>
    /// <param name="settings">The command's settings, which choose the format.</param>
    /// <param name="command">The command name a document format records.</param>
    /// <param name="subjectLabel">Names what the findings are about.</param>
    public static void Finish(
        IAnsiConsole console,
        DiagnosticReport report,
        AnalysisSettings settings,
        string command,
        string subjectLabel)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.NoInfo)
        {
            DiagnosticReport filtered = new DiagnosticReport();
            filtered.AddRange(report.Items.Where(i => i.Severity != Severity.Info));
            foreach (KeyValuePair<string, object?> metric in report.Metrics)
            {
                filtered.AddMetric(metric.Key, metric.Value);
            }

            report = filtered;
        }

        OutputFormat format = settings.FormatFor(console);
        ReportRenderer.Render(console, report, format, command, subjectLabel);
        if (format == OutputFormat.Table)
        {
            console.WriteLine();
            ReportRenderer.RenderSummary(console, report);
        }
        else if (format == OutputFormat.Lines)
        {
            ReportRenderer.RenderSummary(console, report);
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> with <paramref name="console"/>'s folding lifted, then restores its width.
    /// </summary>
    /// <param name="console">The console whose width is lifted for the write.</param>
    /// <param name="write">The output to produce at full width.</param>
    /// <remarks>
    /// <para>
    /// A redirected console folds at 80 columns, which splits a repository-relative path, a rule id
    /// or the remedy command the operator is meant to copy onto a second line — in the one message
    /// whose whole value is being copy-pasteable.
    /// </para>
    /// <para>
    /// It lives here, once, because every branch reports findings this way and the rule for
    /// restoring the width is not obvious: a console that cannot measure itself reports 0, and
    /// Spectre's setter rejects 0, so restoring it verbatim threw from the <c>finally</c> and
    /// replaced the command's own output and exit code with an unhandled exception. Such a console
    /// is restored to <see cref="ConsoleWidth.Default"/>. A copy per branch is a fix that lands in
    /// one of them, and two commands that then render the same finding differently.
    /// </para>
    /// <para>
    /// The width belongs to the console the output goes to, so a command that writes to an injected console lifts that
    /// one rather than the process-wide console.
    /// </para>
    /// </remarks>
    public static void WriteUnfolded(IAnsiConsole console, Action write)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(write);

        // The profile is read once: the console a caller holds may name a different console by the time the write
        // returns, and restoring through it would leave this one unfolded and overwrite that one's width.
        Profile profile = console.Profile;
        int originalWidth = profile.Width;
        profile.Width = int.MaxValue;
        try
        {
            write();
        }
        finally
        {
            profile.Width = originalWidth > 0 ? originalWidth : ConsoleWidth.Default;
        }
    }
}
