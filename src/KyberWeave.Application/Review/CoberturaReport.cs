using System.Globalization;
using System.Xml.Linq;

namespace KyberWeave.Core.Review;

/// <summary>Reads Coverlet and Istanbul Cobertura reports a test gate produced.</summary>
public static class CoberturaReport
{
    // Coverlet's name, and the one Istanbul's cobertura reporter (vitest, jest, nyc) writes.
    private static readonly string[] ReportNames = ["coverage.cobertura.xml", "cobertura-coverage.xml"];

    /// <summary>The line and class coverage a Cobertura document reports; null when it is not one.</summary>
    /// <param name="document">The parsed report.</param>
    public static CoverageResult? Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

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

    private static double Percent(string? rate) =>
        double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value * 100
            : 0;

    /// <summary>Whether a file name is one a coverage tool writes its Cobertura report under.</summary>
    /// <param name="path">The report path.</param>
    public static bool IsReportName(string path)
    {
        string fileName = Path.GetFileName(path);
        if (ReportNames.Contains(fileName, StringComparer.Ordinal))
            return true;

        // coverlet.MTP adds a run timestamp to avoid overwriting reports in the same output directory.
        const string prefix = "coverage.cobertura.";
        const string extension = ".xml";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(extension, StringComparison.Ordinal))
        {
            return false;
        }

        string timestamp = fileName[prefix.Length..^extension.Length];
        return timestamp.Length > 0 && timestamp.All(char.IsAsciiDigit);
    }
}
