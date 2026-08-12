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
        var tokenSets = request.Claims.Select(claim => LexicalSimilarity.Tokens(claim.ContextualText)).ToArray();
        var postings = BuildPostings(tokenSets);
        var postingLimit = Math.Max(8, request.Search.MaxNeighborsPerClaim * 8);
        var compared = new HashSet<IndexPair>();
        var scored = new Dictionary<IndexPair, double>();

        for (var leftIndex = 0; leftIndex < request.Claims.Count; leftIndex++)
        {
            var possible = request.Search.Mode == DocsAnalysisSearchMode.HighRecall
                ? Enumerable.Range(0, request.Claims.Count)
                    .Where(index => index != leftIndex)
                    .ToHashSet()
                : FindSparseNeighbors(leftIndex, tokenSets[leftIndex], postings, postingLimit);

            foreach (var rightIndex in possible)
            {
                var identity = IndexPair.Create(leftIndex, rightIndex);
                if (!compared.Add(identity)) continue;
                var score = LexicalSimilarity.Score(
                    request.Claims[leftIndex].ContextualText,
                    request.Claims[rightIndex].ContextualText);
                scored[identity] = score;
            }
        }

        var selected = new HashSet<IndexPair>();
        for (var claimIndex = 0; claimIndex < request.Claims.Count; claimIndex++)
        {
            foreach (var pair in scored
                         .Where(item => item.Key.Left == claimIndex || item.Key.Right == claimIndex)
                         .OrderByDescending(item => item.Value)
                         .ThenBy(item => item.Key.Left)
                         .ThenBy(item => item.Key.Right)
                         .Take(request.Search.MaxNeighborsPerClaim))
            {
                selected.Add(pair.Key);
            }
        }

        var truncated = selected.Count > request.Search.MaxCandidates;
        var pairs = selected
            .OrderBy(pair => pair.Left)
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
        var possible = new HashSet<int>();
        foreach (var token in tokens)
        {
            if (!postings.TryGetValue(token, out var indexes) || indexes.Count > postingLimit) continue;
            foreach (var index in indexes)
            {
                if (index != leftIndex) possible.Add(index);
            }
        }

        return possible;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> BuildPostings(
        IReadOnlyList<IReadOnlySet<string>> tokenSets)
    {
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < tokenSets.Count; index++)
        {
            foreach (var token in tokenSets[index])
            {
                if (!postings.TryGetValue(token, out var indexes))
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
