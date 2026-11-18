using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The CLI's command registration, read from the checkout's own source file: the analyze, review and glossary
/// commands are registered where the product registers them.
/// </summary>
public sealed class DocsAnalysisCommandRegistrationTests
{
    [Fact]
    public void ProgramRegistersAnalyzeReviewAndGlossaryCommands()
    {
        string program = CheckoutCorpusSource.Checkout.ReadText("src/KyberWeave.Cli/CliApplication.cs");

        Assert.Contains("AddCommand<DocsIntegrityCheckCommand>(\"integrity-check\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsExportGraphCommand>(\"export-graph\")", program, StringComparison.Ordinal);
        Assert.Contains("AddBranch(\"review\"", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsReviewExportCommand>(\"export\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsReviewImportCommand>(\"import\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsGlossaryCommand>(\"glossary\")", program, StringComparison.Ordinal);
    }
}
