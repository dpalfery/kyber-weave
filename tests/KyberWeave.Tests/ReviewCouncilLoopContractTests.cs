using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Keeps the retired <c>review verdict</c> command form out of every product file and the root
/// <c>AGENTS.md</c>. The review council loop's rules are checked by the prose rule catalog,
/// <c>Fixtures/prose-rules/catalog.yml</c>.
/// </summary>
public sealed class ReviewCouncilLoopContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Every file under the product root, and the root <c>AGENTS.md</c>.
    /// </summary>
    private static IEnumerable<string> ScannedFiles()
    {
        foreach (string file in Directory.EnumerateFiles(ProductRoot, "*", SearchOption.AllDirectories))
        {
            yield return file;
        }

        yield return Path.Combine(KyberWeaveTestPaths.ToolRoot, "AGENTS.md");
    }

    [Theory]
    [InlineData("kyber-weave review verdict . --findings")]
    public void RetiredPhraseIsAbsentFromEveryProductFileAndTheRootAgentsFile(string retiredPhrase)
    {
        List<string> files = ScannedFiles().ToList();
        Assert.NotEmpty(files);

        string[] offenders = files
            .Where(file => File.ReadAllText(file).Contains(retiredPhrase, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(KyberWeaveTestPaths.ToolRoot, file))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Retired phrase '{retiredPhrase}' is still present in: {string.Join(", ", offenders)}");
    }
}
