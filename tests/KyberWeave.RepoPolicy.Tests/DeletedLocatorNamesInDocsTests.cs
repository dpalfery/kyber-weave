using System.Text.RegularExpressions;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Current documentation names the type that does the work today, not the service locator deleted with the
/// interface-driven unit-test work.
/// </summary>
/// <remarks>
/// A reader who follows a deleted name finds no code, and a fixer sent to it edits the wrong file. The
/// surviving name is the one the containers build.
/// </remarks>
public sealed class DeletedLocatorNamesInDocsTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>The document never names the deleted locator on its own; the surviving type carries the name.</summary>
    [Theory]
    [InlineData("docs/code-review/architecture.md", "GateCatalogue", "GateCatalogueRenderer")]
    public void TheDocumentNamesTheSurvivingType(string document, string deleted, string surviving)
    {
        string text = Corpus.ReadText(document);

        Assert.False(
            Regex.IsMatch(text, $@"\b{deleted}\b", RegexOptions.CultureInvariant),
            $"{document} still names '{deleted}', which no longer exists. Name '{surviving}', the type the " +
            "container builds, so a reader looking for the owner finds it.");
    }
}
