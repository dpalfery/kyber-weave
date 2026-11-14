using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// The file rules the plan lifecycle commands share: which path is an active plan, where the plan index is, and how
/// the files a command touched are staged. <see cref="PlanCloseout"/> and <see cref="PlanFinalize"/> both refuse any
/// path that is not a plan file directly under the plans folder, so neither can be pointed at code or other documents.
/// </summary>
internal sealed class ActivePlanFiles
{
    private static readonly string[] StageArguments = ["add", "-A", "--"];
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;

    internal ActivePlanFiles(string repoRoot, KyberWeaveConfig config)
    {
        _repoRoot = repoRoot;
        _config = config;
    }

    /// <summary>The plan index's repository-relative path, or null when the configuration registers none.</summary>
    internal string? PlanIndexPath() =>
        _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(entry => string.Equals(entry.Name, ConfigRegConfig.PlanIndexProperty, StringComparison.Ordinal))
            ?.Path;

    /// <summary>Why <paramref name="plan"/> is not an active plan, or null when it is.</summary>
    internal string? CheckActivePlan(string plan)
    {
        string? index = PlanIndexPath();
        if (index is null)
            return "The configuration registers no plan index, so no plan is active.";

        string folder = Path.GetDirectoryName(index)?.Replace('\\', '/') ?? string.Empty;
        if (!File.Exists(Absolute(plan)))
            return $"'{plan}' does not exist.";

        string name = Path.GetFileName(plan);
        bool directlyInFolder = string.Equals(Path.GetDirectoryName(plan)?.Replace('\\', '/') ?? string.Empty, folder,
            StringComparison.OrdinalIgnoreCase);
        if (!directlyInFolder || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(plan, index, StringComparison.OrdinalIgnoreCase))
            return $"'{plan}' is not a plan file directly under '{folder}'.";

        return DocsRootOf(plan).Length == 0 && _config.Ontology.DocsRoots.All(root => root != ".")
            ? $"'{plan}' is under no documentation root."
            : null;
    }

    /// <summary>The documentation root a path sits under; empty for the repository root itself.</summary>
    internal string DocsRootOf(string relative)
    {
        foreach (string root in _config.Ontology.DocsRoots.OrderByDescending(root => root.Length))
        {
            if (root == "." || relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return root == "." ? string.Empty : root;
        }

        return string.Empty;
    }

    internal string Absolute(string relative) =>
        Path.Combine(_repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Stages the paths a command touched, so a commit that only takes tracked files cannot leave a new one behind.</summary>
    internal void Stage(IEnumerable<string> paths, DiagnosticReport report, string plan, string stepCode)
    {
        if (!Directory.Exists(Path.Combine(_repoRoot, ".git")) && !File.Exists(Path.Combine(_repoRoot, ".git")))
            return;

        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in StageArguments.Concat(paths))
            startInfo.ArgumentList.Add(argument);

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
            if (result.ExitCode == 0)
            {
                report.Add(new Diagnostic(stepCode, Severity.Info, "Staged the moved and edited files.", plan, plan));
                return;
            }

            report.Add(new Diagnostic(stepCode, Severity.Warning, $"git add failed ({result.StandardError.Trim()}); stage the files before committing.", plan, plan));
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
        {
            report.Add(new Diagnostic(stepCode, Severity.Warning, $"git could not be run ({ex.Message}); stage the files before committing.", plan, plan));
        }
    }
}
