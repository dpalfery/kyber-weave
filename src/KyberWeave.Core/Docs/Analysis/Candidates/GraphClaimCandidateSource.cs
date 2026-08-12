using KyberWeave.Core.Docs.Analysis.Claims;

namespace KyberWeave.Core.Docs.Analysis.Candidates;

/// <summary>Shortlists claims whose documents are neighbors in the shared DocGraph projection.</summary>
public sealed class GraphClaimCandidateSource : IClaimCandidateSource
{
    public CandidateSourceKind Kind => CandidateSourceKind.Graph;

    public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scored = SparseRelatedPairs(request);
        var selected = new List<ClaimPairCandidate>();
        using var enumerator = SelectCapacityBoundedTopK(
                scored,
                request.Search.MaxNeighborsPerClaim)
            .GetEnumerator();
        while (selected.Count < request.Search.MaxCandidates && enumerator.MoveNext())
            selected.Add(enumerator.Current);
        var truncated = enumerator.MoveNext();
        return new ClaimCandidateSourceResult(selected, scored.Count, truncated);
    }

    private static IReadOnlyList<ClaimPairCandidate> SparseRelatedPairs(ClaimCandidateSourceRequest request)
    {
        var tokens = request.Claims
            .Select(claim => LexicalSimilarity.Tokens(claim.ContextualText))
            .ToArray();
        var postings = BuildPostings(tokens);
        var postingLimit = Math.Max(8, request.Search.MaxNeighborsPerClaim * 8);
        var pairs = new Dictionary<ClaimPair, ClaimPairCandidate>();

        for (var left = 0; left < request.Claims.Count; left++)
        {
            var overlap = new Dictionary<int, int>();
            foreach (var token in tokens[left])
            {
                if (!postings.TryGetValue(token, out var indexes) || indexes.Count > postingLimit)
                    continue;
                foreach (var right in indexes)
                {
                    if (right == left || !AreRelated(request, request.Claims[left], request.Claims[right]))
                        continue;
                    overlap.TryGetValue(right, out var count);
                    overlap[right] = count + 1;
                }
            }

            foreach (var right in overlap
                         .OrderByDescending(item => Score(tokens[left], tokens[item.Key], item.Value))
                         .ThenBy(item => item.Key)
                         .Take(request.Search.MaxNeighborsPerClaim)
                         .Select(item => item.Key))
            {
                var identity = ClaimPair.Create(request.Claims[left], request.Claims[right]);
                pairs.TryAdd(identity, new ClaimPairCandidate(
                    identity.Left,
                    identity.Right,
                    CandidateSourceKind.Graph,
                    new CandidateScore(
                        LexicalSimilarity.Score(identity.Left.ContextualText, identity.Right.ContextualText),
                        null,
                        1)));
            }
        }

        return pairs.Values.ToArray();
    }

    private static double Score(
        IReadOnlySet<string> left,
        IReadOnlySet<string> right,
        int overlap) =>
        left.Count == 0 || right.Count == 0 ? 0 : (double)overlap / Math.Min(left.Count, right.Count);

    private static bool AreRelated(ClaimCandidateSourceRequest request, Claim left, Claim right) =>
        request.Graph.AreDocumentsRelated(DocumentNodeId(left), DocumentNodeId(right));

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> BuildPostings(
        IReadOnlyList<IReadOnlySet<string>> tokenSets)
    {
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < tokenSets.Count; index++)
        foreach (var token in tokenSets[index])
        {
            if (!postings.TryGetValue(token, out var indexes))
            {
                indexes = [];
                postings[token] = indexes;
            }

            indexes.Add(index);
        }

        return postings.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<int>)item.Value,
            StringComparer.Ordinal);
    }

    private static IEnumerable<ClaimPairCandidate> SelectCapacityBoundedTopK(
        IEnumerable<ClaimPairCandidate> pairs,
        int maximumNeighbors)
    {
        var counts = new Dictionary<Claim, int>();
        foreach (var pair in pairs
                     .OrderByDescending(pair => pair.Score.Lexical)
                     .ThenBy(pair => pair.Left.FilePath, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Right.FilePath, StringComparer.Ordinal))
        {
            counts.TryGetValue(pair.Left, out var leftCount);
            counts.TryGetValue(pair.Right, out var rightCount);
            if (leftCount >= maximumNeighbors || rightCount >= maximumNeighbors) continue;
            counts[pair.Left] = leftCount + 1;
            counts[pair.Right] = rightCount + 1;
            yield return pair;
        }
    }

    private static string DocumentNodeId(Claim claim) => $"doc:{claim.DocumentIdentity}";

    private readonly record struct ClaimPair(Claim Left, Claim Right)
    {
        internal static ClaimPair Create(Claim left, Claim right) =>
            Compare(left, right) <= 0 ? new ClaimPair(left, right) : new ClaimPair(right, left);

        private static int Compare(Claim left, Claim right)
        {
            var path = StringComparer.Ordinal.Compare(left.FilePath, right.FilePath);
            if (path != 0) return path;
            var line = left.StartLine.CompareTo(right.StartLine);
            return line != 0 ? line : StringComparer.Ordinal.Compare(left.ContentHash, right.ContentHash);
        }
    }
}
