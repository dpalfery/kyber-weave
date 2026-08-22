using System.Globalization;
using System.Xml.Linq;

namespace KyberWeave.Core.Review;

/// <summary>Reads Coverlet Cobertura output produced by a test gate.</summary>
internal static class CoverageCollector
{
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
