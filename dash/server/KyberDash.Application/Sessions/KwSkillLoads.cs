using KyberDash.Contracts.Repositories;

namespace KyberDash.Application.Sessions;

/// <summary>A KW skill load (D17): the load's span, the skill its reply names when recorded, and when it started.</summary>
/// <param name="SpanId">Span identifier of the load's tool call.</param>
/// <param name="Skill">The name on the reply's <c>skill:</c> header line, or <see langword="null"/> when the reply names no valid skill.</param>
/// <param name="StartedAtUtc">The owning record's start time.</param>
public sealed record KwSkillLoad(string SpanId, string? Skill, DateTimeOffset StartedAtUtc);

/// <summary>
/// Recognizes KW skill loads among a session's tool calls and reads the skill each one names (D17).
/// </summary>
/// <remarks>
/// <para>
/// A load is a tool call the kw-docs server's <c>load_skill</c> tool made. When the harness split
/// the MCP server and tool apart, the split decides: the server name, lower-cased with <c>_</c>
/// read as <c>-</c>, must be <c>kw-docs</c> and the tool must be <c>load_skill</c>. Without a split,
/// the tool name must be one of the five unsplit spellings. A split call is judged by its split
/// alone, so another server's <c>load_skill</c> is never a load, even when its tool name reads as
/// kw-docs.
/// </para>
/// <para>
/// This type is pure: it takes the candidate rows the reader returns and touches no store, clock or
/// file. The reader's SQL narrows to the same names, and this recognizer is the authority. The
/// skill is read from the reply, never from the call's input, which is never stored as plain text.
/// </para>
/// </remarks>
public static class KwSkillLoads
{
    /// <summary>The MCP server a KW skill load is made to.</summary>
    public const string ServerName = "kw-docs";

    /// <summary>The MCP tool a KW skill load calls.</summary>
    public const string ToolName = "load_skill";

    /// <summary>The longest skill name the header may carry (D17).</summary>
    public const int MaxSkillNameLength = 64;

    private const string SkillLinePrefix = "skill:";

    private static readonly string[] UnsplitToolNames =
    [
        "mcp__kw-docs__load_skill",
        "mcp__kw_docs__load_skill",
        "kw-docs_load_skill",
        "kw-docs__load_skill",
        "kw-docs/load_skill",
    ];

    /// <summary>Recognizes the loads among <paramref name="calls"/>, keeping their call order.</summary>
    /// <param name="calls">Candidate tool calls of one session, in call order.</param>
    /// <returns>One <see cref="KwSkillLoad"/> per call that is a load; other calls are dropped.</returns>
    public static IReadOnlyList<KwSkillLoad> Recognize(IEnumerable<StoredKwSkillLoad> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        List<KwSkillLoad> loads = [];
        foreach (StoredKwSkillLoad call in calls)
        {
            if (IsLoad(call))
            {
                loads.Add(new KwSkillLoad(call.SpanId, SkillFromReply(call.ReplyHead), call.StartedAtUtc));
            }
        }

        return loads;
    }

    /// <summary>Decides whether one tool call is a KW skill load (D17).</summary>
    /// <param name="call">Candidate tool call.</param>
    /// <returns><see langword="true"/> when the call is a kw-docs <c>load_skill</c>.</returns>
    public static bool IsLoad(StoredKwSkillLoad call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.McpServerName is not null || call.McpToolName is not null)
        {
            return string.Equals(NormalizeServerName(call.McpServerName), ServerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(call.McpToolName, ToolName, StringComparison.Ordinal);
        }

        return Array.IndexOf(UnsplitToolNames, call.ToolName) >= 0;
    }

    /// <summary>Normalizes an MCP server name for comparison: <c>_</c> read as <c>-</c>, compared case-insensitively.</summary>
    /// <param name="serverName">Server name as recorded, or <see langword="null"/> when none was.</param>
    /// <returns>The normalized name, or <see langword="null"/> when none was recorded.</returns>
    /// <remarks>Case is folded at the comparison, not here: the lower-case form is the D17 rule, and an ordinal-ignore-case compare is that rule without an allocation.</remarks>
    public static string? NormalizeServerName(string? serverName) =>
        serverName?.Replace('_', '-');

    /// <summary>Reads the skill a load names from the header of its reply.</summary>
    /// <param name="replyHead">The reply's first 1024 characters, or <see langword="null"/> when no reply was captured.</param>
    /// <returns>
    /// The name on the first <c>skill:</c> line before the first blank line, when that name passes
    /// <see cref="IsSkillName"/>; otherwise <see langword="null"/>. A <c>skill:</c> line after the
    /// header (in the body) is never read, and a first header line that names no valid skill is not
    /// skipped in favour of a later one.
    /// </returns>
    /// <remarks>
    /// The load happened whether or not its reply names a skill, so an unreadable header becomes a
    /// null skill on the row rather than dropping the row. A CRLF line ending is tolerated. The name
    /// is only ever compared and shown back as a string, and it leaves the application only after
    /// the name pattern has accepted it.
    /// </remarks>
    public static string? SkillFromReply(string? replyHead)
    {
        if (string.IsNullOrEmpty(replyHead))
        {
            return null;
        }

        foreach (string rawLine in replyHead.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            if (line.StartsWith(SkillLinePrefix, StringComparison.Ordinal))
            {
                string name = line[SkillLinePrefix.Length..].Trim();
                return IsSkillName(name) ? name : null;
            }
        }

        return null;
    }

    /// <summary>Decides whether a value is a skill name: lower-case words of letters and digits joined by single hyphens, at most 64 characters.</summary>
    /// <param name="value">Candidate name.</param>
    /// <returns><see langword="true"/> when the value matches <c>^[a-z0-9]+(-[a-z0-9]+)*$</c> and its length limit.</returns>
    /// <remarks>
    /// Written as a character walk rather than a regular expression, so no pattern can backtrack
    /// over a value from outside the process. The rule is the one D17 states.
    /// </remarks>
    public static bool IsSkillName(string value)
    {
        if (value.Length is 0 or > MaxSkillNameLength)
        {
            return false;
        }

        bool expectingWord = true;
        foreach (char character in value)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                expectingWord = false;
            }
            else if (character == '-' && !expectingWord)
            {
                expectingWord = true;
            }
            else
            {
                return false;
            }
        }

        return !expectingWord;
    }
}
