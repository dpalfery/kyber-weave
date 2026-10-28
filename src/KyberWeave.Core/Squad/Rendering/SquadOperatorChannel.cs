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
internal static class SquadOperatorChannel
{
    internal const string GateVariable = "KYBER_SQUAD_OPERATOR_CHANNEL";

    internal const string ServerVariable = "KYBER_SQUAD_OPERATOR_CHANNEL_SERVER";

    /// <summary>The token in the overlay that is replaced with the validated server name.</summary>
    internal const string ServerPlaceholder = "{{operator-channel-server}}";

    /// <summary>The longest server name the renderer accepts.</summary>
    internal const int MaxServerNameLength = 64;

    internal const string ConductorAgentName = "conductor";

    /// <summary>The overlay file, relative to the canonical Squad source root.</summary>
    internal const string OverlayRelativePath = "agents/conductor/overlays/operator-channel.md";

    internal const string ServerAbsentCode = "operator-channel-server-absent";
    internal const string ServerNameMissingCode = "operator-channel-server-name-missing";
    internal const string ServerNameInvalidCode = "operator-channel-server-name-invalid";

    /// <summary>Internal marker: the overlay was inlined. Never shown on the CLI.</summary>
    internal const string OverlayAppliedCode = "operator-channel-overlay-applied";
    internal const string NotRenderedCode = "operator-channel-not-rendered";

    internal const string NotRenderedDetails =
        "The operator channel overlay renders for Copilot CLI global scope only, so this target's conductor is unchanged.";

    /// <summary>
    /// The renderer warning codes the install and update output shows. Every other renderer warning stays off
    /// the CLI, so a default install prints exactly what it printed before the overlay existed.
    /// </summary>
    internal static bool IsOperatorChannelWarning(string code) =>
        code is ServerAbsentCode or ServerNameMissingCode or ServerNameInvalidCode;

    /// <summary>True only when the gate variable is exactly <c>true</c>, compared ordinally.</summary>
    internal static bool IsGateOn()
    {
        return string.Equals(Environment.GetEnvironmentVariable(GateVariable), "true", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the server name from the environment. It returns either a valid name or the warning that
    /// turns the overlay off. The warning never carries the raw value, because a value that failed
    /// validation must not reach any output.
    /// </summary>
    internal static ServerName ResolveServerName()
    {
        string? raw = Environment.GetEnvironmentVariable(ServerVariable);
        if (string.IsNullOrEmpty(raw))
        {
            return new ServerName(
                null,
                Warning(
                    ServerNameMissingCode,
                    $"The operator channel is enabled, but {ServerVariable} is unset or empty. " +
                    "The conductor was rendered without the operator channel. Set it to the name of your MCP server."));
        }

        if (!IsValidServerName(raw))
        {
            return new ServerName(
                null,
                Warning(
                    ServerNameInvalidCode,
                    $"{ServerVariable} must be 1 to {MaxServerNameLength} ASCII letters, digits, '.', '_' or '-'. " +
                    "The value was not used, so the conductor was rendered without the operator channel."));
        }

        return new ServerName(raw, null);
    }

    /// <summary>Accepts exactly the names matching <c>^[A-Za-z0-9._-]{1,64}$</c>, compared ordinally.</summary>
    /// <remarks>
    /// Written as a loop rather than a regular expression, because <c>$</c> also matches before a trailing
    /// newline, and a name ending in a newline must still be refused.
    /// </remarks>
    internal static bool IsValidServerName(string candidate)
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
    /// Reads the overlay, substitutes the validated server name for its placeholder, and returns the tool entry
    /// and the body text. A missing overlay is a source defect, so it fails the render rather than shipping a
    /// conductor that promises a channel it does not describe.
    /// </summary>
    internal static Overlay LoadOverlay(string sourceDirectory, string serverName)
    {
        string path = Path.Combine(
            sourceDirectory,
            OverlayRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // The read itself is the check. An exists-then-read pair would race the file system, so the failure
        // is taken from the read and translated into a render error that names the path and the cause.
        string raw;
        try
        {
            raw = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SquadRenderValidationException(
                $"The operator channel overlay is missing from the Squad source at '{path}'.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SquadRenderValidationException(
                $"The operator channel overlay at '{path}' could not be read: {ex.Message}", ex);
        }

        string text = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(ServerPlaceholder, serverName, StringComparison.Ordinal)
            .TrimEnd();
        return new Overlay($"{serverName}/*", text);
    }

    /// <summary>
    /// The marker the renderer adds when the overlay was inlined. It is not a CLI warning. The lifecycle reads it to
    /// decide whether the conductor's body-length warning belongs to the operator channel, and it never reaches output.
    /// </summary>
    internal static SquadRenderWarning OverlayApplied() =>
        Warning(
            OverlayAppliedCode,
            "The operator channel overlay was inlined into the Copilot conductor.");

    private static SquadRenderWarning Warning(string code, string message) =>
        new(code, message, "copilot");

    /// <summary>The tool allow-list entry and the body text of the overlay, ready to render.</summary>
    internal sealed record Overlay(string ToolEntry, string Body);

    /// <summary>
    /// The result of resolving the server name: a valid name, or the warning that turns the overlay off.
    /// Exactly one of the two is set.
    /// </summary>
    internal sealed record ServerName(string? Name, SquadRenderWarning? Warning);
}
