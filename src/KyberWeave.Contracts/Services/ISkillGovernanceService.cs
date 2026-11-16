using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Routing;

namespace KyberWeave.Contracts.Services;

/// <summary>How a <c>skill pack</c> run ended.</summary>
public enum SkillPackStatus
{
    /// <summary>The archive was written.</summary>
    Packed,

    /// <summary>The directory holds no SKILL.md.</summary>
    NoSkillFile,

    /// <summary>Spec validation failed and the pack was refused.</summary>
    ValidationFailed
}

/// <summary>The outcome of packing one skill.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Name">The skill's name, when it was parsed.</param>
/// <param name="Output">The archive path, when it was written.</param>
/// <param name="BundledFiles">How many resource files the archive carries besides SKILL.md.</param>
/// <param name="Report">The validation findings; empty unless validation ran.</param>
public sealed record SkillPackResult(SkillPackStatus Status, string Name, string Output, int BundledFiles, DiagnosticReport Report);

/// <summary>How a <c>skill new</c> run ended.</summary>
public enum SkillScaffoldStatus
{
    /// <summary>The skill folder was created.</summary>
    Created,

    /// <summary>The name is not lowercase letters, digits and single hyphens.</summary>
    InvalidName,

    /// <summary>A folder of that name already exists.</summary>
    AlreadyExists
}

/// <summary>The outcome of scaffolding one skill.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Directory">The skill folder, resolved against the output directory.</param>
public sealed record SkillScaffoldResult(SkillScaffoldStatus Status, string Directory);

/// <summary>The findings of a lint run together with the skills it read.</summary>
/// <param name="Report">Every lint finding.</param>
/// <param name="Skills">The skills that parsed.</param>
public sealed record SkillLintResult(DiagnosticReport Report, SkillSet Skills);

/// <summary>What the <c>skill</c> commands ask of the skill governance library: validate, lint, scan, pack and scaffold.</summary>
public interface ISkillGovernanceService
{
    /// <summary>Checks every skill under <paramref name="path"/> against the Agent Skills spec.</summary>
    /// <param name="path">A skill folder or a tree of them.</param>
    DiagnosticReport Validate(string path);

    /// <summary>Lints every skill under <paramref name="path"/> and the set as a whole for routing quality.</summary>
    /// <param name="path">A skill folder or a tree of them.</param>
    /// <param name="minDescriptionScore">The routing score a description must reach.</param>
    SkillLintResult Lint(string path, int minDescriptionScore);

    /// <summary>Scans every skill under <paramref name="path"/> as a trust surface.</summary>
    /// <param name="path">A skill folder or a tree of them.</param>
    DiagnosticReport Scan(string path);

    /// <summary>Loads the skills under <paramref name="path"/> that parse, ignoring the rest.</summary>
    /// <param name="path">A skill folder or a tree of them.</param>
    SkillSet LoadSet(string path);

    /// <summary>Runs a routing eval file against <paramref name="skills"/>, or returns null when the file does not exist.</summary>
    /// <param name="evalFile">The YAML eval file.</param>
    /// <param name="strategy">The routing strategy under test.</param>
    /// <param name="skills">The skills routed over.</param>
    RoutingEvalSummary? Evaluate(string evalFile, IRoutingStrategy strategy, SkillSet skills);

    /// <summary>Bundles one skill into a .zip.</summary>
    /// <param name="skillDirectory">The folder holding SKILL.md.</param>
    /// <param name="output">The archive path, or null for <c>&lt;name&gt;.zip</c> in the current directory.</param>
    /// <param name="skipValidation">Pack even when spec validation fails.</param>
    SkillPackResult Pack(string skillDirectory, string? output, bool skipValidation);

    /// <summary>Creates a spec-correct skill folder from a template.</summary>
    /// <param name="name">The skill name; also the folder name.</param>
    /// <param name="outputDirectory">The folder the skill folder is created in.</param>
    /// <param name="templateName">The template name.</param>
    /// <param name="includeLicense">Whether the license field is written.</param>
    /// <param name="includeMetadata">Whether the metadata block is written.</param>
    SkillScaffoldResult Scaffold(string name, string outputDirectory, string templateName, bool includeLicense, bool includeMetadata);
}
