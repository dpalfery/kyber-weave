using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Source scans of the Squad transaction's restore path. They read the product source as their subject, so they belong
/// in the repository-policy project rather than beside the behaviour tests that exercise the same path in memory.
/// </summary>
public sealed class SquadTransactionRestoreSourceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void RestoreIntentRestoreSourcesAreReverifiedAtAdjacentUseBoundary()
    {
        string transactionSource = Corpus.ReadText("src/KyberWeave.Application/Squad/Deployment/SquadTransaction.cs");
        string restoreIntent = ExtractMethod(transactionSource, "RestoreIntent");
        string restoreEntry = ExtractMethod(transactionSource, "RestoreEntry");

        Assert.Contains("VerifyRestore", restoreIntent, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", restoreEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreIntentUsesSingleVerifiedImmutablePayload()
    {
        string source = Corpus.ReadText("src/KyberWeave.Application/Squad/Deployment/SquadTransaction.cs");
        string capture = ExtractMethod(source, "CaptureVerifiedRestorePayload");
        string compare = ExtractMethod(source, "CompareAndRestoreEntry");
        string restore = ExtractMethod(source, "RestoreEntry");

        Assert.Contains("ReadOnlyMemory<byte>", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(compare, "CaptureVerifiedRestorePayload("));
        Assert.DoesNotContain("_files.ReadAll", compare, StringComparison.Ordinal);
        Assert.DoesNotContain("_files.ReadAll", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyRestoreArtifact", restore, StringComparison.Ordinal);
        Assert.Contains("payload", restore, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetEntryKind", capture, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(capture, "GetEntryKind") >= 2,
            "Restore payload capture must verify the source node kind before and after its single read.");
        Assert.Equal(1, CountOccurrences(capture, "_files.ReadAllBytes(backupPath)"));
        Assert.Equal(1, CountOccurrences(capture, "_files.ReadAllBytes(linkMetadataPath)"));
        Assert.Contains("WriteNewFile", restore, StringComparison.Ordinal);
        Assert.Contains("OpenWriteNew", ExtractMethod(source, "WriteNewFile"), StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create,", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create)", restore, StringComparison.Ordinal);
        Assert.Contains("CreateFileLink", restore, StringComparison.Ordinal);
        Assert.Contains("GetEntryKind(targetPath)", restore, StringComparison.Ordinal);
        int liveRecheck = restore.LastIndexOf(
            "GetEntryKind(targetPath)",
            StringComparison.Ordinal);
        int liveRemoval = restore.IndexOf(
            "DeleteEntry(targetPath",
            liveRecheck,
            StringComparison.Ordinal);
        Assert.True(
            liveRecheck >= 0 && liveRemoval > liveRecheck,
            "The live after-image must be revalidated immediately before removal.");
        string adjacentRemoval = restore[liveRecheck..liveRemoval];
        Assert.DoesNotContain("File.ReadAll", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("await", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIntent", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("await", restore, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
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
