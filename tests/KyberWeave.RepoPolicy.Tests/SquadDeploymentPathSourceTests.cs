using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Source scans of the Squad deployment's path handling. They read the product source as their subject, so they belong
/// in the repository-policy project rather than beside the behaviour tests that exercise the same path rules in memory.
/// </summary>
public sealed class SquadDeploymentPathSourceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void ResolvePhysicalRootComparisonPolicyIsNotOperatingSystemWideCaseFolding()
    {
        string identitySource = Corpus.ReadText(
            "src/KyberWeave.Application/Squad/Deployment/SquadPhysicalRootIdentities.cs");
        string transactionSource = Corpus.ReadText(
            "src/KyberWeave.Application/Squad/Deployment/SquadTransaction.cs");

        Assert.DoesNotContain("physicalPath.ToUpperInvariant()", identitySource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
            transactionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AllContainmentHelpersUseCanonicalFileSystemSemantics()
    {
        string[] deploymentSources =
        [
            .. Corpus.EnumerateFiles("src", "*.cs")
                .Where(path => path.Contains("Squad", StringComparison.Ordinal)
                               && path.Split('/').Contains("Deployment", StringComparer.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
        const string semanticsPath = "src/KyberWeave.Data/Squad/Deployment/SquadFileSystemPathSemantics.cs";

        Assert.True(
            Corpus.Exists(semanticsPath),
            "Containment must be centralized in SquadFileSystemPathSemantics.");

        List<string> offenders = new();
        foreach (string sourcePath in deploymentSources)
        {
            if (string.Equals(sourcePath, semanticsPath, StringComparison.Ordinal))
                continue;

            string source = Corpus.ReadText(sourcePath);
            if (source.Contains("private static StringComparison PathComparison", StringComparison.Ordinal) ||
                source.Contains("private static bool IsWithin(", StringComparison.Ordinal) ||
                source.Contains("private static bool PathsEqual(", StringComparison.Ordinal) ||
                source.Contains(
                    "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
                    StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(sourcePath));
        }

        Assert.True(
            offenders.Count == 0,
            "Deployment containment helpers must delegate to " +
            $"SquadFileSystemPathSemantics: {string.Join(", ", offenders)}");
    }
}
