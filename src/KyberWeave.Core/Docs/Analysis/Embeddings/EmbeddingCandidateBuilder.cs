using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Embeddings;

/// <summary>Creates semantic claim pairs from validated, ordered embeddings.</summary>
internal static class EmbeddingCandidateBuilder
{
    public static ClaimCandidateSourceResult Build(
        IReadOnlyList<Claim> claims,
        IReadOnlyList<StoredEmbedding> embeddings,
        IReadOnlyCollection<ClaimPairCandidate> seedPairs,
        DocsAnalysisSearchConfig search)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(seedPairs);
        ArgumentNullException.ThrowIfNull(search);
        if (claims.Count != embeddings.Count)
            throw new InvalidDataException("Embedding count must match the analyzed claim count.");

        Dictionary<Claim, int> claimIndexes = claims
            .Select((claim, index) => new { claim, index })
            .ToDictionary(item => item.claim, item => item.index);
        IReadOnlyList<IndexPair> pairs = search.Mode == DocsAnalysisSearchMode.HighRecall
            ? AllPairs(claims.Count)
            : SeedPairs(seedPairs, claimIndexes);
        ScoredPair[] scored = pairs
            .Select(pair => new ScoredPair(pair, Cosine(
                embeddings[pair.Left].Vector,
                embeddings[pair.Right].Vector)))
            .ToArray();

        IReadOnlyList<ScoredPair> selected = search.Mode == DocsAnalysisSearchMode.HighRecall
            ? SelectTopNeighbors(scored, claims.Count, search.MaxNeighborsPerClaim)
            : scored;
        bool truncated = selected.Count > search.MaxCandidates;
        ClaimPairCandidate[] candidates = selected
            .OrderBy(item => item.Pair.Left)
            .ThenBy(item => item.Pair.Right)
            .Take(search.MaxCandidates)
            .Select(item =>
            {
                ClaimPairCandidate? seed = FindSeed(seedPairs, claims[item.Pair.Left], claims[item.Pair.Right]);
                return new ClaimPairCandidate(
                    claims[item.Pair.Left],
                    claims[item.Pair.Right],
                    CandidateSourceKind.Embedding,
                    new CandidateScore(
                        seed?.Score.Lexical ?? LexicalSimilarity.Score(
                            claims[item.Pair.Left].ContextualText,
                            claims[item.Pair.Right].ContextualText),
                        item.Score,
                        seed?.Score.Graph ?? 0));
            })
            .ToArray();
        return new ClaimCandidateSourceResult(candidates, scored.Length, truncated);
    }

    private static IReadOnlyList<ScoredPair> SelectTopNeighbors(
        IReadOnlyList<ScoredPair> scored,
        int claimCount,
        int maximumNeighbors)
    {
        List<ScoredPair>[] neighbors = Enumerable.Range(0, claimCount)
            .Select(_ => new List<ScoredPair>())
            .ToArray();
        foreach (ScoredPair item in scored)
        {
            neighbors[item.Pair.Left].Add(item);
            neighbors[item.Pair.Right].Add(item);
        }

        HashSet<IndexPair> selected = new HashSet<IndexPair>();
        for (int claimIndex = 0; claimIndex < claimCount; claimIndex++)
        {
            foreach (ScoredPair? item in neighbors[claimIndex]
                         .OrderByDescending(item => item.Score)
                         .ThenBy(item => item.Pair.Left)
                         .ThenBy(item => item.Pair.Right)
                         .Take(maximumNeighbors))
            {
                selected.Add(item.Pair);
            }
        }

        return scored.Where(item => selected.Contains(item.Pair)).ToArray();
    }

    private static IReadOnlyList<IndexPair> AllPairs(int count)
    {
        List<IndexPair> pairs = new List<IndexPair>();
        for (int left = 0; left < count; left++)
        {
            for (int right = left + 1; right < count; right++)
                pairs.Add(new IndexPair(left, right));
        }
        return pairs;
    }

    private static IReadOnlyList<IndexPair> SeedPairs(
        IEnumerable<ClaimPairCandidate> seeds,
        IReadOnlyDictionary<Claim, int> indexes) =>
        seeds
            .Where(seed => indexes.ContainsKey(seed.Left) && indexes.ContainsKey(seed.Right))
            .Select(seed => IndexPair.Create(indexes[seed.Left], indexes[seed.Right]))
            .Distinct()
            .ToArray();

    private static ClaimPairCandidate? FindSeed(
        IEnumerable<ClaimPairCandidate> seeds,
        Claim left,
        Claim right) =>
        seeds.FirstOrDefault(seed =>
            (seed.Left == left && seed.Right == right)
            || (seed.Left == right && seed.Right == left));

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
            throw new InvalidDataException("Cached embedding vectors must have consistent, non-zero dimensions.");

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (int index = 0; index < left.Count; index++)
        {
            if (!float.IsFinite(left[index]) || !float.IsFinite(right[index]))
                throw new InvalidDataException("Cached embedding vectors must contain only finite values.");
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm <= 0 || rightNorm <= 0)
            throw new InvalidDataException("Cached embedding vectors must have non-zero norms.");
        return Math.Clamp(dot / Math.Sqrt(leftNorm * rightNorm), -1, 1);
    }

    private readonly record struct IndexPair(int Left, int Right)
    {
        public static IndexPair Create(int left, int right) =>
            left < right ? new IndexPair(left, right) : new IndexPair(right, left);
    }

    private sealed record ScoredPair(IndexPair Pair, double Score);
}
