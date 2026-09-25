using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Coverlet reports line-rate as a 0–1 fraction. The collector must scale it to a
/// percent, or a 87.5% report would be graded as 0.875% and every floor would fail.
/// </summary>
public sealed class CoverageCollectorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadCoberturaScalesLineRateToAPercent()
    {
        string path = Path.Combine(_temp.Path, "coverage.cobertura.xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.875" branch-rate="0.5" version="1.9">
              <packages>
                <package name="KyberWeave.Core" line-rate="0.875">
                  <classes>
                    <class name="A" filename="A.cs" line-rate="1.0" />
                    <class name="B" filename="B.cs" line-rate="0.75" />
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        CoverageResult? coverage = CoverageCollector.ReadCobertura(path);

        Assert.NotNull(coverage);
        Assert.Equal(87.5, coverage.FileLinePercent);
        Assert.Equal(87.5, coverage.ClassLinePercent);
    }

    /// <summary>
    /// A suite that tests two languages writes two reports. The floor reads one, so the other
    /// is visible only through the list, and neither may displace the other.
    /// </summary>
    [Fact]
    public void ReadAllListsEveryReportTheRunWroteWithoutDisplacingTheFloorsReport()
    {
        DateTime started = DateTime.UtcNow;
        string dotnet = WriteReport(Path.Combine("artifacts", "coverage", "run", "coverage.cobertura.xml"), "0.8");
        string vitest = WriteReport(Path.Combine("artifacts", "coverage-dash", "cobertura-coverage.xml"), "0.6");
        string stale = WriteReport(Path.Combine("TestResults", "old", "coverage.cobertura.xml"), "0.1");
        File.SetLastWriteTimeUtc(stale, started.AddHours(-1));
        // The vitest report is the newer, as the ts-test gate runs after the .NET one.
        File.SetLastWriteTimeUtc(dotnet, started.AddSeconds(1));
        File.SetLastWriteTimeUtc(vitest, started.AddSeconds(2));

        IReadOnlyList<CoverageReport> reports = CoverageCollector.ReadAll(_temp.Path, started);
        CoverageResult? floor = CoverageCollector.ReadNewest(_temp.Path, started);

        string[] expected =
        [
            "artifacts/coverage-dash/cobertura-coverage.xml",
            "artifacts/coverage/run/coverage.cobertura.xml",
        ];
        Assert.Equal(expected, reports.Select(report => report.Path));
        Assert.Equal(60, reports[0].Coverage.FileLinePercent, precision: 6);
        Assert.Equal(80, reports[1].Coverage.FileLinePercent, precision: 6);
        Assert.NotNull(floor);
        Assert.Equal(80, floor.FileLinePercent, precision: 6);
    }

    [Fact]
    public void GateReportRoundTripsItsCoverageReports()
    {
        GateReport report = new(
            GateReport.CurrentSchema,
            [],
            new CoverageResult(80, 85),
            [new CoverageReport("artifacts/coverage-dash/cobertura-coverage.xml", new CoverageResult(60, 70))]);

        GateReport read = ReviewJson.ReadGates(ReviewJson.Write(report));

        CoverageReport only = Assert.Single(read.CoverageReports!);
        Assert.Equal("artifacts/coverage-dash/cobertura-coverage.xml", only.Path);
        Assert.Equal(60, only.Coverage.FileLinePercent);
    }

    private string WriteReport(string relativePath, string lineRate)
    {
        string path = Path.Combine(_temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="{lineRate}" version="1.9"><packages /></coverage>
            """);
        return path;
    }
}
