namespace KyberWeave.Core.Skills.Model;

/// <summary>The kind of an on-disk resource bundled alongside a skill.</summary>
public enum SkillResourceKind
{
    Script,
    Reference,
    Asset,
    Other
}

/// <summary>A file bundled with a skill (under scripts/, references/, assets/, etc.).</summary>
public sealed record SkillResource(string RelativePath, string AbsolutePath, SkillResourceKind Kind);

/// <summary>A relative link discovered in the SKILL.md body (e.g. to scripts/run.py).</summary>
public sealed record SkillReferenceLink(string Target, bool Resolves);

/// <summary>
/// A fully parsed skill: its directory, front matter, instruction body, the relative
/// links its body points at, and the resource files discovered on disk.
/// </summary>
public sealed class Skill
{
    /// <summary>Absolute path to the SKILL.md file.</summary>
    public required string SkillFilePath { get; init; }

    /// <summary>Absolute path to the skill's directory.</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>The directory name (used to validate the name == folder rule).</summary>
    public string DirectoryName => System.IO.Path.GetFileName(DirectoryPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    /// <summary>Parsed front matter. Never null after a successful parse.</summary>
    public required SkillFrontmatter Frontmatter { get; init; }

    /// <summary>Raw YAML text of the front matter block (between the --- fences).</summary>
    public required string RawFrontmatter { get; init; }

    /// <summary>The Markdown instructions body (everything after the front matter).</summary>
    public required string InstructionsBody { get; init; }

    /// <summary>Relative links found in the body (markdown links + inline code paths).</summary>
    public IReadOnlyList<SkillReferenceLink> ReferenceLinks { get; init; } = Array.Empty<SkillReferenceLink>();

    /// <summary>Resource files discovered under the skill directory.</summary>
    public IReadOnlyList<SkillResource> Resources { get; init; } = Array.Empty<SkillResource>();

    /// <summary>Approximate token count of the body (chars / 4 heuristic).</summary>
    public int ApproximateBodyTokens => InstructionsBody.Length / 4;

    /// <summary>Line count of the body.</summary>
    public int BodyLineCount => InstructionsBody.Count(c => c == '\n') + 1;

    public IEnumerable<SkillResource> Scripts => Resources.Where(r => r.Kind == SkillResourceKind.Script);
}
