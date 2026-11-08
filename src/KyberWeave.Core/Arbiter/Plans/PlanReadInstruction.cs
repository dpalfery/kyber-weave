using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// Detects a prompt that sends a worker into a plan or a specification.
/// </summary>
/// <remarks>
/// <para>
/// The read guard is advisory wherever a harness's hooks are, so the ban is enforced in two
/// places that have to agree on what the ban is: <c>KW-DOC-TASKS-005</c> at validation and
/// <c>KW-ARB-PLANREAD-001</c> at dispatch (ADR 0053). One detector answers both, because two
/// copies drift the first time one learns a spelling the other does not, and the worker meets
/// the gap as a rule that fires on one machine and not the other.
/// </para>
/// <para>
/// It lives in <see cref="KyberWeave.Core" /> because that is the only assembly the Arbiter and
/// the docs validator share (ADR 0057): the Arbiter references <c>KyberWeave.Core</c> alone, so
/// a detector placed in <c>KyberWeave.Core.Docs</c> would be unreachable from the rule that
/// fires at dispatch. It reads text and nothing else.
/// </para>
/// <para>
/// Two questions are asked. <see cref="Find" /> asks the validation one, and needs both halves
/// of an instruction in one sentence — a read verb and a plan being named — so a prompt that
/// says "the plan" in passing is not a finding. <see cref="NamesPathUnder" /> asks the dispatch
/// one alone, because a prompt that names a file inside the planning directories is what
/// <c>KW-ARB-PLANREAD-001</c> fails on whether or not it also says "read".
/// </para>
/// <para>
/// A sentence that forbids the read is not an instruction to read it: "Do not open the plan"
/// and "never consult the task file's plan" are what the conductor is supposed to say, so a
/// negated sentence is skipped.
/// </para>
/// </remarks>
public static partial class PlanReadInstruction
{
    /// <summary>
    /// The instructions in <paramref name="text" /> that tell a worker to open a plan, one entry
    /// per offending sentence.
    /// </summary>
    /// <param name="text">A prompt, a dispatch body, or any prose addressed to a worker.</param>
    /// <param name="planningDirectories">
    /// Repository-relative directories holding plans and specifications, as bare directories
    /// (<c>docs/plans</c>) or as the prefix globs the Arbiter publishes
    /// (<c>docs/plans/**</c>). An empty list leaves only the named-plan half of the rule.
    /// </param>
    /// <returns>The offending sentences, trimmed of surrounding whitespace; empty when none.</returns>
    public static IReadOnlyList<string> Find(string text, IReadOnlyList<string> planningDirectories)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(planningDirectories);

        List<string> found = [];
        foreach (string sentence in Sentences(text))
        {
            if (NegationRegex().IsMatch(sentence) || !ReadVerbRegex().IsMatch(sentence))
            {
                continue;
            }

            if (PlanNounRegex().IsMatch(sentence) || NamesPathUnder(sentence, planningDirectories).Count > 0)
            {
                found.Add(sentence.Trim());
            }
        }

        return found;
    }

    /// <summary>True when <paramref name="text" /> carries at least one plan-read instruction.</summary>
    /// <param name="text">A prompt, a dispatch body, or any prose addressed to a worker.</param>
    /// <param name="planningDirectories">The planning directories, as <see cref="Find" /> takes them.</param>
    public static bool IsPresent(string text, IReadOnlyList<string> planningDirectories) =>
        Find(text, planningDirectories).Count > 0;

    /// <summary>
    /// The paths in <paramref name="text" /> that name a file inside one of
    /// <paramref name="planningDirectories" />.
    /// </summary>
    /// <remarks>
    /// This is the dispatch rule's whole question: a prompt naming
    /// <c>docs/plans/&lt;plan&gt;.md</c> is a read the worker will perform however it is phrased.
    /// Backticked spans and bare path-shaped tokens are both read, because a prompt written for
    /// a terminal backticks its commands and a prompt written for prose does not.
    /// </remarks>
    /// <param name="text">A prompt, a dispatch body, or any prose addressed to a worker.</param>
    /// <param name="planningDirectories">
    /// Repository-relative directories, as bare directories or prefix globs. An entry that
    /// normalizes to nothing — the repository root as <c>/**</c> — is dropped rather than
    /// matching every path.
    /// </param>
    /// <returns>The matching paths as written, without surrounding punctuation; empty when none.</returns>
    public static IReadOnlyList<string> NamesPathUnder(string text, IReadOnlyList<string> planningDirectories)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(planningDirectories);

        List<string> directories = [.. planningDirectories
            .Select(DirectoryPrefix)
            .Where(directory => directory.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        List<string> named = [];
        if (directories.Count == 0)
        {
            return named;
        }

        foreach (string candidate in CandidatePaths(text))
        {
            string path = candidate.Trim().Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']', '"', '\'');
            string normalized = NormalizePath(path);
            if (path.Length > 0
                && (directories.Exists(directory => IsUnder(path, directory))
                    || (normalized.Length > 0 && directories.Exists(directory => IsUnder(normalized, directory))))
                && !named.Contains(path, StringComparer.Ordinal))
            {
                named.Add(path);
            }
        }

        return named;
    }

    /// <summary>
    /// Lexically normalizes a path: collapses redundant separators, removes <c>.</c> segments,
    /// and resolves <c>..</c> segments.
    /// </summary>
    private static string NormalizePath(string raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        string[] segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> normalized = [];
        foreach (string segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (normalized.Count > 0 && normalized[^1] != "..")
                {
                    normalized.RemoveAt(normalized.Count - 1);
                }
                else
                {
                    normalized.Add(segment);
                }
            }
            else
            {
                normalized.Add(segment);
            }
        }

        return string.Join('/', normalized);
    }

    private static bool IsUnder(string path, string directory)
    {
        if (DocsRootPath.PathComparer.Compare(path, directory) == 0)
        {
            return true;
        }

        string prefix = directory + "/";
        return path.Length > prefix.Length
               && DocsRootPath.PathComparer.Compare(path[..prefix.Length], prefix) == 0;
    }

    /// <summary>One configured directory, without its separator or its prefix-glob suffix.</summary>
    private static string DirectoryPrefix(string directory)
    {
        string value = directory.Trim().Replace('\\', '/').Trim('/');
        if (value.EndsWith("/**", StringComparison.Ordinal))
        {
            value = value[..^3];
        }
        else if (value.EndsWith("/*", StringComparison.Ordinal))
        {
            value = value[..^2];
        }

        return value.TrimEnd('/');
    }

    /// <summary>Every path-shaped token in the text: backticked spans first, then bare tokens.</summary>
    private static IEnumerable<string> CandidatePaths(string text)
    {
        foreach (Match match in BacktickedPathRegex().Matches(text))
        {
            yield return match.Groups["path"].Value;
        }

        foreach (Match match in BarePathRegex().Matches(text))
        {
            yield return match.Groups["path"].Value;
        }
    }

    /// <summary>
    /// The text split into sentences: each line, then each sentence on that line. Splitting on a
    /// terminator followed by whitespace keeps <c>plan.md</c> and <c>docs/plans</c> whole.
    /// </summary>
    private static IEnumerable<string> Sentences(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            foreach (string sentence in SentenceBoundaryRegex().Split(line))
            {
                if (!string.IsNullOrWhiteSpace(sentence))
                {
                    yield return sentence;
                }
            }
        }
    }

    [GeneratedRegex(@"(?<=[.!?])[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(
        @"\b(?:re-read|reread|read|open|consult|load|review|study|see|refer\s+to|look\s+at|follow)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadVerbRegex();

    [GeneratedRegex(
        @"\b(?:do\s+not|don't|never|must\s+not|should\s+not|without|avoid|no\s+need\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegationRegex();

    // "the plan", "this plan", "the child plan", "the plan file": a plan referred to by role
    // rather than by path. `plan\b` stops "the planning directories" from matching.
    [GeneratedRegex(
        @"\b(?:the|this|that|your|its|their|parent|child|whole|full|original)\s+(?:[\w-]+\s+){0,2}plan\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlanNounRegex();

    [GeneratedRegex(@"`(?<path>[^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickedPathRegex();

    // A bare path: at least one separator, so a filename alone is not one. The lookbehind keeps
    // the match from starting inside a backticked span's path or after a word character.
    [GeneratedRegex(@"(?<![\w`/.\-])(?<path>[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex BarePathRegex();
}
