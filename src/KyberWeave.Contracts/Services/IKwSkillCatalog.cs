using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>Where a KW skill was found.</summary>
public enum KwSkillSource
{
    /// <summary>The repository's own <c>standards/skills</c> folder.</summary>
    Repository,

    /// <summary>The pool embedded in the product.</summary>
    Shipped
}

/// <summary>One skill a scope lists: its name, what it is for, and which pool supplied it.</summary>
/// <param name="Name">The skill's name, which is its folder name.</param>
/// <param name="Description">The <c>description</c> from the skill's frontmatter.</param>
/// <param name="Source">The pool the skill resolved from.</param>
public sealed record KwSkillEntry(string Name, string Description, KwSkillSource Source);

/// <summary>A problem found while listing: a file that is not a list of names, or a name that is not a skill.</summary>
/// <param name="Path">The file the problem is in.</param>
/// <param name="Message">What is wrong, naming the rule id and the offending value.</param>
public sealed record KwSkillProblem(string Path, string Message);

/// <summary>The skills a technology and role agent get, in order, and what was wrong while listing them.</summary>
/// <param name="Entries">Each skill once, parent technology first, then the technology, then the role.</param>
/// <param name="Problems">Everything that made a list or an entry unusable. Empty when the lists are sound.</param>
public sealed record KwSkillList(IReadOnlyList<KwSkillEntry> Entries, IReadOnlyList<KwSkillProblem> Problems);

/// <summary>How a load ended.</summary>
public enum KwSkillLoadOutcome
{
    /// <summary>The skill was found and read.</summary>
    Found,

    /// <summary>No skill has that name.</summary>
    Unknown,

    /// <summary>The skill exists but its frontmatter lacks a required field.</summary>
    Malformed
}

/// <summary>The result of loading one skill by name.</summary>
/// <param name="Outcome">How the load ended; decides which other members carry a value.</param>
/// <param name="Name">The trimmed name asked for.</param>
/// <param name="Body">Found: the <c>SKILL.md</c> text below its frontmatter.</param>
/// <param name="Folder">Found: the absolute folder, or <c>(not materialized: reason)</c> when a shipped skill could not be written. Malformed: the folder the skill was read from.</param>
/// <param name="Source">Found: the pool the skill resolved from.</param>
/// <param name="References">Found: the absolute path of every other file in the folder, in ordinal order. Never their contents.</param>
/// <param name="ExistingNames">Unknown: every name that does exist.</param>
/// <param name="MissingFields">Malformed: the frontmatter fields that are missing, or the reason the frontmatter cannot be read.</param>
public sealed record KwSkillLoadResult(
    KwSkillLoadOutcome Outcome,
    string Name,
    string Body,
    string Folder,
    KwSkillSource? Source,
    IReadOnlyList<string> References,
    IReadOnlyList<string> ExistingNames,
    IReadOnlyList<string> MissingFields)
{
    /// <summary>Creates a found result.</summary>
    public static KwSkillLoadResult Found(
        string name, string body, string folder, KwSkillSource source, IReadOnlyList<string> references) =>
        new(KwSkillLoadOutcome.Found, name, body, folder, source, references, [], []);

    /// <summary>Creates an unknown-name result.</summary>
    public static KwSkillLoadResult Unknown(string name, IReadOnlyList<string> existingNames) =>
        new(KwSkillLoadOutcome.Unknown, name, string.Empty, string.Empty, null, [], existingNames, []);

    /// <summary>Creates a malformed-skill result.</summary>
    public static KwSkillLoadResult Malformed(string name, string folder, IReadOnlyList<string> missing) =>
        new(KwSkillLoadOutcome.Malformed, name, string.Empty, folder, null, [], [], missing);
}

/// <summary>
/// Answers which KW skills a technology and a role agent get, and loads one by name. The repository's
/// pool wins a name over the shipped one.
/// </summary>
/// <remarks>
/// A name arrives from a caller, so the catalog checks its spelling and that it is a known skill before
/// it builds any path from it.
/// </remarks>
public interface IKwSkillCatalog
{
    /// <summary>Lists the skills for a technology and a role agent.</summary>
    /// <param name="technology">The technology, or null for none. Its parent (<c>builds-on</c>) is listed first.</param>
    /// <param name="roleAgent">The agent name, or null for none; mapped to a role folder.</param>
    KwSkillList SkillsFor(string? technology, string? roleAgent);

    /// <summary>Loads one skill.</summary>
    /// <param name="name">The skill name; trimmed before use.</param>
    KwSkillLoadResult Load(string name);

    /// <summary>Gets every skill name in either pool, in ordinal order.</summary>
    IReadOnlyList<string> Names();
}

/// <summary>Builds the catalog for one checkout, like the task prompt service's factory.</summary>
public interface IKwSkillCatalogFactory
{
    /// <summary>Builds the catalog over <paramref name="repositoryRoot"/>'s documentation roots.</summary>
    /// <param name="repositoryRoot">The checkout whose skills the catalog reads.</param>
    /// <param name="config">The effective host configuration, which places the docs roots and declares the roles.</param>
    IKwSkillCatalog ForRepository(string repositoryRoot, KyberWeaveConfig config);
}
