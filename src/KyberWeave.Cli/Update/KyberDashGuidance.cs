namespace KyberWeave.Cli.Update;

/// <summary>
///     Reads the OpenCode guidance that KyberDash prints from its capture-plugin verb. KyberDash owns the wording
///     (ADR 0031, decision D22). Squad and the self-updater only relay it, so neither keeps a restart line of its own.
/// </summary>
internal static class KyberDashGuidance
{
    /// <summary>The marker that starts every guidance line on the verb's stdout.</summary>
    internal const string HarnessOpenCodePrefix = "harness opencode: ";

    /// <summary>
    ///     The guidance lines in a verb's stdout, in printed order, with the marker removed. Any other line, including
    ///     a line that only mentions the marker mid-text, is not guidance and is left out.
    /// </summary>
    internal static IReadOnlyList<string> HarnessOpenCodeLines(string standardOutput)
    {
        List<string> lines = [];
        foreach (string raw in standardOutput.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!line.StartsWith(HarnessOpenCodePrefix, StringComparison.Ordinal))
                continue;

            string copy = line[HarnessOpenCodePrefix.Length..].Trim();
            if (copy.Length > 0)
                lines.Add(copy);
        }

        return lines;
    }
}
