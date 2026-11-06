using KyberWeave.Core.Docs.Analysis.Claims;

namespace KyberWeave.Core.Docs.Analysis.Candidates;

/// <summary>Shortlists claims whose documents are neighbors in the shared DocGraph projection.</summary>
public sealed class GraphClaimCandidateSource : IClaimCandidateSource
{
    public CandidateSourceKind Kind => CandidateSourceKind.Graph;

    public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<ClaimPairCandidate> scored = SparseRelatedPairs(request);
        List<ClaimPairCandidate> selected = new List<ClaimPairCandidate>();
        using IEnumerator<ClaimPairCandidate> enumerator = SelectCapacityBoundedTopK(
                scored,
                request.Search.MaxNeighborsPerClaim)
            .GetEnumerator();
        while (selected.Count < request.Search.MaxCandidates && enumerator.MoveNext())
            selected.Add(enumerator.Current);
        bool truncated = enumerator.MoveNext();
        return new ClaimCandidateSourceResult(selected, scored.Count, truncated);
    }

    private static IReadOnlyList<ClaimPairCandidate> SparseRelatedPairs(ClaimCandidateSourceRequest request)
    {
        IReadOnlySet<string>[] tokens = request.Claims
            .Select(claim => LexicalSimilarity.Tokens(claim.ContextualText))
            .ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<int>> postings = BuildPostings(tokens);
        int postingLimit = Math.Max(8, request.Search.MaxNeighborsPerClaim * 8);
        Dictionary<ClaimPair, ClaimPairCandidate> pairs = new Dictionary<ClaimPair, ClaimPairCandidate>();

        for (int left = 0; left < request.Claims.Count; left++)
        {
            Dictionary<int, int> overlap = new Dictionary<int, int>();
            foreach (string token in tokens[left])
            {
                if (!postings.TryGetValue(token, out IReadOnlyList<int>? indexes) || indexes.Count > postingLimit)
                    continue;
                foreach (int right in indexes)
                {
                    if (right == left || !AreRelated(request, request.Claims[left], request.Claims[right]))
                        continue;
                    overlap.TryGetValue(right, out int count);
                    overlap[right] = count + 1;
                }
            }

            foreach (KeyValuePair<int, int> item in overlap
                         .OrderByDescending(item => Score(tokens[left], tokens[item.Key], item.Value))
                         .ThenBy(item => item.Key)
                         .Take(request.Search.MaxNeighborsPerClaim))
            {
                ClaimPair identity = ClaimPair.Create(request.Claims[left], request.Claims[item.Key]);
                pairs.TryAdd(identity, new ClaimPairCandidate(
                    identity.Left,
                    identity.Right,
                    CandidateSourceKind.Graph,
                    new CandidateScore(
                        Score(tokens[left], tokens[item.Key], item.Value),
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
        Dictionary<string, List<int>> postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < tokenSets.Count; index++)
            foreach (string token in tokenSets[index])
            {
                if (!postings.TryGetValue(token, out List<int>? indexes))
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
        Dictionary<Claim, int> counts = new Dictionary<Claim, int>();
        foreach (ClaimPairCandidate pair in pairs
                     .OrderByDescending(pair => pair.Score.Lexical)
                     .ThenBy(pair => pair.Left.FilePath, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Right.FilePath, StringComparer.Ordinal))
        {
            counts.TryGetValue(pair.Left, out int leftCount);
            counts.TryGetValue(pair.Right, out int rightCount);
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
            int path = StringComparer.Ordinal.Compare(left.FilePath, right.FilePath);
            if (path != 0) return path;
            int line = left.StartLine.CompareTo(right.StartLine);
            return line != 0 ? line : StringComparer.Ordinal.Compare(left.ContentHash, right.ContentHash);
        }
    }
}
