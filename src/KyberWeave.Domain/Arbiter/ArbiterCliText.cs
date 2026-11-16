using System.Globalization;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Arbiter;

/// <summary>The pure text rules the Arbiter command line applies: the Ollama version gate, the project-config sanitiser and the user override's YAML.</summary>
public static class ArbiterCliText
{
    /// <summary>Whether an Ollama <c>/api/version</c> version is 0.35 or later.</summary>
    /// <param name="version">The version string the daemon answered.</param>
    /// <returns>True when the version parses and is at least 0.35.</returns>
    public static bool IsOllamaSupported(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        Match match = Regex.Match(version.Trim(), @"^(?<major>\d+)\.(?<minor>\d+)");
        if (!match.Success)
            return false;

        if (!int.TryParse(
                match.Groups["major"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(
                match.Groups["minor"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minor))
        {
            return false;
        }

        return major > 0 || (major == 0 && minor >= 35);
    }

    /// <summary>
    /// Removes the <c>arbiter.global</c> block from project configuration text, so a project file that wrongly declares
    /// global targets can still be read with the rest of its settings.
    /// </summary>
    /// <param name="yaml">The project configuration text.</param>
    /// <returns>The text without the block, with line endings normalised to <c>\n</c>.</returns>
    public static string StripProjectGlobalTargets(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        string normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');
        List<string> result = new(lines.Length);

        bool inArbiter = false;
        int arbiterIndent = -1;
        bool inGlobal = false;
        int globalIndent = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (!inArbiter)
            {
                if (trimmed.StartsWith("arbiter:", StringComparison.Ordinal))
                {
                    inArbiter = true;
                    arbiterIndent = CountIndent(line);
                    result.Add(line);
                }
                else
                {
                    result.Add(line);
                }

                continue;
            }

            if (!inGlobal)
            {
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    result.Add(line);
                    continue;
                }

                if (CountIndent(line) <= arbiterIndent)
                {
                    inArbiter = false;
                    arbiterIndent = -1;
                    result.Add(line);
                    continue;
                }

                if (trimmed.StartsWith("global:", StringComparison.Ordinal))
                {
                    inGlobal = true;
                    globalIndent = CountIndent(line);
                    continue;
                }

                result.Add(line);
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (CountIndent(line) <= globalIndent)
            {
                inGlobal = false;
                globalIndent = -1;
                result.Add(line);
                continue;
            }
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Renders the user override. Every value is written as a single-quoted YAML scalar
    /// and every control character is rejected, so a value carrying YAML syntax is read
    /// back as the value rather than as structure.
    /// </summary>
    /// <remarks>
    /// The model, and the endpoint, come from the command line. Interpolated raw, a value
    /// containing a colon, a quote or a <c>#</c> changed what the next read saw, and a
    /// newline could append arbitrary keys to the operator's override. Single quotes are
    /// the only YAML quoting with no escape sequences: the one character that cannot
    /// appear inside them is the single quote itself, which is written doubled. A newline
    /// is rejected rather than escaped, because no quoting carries one on a single line.
    /// </remarks>
    /// <param name="kindName">The provider kind token.</param>
    /// <param name="endpoint">The provider endpoint.</param>
    /// <param name="model">The answering model.</param>
    /// <returns>The override file's text.</returns>
    /// <exception cref="ArgumentException">Thrown when a value contains a control character.</exception>
    public static string RenderUserOverride(string kindName, string endpoint, string model) =>
        $"provider:\n  kind: {YamlScalar(kindName)}\n  endpoint: {YamlScalar(endpoint)}\n  model: {YamlScalar(model)}\n";

    private static string YamlScalar(string value)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An arbiter setup value cannot contain newline or other control characters: " +
                "it would not survive the YAML round trip.",
                nameof(value));
        }

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static int CountIndent(string line)
    {
        int count = 0;
        while (count < line.Length && char.IsWhiteSpace(line[count]))
        {
            count++;
        }

        return count;
    }
}
