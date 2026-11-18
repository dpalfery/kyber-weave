using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The CLI's <c>squad instance</c> registration, read from the checkout's own source file.
/// </summary>
public sealed class SquadInstanceCommandRegistrationSourceTests
{
    /// <summary>
    ///     Verifies that CliApplication.cs registers the <c>squad instance</c> branch with the <c>remove</c> command,
    ///     options, and examples.
    /// </summary>
    [Fact]
    public void ProgramRegistersSquadInstanceRemoveBranchAndCommand()
    {
        string program = CheckoutCorpusSource.Checkout.ReadText("src/KyberWeave.Cli/CliApplication.cs");

        Assert.Contains("AddBranch(\"instance\"", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<SquadInstanceRemoveCommand>(\"remove\")", program, StringComparison.Ordinal);
        Assert.Contains("WithExample(\"squad\", \"instance\", \"remove\"", program, StringComparison.Ordinal);
    }
}
