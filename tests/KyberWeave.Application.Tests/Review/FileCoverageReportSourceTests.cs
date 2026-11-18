using System.Globalization;
using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The coverage report source over the in-memory file system: which reports a run includes, the order they come back in,
/// and how a report becomes its line percentages are proven without a disk.
/// </summary>
public sealed class FileCoverageReportSourceTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static readonly DateTimeOffset RunStarted = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(RunStarted.AddMinutes(-10));
    private readonly InMemoryFileSystem _files;
    private readonly FileCoverageReportSource _source;

    public FileCoverageReportSourceTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _source = new FileCoverageReportSource(_files);
    }

    [Fact]
    public void ReadNewestPicksTheNewestReportTheRunWrote()
    {
        Write("artifacts/coverage/run/coverage.cobertura.1.xml", 0.8, RunStarted.AddSeconds(1));
        Write("TestResults/later/coverage.cobertura.xml", 0.9, RunStarted.AddSeconds(2));

        CoverageResult? newest = _source.ReadNewest(Root, RunStarted.UtcDateTime);

        Assert.NotNull(newest);
        Assert.Equal(90, newest.FileLinePercent, precision: 6);
    }

    [Fact]
    public void ReadNewestLeavesAReportOlderThanTheRunOut()
    {
        Write("artifacts/coverage/coverage.cobertura.xml", 0.8, RunStarted.AddMinutes(-5));

        Assert.Null(_source.ReadNewest(Root, RunStarted.UtcDateTime));
    }

    [Fact]
    public void ReadNewestReturnsNothingWhenTheGateWroteNoReport()
    {
        Assert.Null(_source.ReadNewest(Root, RunStarted.UtcDateTime));
    }

    [Fact]
    public void ReadAllListsEveryReportTheRunWroteInPathOrderAndLeavesAStaleOneOut()
    {
        Write("TestResults/old/coverage.cobertura.xml", 0.1, RunStarted.AddMinutes(-5));
        Write("artifacts/coverage-dash/cobertura-coverage.xml", 0.6, RunStarted.AddSeconds(1));
        Write("artifacts/coverage/run/coverage.cobertura.2.xml", 0.8, RunStarted.AddSeconds(2));
        Write("artifacts/coverage/notes.xml", 0.1, RunStarted.AddSeconds(3));

        IReadOnlyList<CoverageReport> reports = _source.ReadAll(Root, RunStarted.UtcDateTime);

        Assert.Equal(
            ["artifacts/coverage-dash/cobertura-coverage.xml", "artifacts/coverage/run/coverage.cobertura.2.xml"],
            reports.Select(report => report.Path));
        Assert.Equal(60, reports[0].Coverage.FileLinePercent, precision: 6);
        Assert.Equal(80, reports[1].Coverage.FileLinePercent, precision: 6);
    }

    [Fact]
    public void ReadFileScalesTheReportAndReturnsNothingWhenItCannotBeRead()
    {
        Write("artifacts/coverage/coverage.cobertura.xml", 0.875, RunStarted);
        string readable = Path.Combine(Root, "artifacts", "coverage", "coverage.cobertura.xml");
        string malformed = Path.Combine(Root, "artifacts", "coverage", "broken.xml");
        _files.WriteAllText(malformed, "<coverage");
        string missing = Path.Combine(Root, "artifacts", "coverage", "absent.xml");

        CoverageResult? coverage = _source.ReadFile(readable);

        Assert.NotNull(coverage);
        Assert.Equal(87.5, coverage.FileLinePercent, precision: 6);
        Assert.Null(_source.ReadFile(malformed));
        Assert.Null(_source.ReadFile(missing));
    }

    private void Write(string relativePath, double lineRate, DateTimeOffset writtenAt)
    {
        _clock.SetUtcNow(writtenAt);
        string path = Path.Combine(Root, relativePath);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, Report(lineRate));
    }

    private static string Report(double lineRate) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="{lineRate.ToString(CultureInfo.InvariantCulture)}" branch-rate="0.5" version="1.9">
          <packages />
        </coverage>
        """;
}
