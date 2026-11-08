using System.Text.RegularExpressions;

namespace KyberWeave.Core.RunStart;

/// <summary>One task a branch records as finished, and the report the commit that finished it carried.</summary>
/// <param name="Id">The task id the commit's <c>Task:</c> trailer names.</param>
/// <param name="Report">
///     The commit body above the trailer area, with its paragraphs kept and its blank edge lines trimmed;
///     the empty string when the commit recorded the task with no report.
/// </param>
public sealed record RunStartCompletedTask(string Id, string Report);

/// <summary>
///     Reads the <c>Task:</c> commit lines a resumed branch carries, so a run that lost its context can
///     learn which tasks finished and what each one reported without reading its plan again.
/// </summary>
/// <remarks>
///     <para>
///         The line is read from the commit's trailer area — the last paragraph of the message — because a
///         commit body is prose, and prose names other tasks. A reader that scanned every line would treat
///         an id quoted from an earlier report, or one a later commit superseded in a sentence, as finished
///         work, and the resumed run would skip a task nobody did.
///     </para>
///     <para>
///         A paragraph is the trailer area only when it is a trailer block: every line is
///         <c>Token: value</c>, or a whitespace-led continuation of the line above. Git's own
///         <c>interpret-trailers</c> tolerates roughly a quarter of the lines being prose; this reader does
///         not, because the case that tolerance would let through — a report whose closing line happens to
///         name a task — is the case the trailer rule exists to exclude.
///     </para>
///     <para>
///         The input is a commit message, which anyone can write, so nothing here throws on one it cannot
///         read. An empty message, an ad-hoc one-line message and a message whose last paragraph is prose
///         all yield no record.
///     </para>
/// </remarks>
public static partial class RunStartResumeReader
{
    private const char FieldSeparator = '\0';
    private const char LineSeparator = '\n';

    /// <summary>Reads the completed tasks out of the output of <c>git log -z --format=%H%n%B</c>.</summary>
    /// <param name="logOutput">The NUL-separated log output; null or empty yields no records.</param>
    /// <returns>
    ///     One record per commit whose trailer area names a task, in the order git printed them, so the
    ///     order is the caller's to choose through the range it logs.
    /// </returns>
    public static IReadOnlyList<RunStartCompletedTask> Read(string? logOutput)
    {
        if (string.IsNullOrEmpty(logOutput))
            return [];

        List<RunStartCompletedTask> completed = [];
        foreach (string field in logOutput.Split(FieldSeparator))
        {
            RunStartCompletedTask? record = ReadCommit(field);
            if (record is not null)
                completed.Add(record);
        }

        return completed;
    }

    /// <summary>Reads one NUL-separated log field: a commit hash on the first line, then its message.</summary>
    private static RunStartCompletedTask? ReadCommit(string field)
    {
        string[] lines = field.Split(LineSeparator);

        // Two lines are the hash and the subject, and no message is shorter: a field with fewer carries
        // no message to read, which is what an empty log field looks like.
        if (lines.Length < 2)
            return null;

        List<string> message = [];
        for (int index = 1; index < lines.Length; index++)
            message.Add(lines[index].TrimEnd('\r'));

        int end = message.Count;
        while (end > 0 && message[end - 1].Trim().Length == 0)
            end--;

        int start = end;
        while (start > 0 && message[start - 1].Trim().Length != 0)
            start--;

        // The trailer block must be separated from the subject by an empty line: a paragraph
        // reaching line 0 is the subject paragraph, which cannot be a trailer block in git.
        if (start == 0 || start >= end)
            return null;

        string? id = TaskIdIn(message, start, end);
        if (id is null)
            return null;

        List<string> report = [];
        for (int index = 1; index < start; index++)
            report.Add(message[index]);

        return new RunStartCompletedTask(id, string.Join(LineSeparator, report).Trim());
    }

    /// <summary>
    ///     The task id the trailer area names, or null when that paragraph is prose or names no task.
    /// </summary>
    private static string? TaskIdIn(List<string> body, int start, int end)
    {
        string? id = null;
        bool seenTrailer = false;
        for (int index = start; index < end; index++)
        {
            string line = body[index];
            if (IsContinuation(line))
            {
                if (!seenTrailer)
                    return null;
                continue;
            }

            if (!TrailerLine().IsMatch(line))
                return null;

            seenTrailer = true;
            Match task = TaskTrailer().Match(line);
            if (task.Success)
                id = task.Groups["id"].Value;
        }

        return id;
    }

    private static bool IsContinuation(string line)
    {
        return line.Length > 0 && (line[0] == ' ' || line[0] == '\t');
    }

    /// <summary>A line in the shape git reads as a trailer: a token, a colon, and a value.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9-]*:[ \t]+\S")]
    private static partial Regex TrailerLine();

    /// <summary>
    ///     A <c>Task:</c> trailer whose value is one token. A value with spaces in it names no single task,
    ///     so it is not read as a record even inside a trailer block.
    /// </summary>
    [GeneratedRegex(@"^Task:[ \t]+(?<id>\S+)[ \t]*$")]
    private static partial Regex TaskTrailer();
}
