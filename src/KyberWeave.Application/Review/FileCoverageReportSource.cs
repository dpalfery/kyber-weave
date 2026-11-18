using System.Xml.Linq;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Reads Cobertura coverage reports from the file system.</summary>
/// <param name="files">The file system the reports are found and read through.</param>
public sealed class FileCoverageReportSource(IFileSystem files) : ICoverageReportSource
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
            .SelectMany(Reports)
            .Where(CoberturaReport.IsReportName)
            .Select(path => (Path: path, Written: files.GetLastWriteTimeUtc(path)))
            .Where(report => report.Written >= notBeforeUtc.AddSeconds(-1))
            .OrderByDescending(report => report.Written)
            .Select(report => report.Path)
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
        foreach (string path in roots
            .SelectMany(Reports)
            .Where(CoberturaReport.IsReportName)
            .Where(path => files.GetLastWriteTimeUtc(path) >= notBeforeUtc.AddSeconds(-1)))
        {
            CoverageResult? coverage = ReadFile(path);
            if (coverage is null)
                continue;

            string relative = Path.GetRelativePath(workingDirectory, path).Replace('\\', '/');
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
            using Stream stream = files.OpenRead(path);
            return CoberturaReport.Parse(XDocument.Load(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every <c>*.xml</c> file under <paramref name="root"/>, one directory at a time so no folder name is skipped: a
    /// run's Coverlet report sits under <c>artifacts/coverage</c>, which the file system's tree walk leaves out.
    /// </summary>
    private IEnumerable<string> Reports(string root)
    {
        if (!files.DirectoryExists(root))
            yield break;

        foreach (FileSystemEntry entry in files.EnumerateEntries(root))
        {
            if (entry.IsDirectory)
            {
                foreach (string nested in Reports(entry.Path))
                    yield return nested;
            }
            else if (Path.GetExtension(entry.Path).Equals(
                ".xml", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                yield return entry.Path;
            }
        }
    }
}
