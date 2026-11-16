using System.Xml.Linq;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Reads Cobertura coverage reports from the file system.</summary>
public sealed class FileCoverageReportSource : ICoverageReportSource
{
    /// <summary>
    /// Returns the newest Cobertura report written at or after <paramref name="notBeforeUtc"/>,
    /// or null when the test gate produced none.
    /// </summary>
    public CoverageResult? ReadNewest(string workingDirectory, DateTime notBeforeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string[] roots =
        [
            Path.Combine(workingDirectory, "artifacts", "coverage"),
            Path.Combine(workingDirectory, "TestResults")
        ];

        string? newest = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            .Where(CoberturaReport.IsReportName)
            .Select(path => new FileInfo(path))
            .Where(info => info.LastWriteTimeUtc >= notBeforeUtc.AddSeconds(-1))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault();

        return newest is null ? null : ReadFile(newest);
    }

    /// <summary>
    /// Every Cobertura report written under <c>artifacts</c> or <c>TestResults</c> at or after
    /// <paramref name="notBeforeUtc"/>, in path order.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadNewest"/> picks one Coverlet report because a floor needs one number. A
    /// suite that also tests another language writes another report, which Istanbul-style
    /// tools such as vitest name <c>cobertura-coverage.xml</c>; without this list the review
    /// has no way to see it. Reports older than the run are left out, as they are there.
    /// </remarks>
    public IReadOnlyList<CoverageReport> ReadAll(string workingDirectory, DateTime notBeforeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string[] roots =
        [
            Path.Combine(workingDirectory, "artifacts"),
            Path.Combine(workingDirectory, "TestResults")
        ];

        List<CoverageReport> reports = [];
        foreach (FileInfo info in roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            .Where(CoberturaReport.IsReportName)
            .Select(path => new FileInfo(path))
            .Where(info => info.LastWriteTimeUtc >= notBeforeUtc.AddSeconds(-1)))
        {
            CoverageResult? coverage = ReadFile(info.FullName);
            if (coverage is null)
                continue;

            string relative = Path.GetRelativePath(workingDirectory, info.FullName).Replace('\\', '/');
            reports.Add(new CoverageReport(relative, coverage));
        }

        return reports.OrderBy(report => report.Path, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public CoverageResult? ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return CoberturaReport.Parse(XDocument.Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
