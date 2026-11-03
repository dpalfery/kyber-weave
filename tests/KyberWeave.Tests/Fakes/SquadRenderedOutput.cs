using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Reads rendered Squad output back for assertions. Shared by the harness contract suites, which
/// each used to carry an identical private copy.
/// </summary>
internal static class SquadRenderedOutput
{
    /// <summary>Every identity a fallback profile declares as shared between an agent and a skill.</summary>
    internal static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The shared identities whose canonical skill a native renderer suppresses because the agent
    /// file already carries the same body.
    /// </summary>
    internal static HashSet<string> SuppressedSkillIdentities(SquadSource source) =>
        SquadSharedIdentityProjection.SuppressedSkillIdentities(source, SharedIdentities(source));

    /// <summary>Splits an LF-normalized <c>---</c>-fenced YAML frontmatter block from its body.</summary>
    internal static (YamlMappingNode Frontmatter, string Body) SplitFrontmatter(string text, string identity)
    {
        const string delimiter = "---\n";
        Assert.True(
            text.StartsWith(delimiter, StringComparison.Ordinal),
            $"'{identity}' is missing the opening frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{identity}' is missing a closing '---' frontmatter delimiter.");

        string yaml = text[delimiter.Length..(end + 1)];
        string body = text[(end + 5)..];

        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        Assert.True(
            stream.Documents.Count == 1,
            $"'{identity}' frontmatter holds {stream.Documents.Count} YAML documents; expected exactly one.");
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
        return (root, body);
    }

    /// <summary>Reads a required scalar, naming the keys that are present when it is missing.</summary>
    internal static string RequireScalar(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlScalarNode>(value).Value
               ?? throw new InvalidOperationException($"'{identity}' key '{key}' has a null scalar value.");
    }
}
