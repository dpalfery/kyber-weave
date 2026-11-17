using System.Text.Json;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Assertions over the JSON a review command prints under <c>--format json</c>. A fact that checks a rule id and
/// its severity, and the message it carries, names the defect it guards. A bare word matched anywhere in the output
/// can come from another diagnostic or a count, so it does not.
/// </summary>
internal static class ReviewDiagnosticAssert
{
    /// <summary>Asserts the output carries a diagnostic with this rule id at this severity, and, when given, a message containing the fragment.</summary>
    public static void AssertDiagnostic(string output, string code, string severity, string? messageFragment = null)
    {
        using JsonDocument document = JsonDocument.Parse(JsonPayload(output));
        Assert.True(
            ContainsDiagnostic(document.RootElement, code, severity, messageFragment),
            $"Expected {code} at {severity}{(messageFragment is null ? string.Empty : $" mentioning '{messageFragment}'")}. " +
            $"Output:{Environment.NewLine}{output}");
    }

    /// <summary>Asserts the output carries a diagnostic with this rule id, at any severity.</summary>
    public static void AssertDiagnosticCode(string output, string code)
    {
        using JsonDocument document = JsonDocument.Parse(JsonPayload(output));
        Assert.True(
            ContainsDiagnostic(document.RootElement, code, severity: null, messageFragment: null),
            $"Expected a {code} diagnostic. Output:{Environment.NewLine}{output}");
    }

    /// <summary>Every message the output carries under this rule id, in output order, so a fact can count them or read them.</summary>
    public static List<string> MessagesWithCode(string output, string code)
    {
        using JsonDocument document = JsonDocument.Parse(JsonPayload(output));
        List<string> messages = [];
        CollectMessages(document.RootElement, code, messages);
        return messages;
    }

    private static void CollectMessages(JsonElement element, string code, List<string> messages)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                CollectMessages(item, code, messages);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("code", out JsonElement foundCode) && foundCode.GetString() == code
            && element.TryGetProperty("message", out JsonElement foundMessage))
            messages.Add(foundMessage.GetString() ?? string.Empty);

        foreach (JsonProperty property in element.EnumerateObject())
            CollectMessages(property.Value, code, messages);
    }

    private static string JsonPayload(string output)
    {
        char[] opening = ['{', '['];
        char[] closing = ['}', ']'];
        int first = output.IndexOfAny(opening);
        int last = output.LastIndexOfAny(closing);
        Assert.True(first >= 0 && last > first, $"No JSON payload in the output:{Environment.NewLine}{output}");
        return output[first..(last + 1)];
    }

    private static bool ContainsDiagnostic(JsonElement element, string code, string? severity, string? messageFragment)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                if (ContainsDiagnostic(item, code, severity, messageFragment))
                    return true;

            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty("code", out JsonElement foundCode) && foundCode.GetString() == code
            && (severity is null
                || (element.TryGetProperty("severity", out JsonElement foundSeverity)
                    && string.Equals(foundSeverity.GetString(), severity, StringComparison.OrdinalIgnoreCase)))
            && (messageFragment is null
                || (element.TryGetProperty("message", out JsonElement foundMessage)
                    && (foundMessage.GetString() ?? string.Empty).Contains(messageFragment, StringComparison.Ordinal))))
            return true;

        foreach (JsonProperty property in element.EnumerateObject())
            if (ContainsDiagnostic(property.Value, code, severity, messageFragment))
                return true;

        return false;
    }
}
