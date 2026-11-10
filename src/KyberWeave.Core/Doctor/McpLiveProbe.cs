using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     Proves the Kyber-Weave docs MCP server answers for this checkout, over a real stdio session.
/// </summary>
/// <remarks>
///     <para>
///         Process liveness is not enough. The probe runs <c>initialize</c>, then <c>notifications/initialized</c>,
///         then <c>tools/call docs_answer_question_from_repo_documentation</c>. A non-error result must carry <c>provenance: root=</c> equal to the
///         repository root, which catches a server answering from another checkout. <c>docs_get_source_repository_and_revision</c> alone would
///         pass a server whose index never loaded, which is why <c>docs_answer_question_from_repo_documentation</c> was chosen.
///     </para>
///     <para>
///         Each step's failure is reported with its check id and a fix line. The child's stderr is shown only as its
///         first error line, truncated, and the environment is never read or shown.
///     </para>
/// </remarks>
public static partial class McpLiveProbe
{
    /// <summary>
    ///     The metadata the provenance line carries after the root path, as <c>key=value</c> tokens:
    ///     <c>branch=</c>, <c>repo=</c>, <c>rev=</c>, <c>dirty=</c> and <c>documents=</c>.
    /// </summary>
    /// <remarks>
    ///     The tokens are anchored on the fixed trailing order guaranteed by <c>CorpusProvenance.Line</c>
    ///     (<c>rev=</c>, <c>dirty=</c>, <c>documents=</c> at end-of-line, preceded by optional <c>branch=</c>
    ///     and <c>repo=</c>). Parsing from the right prevents root and repository paths containing token-like
    ///     substrings (such as <c>rev=evil</c> or spaces) from being prematurely truncated.
    /// </remarks>
    [GeneratedRegex(@"(?:(?:\s+branch=\S+)?\s+repo=.*?(?:\s+branch=\S+)?|\s+branch=\S+)?\s+rev=\S+\s+dirty=\S+\s+documents=\S+$")]
    private static partial Regex TrailingMetadataRegex();
    /// <summary>The MCP protocol revision the probe offers. The server answers with the revision it supports.</summary>
    private const string ProtocolVersion = "2024-11-05";

    /// <summary>The prefix the server puts on the first line of its provenance block.</summary>
    private const string ProvenancePrefix = "provenance: root=";

    /// <summary>The flag that stops the server refreshing CodeGraph during the probe's tool call.</summary>
    private const string NoCodeGraphRefreshFlag = "--no-codegraph-refresh";

    /// <summary>The longest a child stderr line is allowed into a failure reason.</summary>
    private const int MaxDetailLength = 160;

    /// <summary>The handshake failure a larger budget can actually help.</summary>
    private const string NoAnswerWithinBudget = "the server did not answer within the MCP budget";

    /// <summary>The rule id the server writes to stderr when the folder has no host configuration.</summary>
    private const string NoHostConfigurationCode = "KW-MCP-ROOT-001";

    /// <summary>How long to let stderr finish once a server has closed its output.</summary>
    private static readonly TimeSpan ServerErrorWait = TimeSpan.FromSeconds(1);

    /// <summary>Runs the live proof and returns the <c>mcp.*</c> checks that ran.</summary>
    /// <param name="root">The resolved repository root that the server's provenance must name.</param>
    /// <param name="options">The MCP executable and total session budget.</param>
    public static IReadOnlyList<DoctorCheck> Run(string root, DoctorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);

        List<DoctorCheck> checks = [];
        using StdioSession? session = TryStart(root, options);
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
    private static StdioSession? TryStart(string root, DoctorOptions options)
    {
        try
        {
            // The server resolves its root from --repo-root first, then KYBER_WEAVE_REPO_ROOT, then the
            // working directory. Passing the flag is what makes the probe answer for the repository this
            // run resolved; without it the child inherits doctor's cwd and a healthy target can fail
            // mcp.root, or pass against the wrong checkout.
            //
            // --no-codegraph-refresh keeps the promise --check makes. The server refreshes CodeGraph on its
            // first tool call, so a probe without this flag would make the read-only verb write an index.
            return new StdioSession(
                options.McpExecutable,
                ["--repo-root", root, NoCodeGraphRefreshFlag],
                options.McpTimeout);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Every one of these means the same thing to the caller: the process did not start. The probe exists to
            // report that as mcp.start, so it must not surface as an exception that skips the renderer.
            return null;
        }
    }

    /// <summary>Sends one line, turning a closed or broken pipe into <c>false</c> rather than an exception.</summary>
    private static bool TrySend(StdioSession session, string line)
    {
        try
        {
            session.SendLine(line);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The remedy a check prints when the server did not answer inside the budget.</summary>
    /// <remarks>
    ///     The suggestion is computed in <see cref="long"/> and capped at <see cref="int.MaxValue"/> seconds, the
    ///     largest value <c>--mcp-timeout</c> parses. Doubling a budget near that limit inside an <see cref="int"/>
    ///     wraps negative, and the remedy would then tell a human to run a command the option refuses.
    /// </remarks>
    private static string TimeoutFix(DoctorOptions options)
    {
        long seconds = Math.Max(1L, (long)Math.Ceiling(options.McpTimeout.TotalSeconds));
        long suggested = Math.Min(seconds * 2L, int.MaxValue);
        return $"kyber-weave doctor --check --mcp-timeout {suggested}";
    }

    private static void Exchange(StdioSession session, string root, DoctorOptions options, List<DoctorCheck> checks)
    {
        string budget = Seconds(options.McpTimeout);

        if (!TrySend(
                session,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":" +
                $"{{\"protocolVersion\":\"{ProtocolVersion}\",\"capabilities\":{{}}," +
                "\"clientInfo\":{\"name\":\"kyber-weave-doctor\",\"version\":\"1\"}}}"))
        {
            // A server that exited between starting and the first request is a failed handshake, not a crash.
            checks.Add(Handshake(false, "the server closed its input before the initialize request", session, options, root));
            return;
        }

        if (!TryRead(session, out string? initializeReply, out string? handshakeFailure))
        {
            checks.Add(Handshake(false, handshakeFailure!, session, options, root));
            return;
        }

        if (!TryResult(initializeReply!, out _))
        {
            checks.Add(Handshake(false, "initialize did not return a result", session, options, root));
            return;
        }

        checks.Add(Handshake(true, $"initialize answered within {budget}s", session, options, root));

        if (!TrySend(session, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")
            || !TrySend(
                session,
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":" +
                "{\"name\":\"docs_answer_question_from_repo_documentation\",\"arguments\":{\"query\":\"README\"}}}"))
        {
            checks.Add(new DoctorCheck(
                "mcp.docs_explore",
                false,
                "the server closed its input before docs_answer_question_from_repo_documentation was requested",
                "stop the kyber-weave-mcp server and re-run: kyber-weave doctor --check"));
            return;
        }

        if (!TryRead(session, out string? callReply, out string? callFailure))
        {
            checks.Add(new DoctorCheck("mcp.docs_explore", false, callFailure!, TimeoutFix(options)));
            return;
        }

        if (!TryResult(callReply!, out JsonElement result) || !TryText(result, out string text))
        {
            checks.Add(new DoctorCheck("mcp.docs_explore", false, "docs_answer_question_from_repo_documentation reply was not a tool result", "kyber-weave update"));
            return;
        }

        if (result.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
        {
            checks.Add(new DoctorCheck(
                "mcp.docs_explore",
                false,
                $"docs_answer_question_from_repo_documentation returned an error: {Truncate(FirstLine(text))}",
                "kyber-weave update"));
            return;
        }

        checks.Add(new DoctorCheck("mcp.docs_explore", true, "docs_answer_question_from_repo_documentation answered", null));
        checks.Add(CheckRoot(text, root));
    }

    private static DoctorCheck CheckRoot(string text, string root)
    {
        int index = text.IndexOf(ProvenancePrefix, StringComparison.Ordinal);
        if (index < 0)
            return new DoctorCheck(
                "mcp.root",
                false,
                "docs_answer_question_from_repo_documentation did not report its provenance root",
                "kyber-weave update");

        string served = FirstLine(text[(index + ProvenancePrefix.Length)..]).Trim();

        // The provenance line carries metadata after the root, e.g. `root=<path> rev=<rev> dirty=<...> documents=<n>`.
        // Strip the metadata while preserving spaces inside the real path, then compare the bare root to the resolved repo.
        Match metadataMatch = TrailingMetadataRegex().Match(served);
        if (metadataMatch.Success)
            served = served[..metadataMatch.Index].Trim();

        bool matches = served.Length > 0 && SamePath(served, root);
        return matches
            ? new DoctorCheck("mcp.root", true, "server answers for this repository", null)
            : new DoctorCheck(
                "mcp.root",
                false,
                $"server answers from '{Truncate(served)}', not this repository",
                "stop the stale kyber-weave-mcp server and re-run: kyber-weave doctor --check");
    }

    private static DoctorCheck Handshake(
        bool passed,
        string reason,
        StdioSession session,
        DoctorOptions options,
        string root)
    {
        if (passed)
            return new DoctorCheck("mcp.handshake", true, reason, null);

        bool timedOut = string.Equals(reason, NoAnswerWithinBudget, StringComparison.Ordinal);
        string? serverError = timedOut ? session.FirstErrorLine : session.WaitForFirstErrorLine(ServerErrorWait);

        if (serverError?.StartsWith(NoHostConfigurationCode, StringComparison.Ordinal) == true)
        {
            return new DoctorCheck(
                "mcp.handshake",
                false,
                "the docs server refused to start because this folder has no .kyber-weave/kyber-weave.yml. " +
                "A new worktree only has that file if its base branch has it committed",
                $"kyber-weave docs init \"{root}\"");
        }

        string detail = serverError is { } line ? $" ({Truncate(line)})" : string.Empty;
        string fix = timedOut
            ? TimeoutFix(options)
            : $"read the server's own error: {options.McpExecutable} --repo-root \"{root}\"";
        return new DoctorCheck("mcp.handshake", false, reason + detail, fix);
    }

    /// <summary>
    ///     Reads one reply line, turning a timeout, a closed stream, or a line past the session's cap into a reason
    ///     rather than an exception.
    /// </summary>
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
            failure = NoAnswerWithinBudget;
            return false;
        }
        catch (EndOfStreamException)
        {
            line = null;
            failure = "the server closed its output before answering";
            return false;
        }
        catch (InvalidDataException)
        {
            // The session's line cap is how a child that never ends a line gets stopped. The reply is the child's
            // data, so an over-long one is a failed check, not an exception that skips the renderer. The session is
            // unusable afterwards, and the caller's `using` disposes it.
            line = null;
            failure = "the server wrote past the session's line cap without ending the reply";
            return false;
        }
    }

    /// <summary>Parses a JSON-RPC reply and returns its <c>result</c> member.</summary>
    private static bool TryResult(string line, out JsonElement result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);

            // TryGetProperty throws when the element is not an object, and the reply is the child's data: an array
            // where an object belongs must read as a non-reply, not as an exception.
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("result", out JsonElement value))
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

    /// <summary>Returns the first text content item of a tool result, or <c>false</c> when the shape is not one.</summary>
    private static bool TryText(JsonElement result, out string text)
    {
        text = string.Empty;

        // Each of these members is the child's data, and TryGetProperty throws on a non-object. Check the kind
        // first so a reply like {"content":[42]} fails mcp.docs_explore instead of throwing.
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } found)
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

    /// <summary>
    ///     Whether the two paths name the same directory, where either may have come from the child.
    /// </summary>
    /// <remarks>
    ///     The served root is server-controlled text, so a value the platform cannot canonicalise — one holding a
    ///     NUL, for instance — must fail the check rather than throw out of the probe.
    /// </remarks>
    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Normalise(left), Normalise(right), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
