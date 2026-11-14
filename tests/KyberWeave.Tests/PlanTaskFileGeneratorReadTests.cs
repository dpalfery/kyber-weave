using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
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
        new PlanTaskFileGenerator(fixture.Root, KyberWeaveConfig.ProductDefaults).Run(PlanTaskFileProbe.PlanPath, PlanTaskFileMode.Check);

    [Fact]
    public void ATaskFileThatCannotBeReadIsOneMalformedFindingNotAnException()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        Directory.CreateDirectory(Path.Combine(fixture.Root, PlanTaskFileProbe.TaskFilePath.Replace('/', Path.DirectorySeparatorChar)));

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileValidator.Malformed, finding.Code);
        Assert.Contains("cannot be read", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentTaskFileIsStillTheMissingFileFinding()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileValidator.MissingTaskFile, finding.Code);
    }
}
