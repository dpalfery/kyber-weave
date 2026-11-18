using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The check of a plan's task file reads the file once. A file that is absent is the "run the generator" finding; one
/// that is there but cannot be read (a folder in its place) is a finding of its own, never an exception.
/// </summary>
public sealed class PlanTaskFileGeneratorReadTests
{
    private static PlanTaskFileOutcome Check(DocFixture fixture) =>
        new PlanTaskFileGenerator(fixture.Root, KyberWeaveConfigDefaults.Product, fixture.Store).Run(PlanTaskFileProbe.PlanPath, PlanTaskFileMode.Check);

    [Fact]
    public void ATaskFileThatCannotBeReadIsOneMalformedFindingNotAnException()
    {
        using DocFixture fixture = PlanTaskFileProbe.CorpusInMemory();
        _ = fixture.EnsureDirectory(PlanTaskFileProbe.TaskFilePath);

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileRules.Malformed, finding.Code);
        Assert.Contains("cannot be read", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanOverTheReadCapIsOnePlanNotReadableFindingAndIsNotLoaded()
    {
        using DocFixture fixture = PlanTaskFileProbe.CorpusInMemory();
        fixture.Write(PlanTaskFileProbe.PlanPath, new string(' ', (int)BoundedFile.MaxBytes + 1));

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileGenerator.PlanNotReadable, finding.Code);
        Assert.Contains((BoundedFile.MaxBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanThatIsASymbolicLinkIsNotReadThroughTheLink()
    {
        using DocFixture fixture = PlanTaskFileProbe.CorpusInMemory();
        string plan = fixture.FullPath(PlanTaskFileProbe.PlanPath);
        string target = fixture.FullPath("real-plan.md");
        fixture.Memory!.MoveFile(plan, target);
        fixture.Memory.CreateFileLink(plan, target);

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileGenerator.PlanNotReadable, finding.Code);
        Assert.Contains("symbolic link", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentTaskFileIsStillTheMissingFileFinding()
    {
        using DocFixture fixture = PlanTaskFileProbe.CorpusInMemory();

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileRules.MissingTaskFile, finding.Code);
    }
}
