using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Source scans of the Squad transaction's claim and publication path. They read the product source as their subject, so
/// they belong in the repository-policy project rather than beside the behaviour tests that exercise the same path in memory.
/// </summary>
public sealed class SquadTransactionClaimSourceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void ExecuteClaimPublicationUsesNoOverwrite()
    {
        string source = Corpus.ReadText("src/KyberWeave.Application/Squad/Deployment/SquadTransaction.cs");
        int applyStart = source.IndexOf("private void ApplyFiles", StringComparison.Ordinal);
        int restoreStart =
            source.IndexOf("private IReadOnlyList<string> RestoreIntent", StringComparison.Ordinal);
        Assert.True(applyStart >= 0 && restoreStart > applyStart);
        string liveApplySource = source[applyStart..restoreStart];

        Assert.Contains("ClaimExistingNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("PublishStageNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("VerifyAfterImage", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("overwrite: true", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteEntry(targetPath", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "ClaimExistingNoOverwrite"), StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "PublishStageNoOverwrite"), StringComparison.Ordinal);
    }

    private static string ExtractMethodDeclaration(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int declarationStart = source.LastIndexOf(
                "    private ",
                methodNameIndex,
                StringComparison.Ordinal);
            int bodyStart = source.IndexOf('{', methodNameIndex);
            Assert.True(declarationStart >= 0 && bodyStart > methodNameIndex);
            string declaration = source[declarationStart..bodyStart];
            if (declaration.Contains(methodName, StringComparison.Ordinal))
                return declaration;

            searchFrom = methodNameIndex + methodName.Length;
        }
    }

    private static string ExtractMethod(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int lineStart = source.LastIndexOf('\n', methodNameIndex) + 1;
            string declarationPrefix = source[lineStart..methodNameIndex];
            if (declarationPrefix.Contains("private", StringComparison.Ordinal))
            {
                int bodyStart = source.IndexOf('{', methodNameIndex);
                Assert.True(bodyStart >= 0);
                int depth = 0;
                for (int index = bodyStart; index < source.Length; index++)
                {
                    depth += source[index] switch
                    {
                        '{' => 1,
                        '}' => -1,
                        _ => 0
                    };
                    if (depth == 0)
                        return source[bodyStart..(index + 1)];
                }
            }

            searchFrom = methodNameIndex + methodName.Length;
        }
    }
}
