using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Authoring;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Routing;
using KyberWeave.Core.Skills.Security;
using KyberWeave.Core.Skills.Validation;

namespace KyberWeave.Application.Skills;

/// <summary>Runs the skill governance library over a path and writes what the authoring commands produce.</summary>
public sealed class SkillGovernanceService(IWritableFileSystem files, IPathResolver paths, ISkillArchiveWriter archive) : ISkillGovernanceService
{
    private const string ParseErrorCode = "KW-PARSE-000";

    private readonly SkillFileReader _reader = new(files, paths);

    /// <inheritdoc />
    public DiagnosticReport Validate(string path)
    {
        DiagnosticReport report = new();
        SkillSet set = LoadOrReport(path, report);

        foreach (Skill skill in set.Skills)
            report.AddRange(SpecValidator.Validate(skill, files.DirectoryExists(skill.DirectoryPath)));

        return report;
    }

    /// <inheritdoc />
    public SkillLintResult Lint(string path, int minDescriptionScore)
    {
        DiagnosticReport report = new();
        SkillSet set = LoadOrReport(path, report);

        RoutingLinter linter = new() { MinDescriptionScore = minDescriptionScore };

        foreach (Skill skill in set.Skills)
            report.AddRange(linter.LintSkill(skill));
        report.AddRange(linter.LintSet(set));

        return new SkillLintResult(report, set);
    }

    /// <inheritdoc />
    public DiagnosticReport Scan(string path)
    {
        DiagnosticReport report = new();
        SkillSet set = LoadOrReport(path, report);

        SkillScanner scanner = new();
        foreach (Skill skill in set.Skills)
            report.AddRange(scanner.Scan(skill, ReadScript));

        return report;
    }

    /// <inheritdoc />
    public SkillSet LoadSet(string path) => _reader.LoadSet(path);

    /// <inheritdoc />
    public RoutingEvalSummary? Evaluate(string evalFile, IRoutingStrategy strategy, SkillSet skills)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(skills);

        if (!files.FileExists(evalFile))
            return null;

        RoutingEvalFile loaded = RoutingEvalFileParser.Parse(files.ReadAllText(evalFile));
        return new RoutingEvaluator(strategy).Evaluate(loaded, skills);
    }

    /// <inheritdoc />
    public SkillPackResult Pack(string skillDirectory, string? output, bool skipValidation)
    {
        DiagnosticReport report = new();
        string skillFile = Path.Combine(skillDirectory, "SKILL.md");
        if (!files.FileExists(skillFile))
            return new SkillPackResult(SkillPackStatus.NoSkillFile, string.Empty, string.Empty, 0, report);

        Skill skill = _reader.ReadFile(skillFile);

        if (!skipValidation)
        {
            report.AddRange(SpecValidator.Validate(skill, files.DirectoryExists(skill.DirectoryPath)));
            if (report.HasErrors)
                return new SkillPackResult(SkillPackStatus.ValidationFailed, string.Empty, string.Empty, 0, report);
        }

        string name = skill.Frontmatter.Name ?? skill.DirectoryName;
        string target = output ?? $"{name}.zip";
        if (files.FileExists(target))
        {
            files.DeleteFile(target);
        }

        // Bundle the SKILL.md at the archive root plus scripts/, references/, assets/ — the
        // shape Copilot Studio accepts for an uploaded Skill .zip.
        List<SkillArchiveEntry> entries = [new SkillArchiveEntry(skill.SkillFilePath, "SKILL.md")];
        entries.AddRange(skill.Resources.Select(res => new SkillArchiveEntry(res.AbsolutePath, res.RelativePath)));
        archive.Write(target, entries);

        return new SkillPackResult(SkillPackStatus.Packed, name, target, skill.Resources.Count, report);
    }

    /// <inheritdoc />
    public SkillScaffoldResult Scaffold(string name, string outputDirectory, string templateName, bool includeLicense, bool includeMetadata)
    {
        if (!SkillScaffold.IsValidName(name))
            return new SkillScaffoldResult(SkillScaffoldStatus.InvalidName, string.Empty);

        string dir = Path.Combine(outputDirectory, name);
        if (files.DirectoryExists(dir))
            return new SkillScaffoldResult(SkillScaffoldStatus.AlreadyExists, dir);

        files.CreateDirectory(dir);
        files.CreateDirectory(Path.Combine(dir, "references"));

        string body = SkillScaffold.Template(templateName, name, includeLicense, includeMetadata);
        files.WriteAllText(Path.Combine(dir, "SKILL.md"), body);

        return new SkillScaffoldResult(SkillScaffoldStatus.Created, dir);
    }

    /// <summary>Loads the skills under a path, reporting every one that fails to parse as an error and keeping the rest.</summary>
    private SkillSet LoadOrReport(string path, DiagnosticReport report)
    {
        IReadOnlyList<SkillLoadResult> results = _reader.Load(path);
        List<Skill> skills = [];

        foreach (SkillLoadResult r in results)
        {
            if (r.Success)
            {
                skills.Add(r.Skill!);
            }
            else
            {
                report.Add(new Diagnostic(ParseErrorCode, Severity.Error,
                    r.Error ?? "Failed to parse skill.", Path.GetFileName(Path.GetDirectoryName(r.Path) ?? r.Path), r.Path));
            }
        }

        return new SkillSet(skills);
    }

    /// <summary>Reads one bundled script, treating an unreadable file as nothing to scan.</summary>
    private string? ReadScript(SkillResource script)
    {
        try
        {
            return files.ReadAllText(script.AbsolutePath);
        }
        catch
        {
            return null;
        }
    }
}
