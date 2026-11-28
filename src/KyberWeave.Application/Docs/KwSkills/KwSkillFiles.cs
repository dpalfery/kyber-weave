using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>
/// What a KW skill list and a <c>SKILL.md</c> must hold, read one way for the catalog and for the
/// validator so the two never disagree.
/// </summary>
internal static class KwSkillFiles
{
    /// <summary>Reads a list as one YAML sequence of non-empty scalars and nothing else.</summary>
    /// <param name="text">The list file's text.</param>
    /// <param name="names">The trimmed names when the text is a sound list; empty otherwise.</param>
    /// <returns>True when the text is a sound list.</returns>
    public static bool TryParseList(string text, out List<string> names) =>
        TryParseList(text, out names, out _);

    /// <summary>Reads a list as one YAML sequence of non-empty scalars and nothing else.</summary>
    /// <param name="text">The list file's text.</param>
    /// <param name="names">The trimmed names when the text is a sound list; empty otherwise.</param>
    /// <param name="parseError">Why the YAML could not be read; null when the text parsed but is not a sound list's shape.</param>
    /// <returns>True when the text is a sound list.</returns>
    public static bool TryParseList(string text, out List<string> names, out string? parseError)
    {
        names = [];
        parseError = null;
        try
        {
            YamlStream stream = new();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 1
                && stream.Documents[0].RootNode is YamlSequenceNode sequence
                && sequence.Children.All(child => child is YamlScalarNode { Value: { } value } && value.Trim().Length > 0))
            {
                names = [.. sequence.Children.Cast<YamlScalarNode>().Select(node => (node.Value
                    ?? throw new InvalidOperationException("A sound YAML list scalar always has a value.")).Trim())];
                return true;
            }
        }
        catch (YamlException exception)
        {
            // Not a YAML document at all: the caller reports the shape, carrying this reason when there is one.
            parseError = exception.Message.ReplaceLineEndings(" ");
        }

        return false;
    }

    /// <summary>Parses a <c>SKILL.md</c> and finds which of <c>name</c> and <c>description</c> it lacks.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="file">The file's path, for the parser.</param>
    /// <param name="folder">The skill's folder.</param>
    /// <param name="skill">The parsed skill when its frontmatter could be read.</param>
    /// <param name="parseError">Why the frontmatter could not be read, otherwise null.</param>
    /// <param name="missing">The required fields the frontmatter lacks.</param>
    /// <returns>True when the skill parsed and declares every required field.</returns>
    public static bool TryInspect(
        string text,
        string file,
        string folder,
        [NotNullWhen(true)] out Skill? skill,
        out string? parseError,
        out List<string> missing)
    {
        missing = [];
        parseError = null;
        try
        {
            skill = SkillParser.Parse(text, file, folder, [], []);
        }
        catch (SkillParseException exception)
        {
            skill = null;
            parseError = exception.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(skill.Frontmatter.Name)) missing.Add("name");
        if (string.IsNullOrWhiteSpace(skill.Frontmatter.Description)) missing.Add("description");
        return missing.Count == 0;
    }
}
