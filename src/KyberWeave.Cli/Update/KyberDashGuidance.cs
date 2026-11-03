namespace KyberWeave.Cli.Update;

/// <summary>
///     Reads the guidance that KyberDash prints from its capture-plugin verb. KyberDash owns the wording
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

    /// <summary>
    ///     The guidance a capture-plugin verb printed after its verdict, in printed order. The OpenCode verb marks
    ///     every guidance line with its harness prefix and prints other chatter beside them, so only the marked lines
    ///     are relayed. The Factory verb's stdout is a contract — every line of it that begins with <c>factory</c>
    ///     is relayed, except the verdict line itself, and the verb's other chatter is not. One of the relayed
    ///     lines names the harness CLI rather than the plugin.
    /// </summary>
    /// <param name="harness">
    ///     The harness the verb installed the plugin for, named as the verb names it: <c>opencode</c> or
    ///     <c>factory</c>. The self-updater and the Squad step both pass the token the verb itself takes.
    /// </param>
    /// <param name="standardOutput">Everything the verb printed on stdout.</param>
    /// <param name="verdict">The verdict line, which is the verb's own summary and never guidance.</param>
    internal static IReadOnlyList<string> CapturePluginGuidanceLines(
        string harness,
        string standardOutput,
        string verdict)
    {
        if (harness == "opencode")
            return HarnessOpenCodeLines(standardOutput);

        List<string> lines = [];
        foreach (string raw in standardOutput.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line == verdict || !line.StartsWith("factory", StringComparison.Ordinal))
                continue;

            lines.Add(line);
        }

        return lines;
    }
}
