using System.Globalization;
using System.Xml.Linq;

namespace KyberWeave.Core.Review;

/// <summary>Reads Coverlet Cobertura output produced by a test gate.</summary>
internal static class CoverageCollector
{
    // Coverlet's name, and the one Istanbul's cobertura reporter (vitest, jest, nyc) writes.
    private static readonly string[] ReportNames = ["coverage.cobertura.xml", "cobertura-coverage.xml"];

    /// <summary>
    /// Returns the newest Cobertura report written at or after <paramref name="notBeforeUtc"/>,
    /// or null when the test gate produced none.
    /// </summary>
    public static CoverageResult? ReadNewest(string workingDirectory, DateTime notBeforeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string[] roots =
        [
            Path.Combine(workingDirectory, "artifacts", "coverage"),
            Path.Combine(workingDirectory, "TestResults")
        ];

        string? newest = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "coverage.cobertura.xml", SearchOption.AllDirectories))
            .Select(path => new FileInfo(path))
            .Where(info => info.LastWriteTimeUtc >= notBeforeUtc.AddSeconds(-1))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault();

        return newest is null ? null : ReadCobertura(newest);
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
    public static IReadOnlyList<CoverageReport> ReadAll(string workingDirectory, DateTime notBeforeUtc)
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
            .Where(path => ReportNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Select(path => new FileInfo(path))
            .Where(info => info.LastWriteTimeUtc >= notBeforeUtc.AddSeconds(-1)))
        {
            CoverageResult? coverage = ReadCobertura(info.FullName);
            if (coverage is null)
                continue;

            string relative = Path.GetRelativePath(workingDirectory, info.FullName).Replace('\\', '/');
            reports.Add(new CoverageReport(relative, coverage));
        }

        return reports.OrderBy(report => report.Path, StringComparer.Ordinal).ToArray();
    }

    internal static CoverageResult? ReadCobertura(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            XDocument document = XDocument.Load(path);
            XElement? coverage = document.Root;
            if (coverage is null || !string.Equals(coverage.Name.LocalName, "coverage", StringComparison.Ordinal))
                return null;

            double file = Percent(coverage.Attribute("line-rate")?.Value);
            XElement[] classes = coverage.Descendants().Where(e => e.Name.LocalName == "class").ToArray();
            double classPercent = classes.Length == 0
                ? file
                : classes.Average(c => Percent(c.Attribute("line-rate")?.Value));

            return new CoverageResult(file, classPercent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static double Percent(string? rate) =>
        double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value * 100
            : 0;
}
