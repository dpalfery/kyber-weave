using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.KwSkills;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Text;
using KyberWeave.Domain.FileSystem;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// The KW-DOC-SKILL rules: what a scope's <c>&lt;scope&gt;.skills.yml</c> list holds, what a repository
/// pool <c>SKILL.md</c> declares, the legacy <c>.skills.md</c> warning, and the reserved pool name.
/// </summary>
/// <remarks>
/// <para>
/// The pool folder is <c>standards/skills</c>, one folder per skill. It is read with links left
/// unfollowed, like every other folder the validator reads, so a link cannot send a check outside the
/// checkout.
/// </para>
/// <para>
/// A list name is checked against <see cref="IKwSkillCatalog.Names"/>, which holds both the repository
/// pool and the shipped pool. Without a catalog the name check is skipped rather than guessed, because
/// a shipped name reported as missing would be a false error.
/// </para>
/// </remarks>
internal sealed class KwSkillRulesValidator(string repoRoot, IFileSystem files, IKwSkillCatalog? catalog)
{
    private const string SkillFile = "SKILL.md";

    /// <summary>Checks one scope's skills list: it is a YAML list of names, and each names a skill that exists.</summary>
    /// <param name="relativeFile">The list file, repository-relative.</param>
    /// <param name="scope">The technology or role whose folder holds it.</param>
    /// <param name="report">Receives the findings.</param>
    public void CheckList(string relativeFile, string scope, DiagnosticReport report)
    {
        string text;
        try
        {
            text = files.ReadAllText(Combine(relativeFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                DocSpecValidator.SkillListMalformed, Severity.Error,
                $"'{relativeFile}' could not be read: {exception.Message}",
                scope, relativeFile,
                "Make the skills list readable, then re-run docs validate."));
            return;
        }

        if (!KwSkillFiles.TryParseList(text, out List<string> names))
        {
            report.Add(new Diagnostic(
                DocSpecValidator.SkillListMalformed, Severity.Error,
                $"'{relativeFile}' is not a YAML list of skill names.",
                scope, relativeFile,
                "Write one skill name per line as '- <name>', or '[]' when the scope lists no skills."));
            return;
        }

        if (catalog is null) return;

        IReadOnlyList<string> known = catalog.Names();
        foreach (string name in names)
        {
            if (KwSkillNames.IsValid(name) && known.Contains(name, StringComparer.Ordinal)) continue;

            string hint = StringDistance.Nearest(name, known) is { } near
                ? $"Closest skill: '{near}'."
                : "Add the skill under standards/skills, or remove the name from the list.";
            report.Add(new Diagnostic(
                DocSpecValidator.SkillListUnknownName, Severity.Error,
                $"'{name}' is listed in '{relativeFile}' but is not a skill in the repository or the shipped pool.",
                scope, relativeFile, hint));
        }
    }

    /// <summary>
    /// Checks each skill folder of a repository pool: its <c>SKILL.md</c> names and describes it, and its name
    /// is its folder. A folder holding no <c>SKILL.md</c>, or holding one behind a symbolic link, is an error;
    /// the link is never followed.
    /// </summary>
    /// <param name="relativePool">The pool folder, repository-relative.</param>
    /// <param name="report">Receives the findings.</param>
    public void CheckPool(string relativePool, DiagnosticReport report)
    {
        string absolutePool = Combine(relativePool);
        if (!files.DirectoryExists(absolutePool)) return;

        try
        {
            if (files.GetLinkTarget(absolutePool) is not null) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                DocSpecValidator.SkillPoolMalformedFolder, Severity.Error,
                $"the skill pool '{relativePool}' could not be checked for symbolic links: {exception.Message}",
                relativePool, relativePool,
                "Make the pool folder readable, then re-run docs validate."));
            return;
        }

        foreach (FileSystemEntry entry in files.EnumerateEntries(absolutePool))
        {
            if (!entry.IsDirectory || entry.IsLink) continue;

            string folderName = Path.GetFileName(entry.Path);
            string relativeSkill = $"{relativePool}/{folderName}/{SkillFile}";
            string absoluteSkill = Combine(relativeSkill);

            bool linked;
            try
            {
                // The link is asked about before existence, so a dangling link reports as a link
                // rather than as a missing file, and a linked file is never read.
                linked = files.GetLinkTarget(absoluteSkill) is not null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report.Add(new Diagnostic(
                    DocSpecValidator.SkillPoolMalformedFolder, Severity.Error,
                    $"the skill folder '{folderName}' could not be checked for a symbolic link: {exception.Message}",
                    folderName, relativeSkill,
                    "Make the skill folder readable, then re-run docs validate."));
                continue;
            }

            if (linked)
            {
                report.Add(new Diagnostic(
                    DocSpecValidator.SkillPoolMalformedFolder, Severity.Error,
                    $"the skill folder '{folderName}' holds its '{SkillFile}' behind a symbolic link, which is not followed.",
                    folderName, relativeSkill,
                    $"Replace the link with a real '{SkillFile}' file."));
                continue;
            }

            if (!files.FileExists(absoluteSkill))
            {
                report.Add(new Diagnostic(
                    DocSpecValidator.SkillPoolMalformedFolder, Severity.Error,
                    $"the skill folder '{folderName}' holds no '{SkillFile}'.",
                    folderName, relativeSkill,
                    $"Add a '{SkillFile}' with a 'name:' line equal to the folder name and a 'description:' line that says when to use the skill."));
                continue;
            }

            CheckSkill(folderName, relativeSkill, absoluteSkill, entry.Path, report);
        }
    }

    /// <summary>Reports a legacy prose <c>&lt;scope&gt;.skills.md</c> that still sits beside the list.</summary>
    /// <param name="relativeFile">The legacy file, repository-relative.</param>
    /// <param name="scope">The technology or role whose folder holds it.</param>
    /// <param name="report">Receives the finding.</param>
    public static void ReportLegacySkillsFile(string relativeFile, string scope, DiagnosticReport report) =>
        report.Add(new Diagnostic(
            DocSpecValidator.LegacySkillsFile, Severity.Warning,
            $"the '{scope}' folder still holds the legacy prose file '{relativeFile}'.",
            scope, relativeFile,
            "Move any guidance still needed into a KW skill under standards/skills, then delete this file."));

    /// <summary>Reports a technology or role named <c>skills</c>, which is the pool folder's name.</summary>
    /// <param name="scope">The declared name.</param>
    /// <param name="relativeFolder">The folder the name would own, repository-relative.</param>
    /// <param name="report">Receives the finding.</param>
    public static void ReportReservedScope(string scope, string relativeFolder, DiagnosticReport report) =>
        report.Add(new Diagnostic(
            DocSpecValidator.ReservedSkillsScope, Severity.Error,
            $"'{scope}' is declared as a technology or role, but that name is the KW skill pool folder.",
            scope, relativeFolder,
            "Rename the declared technology or role in .kyber-weave/kyber-weave.yml, and move its folder."));

    private void CheckSkill(
        string folderName,
        string relativeSkill,
        string absoluteSkill,
        string folder,
        DiagnosticReport report)
    {
        string text;
        try
        {
            text = files.ReadAllText(absoluteSkill);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                DocSpecValidator.SkillPoolFrontmatter, Severity.Error,
                $"the skill '{folderName}' could not be read: {exception.Message}",
                folderName, relativeSkill,
                "Make the file readable, then re-run docs validate."));
            return;
        }

        if (!KwSkillFiles.TryInspect(text, absoluteSkill, folder, out Skill? skill, out string? parseError, out List<string> missing))
        {
            if (parseError is not null)
            {
                report.Add(new Diagnostic(
                    DocSpecValidator.SkillPoolFrontmatter, Severity.Error,
                    $"the skill '{folderName}' has no readable frontmatter: {parseError}",
                    folderName, relativeSkill,
                    "Open the file with a frontmatter block that declares 'name' and 'description'."));
                return;
            }

            report.Add(new Diagnostic(
                DocSpecValidator.SkillPoolFrontmatter, Severity.Error,
                $"the skill '{folderName}' declares no {string.Join(" and no ", missing)}.",
                folderName, relativeSkill,
                "Add a 'name:' line equal to the folder name and a 'description:' line that says when to use the skill."));
            return;
        }

        string declared = (skill.Frontmatter.Name
            ?? throw new InvalidOperationException("A skill that passed the frontmatter check declares a name."))
            .Trim();
        if (!string.Equals(declared, folderName, StringComparison.Ordinal))
        {
            report.Add(new Diagnostic(
                DocSpecValidator.SkillPoolNameMismatch, Severity.Error,
                $"the skill's name '{declared}' differs from its folder '{folderName}'.",
                folderName, relativeSkill,
                $"Set 'name: {folderName}'; a skill is looked up by its folder name."));
        }
    }

    private string Combine(string relativePath) =>
        Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
