using System.Text.Json;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Reads the gate report the Arbiter derives its <c>gates.report</c> fact from.</summary>
/// <remarks>
/// A missing or unreadable <c>artifacts/gates.json</c> is an absent fact, not a
/// failure: the change may simply predate any gate run.
/// </remarks>
public static class GateReportFacts
{
    /// <summary>Reads the gate report for a repository root.</summary>
    /// <returns>The report, or <c>null</c> when it is missing or unreadable.</returns>
    public static GateReport? TryRead(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            return null;
        return TryReadFile(Path.Combine(repositoryRoot, "artifacts", "gates.json"));
    }

    /// <summary>Reads a gate report from its file path.</summary>
    /// <returns>The report, or <c>null</c> when it is missing or unreadable.</returns>
    public static GateReport? TryReadFile(string gatesJsonPath)
    {
        try
        {
            return ReviewJson.ReadGates(File.ReadAllText(gatesJsonPath));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            return null;
        }
    }
}
