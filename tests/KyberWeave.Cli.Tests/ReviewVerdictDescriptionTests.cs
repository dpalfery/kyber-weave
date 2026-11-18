using KyberWeave.Cli.Commands.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies the description the <c>review verdict</c> command registers.
/// </summary>
public sealed class ReviewVerdictDescriptionTests
{
    /// <summary>
    /// The <c>review verdict</c> command description must not cite the retired <c>needs-human</c>
    /// verdict option, and must state the binary verdict contract.
    /// </summary>
    [Fact]
    public void TheVerdictDescriptionStatesTheBinaryContractWithoutNeedsHuman()
    {
        Assert.DoesNotContain("needs-human", ReviewVerdictCommand.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approve / request-changes", ReviewVerdictCommand.Description, StringComparison.Ordinal);
    }
}
