using KyberWeave.Core;
namespace KyberWeave.Core.Review;

/// <summary>Counts the changed lines of a review scope that count toward its size, and names the paths that do not.</summary>
public static class ReviewScopeRules
{
    /// <summary>Lines that count toward review scope line-count metadata after generated churn is ignored.</summary>
    /// <remarks>
    /// Lockfiles and scratch output are machine-generated churn, so they are excluded from
    /// the effective changed lines numerator while staying listed in <see cref="ReviewScope.ChangedPaths"/>
    /// for reviewers to see. When per-path counts are present the numerator is their scoped sum;
    /// without them the even-split fallback divides with a ceiling to provide scope line-count
    /// metadata.
    /// </remarks>
    public static int EffectiveChangedLines(this ReviewScope scope) => ComputeEffectiveChangedLines(scope);

    /// <summary>Whether a repository-relative path is excluded from the size numerator.</summary>
    /// <param name="path">Repository-relative path with forward slashes.</param>
    public static bool IsExcludedPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');
        if (normalized.Equals(ScratchLayout.Root, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(ScratchLayout.Root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int slash = normalized.LastIndexOf('/');
        string fileName = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.Equals("bun.lock", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("bun.lockb", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("bun.lock.", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("bun.lock-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("uv.lock", StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputeEffectiveChangedLines(ReviewScope scope)
    {
        if (scope.ChangedLines <= 0)
        {
            return scope.ChangedLines;
        }

        if (scope.PathLineCounts is not null && scope.PathLineCounts.Count > 0)
        {
            // Lookups use the same separator- and case-normalized form IsExcludedPath
            // matches on, so a producer that reports 'SRC\\Foo.cs' still lines up with
            // the 'src/Foo.cs' change entry instead of falling through to the heuristic.
            Dictionary<string, int> normalizedCounts = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> entry in scope.PathLineCounts)
            {
                normalizedCounts[entry.Key.Replace('\\', '/')] = entry.Value;
            }

            bool complete = true;
            foreach (string path in scope.ChangedPaths)
            {
                if (IsExcludedPath(path))
                {
                    continue;
                }

                if (!normalizedCounts.ContainsKey(path.Replace('\\', '/')))
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                int scoped = 0;
                foreach (string path in scope.ChangedPaths)
                {
                    if (IsExcludedPath(path))
                    {
                        continue;
                    }

                    if (normalizedCounts.TryGetValue(path.Replace('\\', '/'), out int lines) && lines > 0)
                    {
                        scoped += lines;
                    }
                }

                return scoped;
            }

            // Incomplete per-path counts fall through to the even-split heuristic below
            // using ceiling division so effective changed lines are not underestimated.
        }

        if (scope.ChangedPaths.Count == 0)
        {
            return scope.ChangedLines;
        }

        int included = 0;
        foreach (string path in scope.ChangedPaths)
        {
            if (!IsExcludedPath(path))
            {
                included++;
            }
        }

        if (included == scope.ChangedPaths.Count)
        {
            return scope.ChangedLines;
        }

        if (included == 0)
        {
            return 0;
        }

        long evenSplit = (long)scope.ChangedLines * included;
        return (int)((evenSplit + scope.ChangedPaths.Count - 1) / scope.ChangedPaths.Count);
    }
}
