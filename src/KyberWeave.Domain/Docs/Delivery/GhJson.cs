using System.Text.Json;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>
/// Reads the fields of a <c>gh</c> JSON answer. The answer is tool output, so a field is never assumed: each one is
/// read through one of these methods, which say why a field is unusable (missing, the wrong type, empty or only
/// whitespace) instead of turning it into an empty string or a zero.
/// </summary>
internal static class GhJson
{
    /// <summary>A required string that holds at least one character other than whitespace.</summary>
    /// <param name="element">The JSON object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The text, when the field is usable; empty otherwise.</param>
    /// <param name="problem">Why the field is unusable, naming it; null when it is usable.</param>
    internal static bool TryRequiredText(JsonElement element, string name, out string value, out string? problem)
    {
        value = string.Empty;
        if (!TryString(element, name, out string text, out problem))
            return false;

        if (string.IsNullOrWhiteSpace(text))
        {
            problem = $"'{name}' is empty";
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>A required string that may be empty, such as a pull request body.</summary>
    internal static bool TryRequiredString(JsonElement element, string name, out string value, out string? problem) =>
        TryString(element, name, out value, out problem);

    /// <summary>A required whole number that fits an int.</summary>
    internal static bool TryRequiredNumber(JsonElement element, string name, out int value, out string? problem)
    {
        value = 0;
        if (!TryProperty(element, name, out JsonElement property, out problem))
            return false;

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            problem = $"'{name}' is not a whole number";
            return false;
        }

        return true;
    }

    /// <summary>A required true or false.</summary>
    internal static bool TryRequiredBoolean(JsonElement element, string name, out bool value, out string? problem)
    {
        value = false;
        if (!TryProperty(element, name, out JsonElement property, out problem))
            return false;

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problem = $"'{name}' is not true or false";
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    /// <summary>A string property of a JSON object; null when the element is not an object or the property is absent or not a string.</summary>
    internal static string? OptionalText(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryString(JsonElement element, string name, out string value, out string? problem)
    {
        value = string.Empty;
        if (!TryProperty(element, name, out JsonElement property, out problem))
            return false;

        if (property.ValueKind != JsonValueKind.String)
        {
            problem = $"'{name}' is not a string";
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement property, out string? problem)
    {
        property = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = "the answer is not an object";
            return false;
        }

        if (!element.TryGetProperty(name, out property))
        {
            problem = $"'{name}' is missing";
            return false;
        }

        problem = null;
        return true;
    }
}
