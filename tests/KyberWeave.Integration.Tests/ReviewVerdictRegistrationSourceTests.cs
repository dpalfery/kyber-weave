using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Review;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies, from the checked-out source, how <c>review verdict</c> is registered.
/// </summary>
public sealed class ReviewVerdictRegistrationSourceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>
    /// The <c>review verdict</c> command description registered in <c>Program.cs</c> must not
    /// cite the retired <c>needs-human</c> verdict option, and must state the binary verdict contract.
    /// </summary>
    [Fact]
    public void ProgramRegistersReviewVerdictWithoutNeedsHumanInDescription()
    {
        string program = Corpus.ReadText("src/KyberWeave.Cli/CliApplication.cs");

        int verdictIndex = program.IndexOf("AddCommand<ReviewVerdictCommand>(\"verdict\")", StringComparison.Ordinal);
        Assert.True(verdictIndex >= 0, "Program.cs must register the review verdict command.");

        int nextCommandIndex = program.IndexOf(".AddCommand", verdictIndex + 1, StringComparison.Ordinal);
        string verdictRegistration = nextCommandIndex >= 0
            ? program[verdictIndex..nextCommandIndex]
            : program[verdictIndex..];

        Assert.DoesNotContain("needs-human", verdictRegistration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            ".WithDescription(ReviewVerdictCommand.Description)",
            verdictRegistration,
            StringComparison.Ordinal);
        Assert.DoesNotContain("needs-human", ReviewVerdictCommand.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approve / request-changes", ReviewVerdictCommand.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one example the <c>review verdict</c> help shows must use the seat folder. The deprecated <c>--findings</c>
    /// form is taught by no example, so a reader copies the form the council now uses.
    /// </summary>
    [Fact]
    public void ProgramVerdictExampleShowsTheSeatFolderNotTheDeprecatedFindingsFlag()
    {
        string program = Corpus.ReadText("src/KyberWeave.Cli/CliApplication.cs");

        int verdictIndex = program.IndexOf("AddCommand<ReviewVerdictCommand>(\"verdict\")", StringComparison.Ordinal);
        Assert.True(verdictIndex >= 0, "Program.cs must register the review verdict command.");

        int nextCommandIndex = program.IndexOf(".AddCommand", verdictIndex + 1, StringComparison.Ordinal);
        string verdictRegistration = nextCommandIndex >= 0
            ? program[verdictIndex..nextCommandIndex]
            : program[verdictIndex..];

        Assert.Contains("\"--seats\"", verdictRegistration, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--findings\"", verdictRegistration, StringComparison.Ordinal);
    }
}
