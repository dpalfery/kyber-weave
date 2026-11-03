using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit tests for <see cref="SquadInstanceCatalog"/>, <see cref="SquadInstanceRow"/>,
/// <see cref="SquadInstanceKind"/>, and <see cref="SquadInstanceStatus"/>.
/// </summary>
public sealed class SquadInstanceCatalogTests
{
    [Fact]
    public void AllSquadInstanceKindEnumValuesAreDefined()
    {
        Assert.True(Enum.IsDefined(SquadInstanceKind.HomeReplace));
        Assert.True(Enum.IsDefined(SquadInstanceKind.ConfigLayer));
        Assert.True(Enum.IsDefined(SquadInstanceKind.None));
        Assert.Equal(3, Enum.GetValues<SquadInstanceKind>().Length);
    }

    [Fact]
    public void AllSquadInstanceStatusEnumValuesAreDefined()
    {
        Assert.True(Enum.IsDefined(SquadInstanceStatus.Ready));
        Assert.True(Enum.IsDefined(SquadInstanceStatus.SpikePending));
        Assert.True(Enum.IsDefined(SquadInstanceStatus.Unsupported));
        Assert.Equal(3, Enum.GetValues<SquadInstanceStatus>().Length);
    }

    [Fact]
    public void SquadInstanceRowPositionalRecordPreservesAllConstructorProperties()
    {
        IReadOnlyList<string> extraLinks = ["auth.json", "custom-config"];
        IReadOnlyList<string> neverLinked = [".git", "node_modules"];

        SquadInstanceRow row = new(
            SquadTarget.Claude,
            "claude",
            SquadInstanceKind.HomeReplace,
            "CLAUDE_CONFIG_DIR",
            extraLinks,
            neverLinked,
            SquadInstanceStatus.Ready,
            null);

        Assert.Equal(SquadTarget.Claude, row.Target);
        Assert.Equal("claude", row.Executable);
        Assert.Equal(SquadInstanceKind.HomeReplace, row.Kind);
        Assert.Equal("CLAUDE_CONFIG_DIR", row.Variable);
        Assert.Same(extraLinks, row.ExtraLinks);
        Assert.Same(neverLinked, row.NeverLinked);
        Assert.Equal(SquadInstanceStatus.Ready, row.Status);
        Assert.Null(row.Reason);

        SquadInstanceRow identicalRow = new(
            SquadTarget.Claude,
            "claude",
            SquadInstanceKind.HomeReplace,
            "CLAUDE_CONFIG_DIR",
            extraLinks,
            neverLinked,
            SquadInstanceStatus.Ready,
            null);

        Assert.Equal(row, identicalRow);
    }

    /// <summary>
    /// Requirement: SquadInstanceCatalog contains exactly one row per SquadTarget
    /// across all 13 approved targets without omissions or duplicates.
    /// </summary>
    [Fact]
    public void CatalogContainsExactlyOneRowPerApprovedSquadTarget()
    {
        IReadOnlyList<SquadInstanceRow> allRows = SquadInstanceCatalog.AllRows;
        Assert.NotNull(allRows);
        Assert.Equal(13, allRows.Count);

        HashSet<SquadTarget> seenTargets = [];
        foreach (SquadInstanceRow row in allRows)
        {
            Assert.True(seenTargets.Add(row.Target), $"Target '{row.Target}' must appear only once in AllRows.");
        }

        Assert.Equal(SquadTargetCatalog.All.Count, seenTargets.Count);
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            Assert.Contains(target, seenTargets);
        }
    }

    /// <summary>
    /// Verifies that GetRow returns the matching row for every approved target.
    /// </summary>
    [Fact]
    public void GetRowReturnsMatchingRowForEveryApprovedTarget()
    {
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
            Assert.NotNull(row);
            Assert.Equal(target, row.Target);
            Assert.Contains(row, SquadInstanceCatalog.AllRows);
        }
    }

    /// <summary>
    /// Verifies that GetRow throws ArgumentOutOfRangeException when an invalid target is requested.
    /// </summary>
    [Fact]
    public void GetRowWithInvalidTargetThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SquadInstanceCatalog.GetRow((SquadTarget)9999));
    }

    /// <summary>
    /// Requirement: Ready targets per design are exactly Claude, Codex, Copilot, and Pi.
    /// Every Ready row has Kind != None and a non-empty Variable.
    /// </summary>
    [Fact]
    public void ReadyTargetsMatchDesignSpecification()
    {
        SquadTarget[] expectedReady =
        [
            SquadTarget.Claude,
            SquadTarget.Codex,
            SquadTarget.Copilot,
            SquadTarget.Pi
        ];

        List<SquadInstanceRow> readyRows = SquadInstanceCatalog.AllRows
            .Where(r => r.Status == SquadInstanceStatus.Ready)
            .ToList();

        Assert.Equal(expectedReady.Length, readyRows.Count);

        foreach (SquadTarget target in expectedReady)
        {
            SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
            Assert.Equal(SquadInstanceStatus.Ready, row.Status);
            Assert.NotEqual(SquadInstanceKind.None, row.Kind);
            Assert.False(string.IsNullOrWhiteSpace(row.Variable), $"Ready target '{target}' must have a non-empty Variable.");
            Assert.False(string.IsNullOrWhiteSpace(row.Executable), $"Ready target '{target}' must have a non-empty Executable.");
            Assert.Null(row.Reason);
        }
    }

    /// <summary>
    /// Requirement: SpikePending targets per design are exactly OpenCode, Kilo, Cursor, and ZCode.
    /// </summary>
    [Fact]
    public void SpikePendingTargetsMatchDesignSpecification()
    {
        SquadTarget[] expectedSpikePending =
        [
            SquadTarget.OpenCode,
            SquadTarget.Kilo,
            SquadTarget.Cursor,
            SquadTarget.ZCode
        ];

        List<SquadInstanceRow> spikePendingRows = SquadInstanceCatalog.AllRows
            .Where(r => r.Status == SquadInstanceStatus.SpikePending)
            .ToList();

        Assert.Equal(expectedSpikePending.Length, spikePendingRows.Count);

        foreach (SquadTarget target in expectedSpikePending)
        {
            SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
            Assert.Equal(SquadInstanceStatus.SpikePending, row.Status);
            Assert.False(string.IsNullOrWhiteSpace(row.Executable), $"SpikePending target '{target}' must have a non-empty Executable.");
        }
    }

    /// <summary>
    /// Requirement: Unsupported targets per design are exactly Antigravity, Factory, CommandCode, Devin, and Warp.
    /// Every Unsupported row has a non-empty Reason.
    /// </summary>
    [Fact]
    public void UnsupportedTargetsMatchDesignSpecification()
    {
        SquadTarget[] expectedUnsupported =
        [
            SquadTarget.Antigravity,
            SquadTarget.Factory,
            SquadTarget.CommandCode,
            SquadTarget.Devin,
            SquadTarget.Warp
        ];

        List<SquadInstanceRow> unsupportedRows = SquadInstanceCatalog.AllRows
            .Where(r => r.Status == SquadInstanceStatus.Unsupported)
            .ToList();

        Assert.Equal(expectedUnsupported.Length, unsupportedRows.Count);

        foreach (SquadTarget target in expectedUnsupported)
        {
            SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
            Assert.Equal(SquadInstanceStatus.Unsupported, row.Status);
            Assert.False(string.IsNullOrWhiteSpace(row.Reason), $"Unsupported target '{target}' must have a non-empty Reason.");
        }
    }

    /// <summary>
    /// Acceptance criterion 7.6: The mechanism SHALL be harness-neutral: one Squad command per project,
    /// whatever the harness. Verifies that all 13 approved squad targets have a defined row in the catalog,
    /// allowing uniform dispatch and status checking regardless of the harness.
    /// </summary>
    [Fact]
    public void AcceptanceCriterion7_6_AllApprovedHarnessesHaveCatalogRowForNeutralDispatch()
    {
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
            Assert.NotNull(row);
            Assert.False(string.IsNullOrWhiteSpace(row.Executable));
            Assert.NotNull(row.ExtraLinks);
            Assert.NotNull(row.NeverLinked);
        }
    }

    /// <summary>
    /// Acceptance criterion 7.7: IF a harness has no usable variable for pointing it at an instance folder
    /// THEN Squad SHALL fail and name the harness. Every unsupported harness row must have an explanatory
    /// reason that identifies why it cannot point at an instance folder, and must have no variable configured.
    /// </summary>
    [Fact]
    public void AcceptanceCriterion7_7_HarnessWithNoUsableVariableIsMarkedUnsupportedWithExplanatoryReason()
    {
        List<SquadInstanceRow> unsupportedRows = SquadInstanceCatalog.AllRows
            .Where(row => row.Status == SquadInstanceStatus.Unsupported)
            .ToList();

        Assert.NotEmpty(unsupportedRows);
        foreach (SquadInstanceRow row in unsupportedRows)
        {
            Assert.Null(row.Variable);
            Assert.Equal(SquadInstanceKind.None, row.Kind);
            Assert.False(
                string.IsNullOrWhiteSpace(row.Reason),
                $"Unsupported target '{row.Target}' must state why it has no usable variable.");
        }
    }

    /// <summary>
    /// Verifies that all 13 catalog rows contain non-null link collections and non-empty executables.
    /// </summary>
    [Fact]
    public void EveryCatalogRowHasValidCollectionsAndExecutable()
    {
        foreach (SquadInstanceRow row in SquadInstanceCatalog.AllRows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Executable), $"Target '{row.Target}' has empty executable.");
            Assert.NotNull(row.ExtraLinks);
            Assert.NotNull(row.NeverLinked);
        }
    }
}
