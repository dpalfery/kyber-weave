using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using Spectre.Console;

namespace KyberWeave.Cli.Commands;

/// <summary>
/// How a command that takes a plan or specification argument turns it into a file: find the repository, then let the
/// shared <see cref="PlanPathResolver"/> accept whichever spelling the caller typed.
/// </summary>
internal static class PlanArguments
{
    /// <summary>The argument matches more than one active plan or specification.</summary>
    public const string Ambiguous = "KW-DOC-PLANREF-001";

    /// <summary>
    /// The repository an argument belongs to: the one holding the file when the argument names one, otherwise the
    /// repository the command was run in. Read before the configuration, because the configuration decides where plans live.
    /// </summary>
    public static string RepositoryRootFor(string argument, string? workingDirectory = null)
    {
        string current = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());
        string plan = Path.GetFullPath(argument, current);
        string start = Path.GetDirectoryName(plan) ?? plan;
        string found = ArbiterRepoRoot.Find(start);
        if (Directory.Exists(Path.Combine(found, ".git")) || File.Exists(Path.Combine(found, ".git")))
        {
            return found;
        }

        return plan.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? current : found;
    }

    /// <summary>
    /// Resolves <paramref name="argument"/>. An argument that names no file resolves to the path as typed, so the command's
    /// own not-found finding, with its own rule id, still reports it; an ambiguous one is reported here and returns false.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    /// <param name="argument">What the caller typed.</param>
    /// <param name="workingDirectory">The directory a relative argument is read against; null for the current one.</param>
    /// <param name="subject">The command name the finding is reported under.</param>
    /// <param name="report">Receives the ambiguity finding.</param>
    /// <param name="relative">The repository-relative path to use.</param>
    public static bool TryResolve(
        string root,
        KyberWeaveConfig config,
        string argument,
        string? workingDirectory,
        string subject,
        DiagnosticReport report,
        out string relative)
    {
        string current = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());
        PlanPathResolution resolution = new PlanPathResolver(root, config).Resolve(argument, current);
        if (resolution.Found)
        {
            relative = resolution.Path!;
            return true;
        }

        if (resolution.Ambiguous)
        {
            report.Add(new Diagnostic(
                Ambiguous,
                Severity.Error,
                $"'{argument}' matches {resolution.Candidates.Count} plans: {string.Join(", ", resolution.Candidates)}.",
                subject,
                Hint: "Name one of them: its full file name including the date prefix, or its path."));
            relative = argument;
            return false;
        }

        string full = Path.GetFullPath(argument, current);
        relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return true;
    }

    /// <summary>The configuration to resolve a plan with; the product defaults when none loads, so resolving never fails on it.</summary>
    public static KyberWeaveConfig ConfigForResolving(string root, string? configPath)
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(root, configPath);
        return result is { Success: true, Config: { } loaded } ? loaded : KyberWeaveConfig.ProductDefaults;
    }

    /// <summary>Says which file the argument resolved to, in the output a person or an agent reads; the structured formats carry it as a metric.</summary>
    public static void Announce(DiagnosticReport report, AnalysisSettings settings, string relative)
    {
        report.AddMetric("plan", relative);
        if (ReportRenderer.IsHuman(settings.ParsedFormat))
        {
            CommandHelpers.WriteUnfolded(() => AnsiConsole.MarkupLine($"Plan: {Markup.Escape(relative)}"));
        }
    }
}
