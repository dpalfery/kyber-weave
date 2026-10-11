using System.Text.RegularExpressions;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Rewrites an owner's authored Markdown links to its declared resources, for the targets
/// whose deployed layout or anchoring means the authored relative link does not resolve.
/// </summary>
/// <remarks>
/// Driven by the declared resource closure rather than a pattern over prose: only a Markdown
/// link target that exactly equals a declared resource's relative path is rewritten, so a body
/// that merely names a file in text, and a link to anything outside the closure, are untouched.
/// The target-specific part is a mapping function from the authored path to the emitted path;
/// the closure test, the <c>#fragment</c>/<c>?query</c> handling, and the CommonMark
/// angle-bracket form for a mapped target containing whitespace or parentheses are shared, so
/// each target opts in without duplicating the regex or the closure logic (ADR 0021
/// introduced this for ZCode; Copilot global scope is the second user). An authored link may
/// carry a title (<c>[x](path "t")</c>) or an angle-bracketed destination
/// (<c>[x](&lt;path&gt;)</c>); both are matched and the title is preserved. Fenced code blocks
/// are left untouched, so an example link shown in a code sample is not turned into a machine
/// path.
/// </remarks>
internal static class SquadResourceLinkRewriter
{
    /// <summary>
    /// A Markdown inline link or image whose target is captured for resource-link rewriting.
    /// Matching the link syntax rather than the bare path keeps the rewrite from touching
    /// prose that merely mentions a filename.
    /// </summary>
    private static readonly Regex MarkdownLinkTarget = new(
        @"(?<prefix>\]\()(?:<(?<angle>[^<>\r\n]+)>|(?<target>[^)\s]+))(?<title>[ \t]+(?:""[^""\r\n]*""|'[^'\r\n]*'))?(?<suffix>\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>A fenced code block (backtick or tilde fence), matched so its content is skipped.</summary>
    private static readonly Regex FencedCodeBlock = new(
        @"^[ ]{0,3}(?<fence>`{3,}|~{3,})[^\r\n]*\r?\n(?:.*?\r?\n)??[ ]{0,3}\k<fence>[`~]*[ \t]*(?:\r?\n|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// Rewrites every link in <paramref name="body"/> whose path equals a declared resource
    /// path, replacing the path with <paramref name="mapPath"/> of the authored path.
    /// </summary>
    /// <param name="body">The authored instruction body.</param>
    /// <param name="resourcePaths">The declared resource relative paths (the closure).</param>
    /// <param name="mapPath">Maps the authored path part, fragment and query excluded, to the emitted path.</param>
    /// <param name="changed">True when at least one link was rewritten.</param>
    /// <returns>The body with in-closure links rewritten.</returns>
    internal static string Rewrite(
        string body,
        IReadOnlySet<string> resourcePaths,
        Func<string, string> mapPath,
        out bool changed)
    {
        bool anyChanged = false;
        var output = new System.Text.StringBuilder(body.Length);
        int position = 0;
        foreach (Match fence in FencedCodeBlock.Matches(body))
        {
            output.Append(RewriteSegment(body[position..fence.Index]));
            output.Append(fence.Value);
            position = fence.Index + fence.Length;
        }

        output.Append(RewriteSegment(body[position..]));
        string rewritten = output.ToString();

        string RewriteSegment(string segment) => MarkdownLinkTarget.Replace(segment, match =>
        {
            string target = match.Groups["angle"].Success
                ? match.Groups["angle"].Value
                : match.Groups["target"].Value;
            int suffixIndex = target.IndexOfAny(['#', '?']);
            string pathPart = suffixIndex < 0 ? target : target[..suffixIndex];
            string linkSuffix = suffixIndex < 0 ? string.Empty : target[suffixIndex..];
            if (pathPart.Length == 0 || !resourcePaths.Contains(Uri.UnescapeDataString(pathPart)))
            {
                return match.Value;
            }

            anyChanged = true;
            string newTarget = mapPath(pathPart) + linkSuffix;
            if (newTarget.Any(char.IsWhiteSpace) || newTarget.Contains('(', StringComparison.Ordinal) || newTarget.Contains(')', StringComparison.Ordinal))
            {
                newTarget = $"<{newTarget}>";
            }

            return $"{match.Groups["prefix"].Value}{newTarget}{match.Groups["title"].Value}{match.Groups["suffix"].Value}";
        });

        changed = anyChanged;
        return rewritten;
    }
}
