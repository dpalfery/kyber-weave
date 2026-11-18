using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review.test-map</c> examples the docs show must key a path this repository has.
/// </summary>
/// <remarks>
/// An operator copies an example verbatim. A key naming a file that has moved never matches a changed
/// path, so <c>{tests}</c> resolves to nothing and the gate skips with <c>KW-REVIEW-026</c> — a silent
/// pass rather than a test run.
/// </remarks>
public sealed class DocumentedTestMapExamplesTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>Each document that shows a <c>test-map</c> example keys a source file that exists.</summary>
    [Theory]
    [InlineData("docs/configuration.md")]
    [InlineData("docs/code-review/architecture.md")]
    public void EveryDocumentedTestMapKeyNamesAnExistingSourceFile(string document)
    {
        string[] keys = TestMapKeys(Corpus.ReadText(document));

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.True(
            File.Exists(Path.Combine(Corpus.Root, key)),
            $"{document} maps '{key}', which is not a file in this repository. A host copying the example " +
            "would map a path no change matches, and its tests gate would skip instead of running."));
    }

    private static string[] TestMapKeys(string markdown)
    {
        List<string> keys = [];
        string[] lines = markdown.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("```yaml", StringComparison.Ordinal))
                continue;

            int end = Array.FindIndex(lines, i + 1, line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));
            if (end < 0)
                continue;

            YamlStream stream = new();
            using (StringReader reader = new(string.Join('\n', lines, i + 1, end - i - 1)))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 0
                || stream.Documents[0].RootNode is not YamlMappingNode root
                || Child(root, "review") is not YamlMappingNode review
                || Child(review, "test-map") is not YamlMappingNode testMap)
            {
                i = end;
                continue;
            }

            keys.AddRange(testMap.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value!).Where(key => key.Length > 0));
            i = end;
        }

        return [.. keys];
    }

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) ? value : null;
}
