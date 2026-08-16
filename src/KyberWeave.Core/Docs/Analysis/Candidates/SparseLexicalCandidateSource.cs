using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Analysis.Candidates;

/// <summary>
/// Uses a sparse inverted index to find a bounded top-k neighborhood without evaluating
/// every corpus pair.
/// </summary>
public sealed class SparseLexicalCandidateSource : IClaimCandidateSource
{
    public CandidateSourceKind Kind => CandidateSourceKind.Lexical;

    public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlySet<string>[] tokenSets = request.Claims.Select(claim => LexicalSimilarity.Tokens(claim.ContextualText)).ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<int>> postings = BuildPostings(tokenSets);
        int postingLimit = Math.Max(8, request.Search.MaxNeighborsPerClaim * 8);
        HashSet<IndexPair> compared = new HashSet<IndexPair>();
        Dictionary<IndexPair, double> scored = new Dictionary<IndexPair, double>();
        List<(IndexPair Pair, double Score)>[] neighbors = Enumerable.Range(0, request.Claims.Count)
            .Select(_ => new List<(IndexPair Pair, double Score)>())
            .ToArray();

        for (int leftIndex = 0; leftIndex < request.Claims.Count; leftIndex++)
        {
            HashSet<int> possible = request.Search.Mode == DocsAnalysisSearchMode.HighRecall
                ? Enumerable.Range(0, request.Claims.Count)
                    .Where(index => index != leftIndex)
                    .ToHashSet()
                : FindSparseNeighbors(leftIndex, tokenSets[leftIndex], postings, postingLimit);

            foreach (int rightIndex in possible)
            {
                IndexPair identity = IndexPair.Create(leftIndex, rightIndex);
                if (!compared.Add(identity)) continue;
                double score = LexicalSimilarity.Score(tokenSets[leftIndex], tokenSets[rightIndex]);
                scored[identity] = score;
                neighbors[identity.Left].Add((identity, score));
                neighbors[identity.Right].Add((identity, score));
            }
        }

        HashSet<IndexPair> selected = new HashSet<IndexPair>();
        for (int claimIndex = 0; claimIndex < request.Claims.Count; claimIndex++)
        {
            foreach ((IndexPair Pair, double Score) item in neighbors[claimIndex]
                         .OrderByDescending(item => item.Score)
                         .ThenBy(item => item.Pair.Left)
                         .ThenBy(item => item.Pair.Right)
                         .Take(request.Search.MaxNeighborsPerClaim))
            {
                selected.Add(item.Pair);
            }
        }

        bool truncated = selected.Count > request.Search.MaxCandidates;
        ClaimPairCandidate[] pairs = selected
            .OrderByDescending(pair => scored[pair])
            .ThenBy(pair => pair.Left)
            .ThenBy(pair => pair.Right)
            .Take(request.Search.MaxCandidates)
            .Select(pair => new ClaimPairCandidate(
                request.Claims[pair.Left],
                request.Claims[pair.Right],
                Kind,
                new CandidateScore(scored[pair], null, 0)))
            .ToArray();
        return new ClaimCandidateSourceResult(pairs, compared.Count, truncated);
    }

    private static HashSet<int> FindSparseNeighbors(
        int leftIndex,
        IReadOnlySet<string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<int>> postings,
        int postingLimit)
    {
        HashSet<int> possible = new HashSet<int>();
        foreach (string token in tokens)
        {
            if (!postings.TryGetValue(token, out IReadOnlyList<int>? indexes) || indexes.Count > postingLimit) continue;
            foreach (int index in indexes)
            {
                if (index != leftIndex) possible.Add(index);
            }
        }

        return possible;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> BuildPostings(
        IReadOnlyList<IReadOnlySet<string>> tokenSets)
    {
        Dictionary<string, List<int>> postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < tokenSets.Count; index++)
        {
            foreach (string token in tokenSets[index])
            {
                if (!postings.TryGetValue(token, out List<int>? indexes))
                {
                    indexes = [];
                    postings[token] = indexes;
                }
                indexes.Add(index);
            }
        }

        return postings.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value,
            StringComparer.Ordinal);
    }

    private readonly record struct IndexPair(int Left, int Right)
    {
        internal static IndexPair Create(int left, int right) =>
            left <= right ? new IndexPair(left, right) : new IndexPair(right, left);
    }
}
