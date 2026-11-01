using System.ComponentModel;
using System.Text.Json;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     Proves the Kyber-Weave docs MCP server answers for this checkout, over a real stdio session.
/// </summary>
/// <remarks>
///     <para>
///         Process liveness is not enough. The probe runs <c>initialize</c>, then <c>notifications/initialized</c>,
///         then <c>tools/call docs_explore</c>. A non-error result must carry <c>provenance: root=</c> equal to the
///         repository root, which catches a server answering from another checkout. <c>docs_status</c> alone would
///         pass a server whose index never loaded, which is why <c>docs_explore</c> was chosen.
///     </para>
///     <para>
///         Each step's failure is reported with its check id and a fix line. The child's stderr is shown only as its
///         first error line, truncated, and the environment is never read or shown.
///     </para>
/// </remarks>
public static class McpLiveProbe
{
    /// <summary>The MCP protocol revision the probe offers. The server answers with the revision it supports.</summary>
    private const string ProtocolVersion = "2024-11-05";

    /// <summary>The prefix the server puts on the first line of its provenance block.</summary>
    private const string ProvenancePrefix = "provenance: root=";

    /// <summary>The longest a child stderr line is allowed into a failure reason.</summary>
    private const int MaxDetailLength = 160;

    /// <summary>Runs the live proof and returns the <c>mcp.*</c> checks that ran.</summary>
    /// <param name="root">The resolved repository root that the server's provenance must name.</param>
    /// <param name="options">The MCP executable and total session budget.</param>
    public static IReadOnlyList<DoctorCheck> Run(string root, DoctorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);

        List<DoctorCheck> checks = [];
        using StdioSession? session = TryStart(options);
        if (session is null)
        {
            // The reason names the executable only. The start failure's text and the environment are never echoed.
            checks.Add(new DoctorCheck(
                "mcp.start",
                false,
                $"'{options.McpExecutable}' could not be started",
                "kyber-weave update"));
            return checks;
        }

        checks.Add(new DoctorCheck("mcp.start", true, "server process started", null));
        Exchange(session, root, options, checks);

        return checks;
    }

    /// <summary>Starts the session, or returns null when the executable cannot be started. Ownership passes to the caller.</summary>
    private static StdioSession? TryStart(DoctorOptions options)
    {
        try
        {
            return new StdioSession(options.McpExecutable, [], options.McpTimeout);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Exchange(StdioSession session, string root, DoctorOptions options, List<DoctorCheck> checks)
    {
        string budget = Seconds(options.McpTimeout);

        session.SendLine(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":" +
            $"{{\"protocolVersion\":\"{ProtocolVersion}\",\"capabilities\":{{}}," +
            "\"clientInfo\":{\"name\":\"kyber-weave-doctor\",\"version\":\"1\"}}}");

        if (!TryRead(session, out string? initializeReply, out string? handshakeFailure))
        {
            checks.Add(Handshake(false, handshakeFailure!, session, options));
            return;
        }

        if (!TryResult(initializeReply!, out _))
        {
            checks.Add(Handshake(false, "initialize did not return a result", session, options));
            return;
        }

        checks.Add(Handshake(true, $"initialize answered within {budget}s", session, options));

        session.SendLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

        session.SendLine(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":" +
            "{\"name\":\"docs_explore\",\"arguments\":{\"query\":\"README\"}}}");

        if (!TryRead(session, out string? callReply, out string? callFailure))
        {
            checks.Add(new DoctorCheck(
                "mcp.docs_explore",
                false,
                callFailure!,
                $"kyber-weave doctor --check --mcp-timeout {Math.Max(1, (int)options.McpTimeout.TotalSeconds) * 2}"));
            return;
        }

        if (!TryResult(callReply!, out JsonElement result) || !TryText(result, out string text))
        {
            checks.Add(new DoctorCheck("mcp.docs_explore", false, "docs_explore reply was not a tool result", "kyber-weave update"));
            return;
        }

        if (result.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
        {
            checks.Add(new DoctorCheck(
                "mcp.docs_explore",
                false,
                $"docs_explore returned an error: {Truncate(FirstLine(text))}",
                "kyber-weave update"));
            return;
        }

        checks.Add(new DoctorCheck("mcp.docs_explore", true, "docs_explore answered", null));
        checks.Add(CheckRoot(text, root));
    }

    private static DoctorCheck CheckRoot(string text, string root)
    {
        int index = text.IndexOf(ProvenancePrefix, StringComparison.Ordinal);
        if (index < 0)
            return new DoctorCheck(
                "mcp.root",
                false,
                "docs_explore did not report its provenance root",
                "kyber-weave update");

        string served = FirstLine(text[(index + ProvenancePrefix.Length)..]).Trim();
        bool matches = served.Length > 0
                       && string.Equals(Normalise(served), Normalise(root), StringComparison.Ordinal);
        return matches
            ? new DoctorCheck("mcp.root", true, "server answers for this repository", null)
            : new DoctorCheck(
                "mcp.root",
                false,
                $"server answers from '{Truncate(served)}', not this repository",
                "stop the stale kyber-weave-mcp server and re-run: kyber-weave doctor --check");
    }

    private static DoctorCheck Handshake(bool passed, string reason, StdioSession session, DoctorOptions options)
    {
        if (passed)
            return new DoctorCheck("mcp.handshake", true, reason, null);

        string detail = session.FirstErrorLine is { } line ? $" ({Truncate(line)})" : string.Empty;
        return new DoctorCheck(
            "mcp.handshake",
            false,
            reason + detail,
            $"kyber-weave doctor --check --mcp-timeout {Math.Max(1, (int)options.McpTimeout.TotalSeconds) * 2}");
    }

    /// <summary>Reads one reply line, turning a timeout or a closed stream into a reason rather than an exception.</summary>
    private static bool TryRead(StdioSession session, out string? line, out string? failure)
    {
        try
        {
            line = session.ReadLine();
            failure = null;
            return true;
        }
        catch (TimeoutException)
        {
            line = null;
            failure = "the server did not answer within the MCP budget";
            return false;
        }
        catch (EndOfStreamException)
        {
            line = null;
            failure = "the server closed its output before answering";
            return false;
        }
    }

    /// <summary>Parses a JSON-RPC reply and returns its <c>result</c> member.</summary>
    private static bool TryResult(string line, out JsonElement result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("result", out JsonElement value))
            {
                result = value.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // Not a JSON-RPC reply; the caller reports it as such.
        }

        result = default;
        return false;
    }

    /// <summary>Returns the first text content item of a tool result.</summary>
    private static bool TryText(JsonElement result, out string text)
    {
        text = string.Empty;
        if (!result.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out JsonElement value) && value.GetString() is { } found)
            {
                text = found;
                return true;
            }
        }

        return false;
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    private static string Truncate(string text) =>
        text.Length > MaxDetailLength ? text[..MaxDetailLength] + "..." : text;

    private static string Normalise(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
