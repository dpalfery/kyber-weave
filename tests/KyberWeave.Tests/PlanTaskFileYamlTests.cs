using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The machine task list is YAML. The JSON form an older release wrote is still read, with one
/// warning that names the convert command, and <c>--convert</c> rewrites it in place.
/// </summary>
public sealed class PlanTaskFileYamlTests
{
    private static PlanTaskFileOutcome Run(DocFixture fixture, PlanTaskFileMode mode) =>
        new PlanTaskFileGenerator(fixture.Root, KyberWeaveConfigDefaults.Product, DocsPorts.Files)
            .Run(PlanTaskFileProbe.PlanPath, mode);

    private static string FullPath(DocFixture fixture, string relative) =>
        Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void AWriteProducesYamlAndNoJsonFile()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();

        PlanTaskFileOutcome outcome = Run(fixture, PlanTaskFileMode.Write);

        Assert.True(outcome.Wrote);
        Assert.True(File.Exists(FullPath(fixture, PlanTaskFileProbe.TaskFilePath)));
        Assert.False(File.Exists(FullPath(fixture, PlanTaskFileProbe.LegacyTaskFilePath)));
        string text = File.ReadAllText(FullPath(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.Contains($"schema-version: {PlanTaskFileRules.SchemaVersion}", text, StringComparison.Ordinal);

        PlanTaskFile? file = PlanTaskFileFormat.Deserialize(PlanTaskFileProbe.TaskFilePath, text);
        Assert.NotNull(file);
        Assert.Equal(2, file.Tasks?.Count);
        Assert.Equal(PlanTaskFileProbe.SecondTask, file.Tasks![1].Id);
    }

    [Fact]
    public void ALegacyJsonFileIsReadWithOneWarningNamingTheConvertCommand()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        File.WriteAllText(
            FullPath(fixture, PlanTaskFileProbe.LegacyTaskFilePath),
            PlanTaskFileProbe.LegacyJsonTaskFile());

        PlanTaskFileOutcome outcome = Run(fixture, PlanTaskFileMode.Check);

        Diagnostic warning = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Warning);
        Assert.Equal(PlanTaskFileRules.LegacyJsonTaskFile, warning.Code);
        Assert.Contains("--convert", warning.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain(outcome.Report.Items, item => item.Severity >= Severity.Error);
    }

    [Fact]
    public void ConvertRewritesTheJsonAsYamlAndRemovesIt()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        File.WriteAllText(
            FullPath(fixture, PlanTaskFileProbe.LegacyTaskFilePath),
            PlanTaskFileProbe.LegacyJsonTaskFile());

        PlanTaskFileOutcome outcome = Run(fixture, PlanTaskFileMode.Convert);

        Assert.True(outcome.Wrote);
        Assert.True(File.Exists(FullPath(fixture, PlanTaskFileProbe.TaskFilePath)));
        Assert.False(File.Exists(FullPath(fixture, PlanTaskFileProbe.LegacyTaskFilePath)));

        PlanTaskFile? converted = PlanTaskFileFormat.Deserialize(
            PlanTaskFileProbe.TaskFilePath,
            File.ReadAllText(FullPath(fixture, PlanTaskFileProbe.TaskFilePath)));
        Assert.NotNull(converted);
        Assert.Equal(2, converted.Tasks?.Count);
        Assert.Equal(PlanTaskFileProbe.ExecutionDigest(PlanTaskFileProbe.PlanMarkdown()), converted.ExecutionDigest);
    }

    /// <summary>
    /// A current YAML file beside a JSON file an older release wrote leaves two task files. The write
    /// removes the JSON and names it, even though the YAML itself needed no rewrite.
    /// </summary>
    [Fact]
    public void AWriteRemovesALegacyJsonLeftBesideACurrentYamlAndReportsIt()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        Assert.True(Run(fixture, PlanTaskFileMode.Write).Wrote);
        File.WriteAllText(
            FullPath(fixture, PlanTaskFileProbe.LegacyTaskFilePath),
            PlanTaskFileProbe.LegacyJsonTaskFile());

        PlanTaskFileOutcome outcome = Run(fixture, PlanTaskFileMode.Write);

        Assert.False(outcome.Wrote);
        Assert.True(File.Exists(FullPath(fixture, PlanTaskFileProbe.TaskFilePath)));
        Assert.False(File.Exists(FullPath(fixture, PlanTaskFileProbe.LegacyTaskFilePath)));
        Assert.Contains(
            outcome.Report.Items,
            item => item.Code == PlanTaskFileRules.LegacyJsonTaskFile
                    && item.Message.Contains(PlanTaskFileProbe.LegacyTaskFilePath, StringComparison.Ordinal));
    }

    /// <summary>The validator names a JSON file left beside the YAML one, with the command that removes it.</summary>
    [Fact]
    public void ValidateReportsALegacyJsonLeftBesideTheYamlTaskFile()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        Assert.True(Run(fixture, PlanTaskFileMode.Write).Wrote);
        File.WriteAllText(
            FullPath(fixture, PlanTaskFileProbe.LegacyTaskFilePath),
            PlanTaskFileProbe.LegacyJsonTaskFile());

        DiagnosticReport report = new PlanTaskFileValidator(
            fixture.Root,
            KyberWeaveConfigDefaults.Product,
            DocsPorts.Files).Validate(fixture.Load());

        Diagnostic leftover = Assert.Single(
            report.Items,
            item => item.Code == PlanTaskFileRules.LegacyJsonTaskFile
                    && item.Severity == Severity.Warning
                    && item.Message.Contains(PlanTaskFileProbe.LegacyTaskFilePath, StringComparison.Ordinal));
        Assert.Contains("--convert", leftover.Hint, StringComparison.Ordinal);
    }

    /// <summary>Converting a plan that already stores YAML reports that there is nothing to convert.</summary>
    [Fact]
    public void ConvertWithNoJsonFileWritesNothing()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();

        PlanTaskFileOutcome outcome = Run(fixture, PlanTaskFileMode.Convert);

        Assert.False(outcome.Wrote);
        Assert.False(File.Exists(FullPath(fixture, PlanTaskFileProbe.TaskFilePath)));
        Assert.Contains(
            outcome.Report.Items,
            item => item.Code == PlanTaskFileRules.LegacyJsonTaskFile
                    && item.Message.Contains("nothing to convert", StringComparison.Ordinal));
    }
}
