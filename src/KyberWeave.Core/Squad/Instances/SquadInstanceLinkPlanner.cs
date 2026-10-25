namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Computes link synchronization actions between a main harness home and an instance home.
/// </summary>
/// <remarks>
/// This planner is a pure function with zero file I/O. It evaluates the main home's listing
/// against files rendered by Squad, catalog rows, and existing instance link records to determine
/// which links to create, which stale links to remove, and which unmanaged instance files to keep.
/// </remarks>
public static class SquadInstanceLinkPlanner
{
    private const string InstanceCopyKeptWarning = "instance copy kept";

    /// <summary>
    /// Plans link synchronization actions for a harness instance.
    /// </summary>
    /// <param name="mainListing">All entries available in the main harness home.</param>
    /// <param name="renderedPaths">Paths rendered directly by Squad that must never be linked.</param>
    /// <param name="row">Instance catalog row specifying ExtraLinks and NeverLinked rules.</param>
    /// <param name="recordedLinks">Previously recorded links established for this instance.</param>
    /// <param name="instanceListing">Current entries present in the instance directory.</param>
    /// <returns>Deterministic list of link actions to apply.</returns>
    public static IReadOnlyList<SquadLinkAction> Plan(
        IReadOnlyList<SquadLinkRecord> mainListing,
        IReadOnlySet<string> renderedPaths,
        SquadInstanceRow row,
        IReadOnlyList<SquadLinkRecord> recordedLinks,
        IReadOnlyList<string> instanceListing)
    {
        ArgumentNullException.ThrowIfNull(mainListing);
        ArgumentNullException.ThrowIfNull(renderedPaths);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(recordedLinks);
        ArgumentNullException.ThrowIfNull(instanceListing);

        HashSet<string> normalizedRendered = new(renderedPaths.Select(NormalizeRelativePath), StringComparer.Ordinal);
        HashSet<string> neverLinkedSet = new(row.NeverLinked.Select(NormalizeRelativePath), StringComparer.Ordinal);

        bool IsNeverLinked(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (neverLinkedSet.Contains(normalized))
            {
                return true;
            }

            foreach (string never in neverLinkedSet)
            {
                if (normalized.StartsWith(never + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        bool ContainsRendered(string dirRelativePath)
        {
            string normalizedDir = NormalizeRelativePath(dirRelativePath);
            if (normalizedRendered.Contains(normalizedDir))
            {
                return true;
            }

            string prefix = normalizedDir + "/";
            foreach (string rendered in normalizedRendered)
            {
                if (rendered.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        List<SquadLinkRecord> candidateDirs = new();
        foreach (SquadLinkRecord record in mainListing)
        {
            if (record.Kind == SquadLinkKind.Directory &&
                !IsNeverLinked(record.RelativePath) &&
                !ContainsRendered(record.RelativePath))
            {
                candidateDirs.Add(record);
            }
        }

        List<SquadLinkRecord> outermostDirs = new();
        foreach (SquadLinkRecord dir in candidateDirs)
        {
            string normDir = NormalizeRelativePath(dir.RelativePath);
            bool hasAncestor = candidateDirs.Any(other =>
            {
                if (ReferenceEquals(other, dir))
                {
                    return false;
                }

                string otherNorm = NormalizeRelativePath(other.RelativePath);
                return normDir.StartsWith(otherNorm + "/", StringComparison.Ordinal);
            });

            if (!hasAncestor)
            {
                outermostDirs.Add(dir);
            }
        }

        List<SquadLinkAction> actions = new();

        // 1. Add actions for planned links
        foreach (SquadLinkRecord record in mainListing)
        {
            if (record.Kind == SquadLinkKind.Directory)
            {
                string normDir = NormalizeRelativePath(record.RelativePath);
                bool isOutermost = outermostDirs.Any(d =>
                    string.Equals(NormalizeRelativePath(d.RelativePath), normDir, StringComparison.Ordinal));

                if (isOutermost)
                {
                    actions.Add(new SquadLinkAction.Add(record));
                }
            }
            else
            {
                string normFile = NormalizeRelativePath(record.RelativePath);
                if (normalizedRendered.Contains(normFile))
                {
                    continue;
                }

                if (IsNeverLinked(record.RelativePath))
                {
                    continue;
                }

                bool coveredByLinkedDirectory = outermostDirs.Any(d =>
                    normFile.StartsWith(NormalizeRelativePath(d.RelativePath) + "/", StringComparison.Ordinal));

                if (!coveredByLinkedDirectory)
                {
                    actions.Add(new SquadLinkAction.Add(record));
                }
            }
        }

        // 2. Remove actions for stale recorded links whose target in main home no longer exists
        foreach (SquadLinkRecord recorded in recordedLinks)
        {
            string normRecordedRelative = NormalizeRelativePath(recorded.RelativePath);
            string normRecordedTarget = NormalizeTargetPath(recorded.TargetPath);

            bool existsInMain = mainListing.Any(m =>
                string.Equals(NormalizeRelativePath(m.RelativePath), normRecordedRelative, StringComparison.Ordinal) &&
                string.Equals(NormalizeTargetPath(m.TargetPath), normRecordedTarget, StringComparison.Ordinal));

            if (!existsInMain)
            {
                actions.Add(new SquadLinkAction.Remove(recorded));
            }
        }

        // 3. Keep actions for existing instance entries that are not recorded links
        HashSet<string> seenKept = new(StringComparer.Ordinal);
        foreach (string instanceEntry in instanceListing)
        {
            string normEntry = NormalizeRelativePath(instanceEntry);
            bool isRecordedLink = recordedLinks.Any(r =>
                string.Equals(NormalizeRelativePath(r.RelativePath), normEntry, StringComparison.Ordinal));

            if (!isRecordedLink && seenKept.Add(normEntry))
            {
                actions.Add(new SquadLinkAction.Keep(instanceEntry, InstanceCopyKeptWarning));
            }
        }

        return actions;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string NormalizeTargetPath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }
}
