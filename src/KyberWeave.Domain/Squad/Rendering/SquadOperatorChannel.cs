using System.Text;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// The opt-in operator channel overlay for the Copilot CLI conductor: the one place that decides
/// whether the overlay is on, which MCP server it names, what it adds, and where its text lives.
/// </summary>
/// <remarks>
/// Two process environment variables feed it, and this type is their only reader. The gate
/// <c>KYBER_SQUAD_OPERATOR_CHANNEL</c> turns the overlay on only when it is exactly <c>true</c>. The
/// server variable <c>KYBER_SQUAD_OPERATOR_CHANNEL_SERVER</c> names the user's MCP server. Kyber-Weave
/// knows nothing about that server beyond its name, so no product or host name is written in code.
/// Install, update, and doctor all render through the renderers, so they resolve both variables here
/// and agree on the bytes; status compares disk against the receipt and never renders.
/// </remarks>
public static class SquadOperatorChannel
{
    public const string GateVariable = "KYBER_SQUAD_OPERATOR_CHANNEL";

    public const string ServerVariable = "KYBER_SQUAD_OPERATOR_CHANNEL_SERVER";

    /// <summary>The token in the overlay that is replaced with the validated server name.</summary>
    public const string ServerPlaceholder = "{{operator-channel-server}}";

    /// <summary>The longest server name the renderer accepts.</summary>
    public const int MaxServerNameLength = 64;

    public const string ConductorAgentName = "conductor";

    /// <summary>The review council's root agent, the second Arbiter fallback caller.</summary>
    public const string CodeReviewerAgentName = "code-reviewer";

    /// <summary>The file in the Copilot root that declares the user's MCP servers.</summary>
    public const string CopilotMcpConfigFileName = "mcp-config.json";

    /// <summary>The overlay file, relative to the canonical Squad source root.</summary>
    public const string OverlayRelativePath = "agents/conductor/overlays/operator-channel.md";

    public const string ServerAbsentCode = "operator-channel-server-absent";
    public const string ServerNameMissingCode = "operator-channel-server-name-missing";
    public const string ServerNameInvalidCode = "operator-channel-server-name-invalid";

    /// <summary>Internal marker: the overlay was inlined. Never shown on the CLI.</summary>
    public const string OverlayAppliedCode = "operator-channel-overlay-applied";

    public const string NotRenderedCode = "operator-channel-not-rendered";

    public const string NotRenderedDetails =
        "The operator channel overlay renders for Copilot CLI global scope only, so this target's conductor is unchanged.";

    /// <summary>
    /// The renderer warning codes the install and update output shows. Every other renderer warning stays off
    /// the CLI, so a default install prints exactly what it printed before the overlay existed.
    /// </summary>
    public static bool IsOperatorChannelWarning(string code)
    {
        return code is ServerAbsentCode or ServerNameMissingCode or ServerNameInvalidCode;
    }

    /// <summary>True only when the gate variable is exactly <c>true</c>, compared ordinally.</summary>
    public static bool IsGateOn(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return string.Equals(getEnvironmentVariable(GateVariable), "true", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the server name from the environment. It returns either a valid name or the warning that
    /// turns the overlay off. The warning never carries the raw value, because a value that failed
    /// validation must not reach any output.
    /// </summary>
    public static SquadOperatorChannelServerName ResolveServerName(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        string? raw = getEnvironmentVariable(ServerVariable);
        if (string.IsNullOrEmpty(raw))
        {
            return new SquadOperatorChannelServerName(
                null,
                Warning(
                    ServerNameMissingCode,
                    $"The operator channel is enabled, but {ServerVariable} is unset or empty. " +
                    "The conductor was rendered without the operator channel. Set it to the name of your MCP server."));
        }

        if (!IsValidServerName(raw))
        {
            return new SquadOperatorChannelServerName(
                null,
                Warning(
                    ServerNameInvalidCode,
                    $"{ServerVariable} must be 1 to {MaxServerNameLength} ASCII letters, digits, '.', '_' or '-'. " +
                    "The value was not used, so the conductor was rendered without the operator channel."));
        }

        return new SquadOperatorChannelServerName(raw, null);
    }

    /// <summary>Accepts exactly the names matching <c>^[A-Za-z0-9._-]{1,64}$</c>, compared ordinally.</summary>
    /// <remarks>
    /// Written as a loop rather than a regular expression, because <c>$</c> also matches before a trailing
    /// newline, and a name ending in a newline must still be refused.
    /// </remarks>
    public static bool IsValidServerName(string candidate)
    {
        if (candidate.Length is < 1 or > MaxServerNameLength)
        {
            return false;
        }

        foreach (char character in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The marker the renderer adds when the overlay was inlined. It is not a CLI warning. The lifecycle reads it to
    /// decide whether the conductor's body-length warning belongs to the operator channel, and it never reaches output.
    /// </summary>
    public static SquadRenderWarning OverlayApplied()
    {
        return Warning(
            OverlayAppliedCode,
            "The operator channel overlay was inlined into the Copilot conductor.");
    }

    private static SquadRenderWarning Warning(string code, string message)
    {
        return new SquadRenderWarning(code, message, "copilot");
    }
}
