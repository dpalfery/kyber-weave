namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>Where a resolved caller identity comes from, highest rank first.</summary>
public enum CallerSource
{
    /// <summary>No source named a caller; the dispatch is unidentified.</summary>
    None,

    /// <summary>Named by the harness payload, which outranks every other source.</summary>
    Harness,

    /// <summary>Named by the rendered <c>--caller</c> a frontmatter hook wrote.</summary>
    Rendered,

    /// <summary>Inferred from the dispatch headers on project-wide-hook harnesses.</summary>
    Header,

    /// <summary>Passed explicitly by <c>serve</c>, which asserts its own identity.</summary>
    Asserted,
}

/// <summary>A caller identity and the source that named it.</summary>
/// <param name="Caller">The resolved caller, or <c>null</c> when unidentified.</param>
/// <param name="Source">The source that named it.</param>
public sealed record CallerResolution(string? Caller, CallerSource Source);

/// <summary>Resolves which Squad agent dispatched the Arbiter.</summary>
/// <remarks>
/// The harness payload outranks the rendered <c>--caller</c>; the two agree whenever
/// a frontmatter hook fires. Header inference only runs when neither named anyone:
/// <c>PLAN_FILE</c> or <c>TASK</c> means the conductor, which alone writes them;
/// <c>LENS</c> or <c>REFUTE</c> means <c>code-reviewer</c>; a planner target means
/// the conductor, whose roster alone holds planners. A dispatch carrying only the
/// marker stays unidentified.
/// </remarks>
public static class CallerResolver
{
    /// <summary>Resolves the caller from the strongest source that names one.</summary>
    public static CallerResolution Resolve(
        string? harnessCaller = null,
        string? renderedCaller = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        string? assertedCaller = null)
    {
        if (!string.IsNullOrWhiteSpace(assertedCaller))
            return new CallerResolution(assertedCaller, CallerSource.Asserted);
        if (!string.IsNullOrWhiteSpace(harnessCaller))
            return new CallerResolution(harnessCaller, CallerSource.Harness);
        if (!string.IsNullOrWhiteSpace(renderedCaller))
            return new CallerResolution(renderedCaller, CallerSource.Rendered);
        if (headers is not null && InferFromHeaders(headers) is { } inferred)
            return new CallerResolution(inferred, CallerSource.Header);
        return new CallerResolution(null, CallerSource.None);
    }

    private static string? InferFromHeaders(IReadOnlyDictionary<string, string?> headers)
    {
        if (Header(headers, "PLAN_FILE") is not null || Header(headers, "TASK") is not null)
            return "conductor";
        if (Header(headers, "LENS") is not null || Header(headers, "REFUTE") is not null)
            return "code-reviewer";
        string? target = Header(headers, "TARGET") ?? Header(headers, "DELEGATION_TARGET");
        if (target is not null && target.Contains("planner", StringComparison.OrdinalIgnoreCase))
            return "conductor";
        return null;
    }

    private static string? Header(IReadOnlyDictionary<string, string?> headers, string name)
    {
        // The lookup is ordinal-ignorant of the dictionary's own comparer on
        // purpose: header blocks arrive from several harnesses, and none promises
        // a case-insensitive map.
        foreach (KeyValuePair<string, string?> pair in headers)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
                return pair.Value;
        }

        return null;
    }
}
