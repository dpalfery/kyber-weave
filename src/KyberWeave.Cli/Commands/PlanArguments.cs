using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs;
using KyberWeave.Core.Docs.Closeout;
using Spectre.Console;

namespace KyberWeave.Cli.Commands;

/// <summary>
/// How a command that takes a plan or specification argument turns it into a file: find the repository, then let the
/// shared <see cref="PlanFileResolver"/> accept whichever spelling the caller typed.
/// </summary>
/// <param name="roots">Finds the repository root above a path.</param>
/// <param name="files">Answers whether a <c>.git</c> entry exists.</param>
/// <param name="paths">Resolves a typed path against the working directory.</param>
/// <param name="environment">Names the working directory.</param>
/// <param name="console">Where the resolved plan is announced.</param>
public sealed class PlanArguments(
    ArbiterRepoRoot roots,
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment,
    IAnsiConsole console)
{
    /// <summary>
    /// The repository an argument belongs to: the one holding the file when the argument names one, otherwise the
    /// repository the command was run in. Read before the configuration, because the configuration decides where plans live.
    /// </summary>
    public string RepositoryRootFor(string argument, string? workingDirectory = null)
    {
        string current = CurrentDirectory(workingDirectory);
        string plan = paths.GetFullPath(argument, current);
        string start = Path.GetDirectoryName(plan) ?? plan;
        string found = roots.Find(start);
        string git = Path.Combine(found, ".git");
        if (files.DirectoryExists(git) || files.FileExists(git))
        {
            return found;
        }

        return plan.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? current : found;
    }

    /// <summary>A resolver over the repository, on the path seam this command set holds.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    /// <param name="planFiles">Probes the planning folders.</param>
    public PlanFileResolver ResolverFor(string root, KyberWeaveConfig config, IRepositoryFileStore planFiles) =>
        new(root, config, planFiles, paths, environment);

    /// <summary>
    /// Resolves <paramref name="argument"/>. An argument that names no file resolves to the path as typed, so the command's
    /// own not-found finding, with its own rule id, still reports it; an ambiguous one is reported here and returns false.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    /// <param name="planFiles">Probes the planning folders.</param>
    /// <param name="argument">What the caller typed.</param>
    /// <param name="workingDirectory">The directory a relative argument is read against; null for the current one.</param>
    /// <param name="subject">The command name the finding is reported under.</param>
    /// <param name="report">Receives the ambiguity finding.</param>
    /// <param name="relative">The repository-relative path to use.</param>
    public bool TryResolve(
        string root,
        KyberWeaveConfig config,
        IRepositoryFileStore planFiles,
        string argument,
        string? workingDirectory,
        string subject,
        DiagnosticReport report,
        out string relative)
    {
        ArgumentNullException.ThrowIfNull(report);
        string current = CurrentDirectory(workingDirectory);
        PlanPathResolution resolution = ResolverFor(root, config, planFiles).Resolve(argument, current);
        if (resolution.Path is { } resolved)
        {
            relative = resolved;
            return true;
        }

        if (resolution.Ambiguous)
        {
            report.Add(new Diagnostic(
                PlanReferenceRuleIds.Ambiguous,
                Severity.Error,
                $"'{argument}' matches {resolution.Candidates.Count} plans: {string.Join(", ", resolution.Candidates)}.",
                subject,
                Hint: "Name one of them: its full file name including the date prefix, or its path."));
            relative = argument;
            return false;
        }

        string full = paths.GetFullPath(argument, current);
        relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return true;
    }

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
    public void Announce(DiagnosticReport report, AnalysisSettings settings, string relative)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(settings);
        report.AddMetric("plan", relative);
        if (ReportRenderer.IsHuman(settings.FormatFor(console)))
        {
            CommandHelpers.WriteUnfolded(console, () => console.MarkupLine($"Plan: {Markup.Escape(relative)}"));
        }
    }

    private string CurrentDirectory(string? workingDirectory) =>
        paths.GetFullPath(workingDirectory ?? environment.CurrentDirectory, environment.CurrentDirectory);
}
