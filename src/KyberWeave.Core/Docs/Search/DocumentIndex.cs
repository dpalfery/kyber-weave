using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Docs.Search;

/// <summary>
/// Retrieval over the documentation corpus, joined to the code graph.
/// </summary>
/// <remarks>
/// <para>
/// The operation that justifies an index over grep is <see cref="ForSymbol"/>: a document
/// <em>formally claims</em> a symbol in its <c>code-refs</c> frontmatter, which is a
/// different and much smaller set than the documents that happen to mention the name in
/// prose. Grep cannot tell the two apart.
/// </para>
/// <para>
/// The index is immutable. Staleness is handled by rebuilding a whole snapshot — see
/// <see cref="DocumentIndexHost"/> — rather than by mutating this object, so a request in
/// flight always sees one coherent view of the corpus.
/// </para>
/// </remarks>
public sealed class DocumentIndex
{
    /// <summary>Frontmatter identity outranks prose: a document that formally claims the
    /// query term is a better answer than one that merely discusses it.</summary>
    private const double IdWeight = 6.0;
    private const double CodeRefWeight = 5.0;
    private const double EndpointWeight = 5.0;
    private const double ComponentWeight = 3.0;
    private const double TitleWeight = 2.0;
    private const double BodyWeight = 1.0;

    /// <summary>
    /// Weights for partial identity matches. A natural-language question almost never
    /// equals a document's id or component verbatim, so exact-equality scoring alone
    /// leaves free-text queries decided entirely by body similarity — where a generic
    /// doc-type word like "architecture" outvotes the subject the user actually named.
    /// Partial matches sit below their exact counterparts but above prose.
    /// </summary>
    private const double IdPartialWeight = 4.5;
    private const double ComponentPartialWeight = 2.5;

    /// <summary>
    /// Section length at which the length damping reaches half strength. Raw cosine
    /// similarity is biased towards very short sections, because a three-word stub can
    /// overlap a query almost perfectly while saying nothing.
    /// </summary>
    private const double SectionSaturation = 40.0;

    /// <summary>Below this, a section is not relevant enough to be worth returning.</summary>
    private const double MinSectionScore = 0.02;

    /// <summary>
    /// Default characters of prose returned across all documents in one call.
    /// </summary>
    /// <remarks>
    /// Sized from the corpus rather than guessed. The median section is ~640 characters
    /// and the median whole document ~5,100, so returning a single section saves a few
    /// hundred characters while routinely forcing the caller to read the entire file —
    /// a net loss. The budget is spent across the returned documents, which means asking
    /// for one document gets depth and asking for five gets breadth, from the same knob.
    /// </remarks>
    public const int DefaultCharBudget = 12000;

    /// <summary>
    /// Floor on any single document's share, so a wide query still returns something
    /// substantive per hit rather than slicing everything into uselessness.
    /// </summary>
    private const int MinPerDocumentBudget = 1500;

    /// <summary>
    /// Total score below which a document is not returned at all.
    /// </summary>
    /// <remarks>
    /// Without a floor every query returned exactly <c>maxDocs</c> documents, so a miss
    /// was indistinguishable from a hit — "how do I make a sandwich" came back with three
    /// confident results. For a tool the repository instructions make mandatory before
    /// grep, that is the most consequential possible failure: the caller has no signal to
    /// fall back and answers from whatever was nearest.
    /// </remarks>
    public const double MinRelevanceScore = 0.25;

    private readonly DocumentCorpus _corpus;
    private readonly Dictionary<string, List<DocumentModel>> _bySymbol;
    private readonly Dictionary<string, IReadOnlyList<CodeJoin>> _joinsByPath;

    private DocumentIndex(
        DocumentCorpus corpus,
        Dictionary<string, List<DocumentModel>> bySymbol,
        Dictionary<string, IReadOnlyList<CodeJoin>> joinsByPath)
    {
        _corpus = corpus;
        _bySymbol = bySymbol;
        _joinsByPath = joinsByPath;
    }

    /// <summary>Documents in scope.</summary>
    public int DocumentCount => _corpus.Documents.Count;

    /// <summary>True when the code graph was readable, so joins are populated.</summary>
    public bool CodeGraphAvailable { get; private init; }

    /// <summary>
    /// Builds an index over an already-computed corpus. Kept separate from
    /// <see cref="DocumentCorpus.Build"/> so a code-graph change can rebuild the joins
    /// without re-reading and re-vectorising every document.
    /// </summary>
    public static DocumentIndex Build(DocumentCorpus corpus, ICodeGraphResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(resolver);

        var bySymbol = new Dictionary<string, List<DocumentModel>>(StringComparer.Ordinal);
        var joinsByPath = new Dictionary<string, IReadOnlyList<CodeJoin>>(StringComparer.Ordinal);

        foreach (var doc in corpus.Documents)
        {
            var joins = new List<CodeJoin>();

            foreach (var symbol in doc.CodeRefs)
            {
                foreach (var key in SymbolKeys(symbol))
                {
                    if (!bySymbol.TryGetValue(key, out var list))
                    {
                        list = [];
                        bySymbol[key] = list;
                    }
                    if (!list.Contains(doc)) list.Add(doc);
                }

                joins.Add(ToJoin(doc, symbol, resolver.ResolveSymbol(symbol)));
            }

            foreach (var endpoint in doc.ApiEndpoints)
            {
                joins.Add(ToJoin(doc, endpoint, resolver.ResolveRoute(endpoint)));
            }

            joinsByPath[doc.RelativePath] = joins;
        }

        return new DocumentIndex(corpus, bySymbol, joinsByPath)
        {
            CodeGraphAvailable = resolver.IsAvailable
        };
    }

    /// <summary>Builds an index from an already-loaded document set and resolver.</summary>
    public static DocumentIndex Build(DocumentSet set, ICodeGraphResolver resolver) =>
        Build(DocumentCorpus.Build(set), resolver);

    /// <summary>
    /// Free-text, symbol, route, component or doc-id retrieval. Returns the highest
    /// ranked documents, each carrying as much of its prose as the budget allows.
    /// </summary>
    /// <param name="query">Free text, or a symbol, route, component or doc-id name.</param>
    /// <param name="maxDocs">How many documents to return.</param>
    /// <param name="charBudget">
    /// Total characters of prose across all returned documents. Split between them, so
    /// narrowing <paramref name="maxDocs"/> deepens each result rather than just
    /// shortening the list.
    /// </param>
    public IReadOnlyList<DocumentHit> Explore(string query, int maxDocs = 5, int charBudget = DefaultCharBudget)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        maxDocs = Math.Clamp(maxDocs, 1, 20);
        charBudget = Math.Clamp(charBudget, 1000, 120_000);

        // The query is fused the same way document bodies are, which makes an adjacent
        // pair of words behave as a weak phrase match: "logged out" yields the term
        // "loggedout", which the troubleshooting runbook has and a spec that merely
        // "logged an error" does not. Fusing only one side threw that signal away.
        var queryVector = TextVectorizer.VectorizeFused(query);

        // Coverage is judged on the plain words only — see DocumentCorpus.ScoreBody.
        var coverageTerms = TextVectorizer.Vectorize(query).Keys.ToList();
        var trimmed = query.Trim();

        // Query terms are weighted by how rare they are corpus-wide before any
        // section-level comparison, so a term every document shares cannot decide which
        // part of a document is the answer.
        var rarityWeighted = _corpus.WeightByRarity(queryVector);

        var scored = new List<(DocumentModel Doc, double Score)>();
        foreach (var doc in _corpus.Documents)
        {
            var score = ScoreExact(doc, trimmed);
            score += ScorePartialIdentity(doc, queryVector);
            score += TitleWeight * TextVectorizer.Similarity(doc.Frontmatter.Title ?? doc.RelativePath, trimmed);
            score += BodyWeight * _corpus.ScoreBody(doc, queryVector, coverageTerms);

            score *= Authority(doc);

            if (score < MinRelevanceScore) continue;
            scored.Add((doc, score));
        }

        var top = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Doc.RelativePath, StringComparer.Ordinal)
            .Take(maxDocs)
            .ToList();

        if (top.Count == 0) return [];

        var perDocument = Math.Max(MinPerDocumentBudget, charBudget / top.Count);

        return top
            .Select(s => new DocumentHit(
                s.Doc, s.Score, Excerpt(s.Doc, rarityWeighted, perDocument), JoinsFor(s.Doc)))
            .ToList();
    }

    /// <summary>
    /// The documents whose <c>code-refs</c> formally claim a symbol. This is the reverse
    /// lookup the ontology exists to make possible; it is a claim of ownership, not a
    /// textual occurrence.
    /// </summary>
    public IReadOnlyList<DocumentHit> ForSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return [];

        if (!_bySymbol.TryGetValue(symbol.Trim(), out var documents)) return [];

        return documents
            .OrderBy(d => d.RelativePath, StringComparer.Ordinal)
            .Select(d => new DocumentHit(d, 1.0, DocumentExcerpt.Empty, JoinsFor(d)))
            .ToList();
    }

    private IReadOnlyList<CodeJoin> JoinsFor(DocumentModel doc) =>
        _joinsByPath.TryGetValue(doc.RelativePath, out var joins) ? joins : [];

    /// <summary>
    /// Both the reference as authored and its last dotted segment are indexed, so a
    /// <c>code-refs</c> entry of <c>MotorcycleRAG.Api.HealthChecks.Foo</c> is findable by
    /// the bare type name a caller would actually type.
    /// </summary>
    private static IEnumerable<string> SymbolKeys(string reference)
    {
        var value = reference.Trim();
        if (value.Length == 0) yield break;

        yield return value;

        var lastDot = value.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < value.Length - 1)
        {
            yield return value[(lastDot + 1)..];
        }
    }

    /// <summary>
    /// Chooses which of several same-named symbols a document actually meant.
    /// </summary>
    /// <remarks>
    /// Bare symbol names collide freely: <c>HostHeaderValidationMiddleware</c>,
    /// <c>CorsServiceConfiguration</c> and <c>WebApplicationExtensions</c> each exist in
    /// both the API and the BFF, and <c>AuthProvider</c> is simultaneously a React context
    /// provider in TypeScript and a C# claims property. Taking the index's first match
    /// therefore joins documents to symbols in projects they do not describe.
    ///
    /// The document already declares which subtree it is about, so scope to its
    /// <c>source-root</c> first and fall back to a repo-wide match only when the name
    /// resolves nowhere beneath it — flagging that fallback rather than hiding it.
    /// </remarks>
    internal static CodeJoin ToJoin(DocumentModel doc, string reference, IReadOnlyList<CodeGraphNode> nodes)
    {
        if (nodes.Count == 0) return new CodeJoin(reference, "unresolved", string.Empty);

        var sourceRoot = doc.Frontmatter.SourceRoot?.Replace('\\', '/').TrimEnd('/');

        var scoped = string.IsNullOrWhiteSpace(sourceRoot) || sourceRoot == "."
            ? []
            : nodes.Where(n => n.FilePath.Replace('\\', '/')
                       .StartsWith(sourceRoot + "/", StringComparison.OrdinalIgnoreCase))
                   .ToList();

        var inSourceRoot = scoped.Count > 0;
        var pool = inSourceRoot ? scoped : nodes;

        // Within the pool, a declaration beats an incidental member of the same name: a
        // class or interface named X is far likelier to be what documentation calls "X"
        // than a property that happens to be called X.
        var node = pool
            .OrderByDescending(n => DeclarationRank(n.Kind))
            .ThenBy(n => n.FilePath, StringComparer.Ordinal)
            .First();

        return new CodeJoin(reference, node.Kind, node.Location, inSourceRoot, pool.Count - 1);
    }

    private static int DeclarationRank(string kind) => kind switch
    {
        "class" or "interface" or "struct" or "enum" or "type_alias" => 3,
        "function" or "method" => 2,
        "route" => 2,
        _ => 1
    };

    private double ScoreExact(DocumentModel doc, string query)
    {
        double score = 0;

        if (string.Equals(doc.Frontmatter.Id, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(doc.RelativePath, query, StringComparison.OrdinalIgnoreCase))
        {
            score += IdWeight;
        }

        if (string.Equals(doc.Frontmatter.Component, query, StringComparison.OrdinalIgnoreCase))
        {
            score += ComponentWeight;
        }

        foreach (var reference in doc.CodeRefs)
        {
            if (SymbolKeys(reference).Contains(query, StringComparer.Ordinal))
            {
                score += CodeRefWeight;
                break;
            }
        }

        foreach (var endpoint in doc.ApiEndpoints)
        {
            if (string.Equals(endpoint, query, StringComparison.OrdinalIgnoreCase))
            {
                score += EndpointWeight;
                break;
            }
        }

        return score;
    }

    /// <summary>
    /// How far a document counts as current guidance, as a multiplier on its relevance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Term statistics measure wordiness, not authority. Asked "what frontmatter keys does
    /// a document need", BM25 correctly preferred the plan that says "frontmatter"
    /// twenty-five times over the documentation standard that says it seven times — and
    /// answered from a work artifact instead of the rule.
    /// </para>
    /// <para>
    /// The repository already takes a position on this: plans are archived once closed and
    /// the archive is explicitly never current guidance, so a plan or a spec is a record of
    /// intent, not the thing an agent should act on. That judgement belongs in ranking. A
    /// demoted document still wins when it is named outright, because an exact id match
    /// scores far above the discount.
    /// </para>
    /// </remarks>
    internal static double Authority(DocumentModel doc)
    {
        var byType = doc.DocType switch
        {
            DocType.Plan or DocType.Spec => 0.55,
            DocType.Adr => 0.9,
            _ => 1.0
        };

        var byStatus = doc.Status switch
        {
            DocStatus.Superseded => 0.4,
            DocStatus.Draft or DocStatus.NeedsReview => 0.85,
            _ => 1.0
        };

        return byType * byStatus;
    }

    /// <summary>
    /// How much of a document's declared identity the query names.
    /// </summary>
    /// <remarks>
    /// Document ids are structured slugs — <c>webui/architecture</c>, <c>api/architecture</c>
    /// — so splitting on the separators yields the closest thing this corpus has to a
    /// controlled vocabulary. A query naming both halves of an id is a far stronger signal
    /// than body similarity, which is what "WebUI architecture" returning the API,
    /// system and Azure architecture documents was: three documents sharing one generic
    /// word, ranked above the one the user actually asked for.
    /// </remarks>
    private static double ScorePartialIdentity(DocumentModel doc, IReadOnlyDictionary<string, double> queryVector)
    {
        if (queryVector.Count == 0) return 0;

        return (IdPartialWeight * Coverage(doc.Frontmatter.Id, queryVector))
             + (ComponentPartialWeight * Coverage(doc.Frontmatter.Component, queryVector));
    }

    /// <summary>
    /// The fraction of an identity's own tokens that the query mentions.
    /// </summary>
    /// <remarks>
    /// A token also counts as mentioned when the query contains it fused to its neighbour.
    /// That fusion is not a nicety: the component "MotorcycleRAG Web UI" has to be
    /// reachable from a query that writes "WebUI" as one word, which is how people type it.
    /// </remarks>
    internal static double Coverage(string? identity, IReadOnlyDictionary<string, double> queryVector)
    {
        if (string.IsNullOrWhiteSpace(identity)) return 0;

        var parts = TextVectorizer
            .Vectorize(identity.Replace('/', ' ').Replace('-', ' '))
            .Keys
            .ToList();

        if (parts.Count == 0) return 0;

        var covered = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var hit = queryVector.ContainsKey(parts[i])
                || (i + 1 < parts.Count && queryVector.ContainsKey(parts[i] + parts[i + 1]))
                || (i > 0 && queryVector.ContainsKey(parts[i - 1] + parts[i]));

            if (hit) covered++;
        }

        return (double)covered / parts.Count;
    }

    /// <summary>
    /// Fills a document's share of the budget with its most relevant <c>##</c> sections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returning exactly one section was the original design and it was wrong for this
    /// corpus. Sections are small — a median of ~640 characters — so one section is about
    /// a third of a document, and the caller who needed the other two thirds read the
    /// whole file anyway. Being stingy cost more than it saved.
    /// </para>
    /// <para>
    /// Sections are <em>chosen</em> by relevance but <em>emitted</em> in document order,
    /// because prose written to be read in sequence is confusing when shuffled. Anything
    /// that did not fit is named rather than silently dropped.
    /// </para>
    /// <para>
    /// Relevance is damped by section length. Undamped cosine similarity systematically
    /// prefers the shortest section sharing any term with the query, so a heading-only
    /// stub beat the paragraph that held the answer.
    /// </para>
    /// </remarks>
    private static DocumentExcerpt Excerpt(
        DocumentModel doc,
        IReadOnlyDictionary<string, double> queryVector,
        int budget)
    {
        if (doc.Sections.Count == 0) return DocumentExcerpt.Empty;

        var ranked = doc.Sections
            .Select(section =>
            {
                var vector = TextVectorizer.VectorizeFused(section.Heading + "\n" + section.Body);
                var length = vector.Values.Sum();
                var score = TextVectorizer.CosineSimilarity(queryVector, vector)
                          * (length / (length + SectionSaturation));
                return (Section: section, Score: score);
            })
            .OrderByDescending(s => s.Score)
            .ToList();

        var chosen = new List<DocumentSection>();
        var omitted = new List<DocumentSection>();
        var spent = 0;
        var budgetExhausted = false;

        foreach (var (section, score) in ranked)
        {
            var cost = section.Heading.Length + section.Body.Length;

            // The top section is always included even if it alone exceeds the budget —
            // it gets truncated at the transport edge. Returning nothing but a path would
            // make the tool a directory listing, which is what sent callers to Read.
            var mustInclude = chosen.Count == 0 && score >= MinSectionScore;

            if (score < MinSectionScore && !mustInclude)
            {
                omitted.Add(section);
                continue;
            }

            if (mustInclude || spent + cost <= budget)
            {
                chosen.Add(section);
                spent += cost;
            }
            else
            {
                omitted.Add(section);
                budgetExhausted = true;
            }
        }

        if (chosen.Count == 0) return DocumentExcerpt.Empty;

        return new DocumentExcerpt(
            chosen.OrderBy(s => s.LineNumber).ToList(),
            omitted.OrderBy(s => s.LineNumber).Select(s => s.Heading).Where(h => h.Length > 0).ToList(),
            omitted.Count == 0,
            budgetExhausted);
    }
}
