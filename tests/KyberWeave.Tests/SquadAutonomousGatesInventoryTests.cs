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
        AssertInventoryContains(ReadInventory(), "Removed by #424");
    }

    /// <summary>
    /// Section 9 lists hard stops that stop an autonomous run. The reserved-path escalation
    /// (<c>KW-REVIEW-008</c>) no longer stops the run: its finding is reported in the draft pull
    /// request, so the always-human bullet must not remain in the hard-stop list.
    /// </summary>
    [Fact]
    public void HardStopsSectionDropsTheAlwaysHumanReservedPathBullet()
    {
        string text = ReadInventory();
        int start = text.IndexOf("## 9. Hard stops", StringComparison.Ordinal);
        int end = text.IndexOf("## 10. Always-human paths", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Section 9 (Hard stops) and section 10 must both be present.");
        string hardStops = text[start..end];

        Assert.DoesNotContain("**Always-human paths**", hardStops, StringComparison.Ordinal);
        Assert.DoesNotContain("**`KW-REVIEW-008`**", hardStops, StringComparison.Ordinal);
    }

    /// <summary>
    /// The protected-path list is a fixed policy surface. Removing the section 9 bullet must not
    /// change any path it guards, so each path and the always-human rule itself stay pinned.
    /// </summary>
    [Fact]
    public void AlwaysHumanPathListIsUnchanged()
    {
        string text = ReadInventory();
        int start = text.IndexOf("## 10. Always-human paths", StringComparison.Ordinal);
        int end = text.IndexOf("## 11.", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Section 10 (Always-human paths) must be present.");
        string section = text[start..end];

        AssertInventoryContains(section, "- `products/kyber-squad/profiles/capabilities.yml`");
        AssertInventoryContains(section, "- `products/kyber-squad/agents/**`");
        AssertInventoryContains(section, "- `.kyber-weave/kyber-weave.yml`");
        AssertInventoryContains(section, "- `**/*secret*`");
        AssertInventoryContains(section, "- `**/*credential*`");
        AssertInventoryContains(section, "`KW-REVIEW-008` itself always stays human.");
    }
}
