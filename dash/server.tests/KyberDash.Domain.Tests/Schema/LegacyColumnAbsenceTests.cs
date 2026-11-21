using System.Reflection;
using System.Text.RegularExpressions;
using KyberDash.Application;
using Xunit;

namespace KyberDash.Domain.Tests.Schema;

/// <summary>
/// Guards the relational cutover: no production source under <c>dash/server</c> may reference the
/// deleted legacy store types (schema, repository, and interface, including any transitional
/// store constructor on <c>CachedTokenCounter</c>) or the removed legacy
/// JSON/payload column identifiers (<c>tokens_json</c>, <c>content_json</c>, <c>cost_json</c>,
/// and friends). The bare words <c>payload</c> and <c>raw</c> are intentionally not scanned: they
/// remain ordinary DTO, span, and prose vocabulary (for example <c>RawSpan</c>), while the
/// relational catalog itself forbids <c>payload</c>/<c>raw</c> column names under its own tests.
/// </summary>
/// <remarks>
/// <c>LegacyTableReplacementMap.cs</c> is the one deliberate exception to the column rule: it is
/// the authoritative historical record of which v24 columns were dropped, so its
/// <c>RemovedDetails</c> strings name them. It stays covered by the legacy-symbol rule.
/// </remarks>
public sealed partial class LegacyColumnAbsenceTests
{
    private static readonly string[] ForbiddenColumnNames =
    [
        "tokens_json",
        "content_json",
        "cost_json",
        "measurability_json",
        "parts_json",
        "summary_json",
        "turn_json",
        "parent_linkage_json",
        "error_bar_json",
        "evidence_links_json",
        "attributes_json",
        "resource_json",
        "scope_json",
        "raw_payload",
    ];

    private const string ColumnExemptionFileName = "LegacyTableReplacementMap.cs";

    [GeneratedRegex(@"\b(CanonSchema|CanonStoreRepository|ICanonStoreRepository|FakeCanonStoreRepository)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySymbolRegex();

    /// <summary>Verifies no production source mentions the deleted legacy store symbols.</summary>
    [Fact]
    public void ProductionSources_ContainNoLegacyStoreSymbols()
    {
        string serverRoot = FindServerRoot();
        List<string> offenders = [];

        foreach (string file in EnumerateProductionSources(serverRoot))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in LegacySymbolRegex().Matches(text))
            {
                offenders.Add($"{RelativePath(serverRoot, file)}:{LineNumber(text, match.Index)}: '{match.Value}'");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Legacy store symbols remain in production sources:{Environment.NewLine}{string.Join(Environment.NewLine, offenders.Take(20))}");
    }

    /// <summary>Verifies no production source mentions removed legacy JSON column identifiers.</summary>
    [Fact]
    public void ProductionSources_ContainNoLegacyJsonColumnNames()
    {
        string serverRoot = FindServerRoot();
        List<string> offenders = [];

        foreach (string file in EnumerateProductionSources(serverRoot))
        {
            if (string.Equals(Path.GetFileName(file), ColumnExemptionFileName, StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string column in ForbiddenColumnNames)
            {
                int index = text.IndexOf(column, StringComparison.Ordinal);
                while (index >= 0)
                {
                    offenders.Add($"{RelativePath(serverRoot, file)}:{LineNumber(text, index)}: '{column}'");
                    index = text.IndexOf(column, index + column.Length, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Legacy column identifiers remain in production sources:{Environment.NewLine}{string.Join(Environment.NewLine, offenders.Take(20))}");
    }

    /// <summary>Verifies the column exemption covers exactly the documented replacement map.</summary>
    [Fact]
    public void ColumnExemption_CoversOnlyTheDocumentedReplacementMap()
    {
        string serverRoot = FindServerRoot();
        List<string> matches = EnumerateProductionSources(serverRoot)
            .Where(static file => string.Equals(Path.GetFileName(file), ColumnExemptionFileName, StringComparison.Ordinal))
            .Select(file => RelativePath(serverRoot, file))
            .ToList();

        Assert.Single(matches);
    }

    /// <summary>Verifies <c>CachedTokenCounter</c> carries no transitional store constructor.</summary>
    [Fact]
    public void CachedTokenCounter_HasNoTransitionalStoreConstructor()
    {
        ConstructorInfo[] constructors = typeof(CachedTokenCounter).GetConstructors();
        Assert.NotEmpty(constructors);

        List<string> offenders = [];
        foreach (ConstructorInfo constructor in constructors)
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                string? typeName = parameter.ParameterType.FullName ?? parameter.ParameterType.Name;
                if (typeName.Contains("CanonStore", StringComparison.Ordinal))
                {
                    offenders.Add($"{constructor} parameter '{parameter.Name}' is typed '{typeName}'");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Transitional store constructors remain on CachedTokenCounter:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    private static string FindServerRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "dash", "server", "KyberDash.Api", "KyberDash.Api.csproj"))
                && Directory.Exists(Path.Combine(current, "dash", "server.tests")))
            {
                return Path.Combine(current, "dash", "server");
            }

            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException("Could not locate the repository root from the test binary directory.");
    }

    private static IEnumerable<string> EnumerateProductionSources(string serverRoot) =>
        Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(static file => file, StringComparer.Ordinal);

    private static string RelativePath(string serverRoot, string file) =>
        Path.GetRelativePath(serverRoot, file);

    private static int LineNumber(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
