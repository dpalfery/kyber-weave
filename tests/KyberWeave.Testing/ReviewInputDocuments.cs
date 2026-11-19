using System.Text.Json.Nodes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The findings and gate documents a reviewer hands to <c>review verdict</c>, as JSON text: one otherwise-valid
/// document with a single member replaced, and the spelling fixtures one case varies. Shared by the unit suite that
/// reads a document as an operand and the suite that reads one from a prior cycle's folder.
/// </summary>
internal static class ReviewInputDocuments
{
    /// <summary>The spellings of a property name a case runs: lowercase, PascalCase and UPPERCASE.</summary>
    public static readonly string[] Spellings = ["lower", "Pascal", "UPPER"];

    /// <summary>An out-of-range severity or confidence under every spelling of the list and field keys.</summary>
    public static TheoryData<string, string, string, string> RangeSpellings()
    {
        TheoryData<string, string, string, string> data = [];
        foreach (string listKind in Spellings)
            foreach (string fieldKind in Spellings)
                foreach ((string field, string value) in new[] { ("severity", "7"), ("confidence", "999") })
                    data.Add(listKind, fieldKind, field, value);
        return data;
    }

    /// <summary>Each list a findings document consumes, under every spelling of its key.</summary>
    public static TheoryData<string, string> KnownFindingsLists()
    {
        TheoryData<string, string> data = [];
        foreach (string kind in Spellings)
            foreach (string key in new[] { "findings", "changedPaths", "coverage" })
                data.Add(key, kind);
        return data;
    }

    /// <summary>Each list a gate report consumes, under every spelling of its key.</summary>
    public static TheoryData<string, string> KnownGateLists()
    {
        TheoryData<string, string> data = [];
        foreach (string kind in Spellings)
            foreach (string key in new[] { "gates", "coverageReports" })
                data.Add(key, kind);
        return data;
    }

    /// <summary>Each object of a findings document, given a second spelling of one of its names, with the JSON path and both spellings.</summary>
    public static TheoryData<string, string, string, string> DuplicateFindingsKeys() => new()
    {
        { "document-list", "$", "findings", "FINDINGS" },
        { "document-scalar", "$", "changedLines", "ChangedLines" },
        { "finding-severity", "$.findings[0]", "severity", "Severity" },
        { "finding-snake", "$.findings[0]", "failure_scenario", "FAILURE_SCENARIO" },
        { "finding-camel", "$.findings[0]", "failureScenario", "FailureScenario" },
        { "finding-snake-and-camel", "$.findings[0]", "failure_scenario", "failureScenario" },
        { "finding-snake-and-pascal", "$.findings[0]", "failure_scenario", "FailureScenario" },
        { "finding-upper-snake-and-camel", "$.findings[0]", "FAILURE_SCENARIO", "failureScenario" },
        { "coverage-entry", "$.coverage[0]", "lens", "LENS" }
    };

    /// <summary>Each object of a gate report, given a second spelling of one of its names, with the JSON path and both spellings.</summary>
    public static TheoryData<string, string, string, string> DuplicateGateKeys() => new()
    {
        { "gate-document", "$", "gates", "GATES" },
        { "gate-result", "$.gates[0]", "id", "ID" },
        { "coverage-report", "$.coverageReports[0]", "path", "Path" },
        { "coverage-result", "$.coverageReports[0].coverage", "fileLinePercent", "FILELINEPERCENT" }
    };

    /// <summary>A valid findings document with one finding and one coverage entry, then one object given a second spelling of a name.</summary>
    public static string DuplicateFindingsDocument(string caseName)
    {
        JsonObject finding = FindingObject("severity", "\"minor\"");
        JsonObject coverage = new() { ["lens"] = "correctness", ["outcome"] = "findings", ["reason"] = null };
        JsonObject document = new()
        {
            ["schema"] = "kyber-weave.review-findings/v1",
            ["findings"] = new JsonArray(finding),
            ["changedPaths"] = new JsonArray(),
            ["changedLines"] = 0,
            ["coverage"] = new JsonArray(coverage)
        };
        switch (caseName)
        {
            case "document-list": document["FINDINGS"] = new JsonArray(); break;
            case "document-scalar": document["ChangedLines"] = 1; break;
            case "finding-severity": finding["Severity"] = "critical"; break;
            case "finding-snake":
                finding.Remove("failureScenario");
                finding["failure_scenario"] = "a";
                finding["FAILURE_SCENARIO"] = "b";
                break;
            case "finding-camel": finding["FailureScenario"] = "b"; break;
            case "finding-snake-and-camel": finding["failure_scenario"] = "a"; break;
            case "finding-snake-and-pascal":
                finding.Remove("failureScenario");
                finding["failure_scenario"] = "a";
                finding["FailureScenario"] = "b";
                break;
            case "finding-upper-snake-and-camel": finding["FAILURE_SCENARIO"] = "a"; break;
            case "coverage-entry": coverage["LENS"] = "security"; break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null);
        }

        return document.ToJsonString();
    }

    /// <summary>A valid gate report with one gate and one coverage report, then one object given a second spelling of a name.</summary>
    public static string DuplicateGateDocument(string caseName)
    {
        JsonObject gate = new() { ["id"] = "build", ["blocking"] = true, ["exitCode"] = 0, ["summary"] = "ok" };
        JsonObject result = new() { ["fileLinePercent"] = 80, ["classLinePercent"] = 80 };
        JsonObject report = new() { ["path"] = "a.xml", ["coverage"] = result };
        JsonObject document = new()
        {
            ["schema"] = "kyber-weave.review-gates/v1",
            ["gates"] = new JsonArray(gate),
            ["coverageReports"] = new JsonArray(report)
        };
        switch (caseName)
        {
            case "gate-document": document["GATES"] = new JsonArray(); break;
            case "gate-result": gate["ID"] = "other"; break;
            case "coverage-report": report["Path"] = "b.xml"; break;
            case "coverage-result": result["FILELINEPERCENT"] = 10; break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null);
        }

        return document.ToJsonString();
    }

    /// <summary>A findings document holding one finding, every property name written in the spelling <paramref name="kind"/> names.</summary>
    public static string SpelledFindingsDocument(string kind, string severity)
    {
        JsonObject finding = FindingObject("severity", JsonValue.Create(severity)!.ToJsonString());
        JsonObject document = new()
        {
            ["schema"] = "kyber-weave.review-findings/v1",
            ["findings"] = new JsonArray(SpellKeys(finding, kind)),
            ["changedPaths"] = new JsonArray(),
            ["changedLines"] = 0
        };
        return SpellKeys(document, kind).ToJsonString();
    }

    /// <summary>The same object with every property name in the spelling <paramref name="kind"/> names (the values are untouched).</summary>
    public static JsonObject SpellKeys(JsonObject node, string kind)
    {
        JsonObject spelled = [];
        foreach (KeyValuePair<string, JsonNode?> property in node)
            spelled[Spell(property.Key, kind)] = property.Value?.DeepClone();
        return spelled;
    }

    /// <summary>
    /// Asserts the one refusal names the file, the object's path and both spellings, each path-qualified
    /// (<c>$.findings[0].severity</c>), so a message that merely contains <c>$</c> or the file name does not pass.
    /// </summary>
    public static void AssertRefusedForDuplicate(
        CapturedConsoleExecution<int> run, string file, string path, string first, string second)
    {
        Assert.Equal(1, run.Result);
        Assert.Contains(ReviewDiagnosticAssert.MessagesWithCode(run.Output, "KW-REVIEW-023"), message =>
            message.Contains(file, StringComparison.Ordinal)
            && message.Contains($"{path}.{first}", StringComparison.Ordinal)
            && message.Contains($"{path}.{second}", StringComparison.Ordinal));
    }

    /// <summary>The spelling of <paramref name="key"/> a case names: all lowercase, PascalCase, or all uppercase.</summary>
    public static string Spell(string key, string kind) => kind switch
    {
        "lower" => string.Concat(key.Select(char.ToLowerInvariant)),
        "Pascal" => char.ToUpperInvariant(key[0]) + key[1..],
        _ => key.ToUpperInvariant()
    };

    /// <summary>One otherwise-valid finding as JSON text, with <paramref name="field"/> set to the raw JSON <paramref name="rawValue"/>.</summary>
    public static string FindingJson(string field, string rawValue) => FindingObject(field, rawValue).ToJsonString();

    /// <summary>One otherwise-valid finding, with <paramref name="field"/> set to the raw JSON <paramref name="rawValue"/>.</summary>
    public static JsonObject FindingObject(string field, string rawValue)
    {
        JsonObject finding = new()
        {
            ["id"] = "correctness/off-by-one",
            ["lens"] = "correctness",
            ["severity"] = "major",
            ["confidence"] = 9,
            ["file"] = "src/Foo.cs",
            ["line"] = 12,
            ["excerpt"] = "if (i < count)",
            ["claim"] = "The loop bound excludes the final element.",
            ["evidence"] = "src/Foo.cs:12",
            ["failureScenario"] = "The last element is never processed.",
            ["suggestion"] = "Use <= or count + 1."
        };
        finding[field] = JsonNode.Parse(rawValue);
        return finding;
    }

    /// <summary>
    /// A findings document as JSON text. <paramref name="member"/> is one extra raw top-level member, or a replacement list for
    /// a known one; <paramref name="findings"/> is the raw JSON of the single finding the document holds, or none.
    /// </summary>
    public static string FindingsDocument(string? member = null, string? findings = null)
    {
        string list = findings is null ? "[]" : "[" + findings + "]";
        string known = member is not null && member.StartsWith("\"findings\":", StringComparison.Ordinal) ? string.Empty : $"\"findings\":{list},";
        string changed = member is not null && member.StartsWith("\"changedPaths\":", StringComparison.Ordinal) ? string.Empty : "\"changedPaths\":[],";
        return "{\"schema\":\"kyber-weave.review-findings/v1\"," + known + changed + "\"changedLines\":0" +
               (member is null ? string.Empty : "," + member) + "}";
    }

    /// <summary>A findings document as JSON text whose <paramref name="key"/> holds <paramref name="value"/> under the spelling <paramref name="kind"/> names.</summary>
    public static string FindingsObject(string key, string kind, JsonNode value)
    {
        JsonObject document = new()
        {
            ["schema"] = "kyber-weave.review-findings/v1",
            ["findings"] = new JsonArray(),
            ["changedPaths"] = new JsonArray(),
            ["changedLines"] = 0
        };
        document.Remove(key);
        document[Spell(key, kind)] = value;
        return document.ToJsonString();
    }

    /// <summary>Renames one property of an object in place, keeping its value.</summary>
    public static void RenameKey(JsonObject node, string from, string to)
    {
        JsonNode? value = node[from];
        node.Remove(from);
        node[to] = value;
    }
}
