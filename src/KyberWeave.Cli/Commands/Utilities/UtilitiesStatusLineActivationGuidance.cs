using KyberWeave.Core.Utilities.StatusLine;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Builds the per-harness activation snippet a deploy or doctor run prints.</summary>
/// <remarks>
///     The snippet names the deployed artifact by its <em>absolute</em> path — never a home-relative one,
///     because <c>agy</c> does not expand <c>~</c> and a snippet that leans on shell expansion is wrong
///     for a settings file. It names the staging path, never a harness settings path: the settings file
///     the snippet is applied to is one Kyber Utilities never writes or reads (A4, C2, D5), so printing
///     its location would invite the operator to hand it to the tool.
/// </remarks>
internal static class UtilitiesStatusLineActivationGuidance
{
    /// <summary>The one line every snippet ends with: activation is the operator's own edit.</summary>
    public const string ApplyYourselfLine =
        "Kyber Utilities never edits your settings. Apply this snippet yourself.";

    /// <summary>
    ///     The snippet lines for <paramref name="target" />, with <paramref name="commandAbsolutePath" />
    ///     already resolved.
    /// </summary>
    public static IReadOnlyList<string> Build(StatusLineTarget target, string commandAbsolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandAbsolutePath);

        return target switch
        {
            StatusLineTarget.Claude =>
            [
                "Claude Code: add this to your Claude settings under 'statusLine':",
                "{",
                "  \"statusLine\": {",
                "    \"type\": \"command\",",
                $"    \"command\": \"{commandAbsolutePath}\"",
                "  }",
                "}"
            ],
            StatusLineTarget.Agy =>
            [
                "Antigravity CLI (agy): add this to your agy settings under 'statusLine'.",
                "agy does not expand '~', so the command is an absolute path:",
                "{",
                "  \"statusLine\": {",
                "    \"type\": \"command\",",
                $"    \"command\": \"{commandAbsolutePath}\"",
                "  }",
                "}"
            ],
            StatusLineTarget.Pi =>
            [
                "Pi: add this absolute path to the 'extensions' array in your settings.json:",
                "{",
                $"  \"extensions\": [\"{commandAbsolutePath}\"]",
                "}",
                "Pi auto-loads extensions from '~/.pi/agent/extensions/', which Kyber Utilities never",
                "writes to. If a collector package still registers its own footer, you may see two."
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No activation guidance exists for this target.")
        };
    }
}
