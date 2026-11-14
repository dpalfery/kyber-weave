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

    [Fact]
    public void AutonomousGatesInventoryPageExists()
    {
        Assert.True(
            File.Exists(InventoryPath),
            $"Gate audit inventory is missing: {InventoryPath}");
    }

    [Fact]
    public void InventoryListsPlanPhaseReadyForReviewGate()
    {
        AssertInventoryContains(ReadInventory(), "READY_FOR_REVIEW");
    }

    [Fact]
    public void InventoryListsDeveloperTaskGate()
    {
        AssertInventoryContains(ReadInventory(), "task-gate");
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

        Assert.DoesNotContain("**`KW-REVIEW-008`**", hardStops, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sections 8 and 10 name the retired rule ids and the two remaining verdicts. That they record
    /// the retirement is graded from the prose rule catalog (<c>needs-human-retired-recorded</c>).
    /// </summary>
    [Fact]
    public void VerdictRulesAndSection10NameTheRetiredIdsAndTheTwoVerdicts()
    {
        string text = ReadInventory();
        int s8Start = text.IndexOf("## 8. Verdict rules", StringComparison.Ordinal);
        int s8End = text.IndexOf("## 9. Hard stops", StringComparison.Ordinal);
        Assert.True(s8Start >= 0 && s8End > s8Start, "Section 8 (Verdict rules) and section 9 (Hard stops) must both be present.");
        string section8 = text[s8Start..s8End];

        int s10Start = text.IndexOf("## 10. Always-human paths", StringComparison.Ordinal);
        int s10End = text.IndexOf("## 11.", s10Start, StringComparison.Ordinal);
        Assert.True(s10Start >= 0 && s10End > s10Start, "Section 10 (Always-human paths) and section 11 must both be present.");
        string section10 = text[s10Start..s10End];

        AssertInventoryContains(section8, "KW-REVIEW-008");
        AssertInventoryContains(section8, "KW-REVIEW-009");
        AssertInventoryContains(section8, "APPROVE");
        AssertInventoryContains(section8, "REQUEST_CHANGES");

        AssertInventoryContains(section10, "NEEDS_HUMAN");
        AssertInventoryContains(section10, "APPROVE");
        AssertInventoryContains(section10, "REQUEST_CHANGES");
    }
}
