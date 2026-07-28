using System.ComponentModel;
using System.Globalization;
using System.Text;
using KyberWeave.Core.Docs.Search;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The documentation retrieval tools. Output is capped the way <c>codegraph_explore</c>
/// caps its own: a retrieval tool that returns a whole corpus is a slower grep.
/// </summary>
[McpServerToolType]
public sealed class DocsTools
{
    /// <summary>Hard ceiling on any single section, so one huge section cannot crowd out
    /// every other document in the response.</summary>
    private const int SectionCharCap = 6000;

    /// <summary>Code joins listed per document.</summary>
    private const int JoinCap = 20;

    private readonly DocumentIndexHost _host;

    public DocsTools(DocumentIndexHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    [McpServerTool(Name = "docs_explore")]
    [Description("""
        Retrieve repository documentation for a question, symbol, route, component or
        document id. Returns ranked documents with their frontmatter identity, their most
        relevant '##' sections, and that document's resolved joins to the code graph as
        symbol -> file:line. Prefer this over grepping the docs tree: ranking uses declared
        frontmatter identity, which prose does not carry, and the corpus excludes the
        archive, which must never be cited as current guidance.

        charBudget is shared across the returned documents, so lowering maxDocs deepens
        each result instead of merely shortening the list. For a single document the whole
        file usually fits — prefer maxDocs=1 with the default budget over falling back to
        reading the file. Any sections that did not fit are named in the response, so ask
        again with a larger budget rather than opening the file.
        """)]
    public string Explore(
        [Description("Free text, or a symbol, route, component or document-id name.")] string query,
        [Description("Maximum documents to return (1-20). Defaults to 5.")] int maxDocs = 5,
        [Description("Total characters of prose across all returned documents (1000-120000). Defaults to 12000.")]
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        var index = _host.Current();
        var hits = index.Explore(query, maxDocs, charBudget);

        if (hits.Count == 0)
        {
            // Saying so plainly is the whole point of the relevance floor. An agent told
            // to use this before grepping needs an explicit signal that it may now grep.
            var docsIndex = Path.Combine(_host.DocsRelativeRoot, "README.md").Replace('\\', '/');
            return $"""
                No document in the governed corpus scored above the relevance threshold for '{query}'.
                {index.DocumentCount} documents were considered.

                This is a real miss, not an empty corpus. Either the subject is undocumented, or the
                question uses vocabulary the documentation does not. Try naming a component, a doc-id
                ("webui/architecture"), or a code symbol; or fall back to reading {docsIndex}.
                """;
        }

        var sb = new StringBuilder();
        sb.Append("Top ").Append(hits.Count).Append(" of ").Append(index.DocumentCount)
          .Append(" documents, best first (relevance ")
          .Append(hits[0].Score.ToString("0.00", CultureInfo.InvariantCulture))
          .Append(" … ")
          .Append(hits[^1].Score.ToString("0.00", CultureInfo.InvariantCulture))
          .AppendLine(").");
        if (!index.CodeGraphAvailable)
        {
            sb.AppendLine("Warning: no CodeGraph index was readable, so code joins are unresolved.");
        }

        foreach (var hit in hits)
        {
            sb.AppendLine();
            AppendIdentity(sb, hit);
            AppendExcerpt(sb, hit.Excerpt);
            AppendJoins(sb, hit);
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "docs_for_symbol")]
    [Description("""
        Reverse lookup: the documents whose 'code-refs' frontmatter formally claims a code
        symbol. This is a claim of ownership, not a textual occurrence, so it excludes the
        documents that merely mention the name in prose — which is exactly what grep
        cannot distinguish. Use before changing or renaming a symbol to find the
        documentation that must change with it.
        """)]
    public string ForSymbol(
        [Description("A bare or fully qualified symbol name, e.g. 'DataProtectionHealthCheck'.")] string symbol)
    {
        var index = _host.Current();
        var hits = index.ForSymbol(symbol);

        if (hits.Count == 0)
        {
            return $"No document declares '{symbol}' in its code-refs frontmatter. " +
                   "It may be undocumented, or documented only in prose.";
        }

        var sb = new StringBuilder();
        sb.Append(hits.Count).Append(hits.Count == 1 ? " document declares '" : " documents declare '")
          .Append(symbol).AppendLine("' in code-refs.");

        foreach (var hit in hits)
        {
            sb.AppendLine();
            AppendIdentity(sb, hit);
            AppendJoins(sb, hit);
        }

        return sb.ToString();
    }

    private static void AppendIdentity(StringBuilder sb, DocumentHit hit)
    {
        var doc = hit.Document;
        sb.Append("### ").AppendLine(doc.Frontmatter.Title ?? doc.RelativePath);
        sb.Append("path: ").AppendLine(doc.RelativePath);
        sb.Append("id: ").AppendLine(doc.Frontmatter.Id ?? "(none)");
        sb.Append("doc-type: ").Append(doc.DocType.ToString().ToLowerInvariant())
          .Append("  status: ").Append(doc.Status.ToString().ToLowerInvariant())
          .Append("  component: ").AppendLine(doc.Frontmatter.Component ?? "(none)");
    }

    /// <summary>
    /// Writes the prose, then names whatever did not fit. Naming the omissions is what
    /// lets a caller decide to ask for more instead of giving up and opening the file.
    /// </summary>
    private static void AppendExcerpt(StringBuilder sb, DocumentExcerpt excerpt)
    {
        foreach (var section in excerpt.Sections)
        {
            if (section.Heading.Length > 0) sb.Append("## ").AppendLine(section.Heading);
            sb.AppendLine(Truncate(section.Body, SectionCharCap));
        }

        if (excerpt.Sections.Count == 0) return;

        if (excerpt.IsComplete)
        {
            sb.AppendLine("[complete document]");
        }
        else if (excerpt.OmittedHeadings.Count > 0)
        {
            // Only suggest a bigger budget when the budget is what actually bit. A section
            // dropped for irrelevance will not come back however much budget is offered.
            sb.Append(excerpt.BudgetExhausted
                    ? "[omitted for space, ask again with a larger charBudget: "
                    : "[also in this document, not matching this query: ")
              .Append(string.Join(" · ", excerpt.OmittedHeadings))
              .AppendLine("]");
        }
    }

    private static void AppendJoins(StringBuilder sb, DocumentHit hit)
    {
        if (hit.CodeJoins.Count == 0) return;

        sb.AppendLine("code joins:");
        foreach (var join in hit.CodeJoins.Take(JoinCap))
        {
            sb.Append("  ").Append(join.Reference).Append(" -> ");

            if (join.Location.Length == 0)
            {
                sb.AppendLine("(unresolved)");
                continue;
            }

            sb.Append(join.Location).Append(" [").Append(join.Kind).Append(']');

            // A bare symbol name can name several symbols. Say when the choice was a
            // guess, so the reader verifies instead of trusting it.
            if (!join.InSourceRoot) sb.Append(" (outside this document's source-root)");
            if (join.OtherCandidates > 0) sb.Append(" (+").Append(join.OtherCandidates).Append(" other same-named)");

            sb.AppendLine();
        }

        if (hit.CodeJoins.Count > JoinCap)
        {
            sb.Append("  … ").Append(hit.CodeJoins.Count - JoinCap).AppendLine(" more.");
        }
    }

    private static string Truncate(string text, int cap) =>
        text.Length <= cap ? text : text[..cap] + $"\n… truncated at {cap} characters.";
}
