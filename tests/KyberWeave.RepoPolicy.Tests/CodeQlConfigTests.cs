using System.Globalization;
using System.Text.RegularExpressions;
using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// The CodeQL configuration leaves every query rule and path active — no path-scoped
/// exclusions (retired in ADR 0020) and no rule filters remain now that the live-gate
/// script has been retired alongside the TypeScript engine cutover.
/// </summary>
public sealed partial class CodeQlConfigTests
{
    private const string ConfigPath = ".github/codeql/codeql-config.yml";

    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void CodeQlConfigHasNoRuleFilters()
    {
        YamlMappingNode root = LoadRoot();

        IEnumerable<string?> keys = root.Children.Keys.Select(key => (key as YamlScalarNode)?.Value);
        Assert.DoesNotContain("query-filters", keys);
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
        string text = Corpus.ReadText(ConfigPath);

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
        string text = Corpus.ReadText(ConfigPath);
        YamlStream stream = new();
        using StringReader reader = new(text);
        stream.Load(reader);

        Assert.Single(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }
}
