using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the autonomous gate audit inventory. Every gate reachable in autonomous mode must be
/// listed in one canonical page with its disposition, so a gate reduction cannot silently
/// drop a human approval without the inventory showing it.
/// </summary>
public sealed class SquadAutonomousGatesInventoryTests
{
    private static string InventoryPath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "reference", "autonomous-gates.md");

    private static string ReadInventory()
    {
        return File.ReadAllText(InventoryPath);
    }

    private static void AssertInventoryContains(string text, string phrase)
    {
        Assert.Contains(phrase, text, StringComparison.Ordinal);
    }

    private static void AssertInventoryMatchesWord(string text, string word)
    {
        Assert.Matches($"\\b{Regex.Escape(word)}\\b", text);
    }

    [Fact]
    public void AutonomousGatesInventoryPageExists()
    {
        Assert.True(
            File.Exists(InventoryPath),
            $"Gate audit inventory is missing: {InventoryPath}");
    }

    [Fact]
    public void InventoryListsIntakeChoiceGate()
    {
        AssertInventoryContains(ReadInventory(), "intake choice");
    }

    [Fact]
    public void InventoryListsPlanPhaseReadyForReviewGate()
    {
        AssertInventoryContains(ReadInventory(), "READY_FOR_REVIEW");
    }

    [Fact]
    public void InventoryListsTasksApprovalGate()
    {
        AssertInventoryContains(ReadInventory(), "tasks approval");
    }

    [Fact]
    public void InventoryListsApproveAndExecuteGate()
    {
        AssertInventoryContains(ReadInventory(), "approve and execute");
    }

    [Fact]
    public void InventoryListsModeChangeReapprovalGate()
    {
        AssertInventoryContains(ReadInventory(), "mode-change re-approval");
    }

    [Fact]
    public void InventoryListsDeveloperTaskGate()
    {
        AssertInventoryContains(ReadInventory(), "task-gate");
    }

    [Fact]
    public void InventoryListsWholeChangeCouncilGate()
    {
        AssertInventoryContains(ReadInventory(), "council");
    }

    [Fact]
    public void InventoryListsPrePushSelfReviewGate()
    {
        AssertInventoryContains(ReadInventory(), "self-review");
    }

    [Fact]
    public void InventoryListsDocsValidateGate()
    {
        AssertInventoryContains(ReadInventory(), "docs validate");
    }

    [Fact]
    public void InventoryListsDocsDriftGate()
    {
        AssertInventoryContains(ReadInventory(), "docs drift");
    }

    [Fact]
    public void InventoryListsVerdictRules()
    {
        AssertInventoryContains(ReadInventory(), "verdict rules");
    }

    [Fact]
    public void InventoryRowCarriesKeepReduceCollapseDisposition()
    {
        string text = ReadInventory();

        AssertInventoryMatchesWord(text, "keep");
        AssertInventoryMatchesWord(text, "reduce");
        AssertInventoryMatchesWord(text, "collapse");
    }

    [Fact]
    public void InventoryKeepsCarryIrreversibleActionJustification()
    {
        AssertInventoryContains(ReadInventory(), "irreversible");
    }

    [Fact]
    public void InventoryMarksRemovalsFromPullRequest424()
    {
        AssertInventoryContains(ReadInventory(), "#424");
    }
}
