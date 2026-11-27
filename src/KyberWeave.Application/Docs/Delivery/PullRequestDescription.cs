using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>What a pull request description says, section by section, before it is laid out.</summary>
/// <remarks>
/// A null value leaves its section out. The renderer, not the caller, decides what an empty list means for the
/// sections a reviewer must always see (Changes, Validation, Not verified, Documentation impact).
/// </remarks>
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
    /// <param name="resources">Reads the template embedded in this assembly.</param>
    public static string Template(IEmbeddedResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return resources.ReadAllText(ResourceName);
    }

    /// <summary>The paths, relative to the repository root, where GitHub reads a default pull request template, in the order it looks.</summary>
    public static IReadOnlyList<string> HostTemplateLocations { get; } =
        ["pull_request_template.md", "docs/pull_request_template.md", ".github/pull_request_template.md"];

    /// <summary>Reads the host's default pull request template, matching the file name case-insensitively, or null when it has none or none can be read.</summary>
    public static string? ReadHostTemplate(IRepositoryFileStore files, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(files);
        return ReadHostTemplate(files, repoRoot, files.ReadAllTextBounded).Text;
    }

    /// <summary>
    /// Reads the host's default pull request template through <paramref name="read"/>. A candidate that cannot be read is
    /// skipped and named in <see cref="HostTemplate.Unreadable"/>, and the next location is tried. A template reached
    /// through a symbolic link is treated the same way, because a link can point outside the repository and the template's
    /// lines are published in the pull request.
    /// </summary>
    /// <param name="files">The file store the folders are listed and checked through.</param>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="read">Reads one template file.</param>
    /// <param name="listFolder">Lists a folder's files; the file store when null. A seam for a folder that cannot be listed.</param>
    internal static HostTemplate ReadHostTemplate(IRepositoryFileStore files, string repoRoot, Func<string, string> read, Func<string, IEnumerable<string>>? listFolder = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(read);
        List<string> unreadable = [];
        foreach (string location in HostTemplateLocations)
        {
            string? match;
            try
            {
                string folder = Path.Combine(repoRoot, Path.GetDirectoryName(location) ?? string.Empty);
                if (!files.DirectoryExists(folder))
                    continue;

                match = (listFolder ?? files.GetFiles)(folder)
                    .FirstOrDefault(file => string.Equals(Path.GetFileName(file), Path.GetFileName(location), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception exception) when (FileFailure.Is(exception))
            {
                // A folder that cannot be listed may hold the template: it is named as unreadable, not skipped in silence.
                unreadable.Add(location);
                continue;
            }

            if (match is null)
                continue;

            if (!files.IsContainedFile(repoRoot, Path.GetRelativePath(repoRoot, match), []))
            {
                unreadable.Add(location);
                continue;
            }

            try
            {
                return new HostTemplate(read(match), unreadable);
            }
            catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidDataException)
            {
                // A template that cannot be read, or is larger than a template can be, is not worth failing the delivery for: the next location, or the standard format, is used.
                unreadable.Add(location);
            }
        }

        return new HostTemplate(null, unreadable);
    }

    /// <summary>The host template that was read, if any, and the locations that held one that could not be read.</summary>
    internal sealed record HostTemplate(string? Text, IReadOnlyList<string> Unreadable);

    /// <summary>The description for <paramref name="content"/>, in the host's template when one is given, otherwise in the standard format.</summary>
    /// <param name="resources">Reads the embedded standard template.</param>
    /// <param name="content">What the description reports.</param>
    /// <param name="hostTemplate">The host's own template, or null for the standard format.</param>
    public static string Render(IEmbeddedResources resources, PullRequestContent content, string? hostTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Dictionary<string, string?> values = Values(content);
        List<Section> standard = Sections(Template(resources));

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
                output.Insert(0, ("Summary", content.Summary.Trim()));
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

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\{\{(?<name>[a-z-]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"^\s*[-*]\s+\[[ xX]\]", RegexOptions.CultureInvariant)]
    private static partial Regex Checklist();
}
