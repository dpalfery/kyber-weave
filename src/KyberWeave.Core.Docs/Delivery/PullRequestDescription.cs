using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>What a pull request description says, section by section, before it is laid out.</summary>
/// <remarks>
/// A null value leaves its section out. The renderer, not the caller, decides what an empty list means for the
/// sections a reviewer must always see (Changes, Validation, Not verified, Documentation impact).
/// </remarks>
public sealed record PullRequestContent
{
    /// <summary>The problem and the outcome, in one to three sentences.</summary>
    public required string Summary { get; init; }

    /// <summary>The kind of change, in words: bug fix, feature, refactor and so on.</summary>
    public string? Type { get; init; }

    /// <summary>The issue or work item, or null when there is none.</summary>
    public string? LinkedWork { get; init; }

    /// <summary>What changed, one short line each; empty when nothing recorded it.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>The evidence, as short readable lines; empty when the run recorded none.</summary>
    public IReadOnlyList<string> Validation { get; init; } = [];

    /// <summary>Every check that did not run or could not be confirmed, with its reason.</summary>
    public IReadOnlyList<string> NotVerified { get; init; } = [];

    /// <summary>Decisions made without a person that rest on no authoritative source; empty leaves the section out.</summary>
    public IReadOnlyList<string> Decisions { get; init; } = [];

    /// <summary>Review findings still open; empty leaves the section out.</summary>
    public IReadOnlyList<string> PendingReview { get; init; } = [];

    /// <summary>The todos filed for work found outside the change's scope, one line each; empty leaves the section out.</summary>
    public IReadOnlyList<string> FollowUps { get; init; } = [];

    /// <summary>The documentation-impact outcome, or null when the run did not record one.</summary>
    public string? DocumentationImpact { get; init; }

    /// <summary>Anything else reviewers should know, or null.</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// Lays a <see cref="PullRequestContent"/> out in the one pull request description format, the
/// <c>create-pull-request</c> skill's <c>assets/pull-request-description.md</c>, embedded here so the skill and the
/// deliver tool cannot drift apart. A host pull request template, when the repository has one, decides the sections
/// instead.
/// </summary>
public static partial class PullRequestDescription
{
    /// <summary>The token of the section that is never dropped, whatever the host template asks for.</summary>
    public const string SummaryToken = "summary";

    private const string ResourceName = "Delivery.pull-request-description.md";
    private const string NothingUnverified = "Nothing: every check listed above ran.";
    private const string HostSectionNotCovered = "Not covered by this run; the reviewer completes it.";

    /// <summary>The sections that follow a host template when it does not ask for them, because a reviewer must see them.</summary>
    private static readonly string[] AlwaysShown = ["not-verified", "decisions", "pending-review", "follow-ups", "notes"];

    /// <summary>The embedded description template, comment included.</summary>
    public static string Template { get; } = ReadTemplate();

    /// <summary>The paths, relative to the repository root, where GitHub reads a default pull request template, in the order it looks.</summary>
    public static IReadOnlyList<string> HostTemplateLocations { get; } =
        ["pull_request_template.md", "docs/pull_request_template.md", ".github/pull_request_template.md"];

    /// <summary>Reads the host's default pull request template, matching the file name case-insensitively, or null when it has none.</summary>
    public static string? ReadHostTemplate(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        foreach (string location in HostTemplateLocations)
        {
            string folder = Path.Combine(repoRoot, Path.GetDirectoryName(location) ?? string.Empty);
            if (!Directory.Exists(folder))
                continue;

            string? match = Directory.EnumerateFiles(folder)
                .FirstOrDefault(file => string.Equals(Path.GetFileName(file), Path.GetFileName(location), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                continue;

            try
            {
                return File.ReadAllText(match);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A template that cannot be read is not worth failing the delivery for: the standard format is used instead.
                return null;
            }
        }

        return null;
    }

    /// <summary>The description for <paramref name="content"/>, in the host's template when one is given, otherwise in the standard format.</summary>
    public static string Render(PullRequestContent content, string? hostTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Dictionary<string, string?> values = Values(content);
        List<Section> standard = Sections(Template);

        List<(string Heading, string Body)> output = [];
        if (string.IsNullOrWhiteSpace(hostTemplate))
        {
            foreach (Section section in standard)
            {
                if (section.Token is { } token && values.GetValueOrDefault(token) is { } value)
                    output.Add((section.Heading, value));
            }
        }
        else
        {
            HashSet<string> used = new(StringComparer.Ordinal);
            foreach (Section host in Sections(hostTemplate))
            {
                string? token = TokenFor(host.Heading);
                if (token is not null && used.Add(token))
                {
                    if (values.GetValueOrDefault(token) is { } value)
                        output.Add((host.Heading, value));
                    continue;
                }

                string kept = string.Join('\n', host.Body.Split('\n').Where(line => Checklist().IsMatch(line))).Trim();
                output.Add((host.Heading, kept.Length > 0 ? kept : HostSectionNotCovered));
            }

            if (!used.Contains(SummaryToken))
                output.Insert(0, ("Summary", values[SummaryToken]!));
            foreach (Section section in standard)
            {
                if (section.Token is { } token && AlwaysShown.Contains(token, StringComparer.Ordinal)
                                                && !used.Contains(token) && values.GetValueOrDefault(token) is { } value)
                    output.Add((section.Heading, value));
            }
        }

        StringBuilder body = new();
        foreach ((string heading, string text) in output)
        {
            if (body.Length > 0)
                body.Append('\n');
            body.Append("## ").Append(heading).Append("\n\n").Append(text.Trim()).Append('\n');
        }

        return body.ToString();
    }

    /// <summary>Which standard section a host heading asks for, or null when it asks for none of them.</summary>
    internal static string? TokenFor(string heading)
    {
        string h = heading;
        if (ContainsAny(h, "not verified", "unverified"))
            return "not-verified";
        if (ContainsAny(h, "decision"))
            return "decisions";
        if (ContainsAny(h, "pending"))
            return "pending-review";
        if (ContainsAny(h, "follow-up", "follow up", "followup"))
            return "follow-ups";
        if (ContainsAny(h, "type"))
            return "type";
        if (ContainsAny(h, "link", "issue", "ticket", "related", "work item"))
            return "linked-work";
        if (ContainsAny(h, "doc"))
            return "documentation-impact";
        if (ContainsAny(h, "test", "validat", "verif"))
            return "validation";
        if (ContainsAny(h, "change"))
            return "changes";
        if (ContainsAny(h, "summary", "description", "overview", "what", "why", "motivation"))
            return SummaryToken;
        if (ContainsAny(h, "note", "other", "additional", "context"))
            return "notes";
        return null;
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string?> Values(PullRequestContent content) => new(StringComparer.Ordinal)
    {
        [SummaryToken] = content.Summary.Trim(),
        ["type"] = content.Type,
        ["linked-work"] = content.LinkedWork,
        ["changes"] = List(content.Changes) ?? "Not recorded by the run; the commits show what changed.",
        ["validation"] = List(content.Validation) ?? "The run recorded no check results.",
        ["not-verified"] = List(content.NotVerified) ?? NothingUnverified,
        ["decisions"] = List(content.Decisions),
        ["pending-review"] = List(content.PendingReview),
        ["follow-ups"] = List(content.FollowUps),
        ["documentation-impact"] = string.IsNullOrWhiteSpace(content.DocumentationImpact)
            ? "Not recorded by the run."
            : content.DocumentationImpact.Trim(),
        ["notes"] = string.IsNullOrWhiteSpace(content.Notes) ? null : content.Notes.Trim(),
    };

    private static string? List(IReadOnlyList<string> items) =>
        items.Count == 0 ? null : string.Join('\n', items.Select(item => "- " + item.Trim()));

    private sealed record Section(string Heading, string Body, string? Token);

    private static List<Section> Sections(string markdown)
    {
        string text = Comment().Replace(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty);
        List<Section> sections = [];
        string? heading = null;
        StringBuilder body = new();
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
                body.Clear();
            }
            else if (heading is not null)
            {
                body.Append(line).Append('\n');
            }
        }

        Flush();
        return sections;

        void Flush()
        {
            if (heading is null)
                return;
            string content = body.ToString();
            Match token = Token().Match(content);
            sections.Add(new Section(heading, content, token.Success ? token.Groups["name"].Value : null));
        }
    }

    private static string ReadTemplate()
    {
        using Stream stream = typeof(PullRequestDescription).Assembly.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\{\{(?<name>[a-z-]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"^\s*[-*]\s+\[[ xX]\]", RegexOptions.CultureInvariant)]
    private static partial Regex Checklist();
}
