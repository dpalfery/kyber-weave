using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The plan closeout service when the world changes under it: a file that appears just before the archive is written is
/// refused and never overwritten, and a plan that vanishes after the active check is refused as missing. Each test
/// builds the repository in memory and hooks the closeout's own steps to make the change at the exact moment.
/// </summary>
public sealed class PlanCloseoutRaceTests
{
    private const string Plan = DocsCloseoutRepository.Plan;
    private const string TaskFile = DocsCloseoutRepository.TaskFile;
    private const string Archived = DocsCloseoutRepository.Archived;
    private const string PlanIndex = DocsCloseoutRepository.PlanIndex;
    private const string Date = DocsCloseoutRepository.Date;
    private const string EarlierArchive = "# an earlier archive entry that is not ours\n";

    private static DocFixture Repository() => DocsCloseoutRepository.Create(inMemory: true);

    private static string Read(DocFixture fixture, string relative) => fixture.ReadText(relative);

    private static bool Exists(DocFixture fixture, string relative) => fixture.Exists(relative);

    private static ScriptedProcessExecutor NotAGitRepository() =>
        new(_ => new ProcessResult(128, string.Empty, "fatal: not a git repository"));

    private static PlanCloseout Closer(DocFixture fixture, Action<string>? afterActivePlanCheck = null, Action<string>? beforeWriteTarget = null) =>
        new(fixture.Root, KyberWeaveConfigDefaults.Product,
            () => fixture.Loader(KyberWeaveConfigDefaults.Product.Ontology),
            fixture.Store, NotAGitRepository(), new InMemoryExclusiveFileWriter(fixture.Memory!), fixture.Paths, fixture.Environment)
        {
            AfterActivePlanCheck = afterActivePlanCheck,
            BeforeWriteTarget = beforeWriteTarget
        };

    private static DateOnly ArchiveDate => DateOnly.Parse(Date, CultureInfo.InvariantCulture);

    private static Diagnostic SingleError(PlanCloseoutOutcome outcome) =>
        Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);

    [Fact]
    public void AnArchiveEntryThatAppearsJustBeforeTheDryRunWriteIsRefusedNotOverwrittenAndNotDeleted()
    {
        using DocFixture fixture = Repository();

        PlanCloseoutOutcome outcome = Closer(fixture, beforeWriteTarget: target => fixture.Memory!.WriteAllText(target, EarlierArchive))
            .Run(Plan, ArchiveDate, archive: false);

        Assert.False(outcome.Archived);
        Assert.Equal(PlanCloseout.ArchiveTargetExists, SingleError(outcome).Code);
        Assert.Equal(EarlierArchive, Read(fixture, Archived));
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
    }

    [Fact]
    public void AnArchiveEntryThatAppearsJustBeforeTheRealMoveIsRefusedAndNothingMoves()
    {
        using DocFixture fixture = Repository();
        int creates = 0;

        // The first create is the evidence check's copy, which the run removes again; the second is the real move.
        PlanCloseoutOutcome outcome = Closer(fixture, beforeWriteTarget: target =>
            {
                creates++;
                if (creates == 2)
                    fixture.Memory!.WriteAllText(target, EarlierArchive);
            })
            .Run(Plan, ArchiveDate, archive: true);

        Assert.Equal(2, creates);
        Assert.False(outcome.Archived);
        Diagnostic refusal = SingleError(outcome);
        Assert.Equal(PlanCloseout.ArchiveTargetExists, refusal.Code);
        Assert.Contains("Nothing was moved", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(EarlierArchive, Read(fixture, Archived));
        Assert.True(Exists(fixture, Plan));
        Assert.True(Exists(fixture, TaskFile));
        Assert.Contains("2026-10-20-widget.md", Read(fixture, PlanIndex), StringComparison.Ordinal);
    }

    [Fact]
    public void APlanThatVanishesAfterTheActiveCheckIsRefusedAsMissingAndNothingIsCreated()
    {
        using DocFixture fixture = Repository();

        PlanCloseoutOutcome outcome = Closer(fixture, afterActivePlanCheck: path => fixture.Memory!.DeleteFile(path)).Run(Plan, ArchiveDate, archive: true);

        Assert.False(outcome.Archived);
        Diagnostic refusal = Assert.Single(outcome.Report.Items, item => item.Code == PlanCloseout.NotAnActivePlan);
        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
        Assert.False(Exists(fixture, Archived));
    }
}
