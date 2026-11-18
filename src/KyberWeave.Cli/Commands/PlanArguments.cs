using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using Spectre.Console;

namespace KyberWeave.Cli.Commands;

/// <summary>
/// How a command that takes a plan or specification argument turns it into a file: find the repository, then let the
/// shared <see cref="PlanFileResolver"/> accept whichever spelling the caller typed.
/// </summary>
internal static class PlanArguments
{
    /// <summary>The argument matches more than one active plan or specification.</summary>
    public const string Ambiguous = "KW-DOC-PLANREF-001";

    /// <summary>
    /// The repository an argument belongs to: the one holding the file when the argument names one, otherwise the
    /// repository the command was run in. Read before the configuration, because the configuration decides where plans live.
    /// </summary>
    /// <param name="argument">The plan or specification as the caller typed it.</param>
    /// <param name="workingDirectory">The directory a relative argument is read against; null for the process's.</param>
    /// <param name="paths">The path seam, when the caller has one; null reads the process's file system.</param>
    /// <param name="environment">The environment seam, when the caller has one; null reads the process's.</param>
    public static string RepositoryRootFor(
        string argument,
        string? workingDirectory = null,
        IPathResolver? paths = null,
        IProcessEnvironment? environment = null)
    {
        string current = CurrentDirectory(workingDirectory, paths, environment);
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
    /// <param name="files">Probes the planning folders.</param>
    /// <param name="argument">What the caller typed.</param>
    /// <param name="workingDirectory">The directory a relative argument is read against; null for the current one.</param>
    /// <param name="subject">The command name the finding is reported under.</param>
    /// <param name="report">Receives the ambiguity finding.</param>
    /// <param name="relative">The repository-relative path to use.</param>
    /// <param name="paths">The path seam, when the caller has one; null reads the process's file system.</param>
    /// <param name="environment">The environment seam, when the caller has one; null reads the process's.</param>
    public static bool TryResolve(
        string root,
        KyberWeaveConfig config,
        IRepositoryFileStore files,
        string argument,
        string? workingDirectory,
        string subject,
        DiagnosticReport report,
        out string relative,
        IPathResolver? paths = null,
        IProcessEnvironment? environment = null)
    {
        string current = CurrentDirectory(workingDirectory, paths, environment);
        PlanPathResolution resolution = new PlanFileResolver(root, config, files).Resolve(argument, current);
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

    /// <summary>The directory a relative argument is read against, through the seams when the caller has them.</summary>
    private static string CurrentDirectory(
        string? workingDirectory,
        IPathResolver? paths,
        IProcessEnvironment? environment) =>
        paths is null || environment is null
            ? Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory())
            : workingDirectory is null
                ? environment.CurrentDirectory
                : paths.GetFullPath(workingDirectory, environment.CurrentDirectory);

    /// <summary>The configuration to resolve a plan with; the product defaults when none loads, so resolving never fails on it.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="configPath">An explicit <c>--config</c> path, or null for the repository's own.</param>
    /// <param name="configs">The repository the configuration is read from.</param>
    public static KyberWeaveConfig ConfigForResolving(string root, string? configPath, IKyberWeaveConfigRepository configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        KyberWeaveConfigLoadResult result = configs.TryLoad(root, configPath);
        return result is { Success: true, Config: { } loaded } ? loaded : KyberWeaveConfigDefaults.Product;
    }

    /// <summary>Says which file the argument resolved to, in the output a person or an agent reads; the structured formats carry it as a metric.</summary>
    public static void Announce(DiagnosticReport report, AnalysisSettings settings, string relative) =>
        Announce(AnsiConsole.Console, report, settings, relative);

    /// <summary>Says which file the argument resolved to, on the console the caller was given.</summary>
    /// <param name="console">The console the line goes to.</param>
    /// <param name="report">Receives the metric the structured formats carry.</param>
    /// <param name="settings">The command's settings, which decide whether a person reads the line.</param>
    /// <param name="relative">The repository-relative path the argument resolved to.</param>
    public static void Announce(
        IAnsiConsole console, DiagnosticReport report, AnalysisSettings settings, string relative)
    {
        report.AddMetric("plan", relative);
        if (ReportRenderer.IsHuman(settings.ParsedFormat))
        {
            CommandHelpers.WriteUnfolded(console, () => console.MarkupLine($"Plan: {Markup.Escape(relative)}"));
        }
    }
}
