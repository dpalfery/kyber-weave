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
        new PlanTaskFileGenerator(fixture.Root, KyberWeaveConfigDefaults.Product, DocsPorts.Files).Run(PlanTaskFileProbe.PlanPath, PlanTaskFileMode.Check);

    [Fact]
    public void ATaskFileThatCannotBeReadIsOneMalformedFindingNotAnException()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        Directory.CreateDirectory(Path.Combine(fixture.Root, PlanTaskFileProbe.TaskFilePath.Replace('/', Path.DirectorySeparatorChar)));

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileRules.Malformed, finding.Code);
        Assert.Contains("cannot be read", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanOverTheReadCapIsOnePlanNotReadableFindingAndIsNotLoaded()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(PlanTaskFileProbe.PlanPath, new string(' ', (int)BoundedFile.MaxBytes + 1));

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileGenerator.PlanNotReadable, finding.Code);
        Assert.Contains((BoundedFile.MaxBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanThatIsASymbolicLinkIsNotReadThroughTheLink()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string plan = Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath.Replace('/', Path.DirectorySeparatorChar));
        string target = Path.Combine(fixture.Root, "real-plan.md");
        File.Move(plan, target);
        try
        {
            File.CreateSymbolicLink(plan, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This host cannot create symbolic links, so a linked plan cannot be simulated.");
            return;
        }

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileGenerator.PlanNotReadable, finding.Code);
        Assert.Contains("symbolic link", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentTaskFileIsStillTheMissingFileFinding()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();

        PlanTaskFileOutcome outcome = Check(fixture);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal(PlanTaskFileRules.MissingTaskFile, finding.Code);
    }
}
