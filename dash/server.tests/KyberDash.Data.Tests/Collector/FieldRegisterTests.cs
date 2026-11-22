using System.Reflection;
using System.Text.RegularExpressions;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Verifies that <c>docs/dash/field-register.md</c> documents every native reader and wire-ingest
/// type property per harness with either a <see cref="RelationalSchemaCatalog"/> mapping or an
/// explicit drop reason, and notes <c>zcode</c> as unevidenced.
/// </summary>
public partial class FieldRegisterTests
{
    private static readonly Type[] SharedTranscriptTypes =
    [
        typeof(SourceRecordEnvelope),
        typeof(SourceRecordProvenance),
        typeof(ParsedProviderCall),
        typeof(ListedSkillsPayload),
        typeof(ListedSkillItem),
        typeof(ReaderTurn),
        typeof(ReaderToolCall),
        typeof(ReaderToolResult),
        typeof(ContentPart),
        typeof(RefreshSourceInput),
    ];

    private static readonly Type[] SharedOtlpTypes =
    [
        typeof(OtlpSpan),
        typeof(OtlpStatus),
        typeof(OtlpScope),
        typeof(OtlpLog),
        typeof(OtlpMetric),
        typeof(OtlpMetricDataPoint),
        typeof(RawSpan),
    ];

    private static readonly Type[] ClaudeExtraTypes =
    [
        typeof(ClaudeTurnGroup),
        typeof(TranscriptReaderRepository).GetNestedType("ClaudeUsageLine", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected nested type TranscriptReaderRepository.ClaudeUsageLine."),
    ];

    private static readonly Type[] DroidExtraTypes =
    [
        typeof(FactoryWireSpan),
        typeof(FactoryWireEntry),
        typeof(FactoryWireValue),
        typeof(FactoryMapOptions),
    ];

    private static readonly IReadOnlyDictionary<string, Type[]> ExpectedHarnessTypes =
        new Dictionary<string, Type[]>(StringComparer.Ordinal)
        {
            ["claude-code"] = [.. ClaudeExtraTypes, .. SharedTranscriptTypes, .. SharedOtlpTypes],
            ["codex"] = [.. SharedTranscriptTypes, .. SharedOtlpTypes],
            ["droid"] = [.. DroidExtraTypes, .. SharedOtlpTypes],
            ["opencode"] = SharedOtlpTypes,
            ["copilot"] = SharedOtlpTypes,
            ["antigravity"] = SharedOtlpTypes,
            ["gemini"] = SharedOtlpTypes,
            ["pi"] = SharedOtlpTypes,
        };

    private static readonly HashSet<string> PlaceholderTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
        "-",
        "—",
        "–",
        "n/a",
        "na",
        "none",
        "tbd",
        "todo",
    };

    [Fact]
    public void FieldRegisterDocument_ExistsWithGovernedReferenceFrontmatter()
    {
        string path = ResolveFieldRegisterPath();
        Assert.True(File.Exists(path), $"Expected field register document at '{path}'.");

        string markdown = File.ReadAllText(path);
        IReadOnlyDictionary<string, string> frontmatter = ParseFrontmatterScalars(markdown);

        Assert.Equal("dash/field-register", frontmatter["id"]);
        Assert.Equal("reference", frontmatter["doc-type"]);
        Assert.Equal("KyberDash", frontmatter["component"]);
        Assert.Equal("dpalfery", frontmatter["owner"]);
        Assert.True(
            frontmatter.TryGetValue("status", out string? status) && !string.IsNullOrWhiteSpace(status),
            "Frontmatter must declare a non-empty status.");
        Assert.True(
            frontmatter.TryGetValue("last-reviewed", out string? reviewed) && DateOnly.TryParse(reviewed, out _),
            "Frontmatter must declare a valid ISO last-reviewed date.");
    }

    [Fact]
    public void FieldRegisterDocument_CoversEveryDefaultAdapterAndZcodeUnevidencedNote()
    {
        string path = ResolveFieldRegisterPath();
        Assert.True(File.Exists(path), $"Expected field register document at '{path}'.");

        string markdown = File.ReadAllText(path);
        IReadOnlyDictionary<string, RegisterSection> sections = ParseSections(markdown);

        foreach (IHarnessAdapter adapter in CanonAdapters.DefaultAdapters)
        {
            Assert.True(
                sections.ContainsKey(adapter.Name),
                $"Field register is missing a '## {adapter.Name}' section for default adapter '{adapter.Name}'.");
        }

        foreach (string harness in ExpectedHarnessTypes.Keys)
        {
            Assert.True(
                sections.ContainsKey(harness),
                $"Field register is missing expected harness section '## {harness}'.");
        }

        Assert.True(
            sections.TryGetValue("zcode", out RegisterSection? zcodeSection),
            "Field register must include a '## zcode' section.");
        Assert.Contains("unevidenced", zcodeSection.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(zcodeSection.Rows);
    }

    [Fact]
    public void FieldRegisterDocument_ReflectsEveryNativeReaderPropertyWithValidMappingOrDropReason()
    {
        string path = ResolveFieldRegisterPath();
        Assert.True(File.Exists(path), $"Expected field register document at '{path}'.");

        string markdown = File.ReadAllText(path);
        IReadOnlyDictionary<string, RegisterSection> sections = ParseSections(markdown);

        foreach (KeyValuePair<string, Type[]> harnessEntry in ExpectedHarnessTypes)
        {
            string harness = harnessEntry.Key;
            Type[] expectedTypes = harnessEntry.Value;

            Assert.True(
                sections.TryGetValue(harness, out RegisterSection? section),
                $"Missing section '## {harness}' in '{path}'.");
            Assert.NotEmpty(section.Rows);

            Dictionary<(string TypeName, string PropertyName), RegisterRow> documentedByKey = [];
            foreach (RegisterRow row in section.Rows)
            {
                (string TypeName, string PropertyName) key = (row.NativeType, row.Property);
                Assert.True(
                    documentedByKey.TryAdd(key, row),
                    $"Duplicate register entry '{row.NativeType}.{row.Property}' in section '## {harness}'.");

                ValidateRowDisposition(harness, row);
            }

            HashSet<(string TypeName, string PropertyName)> reflectedKeys = [];
            foreach (Type nativeType in expectedTypes)
            {
                PropertyInfo[] properties = GetReflectedProperties(nativeType);
                Assert.NotEmpty(properties);

                foreach (PropertyInfo property in properties)
                {
                    (string TypeName, string PropertyName) key = (nativeType.Name, property.Name);
                    Assert.True(reflectedKeys.Add(key));
                    Assert.True(
                        documentedByKey.ContainsKey(key),
                        $"Native property '{nativeType.Name}.{property.Name}' has no documented row under '## {harness}' in '{path}'.");
                }
            }

            foreach ((string TypeName, string PropertyName) documentedKey in documentedByKey.Keys)
            {
                Assert.True(
                    reflectedKeys.Contains(documentedKey),
                    $"Stale or unknown register entry '{documentedKey.TypeName}.{documentedKey.PropertyName}' under '## {harness}' does not match any reflected property.");
            }
        }
    }

    [Fact]
    public void FieldRegisterParser_RejectsDroppedRowWithoutReasonAndUnknownDisposition()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ValidateRowDisposition(
                "claude-code",
                new RegisterRow("ReaderTurn", "ContextWindow", "dropped", "—", "—")));

        Assert.Throws<InvalidOperationException>(() =>
            ValidateRowDisposition(
                "claude-code",
                new RegisterRow("ReaderTurn", "ContextWindow", "ignored", "—", "Some reason")));

        Assert.Throws<InvalidOperationException>(() =>
            ValidateRowDisposition(
                "claude-code",
                new RegisterRow("ReaderTurn", "Parts", "mapped", "`nonexistent_table.missing_column`", "Mapped")));
    }

    private static void ValidateRowDisposition(string harness, RegisterRow row)
    {
        if (string.Equals(row.Disposition, "mapped", StringComparison.Ordinal))
        {
            if (IsPlaceholder(row.TargetMapping))
            {
                throw new InvalidOperationException(
                    $"Mapped entry '{row.NativeType}.{row.Property}' under '## {harness}' must specify a target mapping.");
            }

            MatchCollection catalogRefs = CatalogColumnRefRegex().Matches(row.TargetMapping);
            foreach (Match match in catalogRefs)
            {
                string tableName = match.Groups["table"].Value;
                string columnName = match.Groups["column"].Value;
                if (!RelationalSchemaCatalog.TryGetTable(tableName, out TableDefinition? table) || table is null)
                {
                    throw new InvalidOperationException(
                        $"Entry '{row.NativeType}.{row.Property}' under '## {harness}' references unknown catalog table '{tableName}'.");
                }

                bool hasColumn = table.Columns.Any(
                    col => string.Equals(col.Name, columnName, StringComparison.Ordinal));
                if (!hasColumn)
                {
                    throw new InvalidOperationException(
                        $"Entry '{row.NativeType}.{row.Property}' under '## {harness}' references unknown catalog column '{tableName}.{columnName}'.");
                }
            }

            if (catalogRefs.Count == 0 && !row.TargetMapping.Contains("nested:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Mapped entry '{row.NativeType}.{row.Property}' under '## {harness}' must reference at least one backticked 'table.column' or 'nested: <Type>'.");
            }

            return;
        }

        if (string.Equals(row.Disposition, "dropped", StringComparison.Ordinal))
        {
            if (IsPlaceholder(row.Reason))
            {
                throw new InvalidOperationException(
                    $"Dropped entry '{row.NativeType}.{row.Property}' under '## {harness}' must state a non-empty reason.");
            }

            return;
        }

        throw new InvalidOperationException(
            $"Unknown disposition '{row.Disposition}' for '{row.NativeType}.{row.Property}' under '## {harness}'. Expected 'mapped' or 'dropped'.");
    }

    private static bool IsPlaceholder(string value) =>
        PlaceholderTexts.Contains(value.Trim());

    private static PropertyInfo[] GetReflectedProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(static property =>
                property.GetMethod is not null
                && !string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
            .ToArray();

    private static string ResolveFieldRegisterPath()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string slnCandidate = Path.Combine(current.FullName, "dash", "server", "KyberDash.sln");
            string docsCandidate = Path.Combine(current.FullName, "docs");
            if (File.Exists(slnCandidate) && Directory.Exists(docsCandidate))
            {
                return Path.Combine(docsCandidate, "dash", "field-register.md");
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root containing 'dash/server/KyberDash.sln' from '{AppContext.BaseDirectory}'.");
    }

    private static IReadOnlyDictionary<string, string> ParseFrontmatterScalars(string markdown)
    {
        string normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Field register markdown must open with YAML frontmatter.");
        }

        int closing = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closing < 0)
        {
            throw new InvalidOperationException("Field register YAML frontmatter is missing a closing '---' fence.");
        }

        string yaml = normalized[4..closing];
        Dictionary<string, string> scalars = new(StringComparer.Ordinal);
        foreach (string line in yaml.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            scalars[key] = value;
        }

        return scalars;
    }

    private static IReadOnlyDictionary<string, RegisterSection> ParseSections(string markdown)
    {
        string normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        Dictionary<string, RegisterSection> sections = new(StringComparer.Ordinal);

        string? currentName = null;
        List<string> currentLines = [];

        foreach (string line in normalized.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (currentName is not null)
                {
                    sections[currentName] = BuildSection(currentName, currentLines);
                }

                currentName = line[3..].Trim();
                currentLines = [];
                continue;
            }

            if (currentName is not null)
            {
                currentLines.Add(line);
            }
        }

        if (currentName is not null)
        {
            sections[currentName] = BuildSection(currentName, currentLines);
        }

        return sections;
    }

    private static RegisterSection BuildSection(string name, List<string> lines)
    {
        List<RegisterRow> rows = [];
        bool inTableHeader = false;
        bool sawSeparator = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('|'))
            {
                inTableHeader = false;
                sawSeparator = false;
                continue;
            }

            string[] cells = SplitTableRow(line);
            if (cells.Length != 5)
            {
                throw new InvalidOperationException(
                    $"Expected 5 columns in field-register table under '## {name}', found {cells.Length}: '{line}'.");
            }

            if (!inTableHeader)
            {
                if (string.Equals(cells[0], "Native type", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cells[1], "Property", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cells[2], "Disposition", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cells[3], "Target mapping", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cells[4], "Reason", StringComparison.OrdinalIgnoreCase))
                {
                    inTableHeader = true;
                    sawSeparator = false;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Unexpected table header under '## {name}': '{line}'.");
            }

            if (!sawSeparator)
            {
                if (cells.All(static cell => cell.All(static ch => ch is '-' or ':')))
                {
                    sawSeparator = true;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Missing markdown separator row after table header under '## {name}'.");
            }

            rows.Add(new RegisterRow(
                Unbacktick(cells[0]),
                Unbacktick(cells[1]),
                Unbacktick(cells[2]),
                cells[3].Trim(),
                cells[4].Trim()));
        }

        return new RegisterSection(name, string.Join('\n', lines), rows);
    }

    private static string[] SplitTableRow(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|')
            .Select(static cell => cell.Trim())
            .ToArray();
    }

    private static string Unbacktick(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed.StartsWith('`') && trimmed.EndsWith('`')
            ? trimmed[1..^1]
            : trimmed;
    }

    [GeneratedRegex(@"`(?<table>[a-z][a-z0-9_]*)\.(?<column>[a-z][a-z0-9_]*)`", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogColumnRefRegex();

    private sealed record RegisterRow(
        string NativeType,
        string Property,
        string Disposition,
        string TargetMapping,
        string Reason);

    private sealed record RegisterSection(
        string Name,
        string Body,
        IReadOnlyList<RegisterRow> Rows);
}
