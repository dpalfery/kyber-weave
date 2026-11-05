using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// The CodeQL configuration clears one deliberate alert by rule id, never by path, and says
/// why. Pinning the parsed shape keeps that decision visible in review and stops it drifting
/// into the path-scoped exclusion ADR 0020 retired.
/// </summary>
public sealed partial class CodeQlConfigTests
{
    private static readonly string ConfigPath = Path.Combine(
        KyberWeaveTestPaths.ToolRoot, ".github", "codeql", "codeql-config.yml");

    [Fact]
    public void CodeQlConfigExcludesExactlyTheEvidenceWriteRule()
    {
        YamlMappingNode root = LoadRoot();

        YamlSequenceNode filters = Assert.IsType<YamlSequenceNode>(Require(root, "query-filters"));
        YamlMappingNode filter = Assert.IsType<YamlMappingNode>(Assert.Single(filters.Children));
        Assert.Single(filter.Children);
        YamlMappingNode exclude = Assert.IsType<YamlMappingNode>(Require(filter, "exclude"));

        Assert.Single(exclude.Children);
        Assert.Equal("js/http-to-file-access", Scalar(exclude, "id"));
    }

    [Fact]
    public void CodeQlConfigExcludesByRuleAndNeverByPath()
    {
        YamlMappingNode root = LoadRoot();

        IEnumerable<string?> keys = root.Children.Keys.Select(key => (key as YamlScalarNode)?.Value);
        Assert.DoesNotContain("paths", keys);
        Assert.DoesNotContain("paths-ignore", keys);
    }

    [Fact]
    public void CodeQlConfigRecordsOwnerAndReviewDate()
    {
        string text = File.ReadAllText(ConfigPath);

        Assert.Contains("Owner: dpalfery", text, StringComparison.Ordinal);

        Match reviewDate = ReviewDatePattern().Match(text);
        Assert.True(reviewDate.Success, $"'{ConfigPath}' records no 'Review date: yyyy-MM-dd'.");
        Assert.True(
            DateOnly.TryParseExact(
                reviewDate.Groups[1].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            $"Review date '{reviewDate.Groups[1].Value}' is not a real calendar date.");
    }

    [GeneratedRegex(@"Review date:\s*(\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex ReviewDatePattern();

    private static YamlMappingNode LoadRoot()
    {
        string text = File.ReadAllText(ConfigPath);
        YamlStream stream = new();
        using StringReader reader = new(text);
        stream.Load(reader);

        Assert.Single(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static YamlNode Require(YamlMappingNode mapping, string key)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode { Value: { } name } && string.Equals(name, key, StringComparison.Ordinal))
                return entry.Value;
        }

        throw new Xunit.Sdk.XunitException($"'{key}' is missing from {ConfigPath}.");
    }

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        Require(mapping, key) is YamlScalarNode scalar ? scalar.Value : null;
}
