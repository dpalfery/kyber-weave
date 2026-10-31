using System.Globalization;
using System.Text;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Text;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KyberWeave.Core.Docs.Search;

/// <summary>One <c>docs_explore</c> answer: a whole leaf, confidence, and optional continuation.</summary>
public sealed record ExploreAnswer(
    bool IsMiss,
    bool IsInvalidCursor,
    string? InvalidCursorMessage,
    DocumentModel? SourceDocument,
    string? SourceHeading,
    string Prose,
    double Confidence,
    bool Incomplete,
    string? CursorToken,
    int DocumentsConsidered)
{
    /// <summary>Characters of leaf prose returned in one call.</summary>
    public const int AnswerCharCap = 24000;

    public static ExploreAnswer Miss(int documentsConsidered)
    {
        return new ExploreAnswer(
            true,
            false,
            null,
            null,
            null,
            string.Empty,
            0,
            false,
            null,
            documentsConsidered);
    }

    public static ExploreAnswer InvalidCursor(string message)
    {
        return new ExploreAnswer(
            false,
            true,
            message,
            null,
            null,
            string.Empty,
            0,
            false,
            null,
            0);
    }
}

/// <summary>Leaf-level retrieval for <see cref="DocumentIndex.Explore"/>.</summary>
/// <remarks>
/// Documents clear a relevance floor; leaves inside them score with claim, BM25 body, and
/// optional lead bonus. Topics are <c>##</c> sections (or the preamble); confidence compares
/// the winning topic to the best other topic via <c>tanh</c> of their score difference.
/// </remarks>
public sealed partial class DocumentIndex
{
    private const double Bm25K1 = 1.2;
    private const double Bm25B = 0.75;

    private static readonly MarkdownPipeline ExploreHeadingPipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    /// <summary>
    /// Free-text retrieval returning one answer blob: the whole winning leaf up to
    /// <see cref="ExploreAnswer.AnswerCharCap"/>, with optional continuation via
    /// <paramref name="cursor"/>.
    /// </summary>
    public ExploreAnswer Explore(string query, string? cursor = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ExploreAnswer.Miss(DocumentCount);
        }

        Dictionary<string, double> queryVector = TextVectorizer.VectorizeFused(query);
        string trimmed = query.Trim();
        List<string> coverageTerms = TextVectorizer.Vectorize(query).Keys.ToList();
        Dictionary<string, double> titleQuery = TitleQuery(coverageTerms);

        RankExploreResult rank = RankLeaves(trimmed, queryVector, coverageTerms, titleQuery);
        if (rank.Winner is null)
        {
            return ExploreAnswer.Miss(DocumentCount);
        }

        ScoredLeaf winner = rank.Winner;
        ScoredLeaf? redirected = TryAnswerFromIndexRow(trimmed, queryVector, coverageTerms, titleQuery, winner);
        if (redirected is not null)
        {
            winner = redirected;
        }

        if (cursor is not null)
        {
            if (!ExploreCursor.TryParse(cursor, out ExploreCursor parsed))
            {
                return ExploreAnswer.InvalidCursor(
                    "The cursor does not continue this question. Ask again without a cursor.");
            }

            return ContinueFromCursor(trimmed, rank.RunnerTopicScore, winner, parsed);
        }

        return BuildFirstAnswer(rank.RunnerTopicScore, winner);
    }

    private ExploreAnswer BuildFirstAnswer(double runnerTopicScore, ScoredLeaf winner)
    {
        (string prose, bool incomplete, string? cursorToken) = SliceLeaf(winner, 0, ExploreAnswer.AnswerCharCap);

        double confidence = ComputeTopicConfidence(winner.TopicScore, runnerTopicScore, incomplete);

        return new ExploreAnswer(
            false,
            false,
            null,
            winner.Document,
            FormatSourceHeading(winner),
            prose,
            confidence,
            incomplete,
            cursorToken,
            DocumentCount);
    }

    private ExploreAnswer ContinueFromCursor(
        string trimmed,
        double runnerTopicScore,
        ScoredLeaf winner,
        ExploreCursor parsed)
    {
        if (!string.Equals(parsed.RelativePath, winner.Document.RelativePath, StringComparison.Ordinal)
            || !string.Equals(parsed.LeafId, winner.Leaf.Id, StringComparison.Ordinal))
        {
            return ExploreAnswer.InvalidCursor(
                "The cursor does not continue this question. Ask again without a cursor.");
        }

        (string prose, bool incomplete, string? cursorToken) = SliceLeaf(
            winner,
            parsed.CharacterOffset,
            ExploreAnswer.AnswerCharCap);

        if (prose.Length == 0)
        {
            return ExploreAnswer.InvalidCursor(
                "The cursor does not continue this question. Ask again without a cursor.");
        }

        double confidence = ComputeTopicConfidence(winner.TopicScore, runnerTopicScore, incomplete);

        return new ExploreAnswer(
            false,
            false,
            null,
            winner.Document,
            FormatSourceHeading(winner),
            prose,
            confidence,
            incomplete,
            cursorToken,
            DocumentCount);
    }

    private static (string Prose, bool Incomplete, string? CursorToken) SliceLeaf(
        ScoredLeaf winner,
        int characterOffset,
        int cap)
    {
        string body = winner.Leaf.Body;
        if (characterOffset >= body.Length)
        {
            return (string.Empty, true, null);
        }

        string remainder = body[characterOffset..];
        if (remainder.Length <= cap)
        {
            return (remainder, false, null);
        }

        int cursorEnd = Math.Min(cap, remainder.Length);
        if (remainder.Length > cap)
        {
            int slackEnd = Math.Min(remainder.Length, cap + IncompleteContinuationSlack);
            int paragraphBreak = remainder.IndexOf("\n\n", cap, StringComparison.Ordinal);
            if (paragraphBreak >= 0 && paragraphBreak <= slackEnd)
            {
                int nextBreak = remainder.IndexOf("\n\n", paragraphBreak + 2, StringComparison.Ordinal);
                cursorEnd = nextBreak >= 0 ? nextBreak : Math.Min(remainder.Length, slackEnd);
            }
        }

        string slice = remainder[..cursorEnd];
        string? cursor = cursorEnd < remainder.Length
            ? ExploreCursor.Format(
                winner.Document.RelativePath,
                winner.Leaf.Id,
                characterOffset + cursorEnd)
            : null;
        bool incomplete = cursor is not null;
        return (slice, incomplete, cursor);
    }

    private static string? FormatSourceHeading(ScoredLeaf winner)
    {
        if (winner.IsPreamble)
        {
            return null;
        }

        if (string.IsNullOrEmpty(winner.SubHeading))
        {
            return winner.TopicHeading;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{winner.TopicHeading} › {winner.SubHeading}");
    }

    private RankExploreResult RankLeaves(
        string trimmed,
        IReadOnlyDictionary<string, double> queryVector,
        IReadOnlyList<string> coverageTerms,
        IReadOnlyDictionary<string, double> titleQuery)
    {
        List<(DocumentModel Document, double Identity, double DocumentScore)> qualifying = new();
        foreach (DocumentModel doc in _corpus.Documents)
        {
            double identity = ScoreExact(doc, trimmed)
                              + ScorePartialIdentity(doc, queryVector)
                              + TitleWeight * ScoreTitle(doc, titleQuery)
                              + BodyWeight * _corpus.ScoreBody(doc, queryVector, coverageTerms);
            double documentScore = Authority(doc) * identity;
            if (documentScore >= MinRelevanceScore)
            {
                qualifying.Add((doc, identity, documentScore));
            }
        }

        if (qualifying.Count == 0)
        {
            return new RankExploreResult(null, 0);
        }

        double corpusAverageLength = CorpusAverageBodyLength();
        Dictionary<string, double> topicScores = new(StringComparer.Ordinal);

        foreach ((DocumentModel doc, double identity, double _) in qualifying)
        {
            double authority = Authority(doc);
            bool exactId = IsExactDocumentIdQuery(doc, trimmed);
            double documentIdentity = ScoreLeafDocumentIdentity(doc, queryVector, titleQuery, _corpus, coverageTerms);
            LeafTopicContext context = new(doc);

            foreach (DocumentLeaf leaf in ExpandScoringLeaves(doc))
            {
                LeafTopicAssignment assignment = context.Assign(leaf);
                double claimForTopic = ScoreLeafClaim(assignment, doc, leaf, trimmed, queryVector, false);
                double claim = ScoreLeafClaim(assignment, doc, leaf, trimmed, queryVector, true);
                double body = ScoreLeafBody(doc, leaf, queryVector, coverageTerms, corpusAverageLength);
                string leadLeafId = doc.LeadLeafId;
                double lead = exactId && string.Equals(leaf.Id, leadLeafId, StringComparison.Ordinal) ? IdWeight : 0;
                double topicLeafScore = authority * (documentIdentity + claimForTopic + body + lead);
                double leafScore = authority * (documentIdentity + claim + body + lead);

                string topicKey = assignment.TopicKey;
                if (!topicScores.TryGetValue(topicKey, out double existingTopic) || topicLeafScore > existingTopic)
                {
                    topicScores[topicKey] = topicLeafScore;
                }
            }
        }

        (DocumentModel winningDocument, double _, double _) = qualifying
            .OrderByDescending(entry => entry.DocumentScore)
            .ThenBy(entry => entry.Document.RelativePath, StringComparer.Ordinal)
            .First();

        ScoredLeaf? globalWinner = null;
        (DocumentModel docWinner, double identityWinner, double _) =
            qualifying.First(entry => entry.Document == winningDocument);

        {
            double authority = Authority(docWinner);
            bool exactId = IsExactDocumentIdQuery(docWinner, trimmed);
            double documentIdentity =
                ScoreLeafDocumentIdentity(docWinner, queryVector, titleQuery, _corpus, coverageTerms);
            LeafTopicContext context = new(docWinner);

            foreach (DocumentLeaf leaf in ExpandScoringLeaves(docWinner))
            {
                LeafTopicAssignment assignment = context.Assign(leaf);
                double claimForTopic = ScoreLeafClaim(assignment, docWinner, leaf, trimmed, queryVector, false);
                double claim = ScoreLeafClaim(assignment, docWinner, leaf, trimmed, queryVector, true);
                double body = ScoreLeafBody(docWinner, leaf, queryVector, coverageTerms, corpusAverageLength);
                string leadLeafId = docWinner.LeadLeafId;
                double lead = exactId && string.Equals(leaf.Id, leadLeafId, StringComparison.Ordinal) ? IdWeight : 0;
                double leafScore = authority * (documentIdentity + claim + body + lead);

                string topicKey = assignment.TopicKey;
                if (globalWinner is null
                    || leafScore > globalWinner.LeafScore
                    || (leafScore == globalWinner.LeafScore
                        && assignment.SectionLineNumber < globalWinner.SectionLineNumber))
                {
                    globalWinner = new ScoredLeaf(
                        docWinner,
                        leaf,
                        leafScore,
                        topicScores[topicKey],
                        assignment.IsPreamble,
                        assignment.TopicHeading,
                        assignment.SubHeading,
                        topicKey,
                        assignment.SectionLineNumber);
                }
            }
        }

        if (globalWinner is null)
        {
            return new RankExploreResult(null, 0);
        }

        globalWinner = globalWinner with { TopicScore = topicScores[globalWinner.TopicKey] };

        double runnerTopic = 0;
        foreach ((string key, double score) in topicScores)
        {
            if (string.Equals(key, globalWinner.TopicKey, StringComparison.Ordinal))
            {
                continue;
            }

            runnerTopic = Math.Max(runnerTopic, score);
        }

        return new RankExploreResult(globalWinner, runnerTopic);
    }

    private ScoredLeaf? TryAnswerFromIndexRow(
        string trimmed,
        IReadOnlyDictionary<string, double> queryVector,
        IReadOnlyList<string> coverageTerms,
        IReadOnlyDictionary<string, double> titleQuery,
        ScoredLeaf indexWinner)
    {
        IndexRowEdge? row = _sectionGraph.SelectBestIndexRow(
            indexWinner.Document,
            indexWinner.TopicHeading,
            trimmed,
            queryVector);
        if (row is null)
        {
            return null;
        }

        if (!_sectionGraph.DocumentsByPath.TryGetValue(row.TargetRelativePath, out DocumentModel? target))
        {
            return null;
        }

        return RankBestLeafInDocument(target, trimmed, queryVector, coverageTerms, titleQuery, row.TargetFragment);
    }

    private ScoredLeaf? RankBestLeafInDocument(
        DocumentModel document,
        string trimmed,
        IReadOnlyDictionary<string, double> queryVector,
        IReadOnlyList<string> coverageTerms,
        IReadOnlyDictionary<string, double> titleQuery,
        string? preferredFragment)
    {
        double corpusAverageLength = CorpusAverageBodyLength();
        double authority = Authority(document);
        bool exactId = IsExactDocumentIdQuery(document, trimmed);
        double documentIdentity = ScoreLeafDocumentIdentity(document, queryVector, titleQuery, _corpus, coverageTerms);
        LeafTopicContext context = new(document);
        ScoredLeaf? best = null;

        foreach (DocumentLeaf leaf in ExpandScoringLeaves(document))
        {
            if (!string.IsNullOrEmpty(preferredFragment))
            {
                string docId = document.Frontmatter.Id ?? document.RelativePath;
                string preferredId = string.Create(CultureInfo.InvariantCulture, $"{docId}#{preferredFragment}");
                if (!string.Equals(leaf.Id, preferredId, StringComparison.Ordinal)
                    && !leaf.Id.EndsWith(
                        string.Create(CultureInfo.InvariantCulture, $"#{preferredFragment}"),
                        StringComparison.Ordinal))
                {
                    continue;
                }
            }

            LeafTopicAssignment assignment = context.Assign(leaf);
            double claim = ScoreLeafClaim(assignment, document, leaf, trimmed, queryVector, true);
            double body = ScoreLeafBody(document, leaf, queryVector, coverageTerms, corpusAverageLength);
            string leadLeafId = document.LeadLeafId;
            double lead = exactId && string.Equals(leaf.Id, leadLeafId, StringComparison.Ordinal) ? IdWeight : 0;
            double leafScore = authority * (documentIdentity + claim + body + lead);

            if (best is null
                || leafScore > best.LeafScore
                || (leafScore == best.LeafScore && assignment.SectionLineNumber < best.SectionLineNumber))
            {
                best = new ScoredLeaf(
                    document,
                    leaf,
                    leafScore,
                    leafScore,
                    assignment.IsPreamble,
                    assignment.TopicHeading,
                    assignment.SubHeading,
                    assignment.TopicKey,
                    assignment.SectionLineNumber);
            }
        }

        return best;
    }

    private const int IncompleteContinuationSlack = 500;

    private static IEnumerable<DocumentLeaf> ExpandScoringLeaves(DocumentModel document)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (DocumentLeaf leaf in document.Leaves)
        {
            seen.Add(leaf.Id);
            yield return leaf;
        }

        string docId = document.Frontmatter.Id ?? document.RelativePath;
        MarkdownDocument markdown = Markdown.Parse(document.Body, ExploreHeadingPipeline);
        Dictionary<string, DocumentSection> sectionByHeading = SectionByHeading(document.Sections);

        foreach (HeadingBlock heading in markdown.Descendants<HeadingBlock>())
        {
            if (heading.Level != 2)
            {
                continue;
            }

            string headingText = PlainHeadingText(heading);
            if (!sectionByHeading.TryGetValue(headingText, out DocumentSection? section))
            {
                continue;
            }

            string? attributeId = heading.GetAttributes().Id;
            string slug = attributeId ?? SlugifySectionHeading(headingText);
            string leafId = string.Create(CultureInfo.InvariantCulture, $"{docId}#{slug}");
            if (seen.Contains(leafId))
            {
                continue;
            }

            seen.Add(leafId);
            yield return new DocumentLeaf(leafId, section.Body);
        }
    }

    private static string PlainHeadingText(HeadingBlock heading)
    {
        if (heading.Inline is null)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (Inline inline in heading.Inline)
        {
            if (inline is LiteralInline literal)
            {
                builder.Append(literal.Content.ToString());
            }
        }

        return builder.ToString();
    }

    private static Dictionary<string, DocumentSection> SectionByHeading(
        IReadOnlyList<DocumentSection> sections)
    {
        Dictionary<string, DocumentSection> map = new(StringComparer.Ordinal);
        foreach (DocumentSection section in sections)
        {
            map.TryAdd(section.Heading, section);
        }

        return map;
    }

    private static string SlugifySectionHeading(string heading)
    {
        StringBuilder builder = new();
        bool lastWasDash = false;
        foreach (char ch in heading.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private double CorpusAverageBodyLength()
    {
        if (_corpus.Documents.Count == 0)
        {
            return 1;
        }

        double sum = 0;
        foreach (DocumentModel doc in _corpus.Documents)
        {
            sum += TextVectorizer.VectorizeFused(doc.Body).Values.Sum();
        }

        return Math.Max(1, sum / _corpus.Documents.Count);
    }

    private static bool IsExactDocumentIdQuery(DocumentModel doc, string trimmed)
    {
        return string.Equals(trimmed, doc.Frontmatter.Id, StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, doc.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Document identity inside a leaf score: partial, title, and document body. Exact
    /// identity matches are document-stage only and are not folded into topic confidence.
    /// </summary>
    private static double ScoreLeafDocumentIdentity(
        DocumentModel doc,
        IReadOnlyDictionary<string, double> queryVector,
        IReadOnlyDictionary<string, double> titleQuery,
        DocumentCorpus corpus,
        IReadOnlyList<string> coverageTerms)
    {
        return ScorePartialIdentity(doc, queryVector)
               + TitleWeight * ScoreTitle(doc, titleQuery)
               + BodyWeight * corpus.ScoreBody(doc, queryVector, coverageTerms);
    }

    private double ScoreLeafClaim(
        LeafTopicAssignment assignment,
        DocumentModel document,
        DocumentLeaf leaf,
        string trimmed,
        IReadOnlyDictionary<string, double> queryVector,
        bool includeInboundLinkLabels)
    {
        List<string> labels = new();
        AppendSectionVocabularyLabels(document, leaf, labels, out List<string> sectionCodeRefs);
        if (!assignment.IsPreamble)
        {
            if (!string.IsNullOrEmpty(assignment.TopicHeading))
            {
                labels.Insert(0, assignment.TopicHeading);
            }

            if (!string.IsNullOrEmpty(assignment.SubHeading))
            {
                int insertAt = string.IsNullOrEmpty(assignment.TopicHeading) ? 0 : 1;
                labels.Insert(insertAt, assignment.SubHeading);
            }
        }

        if (includeInboundLinkLabels
            && _sectionGraph.InboundLabelsByLeafId.TryGetValue(leaf.Id, out IReadOnlyList<string>? inbound))
        {
            foreach (string inboundLabel in inbound)
            {
                if (!string.IsNullOrWhiteSpace(inboundLabel))
                {
                    labels.Add(inboundLabel);
                }
            }
        }

        if (labels.Count == 0)
        {
            return 0;
        }

        double coverageSum = 0;
        foreach (string label in labels)
        {
            coverageSum += 3.0 * Coverage(label, queryVector);
        }

        coverageSum = Math.Min(coverageSum, 4.5);

        bool exactSectionCodeRef = false;
        foreach (string reference in sectionCodeRefs)
        {
            if (SymbolKeys(reference).Contains(trimmed, StringComparer.Ordinal))
            {
                coverageSum += CodeRefWeight;
                exactSectionCodeRef = true;
                break;
            }
        }

        if (!exactSectionCodeRef)
        {
            foreach (string label in labels)
            {
                if (string.Equals(trimmed, label, StringComparison.OrdinalIgnoreCase))
                {
                    coverageSum += KeywordWeight;
                    break;
                }
            }
        }

        return coverageSum;
    }

    private static void AppendSectionVocabularyLabels(
        DocumentModel document,
        DocumentLeaf leaf,
        List<string> labels,
        out List<string> sectionCodeRefs)
    {
        sectionCodeRefs = [];
        if (document.Frontmatter.Sections is not { Count: > 0 } sections)
        {
            return;
        }

        string docId = document.Frontmatter.Id ?? document.RelativePath;
        string preambleId = string.Create(CultureInfo.InvariantCulture, $"{docId}#");
        string sectionSlug;
        if (string.Equals(leaf.Id, preambleId, StringComparison.Ordinal))
        {
            sectionSlug = "#";
        }
        else if (leaf.Id.StartsWith(preambleId, StringComparison.Ordinal))
        {
            sectionSlug = leaf.Id[(docId.Length + 1)..];
        }
        else
        {
            return;
        }

        if (!sections.TryGetValue(sectionSlug, out DocumentSectionVocabulary? vocabulary))
        {
            return;
        }

        if (vocabulary.Keywords is not null)
        {
            foreach (string keyword in vocabulary.Keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    labels.Add(keyword);
                }
            }
        }

        if (vocabulary.CodeRefs is not null)
        {
            foreach (string reference in vocabulary.CodeRefs)
            {
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                labels.Add(reference);
                sectionCodeRefs.Add(reference);
            }
        }
    }

    private double ScoreLeafBody(
        DocumentModel document,
        DocumentLeaf leaf,
        IReadOnlyDictionary<string, double> queryTerms,
        IReadOnlyList<string> coverageTerms,
        double corpusAverageLength)
    {
        Dictionary<string, double> vector = TextVectorizer.VectorizeFused(leaf.Body);
        double length = Math.Max(1, vector.Values.Sum());
        double score = 0;

        foreach (string term in queryTerms.Keys)
        {
            if (!_corpus.IsInformative(term))
            {
                continue;
            }

            if (!vector.TryGetValue(term, out double frequency))
            {
                continue;
            }

            double denominator = frequency + Bm25K1 * (1 - Bm25B + Bm25B * length / corpusAverageLength);
            score += _corpus.InverseDocumentFrequency(term) * frequency * (Bm25K1 + 1) / denominator;
        }

        string? docId = document.Frontmatter.Id;
        if (!string.IsNullOrEmpty(docId))
        {
            int slash = docId.LastIndexOf('/');
            string slug = slash >= 0 ? docId[(slash + 1)..] : docId;
            if (_corpus.IsInformative(slug)
                && vector.TryGetValue(slug, out double slugFrequency)
                && slugFrequency > 0)
            {
                double denominator = slugFrequency
                                     + Bm25K1 * (1 - Bm25B + Bm25B * length / corpusAverageLength);
                score += _corpus.InverseDocumentFrequency(slug)
                         * slugFrequency
                         * (Bm25K1 + 1)
                         / denominator;
            }
        }

        if (score <= 0)
        {
            return 0;
        }

        double askedFor = 0;
        double answered = 0;
        foreach (string term in coverageTerms)
        {
            if (!_corpus.IsInformative(term))
            {
                continue;
            }

            double idf = _corpus.InverseDocumentFrequency(term);
            askedFor += idf;
            if (vector.ContainsKey(term))
            {
                answered += idf;
            }
        }

        if (askedFor <= 0 || answered <= 0)
        {
            return score / (score + SectionSaturation);
        }

        return answered / askedFor * score / (score + SectionSaturation);
    }

    private static double ComputeTopicConfidence(double winningTopic, double runnerTopic, bool incomplete)
    {
        double value = Math.Tanh((winningTopic - runnerTopic) / 2.0);
        if (incomplete)
        {
            value *= 0.5;
        }

        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private sealed record RankExploreResult(ScoredLeaf? Winner, double RunnerTopicScore);

    private sealed record LeafTopicAssignment(
        string TopicKey,
        bool IsPreamble,
        string? TopicHeading,
        string? SubHeading,
        int SectionLineNumber);

    private sealed record ScoredLeaf(
        DocumentModel Document,
        DocumentLeaf Leaf,
        double LeafScore,
        double TopicScore,
        bool IsPreamble,
        string? TopicHeading,
        string? SubHeading,
        string TopicKey,
        int SectionLineNumber);

    private sealed class LeafTopicContext
    {
        private readonly DocumentModel _document;
        private readonly Dictionary<string, DocumentSection> _sectionByLeafId;
        private string? _currentTopicHeading;
        private string? _currentTopicKey;

        internal LeafTopicContext(DocumentModel document)
        {
            _document = document;
            string docId = document.Frontmatter.Id ?? document.RelativePath;
            _sectionByLeafId = new Dictionary<string, DocumentSection>(StringComparer.Ordinal);

            MarkdownDocument markdown = Markdown.Parse(document.Body, ExploreHeadingPipeline);
            Dictionary<string, DocumentSection> sectionByHeading = SectionByHeading(document.Sections);

            foreach (HeadingBlock heading in markdown.Descendants<HeadingBlock>())
            {
                if (heading.Level != 2)
                {
                    continue;
                }

                string headingText = PlainHeadingText(heading);
                if (!sectionByHeading.TryGetValue(headingText, out DocumentSection? section))
                {
                    continue;
                }

                string? attributeId = heading.GetAttributes().Id;
                string slug = attributeId ?? SlugifySectionHeading(headingText);
                string leafId = string.Create(CultureInfo.InvariantCulture, $"{docId}#{slug}");
                _sectionByLeafId[leafId] = section;
            }
        }

        internal LeafTopicAssignment Assign(DocumentLeaf leaf)
        {
            string docId = _document.Frontmatter.Id ?? _document.RelativePath;
            string preambleId = string.Create(CultureInfo.InvariantCulture, $"{docId}#");
            if (string.Equals(leaf.Id, preambleId, StringComparison.Ordinal))
            {
                _currentTopicHeading = null;
                _currentTopicKey = string.Create(CultureInfo.InvariantCulture, $"{_document.RelativePath}\0preamble");
                return new LeafTopicAssignment(_currentTopicKey, true, null, null, 0);
            }

            if (_sectionByLeafId.TryGetValue(leaf.Id, out DocumentSection? section))
            {
                _currentTopicHeading = section.Heading;
                _currentTopicKey = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_document.RelativePath}\0{section.LineNumber}");
                return new LeafTopicAssignment(
                    _currentTopicKey,
                    false,
                    section.Heading,
                    null,
                    section.LineNumber);
            }

            string topicKey = _currentTopicKey
                              ?? string.Create(CultureInfo.InvariantCulture, $"{_document.RelativePath}\0unknown");
            string slug = leaf.Id[(docId.Length + 1)..];
            return new LeafTopicAssignment(
                topicKey,
                false,
                _currentTopicHeading,
                TitleFromSlug(slug),
                int.MaxValue);
        }

        private static string TitleFromSlug(string slug)
        {
            if (slug.Length == 0)
            {
                return slug;
            }

            string[] parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
            StringBuilder builder = new();
            foreach (string part in parts)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    builder.Append(part.AsSpan(1));
                }
            }

            return builder.ToString();
        }
    }
}

internal sealed record ExploreCursor(string RelativePath, string LeafId, int CharacterOffset)
{
    public static bool TryParse(string token, out ExploreCursor cursor)
    {
        cursor = null!;
        int lastPipe = token.LastIndexOf('|');
        if (lastPipe <= 0 || lastPipe >= token.Length - 1)
        {
            return false;
        }

        int middlePipe = token.LastIndexOf('|', lastPipe - 1);
        if (middlePipe <= 0)
        {
            return false;
        }

        string relativePath = token[..middlePipe];
        string leafId = token[(middlePipe + 1)..lastPipe];
        if (relativePath.Length == 0 || leafId.Length == 0)
        {
            return false;
        }

        if (!int.TryParse(token[(lastPipe + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset)
            || offset < 0)
        {
            return false;
        }

        cursor = new ExploreCursor(relativePath, leafId, offset);
        return true;
    }

    public static string Format(string relativePath, string leafId, int characterOffset)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{relativePath}|{leafId}|{characterOffset}");
    }
}
