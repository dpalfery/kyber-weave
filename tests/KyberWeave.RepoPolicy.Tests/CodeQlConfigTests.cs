using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// The CodeQL configuration leaves every query rule active. It excludes no path except the frozen
/// TypeScript archive under <c>dash/archived/</c>, which its own README forbids any build, runtime
/// or test target from importing or executing. ADR 0020 retired the vendored-subtree exclusions;
/// this one hides a snapshot of retired code, not live code.
/// </summary>
public sealed partial class CodeQlConfigTests
{
    private static readonly string ConfigPath = Path.Combine(
        KyberWeaveTestPaths.ToolRoot, ".github", "codeql", "codeql-config.yml");

    [Fact]
    public void CodeQlConfigHasNoRuleFilters()
    {
        YamlMappingNode root = LoadRoot();

        IEnumerable<string?> keys = root.Children.Keys.Select(key => (key as YamlScalarNode)?.Value);
        Assert.DoesNotContain("query-filters", keys);
    }

    [Fact]
    public void CodeQlConfigExcludesOnlyTheFrozenArchiveAndNeverByInclusion()
    {
        YamlMappingNode root = LoadRoot();

        IEnumerable<string?> keys = root.Children.Keys.Select(key => (key as YamlScalarNode)?.Value).ToList();
        Assert.DoesNotContain("paths", keys);

        YamlNode? ignoreNode = root.Children
            .Where(pair => string.Equals((pair.Key as YamlScalarNode)?.Value, "paths-ignore", StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .SingleOrDefault();
        if (ignoreNode is null)
            return;

        YamlSequenceNode ignored = Assert.IsType<YamlSequenceNode>(ignoreNode);
        Assert.Equal(
            ["dash/archived"],
            ignored.Children.Select(node => Assert.IsType<YamlScalarNode>(node).Value));
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
}
