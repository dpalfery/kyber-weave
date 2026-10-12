using System.ComponentModel;
using System.Globalization;
using System.Text;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The documentation retrieval tools. Output is capped the way <c>codegraph_explore</c>
/// caps its own: a retrieval tool that returns a whole corpus is a slower grep.
/// </summary>
[McpServerToolType]
public sealed class DocsTools(DocumentIndexHost host)
{
    /// <summary>Hard ceiling on any single section, so one huge section cannot crowd out
    /// every other document in the response.</summary>
    private const int SectionCharCap = 6000;

    /// <summary>Code joins listed per document.</summary>
    private const int JoinCap = 20;

    /// <summary>Hard caps for the conversational analysis surface.</summary>
    private const int AnalysisCandidateCap = 20;
    private const int AnalysisCharCap = 12000;
    private const int AnalysisEvidenceCap = 8;

    private static readonly HashSet<string> AnalysisKindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(AnalysisRuleKind.Duplicate),
        nameof(AnalysisRuleKind.Conflict),
        nameof(AnalysisRuleKind.Terminology)
    };

    private readonly DocumentIndexHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly IDocsAnalysisReader? _analysisReader;

    public DocsTools(DocumentIndexHost host, IDocsAnalysisReader analysisReader)
        : this(host)
    {
        _analysisReader = analysisReader ?? throw new ArgumentNullException(nameof(analysisReader));
    }

    [McpServerTool(Name = "docs_explore", ReadOnly = true, OpenWorld = false)]
    [Description("""
        DOCS FIRST — call before grepping or reading anything under docs/ or other markdown.
        Searches this repository documentation (guides, runbooks, decision records, design
        notes) and returns the best-matching sections of each document in ONE call, ranked, plus the code
        they reference as symbol -> file:line. Treat the returned sections as already read; do
        NOT reopen those files. Retired and archived pages are excluded, so the answer is
        current guidance.

        Use for: how X works, how to install/run/configure it, what a command, flag, or rule
        id means, why a design decision was made, is X documented, and before starting a task
        a runbook or design note might cover. Query is a plain question or a name: command,
        flag, rule id, component, or doc id.

        A miss says how many documents were searched: the subject is undocumented, so grep
        is now fine. Do not use for what the code does right now; read the code instead.
        """)]
    public string Explore(
        [Description("The question in plain words, or a command, flag, rule id, feature or component name, or document id.")] string query,
        [Description("""
            Maximum documents to return (1-20). Defaults to 5. Lowering this deepens each
            result rather than merely shortening the list, because charBudget is shared
            across the documents returned; maxDocs=1 usually fits a whole document.
            """)]
        int maxDocs = 5,
        [Description("""
            Total characters of prose across all returned documents (1000-120000). Defaults
            to 12000. Sections that did not fit are named in the response; re-query with a
            larger budget to retrieve them.
            """)]
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        IReadOnlyList<DocumentHit> hits = index.Explore(query, maxDocs, charBudget);

        if (hits.Count == 0)
        {
            // Saying so plainly is the whole point of the relevance floor. An agent told
            // to use this before grepping needs an explicit signal that it may now grep.
            string docsIndex = Path.Combine(_host.DocsRelativeRoot, "README.md").Replace('\\', '/');
            return Lead(provenance, $"""
                No document in the governed corpus scored above the relevance threshold for '{query}'.
                {index.DocumentCount} documents were considered.

                This is a real miss, not an empty corpus. Either the subject is undocumented, or the
                question uses vocabulary the documentation does not. Try naming a component, a doc-id
                ("webui/architecture"), or a code symbol; or fall back to reading {docsIndex}.
                """);
        }

        StringBuilder sb = new();
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

        foreach (DocumentHit hit in hits)
        {
            sb.AppendLine();
            AppendIdentity(sb, hit);
            AppendExcerpt(sb, hit.Excerpt);
            AppendJoins(sb, hit);
        }

        return Lead(provenance, sb.ToString());
    }

    [McpServerTool(Name = "docs_for_symbol", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when about to rename, move, delete, or change the signature of a class, method,
        property, or endpoint: call BEFORE the change. Returns every document that claims the symbol in its
        'code-refs' frontmatter, with each document's identity and code joins as
        symbol -> file:line: exactly the docs you must update with the change. Also use
        after a change to find stale docs, or to check whether a symbol is documented.

        Empty result: no document claims it, so nothing to update.
        """)]
    public string ForSymbol(
        [Description("A bare or fully qualified symbol name, e.g. 'DataProtectionHealthCheck' or 'KyberWeave.Core.Docs.Search.DocumentIndex'. Both forms resolve.")] string symbol)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        IReadOnlyList<DocumentHit> hits = index.ForSymbol(symbol);

        if (hits.Count == 0)
        {
            return Lead(
                provenance,
                $"No document declares '{symbol}' in its code-refs frontmatter. " +
                "It may be undocumented, or documented only in prose.");
        }

        StringBuilder sb = new();
        sb.Append(hits.Count).Append(hits.Count == 1 ? " document declares '" : " documents declare '")
          .Append(symbol).AppendLine("' in code-refs.");

        foreach (DocumentHit hit in hits)
        {
            sb.AppendLine();
            AppendIdentity(sb, hit);
            AppendJoins(sb, hit);
        }

        return Lead(provenance, sb.ToString());
    }

    [McpServerTool(Name = "docs_analysis_candidates", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Documentation quality audit. Returns a paged list of duplicate claims, conflicting
        statements, and ambiguous terms across the docs, each with line-addressable evidence.

        Use when docs may contradict themselves, when consolidating or deduplicating pages,
        when choosing which of two statements is canonical, or before a release or large
        docs edit. To answer a question, call docs_explore instead.

        Candidates are review evidence only; nothing is applied. Empty: nothing pending.
        """)]
    public string AnalysisCandidates(
        [Description("Limits results to one kind of finding: 'duplicate', 'conflict', or 'terminology'. Omit to return every kind.")] string? kind = null,
        [Description("Opaque candidate id taken from the 'next cursor' line of a previous response, to continue after that item. Omit for the first page.")] string? cursor = null,
        [Description("Maximum candidates to return (1-20). Defaults to 20; larger values are capped at 20.")] int limit = 20,
        [Description("Maximum response characters (up to 12000). Defaults to 12000. Evidence per candidate is capped independently, so one candidate cannot consume the whole page.")] int charBudget = 12000)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        int effectiveBudget = Math.Max(0, Math.Clamp(charBudget, 0, AnalysisCharCap) - provenance.Length - 1);

        if (_analysisReader is null)
            return Lead(provenance, "Documentation analysis is unavailable in this host.");

        if (!TryParseKind(kind, out AnalysisRuleKind? parsedKind))
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"Unknown documentation-analysis kind '{kind}'. Use duplicate, conflict, or terminology.",
                    effectiveBudget));
        }

        DocumentationAnalysisResult result;
        try
        {
            result = _analysisReader.Analyze();
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"Documentation analysis is unavailable: {exception.Message}",
                    effectiveBudget));
        }

        AnalysisCandidate[] ordered =
        [
            .. result.Candidates
                .Where(candidate => parsedKind is null || candidate.Kind == parsedKind)
                .OrderBy(candidate => candidate.Kind)
                .ThenBy(candidate => candidate.Term, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
        ];
        int start = ResolveCursor(ordered, cursor);
        if (start < 0)
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"The analysis cursor '{cursor}' is no longer present. Start again without a cursor.",
                    effectiveBudget));
        }

        int effectiveLimit = Math.Clamp(limit, 1, AnalysisCandidateCap);
        StringBuilder sb = new(Math.Min(effectiveBudget, 4096));
        AppendAnalysisMetrics(sb, result);

        if (start >= ordered.Length)
        {
            sb.AppendLine(parsedKind is null
                ? "No documentation-analysis candidates are pending."
                : $"No {parsedKind.Value.ToString().ToUpperInvariant()} candidates are pending.");
            return Lead(provenance, CapToBudget(sb.ToString(), effectiveBudget));
        }

        int emitted = 0;
        string? lastCursor = null;
        while (emitted < effectiveLimit && start + emitted < ordered.Length)
        {
            AnalysisCandidate candidate = ordered[start + emitted];
            bool moreAfter = start + emitted + 1 < ordered.Length;
            string cursorFooter = moreAfter ? $"next cursor: {candidate.Id}{Environment.NewLine}" : string.Empty;
            int available = effectiveBudget - sb.Length - cursorFooter.Length;
            if (available <= 0) break;

            string block = FormatCandidate(candidate);
            sb.Append(CapToBudget(block, available));
            emitted++;
            lastCursor = candidate.Id;

            if (block.Length > available) break;
        }

        if (emitted == 0)
            return Lead(
                provenance,
                CapToBudget(
                    sb.AppendLine("The response budget is too small for a candidate.").ToString(),
                    effectiveBudget));

        if (start + emitted < ordered.Length && lastCursor is not null)
        {
            string footer = $"next cursor: {lastCursor}{Environment.NewLine}";
            if (sb.Length + footer.Length <= effectiveBudget) sb.Append(footer);
        }

        return Lead(provenance, CapToBudget(sb.ToString(), effectiveBudget));
    }

    [McpServerTool(Name = "docs_glossary", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Looks up ONE term in the managed glossary: its approved, proposed, and rejected
        senses with definitions, scopes, and aliases.

        Use when a word is overloaded or unclear in this codebase, before naming a new
        component or introducing vocabulary, and when writing docs that must match
        established wording. Pass the term alone, not a sentence.

        Unknown term: empty result, no sense declared.
        """)]
    public string Glossary(
        [Description("A single glossary term, matched case-insensitively. Pass the term alone, not a sentence.")] string term)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        int effectiveBudget = Math.Max(0, AnalysisCharCap - provenance.Length - 1);

        if (_analysisReader is null)
            return Lead(provenance, "The managed documentation glossary is unavailable in this host.");
        if (string.IsNullOrWhiteSpace(term))
            return Lead(provenance, "Provide a glossary term to look up.");

        GlossaryLookupResult result;
        try
        {
            result = _analysisReader.LookupGlossary(term);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"The managed documentation glossary is unavailable: {exception.Message}",
                    effectiveBudget));
        }

        if (result.Senses.Count == 0)
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"No glossary senses are declared for '{result.Term.ReplaceLineEndings(" ")}'.",
                    effectiveBudget));
        }

        StringBuilder sb = new();
        sb.Append(result.Senses.Count)
          .Append(result.Senses.Count == 1 ? " glossary sense for '" : " glossary senses for '")
          .Append(result.Term.ReplaceLineEndings(" "))
          .AppendLine("'.");
        foreach (GlossarySense sense in result.Senses
                     .OrderBy(sense => sense.Status)
                     .ThenBy(sense => sense.Id, StringComparer.Ordinal)
                     .Take(AnalysisCandidateCap))
        {
            sb.AppendLine();
            sb.Append("sense: ").AppendLine(sense.Id);
            sb.Append("status: ").AppendLine(sense.Status.ToString().ToUpperInvariant());
            sb.Append("definition: ").AppendLine(
                string.IsNullOrWhiteSpace(sense.Definition)
                    ? "(not yet defined)"
                    : CapToBudget(sense.Definition.ReplaceLineEndings(" "), 1000));
            sb.Append("scope: ").AppendLine(
                sense.Scopes.Count == 0
                    ? "(none)"
                    : CapToBudget(
                        string.Join("; ", sense.Scopes.Take(AnalysisEvidenceCap).Select(scope => CapToBudget(scope, 240))),
                        1000));
            sb.Append("aliases: ").AppendLine(
                sense.Aliases.Count == 0
                    ? "(none)"
                    : CapToBudget(
                        string.Join("; ", sense.Aliases.Take(AnalysisEvidenceCap).Select(alias => CapToBudget(alias, 240))),
                        1000));
        }

        if (result.Senses.Count > AnalysisCandidateCap)
            sb.AppendLine(CultureInfo.InvariantCulture, $"… {result.Senses.Count - AnalysisCandidateCap} more senses omitted by the hard cap.");
        return Lead(provenance, CapToBudget(sb.ToString(), effectiveBudget));
    }

    /// <summary>
    /// Reports the provenance of the corpus this server answers from, with no query. This is
    /// the cheap session-start check an agent calls before trusting any other docs tool.
    /// </summary>
    [McpServerTool(Name = "docs_status", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Which repository is this server answering from? Returns one line: absolute repo
        root, short HEAD revision (with dirty marker), and document count. No arguments.

        Use when a docs answer names a file missing from your checkout, or when several
        servers may be bound to different repositories. Not needed otherwise: every docs
        tool already leads with this line.

        Zero documents is not an error; the line still reports the root.
        """)]
    public string DocsStatus()
    {
        DocumentIndex index = _host.Current();
        return Provenance(index);
    }

    /// <summary>
    /// The provenance header for a response answered from <paramref name="index"/>. Every
    /// user-visible return leads with this so a caller can tell which corpus answered.
    /// </summary>
    private string Provenance(DocumentIndex index) =>
        CorpusProvenance.Line(_host.RepositoryRoot, index.DocumentCount, _host.CorpusBuilds);

    /// <summary>
    /// Puts the provenance line first, then the body. The header is emitted whole and the
    /// body is what gets clipped, so the tell is never the part truncated away.
    /// </summary>
    private static string Lead(string provenance, string body) =>
        provenance + "\n" + body;

    private static bool TryParseKind(string? kind, out AnalysisRuleKind? parsedKind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            parsedKind = null;
            return true;
        }

        string trimmed = kind.Trim();
        if (AnalysisKindNames.Contains(trimmed)
            && Enum.TryParse(trimmed, ignoreCase: true, out AnalysisRuleKind parsed))
        {
            parsedKind = parsed;
            return true;
        }

        parsedKind = null;
        return false;
    }

    private static int ResolveCursor(IReadOnlyList<AnalysisCandidate> candidates, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;

        for (int index = 0; index < candidates.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(candidates[index].Id, cursor)) return index + 1;
        }

        return -1;
    }

    private static void AppendAnalysisMetrics(StringBuilder sb, DocumentationAnalysisResult result)
    {
        AnalysisMetrics metrics = result.Metrics;
        sb.AppendLine("metrics:");
        sb.Append("  extracted claims: ").AppendLine(metrics.ExtractedClaims.ToString(CultureInfo.InvariantCulture));
        sb.Append("  graph comparisons: ").AppendLine(metrics.GraphComparisons.ToString(CultureInfo.InvariantCulture));
        sb.Append("  lexical comparisons: ").AppendLine(metrics.LexicalComparisons.ToString(CultureInfo.InvariantCulture));
        sb.Append("  embedding comparisons: ").AppendLine(metrics.EmbeddingComparisons.ToString(CultureInfo.InvariantCulture));
        sb.Append("  candidates: graph ").Append(metrics.GraphCandidates.ToString(CultureInfo.InvariantCulture))
          .Append(", lexical ").Append(metrics.LexicalCandidates.ToString(CultureInfo.InvariantCulture))
          .Append(", embedding ").AppendLine(metrics.EmbeddingCandidates.ToString(CultureInfo.InvariantCulture));
        sb.Append("  truncated: ").AppendLine(metrics.Truncated ? "yes" : "no");
        AppendOptionalMetric(sb, result, "embeddingCacheHits", "embedding cache hits");
        AppendOptionalMetric(sb, result, "embeddingCacheMisses", "embedding cache misses");
        AppendOptionalMetric(sb, result, "embeddingPromptTokens", "embedding prompt tokens");
        AppendOptionalMetric(sb, result, "embeddingTotalTokens", "embedding total tokens");
        sb.Append("  diagnostics: ")
          .Append(result.Diagnostics.Errors.ToString(CultureInfo.InvariantCulture)).Append(" errors, ")
          .Append(result.Diagnostics.Warnings.ToString(CultureInfo.InvariantCulture)).Append(" warnings, ")
          .Append(result.Diagnostics.Infos.ToString(CultureInfo.InvariantCulture)).AppendLine(" info");
        foreach (Diagnostic diagnostic in result.Diagnostics.Items
                     .Where(item => item.Severity is Severity.Warning or Severity.Error or Severity.Critical)
                     .Take(3))
        {
            sb.Append("  ").Append(diagnostic.Code).Append(" [")
              .Append(diagnostic.Severity.ToString().ToUpperInvariant()).Append("]: ")
              .AppendLine(CapToBudget(diagnostic.Message.ReplaceLineEndings(" "), 240));
        }
        sb.AppendLine();
    }

    private static void AppendOptionalMetric(
        StringBuilder sb,
        DocumentationAnalysisResult result,
        string key,
        string label)
    {
        if (!result.Diagnostics.Metrics.TryGetValue(key, out object? value)) return;
        sb.Append("  ").Append(label).Append(": ").AppendLine(
            Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static string FormatCandidate(AnalysisCandidate candidate)
    {
        StringBuilder sb = new();
        sb.Append("candidate: ").AppendLine(candidate.Id);
        sb.Append("kind: ").AppendLine(candidate.Kind.ToString().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(candidate.Term))
            sb.Append("term: ").AppendLine(Truncate(candidate.Term.ReplaceLineEndings(" "), 240));
        sb.Append("evidence: ").AppendLine(string.Join(
            "; ",
            candidate.Claims
                .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
                .ThenBy(claim => claim.StartLine)
                .Take(AnalysisEvidenceCap)
                .Select(claim => $"{claim.FilePath}:{claim.StartLine}-{claim.EndLine}")));
        sb.Append("scores: lexical ")
          .Append(candidate.Score.Lexical.ToString("0.000", CultureInfo.InvariantCulture))
          .Append(", semantic ")
          .Append(candidate.Score.Semantic?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a")
          .Append(", graph ")
          .AppendLine(candidate.Score.Graph.ToString("0.000", CultureInfo.InvariantCulture));
        if (candidate.Sources.Count > 0)
        {
            sb.Append("sources: ").AppendLine(string.Join(
                ", ",
                candidate.Sources
                    .OrderBy(source => source)
                    .Select(source => source.ToString().ToUpperInvariant())));
        }
        if (candidate.Verdict is not null)
        {
            sb.Append("verdict: ").Append(candidate.Verdict.Label.ToString().ToUpperInvariant())
              .Append(" at ")
              .AppendLine(candidate.Verdict.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
        }

        foreach (Claim claim in candidate.Claims
                     .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
                     .ThenBy(claim => claim.StartLine)
                     .Take(AnalysisEvidenceCap))
        {
            sb.Append("  - ").Append(claim.FilePath).Append(':')
              .Append(claim.StartLine.ToString(CultureInfo.InvariantCulture)).Append('-')
              .Append(claim.EndLine.ToString(CultureInfo.InvariantCulture))
              .Append(" [").Append(claim.DocumentIdentity).AppendLine("]");
            sb.Append("    ").AppendLine(Truncate(claim.Text.ReplaceLineEndings(" "), 240));
        }
        if (candidate.Claims.Count > AnalysisEvidenceCap)
            sb.Append("  … ").Append(candidate.Claims.Count - AnalysisEvidenceCap).AppendLine(" more evidence locations.");
        sb.AppendLine();
        return sb.ToString();
    }

    private static bool IsExpectedReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException;

    private static string CapToBudget(string text, int cap) =>
        text.Length <= cap ? text : text[..cap];

    private static void AppendIdentity(StringBuilder sb, DocumentHit hit)
    {
        DocumentModel doc = hit.Document;
        sb.Append("### ").AppendLine(doc.Frontmatter.Title ?? doc.RelativePath);
        sb.Append("path: ").AppendLine(doc.RelativePath);
        sb.Append("id: ").AppendLine(doc.Frontmatter.Id ?? "(none)");
        sb.Append("doc-type: ").Append(doc.DocType.ToString().ToUpperInvariant())
          .Append("  status: ").Append(doc.Status.ToString().ToUpperInvariant())
          .Append("  component: ").AppendLine(doc.Frontmatter.Component ?? "(none)");
    }

    /// <summary>
    /// Writes the prose, then names whatever did not fit. Naming the omissions is what
    /// lets a caller decide to ask for more instead of giving up and opening the file.
    /// </summary>
    private static void AppendExcerpt(StringBuilder sb, DocumentExcerpt excerpt)
    {
        foreach (DocumentSection section in excerpt.Sections)
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
        foreach (CodeJoin join in hit.CodeJoins.Take(JoinCap))
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
