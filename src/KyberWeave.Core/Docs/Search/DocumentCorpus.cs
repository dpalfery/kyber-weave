using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Docs.Search;

/// <summary>
/// Corpus-level statistics over the document bodies: term rarity and document lengths.
/// </summary>
/// <remarks>
/// <para>
/// This exists because term frequency alone ranks badly on a corpus that is entirely
/// about one system. "user", "authentication", "api" and "session" appear in nearly every
/// document here, so under plain term-frequency cosine they contribute as much evidence
/// as a rare, decisive term — and the documents densest in that shared vocabulary win
/// regardless of what was asked. A plainly-worded question ("why does the user keep
/// getting logged out") returned three requirements documents and never surfaced the
/// troubleshooting runbook that answers it verbatim.
/// </para>
/// <para>
/// Rarity is measured across the corpus and folded into scoring with Okapi BM25, so a
/// term shared by seventy documents counts for almost nothing while a term appearing in
/// three counts for a great deal.
/// </para>
/// <para>
/// The statistics deliberately live here rather than in <see cref="TextVectorizer"/>.
/// That type is stateless and shared with skill routing and agent drift detection, which
/// compare two texts to each other with no corpus in view; giving it corpus awareness
/// would silently change what those linters report.
/// </para>
/// </remarks>
public sealed class DocumentCorpus
{
    /// <summary>Term-frequency saturation. The standard Okapi value.</summary>
    private const double K1 = 1.2;

    /// <summary>Length-normalisation strength. The standard Okapi value.</summary>
    private const double B = 0.75;

    /// <summary>
    /// BM25 score at which body relevance is judged half-convincing. BM25 is unbounded,
    /// and the other scoring terms are fixed weights, so it is squashed into 0..1 to keep
    /// the total interpretable — and to let a single relevance floor mean something.
    /// </summary>
    private const double Saturation = 6.0;

    private readonly Dictionary<string, double> _documentFrequency;
    private readonly Dictionary<string, Dictionary<string, double>> _bodyVectors;
    private readonly Dictionary<string, double> _bodyLengths;
    private readonly double _averageLength;
    private readonly int _documentCount;

    private DocumentCorpus(
        IReadOnlyList<DocumentModel> documents,
        Dictionary<string, double> documentFrequency,
        Dictionary<string, Dictionary<string, double>> bodyVectors,
        Dictionary<string, double> bodyLengths)
    {
        Documents = documents;
        _documentFrequency = documentFrequency;
        _bodyVectors = bodyVectors;
        _bodyLengths = bodyLengths;
        _documentCount = documents.Count;
        _averageLength = bodyLengths.Count == 0 ? 1 : Math.Max(1, bodyLengths.Values.Average());
    }

    public IReadOnlyList<DocumentModel> Documents { get; }

    public static DocumentCorpus Build(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        Dictionary<string, double> documentFrequency = new Dictionary<string, double>(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, double>> bodyVectors = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        Dictionary<string, double> bodyLengths = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (DocumentModel doc in set.Documents)
        {
            Dictionary<string, double> vector = TextVectorizer.VectorizeFused(doc.Body);
            bodyVectors[doc.RelativePath] = vector;
            bodyLengths[doc.RelativePath] = vector.Values.Sum();

            foreach (string term in vector.Keys)
            {
                documentFrequency[term] = documentFrequency.TryGetValue(term, out double n) ? n + 1 : 1;
            }
        }

        return new DocumentCorpus(set.Documents, documentFrequency, bodyVectors, bodyLengths);
    }

    /// <summary>
    /// Share of the corpus a term may appear in before it is treated as carrying no
    /// information at all.
    /// </summary>
    /// <remarks>
    /// A general-purpose stop-word list cannot know that "architecture", "service" and
    /// "configuration" are noise <em>in this corpus</em>. Down-weighting them by rarity
    /// is not enough on its own: a question made entirely of such words still accumulates
    /// a middling score across many documents, which is how "how do I make a sandwich"
    /// came back with three confident results. Terms this common are dropped outright, so
    /// a question containing nothing else scores zero and is honestly reported as a miss.
    /// </remarks>
    private const double UninformativeDocumentShare = 0.5;

    /// <summary>
    /// The scaffolding of a spoken question, as opposed to its subject.
    /// </summary>
    /// <remarks>
    /// These have to be named explicitly because rarity actively misjudges them. Formal
    /// documentation rarely writes "why" or "getting", so inverse document frequency
    /// scores them as highly discriminating — "getting" appears in one document out of
    /// seventy-seven and is rated more informative than "logged". Asked "why does the user
    /// keep getting logged out", the ranking then demanded a document account for "why",
    /// "keep" and "getting", none of which any document will contain, and the query that
    /// prompted all of this work was rejected as a miss.
    ///
    /// This is a property of questions, not of the corpus, so it is a fixed list rather
    /// than a statistic. It is applied only to retrieval; <see cref="TextVectorizer"/>'s
    /// own stop-words are shared with skill routing and agent drift detection and are
    /// deliberately left alone.
    /// </remarks>
    private static readonly HashSet<string> QuestionWords = new(StringComparer.Ordinal)
    {
        "why", "who", "whom", "whose", "where", "whether", "doe", "does", "did", "done",
        "keep", "kept", "get", "getting", "got", "make", "made", "making", "need", "needed",
        "want", "wanted", "tell", "explain", "describe", "show", "give", "let", "know",
        "happen", "happening", "happened", "please", "would", "could", "may", "might",
        "must", "many", "much", "some", "any", "here", "there", "then", "than", "them",
        "they", "their", "our", "we", "us", "me", "my", "mine", "he", "she", "him", "her"
    };

    /// <summary>
    /// False when a term says nothing about which document is relevant — either because
    /// it is question scaffolding, or because this corpus uses it nearly everywhere.
    /// </summary>
    public bool IsInformative(string term)
    {
        if (QuestionWords.Contains(term)) return false;
        if (_documentCount < 4) return true;

        double n = _documentFrequency.GetValueOrDefault(term);
        return n <= _documentCount * UninformativeDocumentShare;
    }

    /// <summary>
    /// Inverse document frequency, Robertson–Sparck Jones form. A term in every document
    /// lands near zero; a term in one or two dominates.
    /// </summary>
    public double InverseDocumentFrequency(string term)
    {
        double n = _documentFrequency.GetValueOrDefault(term);
        return Math.Log(1 + ((_documentCount - n + 0.5) / (n + 0.5)));
    }

    /// <summary>
    /// A copy of the query vector with each term scaled by its rarity, for callers that
    /// still want cosine similarity — section selection, where documents are not the
    /// unit and BM25's length normalisation does not apply.
    /// </summary>
    public Dictionary<string, double> WeightByRarity(IReadOnlyDictionary<string, double> queryVector)
    {
        ArgumentNullException.ThrowIfNull(queryVector);

        Dictionary<string, double> weighted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string? term, double count) in queryVector)
        {
            if (!IsInformative(term)) continue;
            weighted[term] = count * InverseDocumentFrequency(term);
        }
        return weighted;
    }

    /// <summary>
    /// How well a document's body answers the query, on a 0..1 scale where higher is a
    /// stronger match. Okapi BM25, squashed, scaled by how much of the question is
    /// actually answered.
    /// </summary>
    /// <param name="document">The document to score.</param>
    /// <param name="queryTerms">
    /// The query with adjacent pairs fused. Fused pairs earn credit when a document has
    /// them, giving a weak phrase match.
    /// </param>
    /// <param name="coverageTerms">
    /// The query's plain words, without fusions — the terms a document could reasonably
    /// be expected to contain. Coverage is measured over these alone. Measuring it over
    /// the fused set instead makes every ordinary question unanswerable: "why does the
    /// user keep getting logged out" generates five synthetic pairs like "userkeep" that
    /// appear in no document ever written, and demanding them sank the query the whole
    /// exercise began with.
    /// </param>
    public double ScoreBody(
        DocumentModel document,
        IReadOnlyDictionary<string, double> queryTerms,
        IReadOnlyCollection<string> coverageTerms)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(coverageTerms);

        if (!_bodyVectors.TryGetValue(document.RelativePath, out Dictionary<string, double>? body)) return 0;

        double length = _bodyLengths[document.RelativePath];
        double score = 0;

        foreach (string term in queryTerms.Keys)
        {
            if (!IsInformative(term)) continue;
            if (!body.TryGetValue(term, out double frequency)) continue;

            double denominator = frequency + (K1 * (1 - B + (B * length / _averageLength)));
            score += InverseDocumentFrequency(term) * frequency * (K1 + 1) / denominator;
        }

        double askedFor = 0;
        double answered = 0;

        foreach (string term in coverageTerms)
        {
            if (!IsInformative(term)) continue;

            double idf = InverseDocumentFrequency(term);
            askedFor += idf;
            if (body.ContainsKey(term)) answered += idf;
        }

        if (askedFor <= 0 || answered <= 0) return 0;

        // Scale by how much of the question's *information* the document answers, not how
        // many of its words. Counting words punishes the way people actually ask — "why
        // does the user keep getting logged out" can never match every term — while
        // weighting by rarity punishes the right thing: "best hiking trails in patagonia"
        // matches "best" and misses three terms the corpus has never seen, so almost none
        // of what was asked is answered and the score collapses.
        return answered / askedFor * score / (score + Saturation);
    }
}
