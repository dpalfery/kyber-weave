using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using Spectre.Console;

namespace KyberWeave.Cli.Commands;

public static class CommandHelpers
{
    /// <summary>
    /// Loads skills from a path. Adds any parse failures to <paramref name="report"/> as
    /// errors and returns the successfully parsed set. Returns null only when the path
    /// itself is invalid (nothing to do).
    /// </summary>
    public static SkillSet LoadOrReport(string path, DiagnosticReport report)
    {
        IReadOnlyList<SkillLoadResult> results = SkillLoader.Load(path);
        List<Skill> skills = new List<Skill>();

        foreach (SkillLoadResult r in results)
        {
            if (r.Success)
            {
                skills.Add(r.Skill!);
            }
            else
            {
                report.Add(new Diagnostic("KW-PARSE-000", Severity.Error,
                    r.Error ?? "Failed to parse skill.", Path.GetFileName(Path.GetDirectoryName(r.Path) ?? r.Path), r.Path));
            }
        }

        // If nothing parsed and the only result is a "path not found / none found" message, treat as fatal-but-reported.
        if (skills.Count == 0 && results.All(r => !r.Success))
        {
            return new SkillSet(skills); // report already carries the errors; caller decides exit code
        }

        return new SkillSet(skills);
    }

    /// <summary>
    /// Loads <c>.kyber-weave/kyber-weave.yml</c> for CLI commands. On failure, adds
    /// <see cref="KyberWeaveConfigLoader.ConfigLoadErrorCode"/> and returns false.
    /// </summary>
    public static bool TryLoadConfig(
        string repoRoot,
        string? configPath,
        DiagnosticReport report,
        out KyberWeaveConfig config)
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(repoRoot, configPath);
        if (!result.Success)
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                result.Error ?? "Failed to load kyber-weave.yml.",
                "kyber-weave.yml",
                result.ConfigPath));
            config = KyberWeaveConfig.ProductDefaults;
            return false;
        }

        config = result.Config!;
        return true;
    }

    public static void Finish(DiagnosticReport report, AnalysisSettings settings, string command, string subjectLabel)
    {
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

        ReportRenderer.Render(report, settings.ParsedFormat, command, subjectLabel);
        if (settings.ParsedFormat == OutputFormat.Table)
        {
            AnsiConsole.WriteLine();
            ReportRenderer.RenderSummary(report);
        }
    }

    /// <summary>
    /// Writes a report document to <paramref name="path"/>. A write failure is recorded as a
    /// diagnostic rather than thrown: the analysis already ran, and losing its findings to a
    /// filesystem error is a worse outcome than losing the file.
    /// </summary>
    public static void TryWriteReport(string? path, string content, string code, DiagnosticReport report)
    {
        if (path is null)
            return;

        string full = Path.GetFullPath(path);
        try
        {
            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(full, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                code,
                Severity.Error,
                $"Could not write the report to '{full}': {ex.Message}",
                "review",
                full,
                Hint: "Check that the directory is writable, or drop --out."));
        }
    }
}
