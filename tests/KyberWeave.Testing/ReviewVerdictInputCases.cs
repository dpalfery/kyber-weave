using System.Text.Json.Nodes;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The spellings and documents the verdict's case-insensitive JSON checks are driven with: the three spellings of a
/// property name, one otherwise-valid finding, and the assertions a refused duplicate is held to. Shared by the suites
/// that prove the reader binds names ignoring case.
/// </summary>
internal static class ReviewVerdictInputCases
{
    private const string UnreadableInput = "KW-REVIEW-023";

    private static readonly string[] Spellings = ["lower", "Pascal", "UPPER"];

    /// <summary>An out-of-range severity and confidence under every spelling of the list key and of the field key.</summary>
    public static TheoryData<string, string, string, string> RangeSpellings()
    {
        TheoryData<string, string, string, string> data = [];
        foreach (string listKind in Spellings)
            foreach (string fieldKind in Spellings)
                foreach ((string field, string value) in new[] { ("severity", "7"), ("confidence", "999") })
                    data.Add(listKind, fieldKind, field, value);
        return data;
    }

    /// <summary>Every findings list key under every spelling.</summary>
    public static TheoryData<string, string> KnownFindingsLists()
    {
        TheoryData<string, string> data = [];
        foreach (string kind in Spellings)
            foreach (string key in new[] { "findings", "changedPaths", "coverage" })
                data.Add(key, kind);
        return data;
    }

    /// <summary>A property name in two spellings, with the object path the refusal names.</summary>
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

    /// <summary>A valid findings document with one finding and one coverage entry, then one object given a second spelling of a name.</summary>
    internal static string DuplicateFindingsDocument(string caseName)
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
        }

        return document.ToJsonString();
    }

    /// <summary>A findings document holding one finding, with every property name in the spelling <paramref name="kind"/> names.</summary>
    internal static string SpelledFindingsDocument(string kind, string severity)
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
    internal static JsonObject SpellKeys(JsonObject node, string kind)
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
    internal static void AssertRefusedForDuplicate(
        CapturedConsoleExecution<int> run, string file, string path, string first, string second)
    {
        Assert.Equal(1, run.Result);
        Assert.Contains(MessagesWithCode(run.Output, UnreadableInput), message =>
            message.Contains(file, StringComparison.Ordinal)
            && message.Contains($"{path}.{first}", StringComparison.Ordinal)
            && message.Contains($"{path}.{second}", StringComparison.Ordinal));
    }

    /// <summary>The spelling of <paramref name="key"/> a test names: all lowercase, PascalCase, or all uppercase.</summary>
    internal static string Spell(string key, string kind) => kind switch
    {
        "lower" => string.Concat(key.Select(char.ToLowerInvariant)),
        "Pascal" => char.ToUpperInvariant(key[0]) + key[1..],
        _ => key.ToUpperInvariant()
    };

    /// <summary>One otherwise-valid finding, with <paramref name="field"/> set to the raw JSON <paramref name="rawValue"/>.</summary>
    internal static JsonObject FindingObject(string field, string rawValue)
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
}
