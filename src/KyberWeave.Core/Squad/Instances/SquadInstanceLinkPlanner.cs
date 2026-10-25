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
        HashSet<string> neverLinkedExact = new(StringComparer.Ordinal);
        List<string> neverLinkedPrefixes = [];
        foreach (string never in row.NeverLinked)
        {
            string norm = NormalizeRelativePath(never);
            neverLinkedExact.Add(norm);
            neverLinkedPrefixes.Add(norm + "/");
        }

        bool IsNeverLinked(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (neverLinkedExact.Contains(normalized))
            {
                return true;
            }

            foreach (string prefix in neverLinkedPrefixes)
            {
                if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        HashSet<string> renderedAncestors = new(StringComparer.Ordinal);
        foreach (string rendered in normalizedRendered)
        {
            int slash = rendered.LastIndexOf('/');
            while (slash > 0)
            {
                renderedAncestors.Add(rendered[..slash]);
                slash = rendered.LastIndexOf('/', slash - 1);
            }
        }

        bool ContainsRendered(string dirRelativePath)
        {
            string normalizedDir = NormalizeRelativePath(dirRelativePath);
            return normalizedRendered.Contains(normalizedDir) || renderedAncestors.Contains(normalizedDir);
        }

        List<SquadLinkRecord> candidateDirs = [];
        foreach (SquadLinkRecord record in mainListing)
        {
            if (record.Kind == SquadLinkKind.Directory &&
                !IsNeverLinked(record.RelativePath) &&
                !ContainsRendered(record.RelativePath))
            {
                candidateDirs.Add(record);
            }
        }

        HashSet<string> candidateDirNorms = new(candidateDirs.Select(d => NormalizeRelativePath(d.RelativePath)), StringComparer.Ordinal);
        List<SquadLinkRecord> outermostDirs = [];
        HashSet<string> outermostDirNorms = new(StringComparer.Ordinal);

        foreach (SquadLinkRecord dir in candidateDirs)
        {
            string normDir = NormalizeRelativePath(dir.RelativePath);
            bool hasAncestor = false;
            int slash = normDir.LastIndexOf('/');
            while (slash > 0)
            {
                string ancestor = normDir[..slash];
                if (candidateDirNorms.Contains(ancestor))
                {
                    hasAncestor = true;
                    break;
                }

                slash = normDir.LastIndexOf('/', slash - 1);
            }

            if (!hasAncestor)
            {
                outermostDirs.Add(dir);
                outermostDirNorms.Add(normDir);
            }
        }

        List<SquadLinkAction> actions = [];
        HashSet<string> addedPaths = new(StringComparer.Ordinal);
        Dictionary<string, SquadLinkRecord> mainByRelative = new(mainListing.Count, StringComparer.Ordinal);
        Dictionary<string, string> mainLookup = new(mainListing.Count, StringComparer.Ordinal);

        foreach (SquadLinkRecord m in mainListing)
        {
            string key = NormalizeRelativePath(m.RelativePath);
            mainByRelative.TryAdd(key, m);
            mainLookup.TryAdd(key, NormalizeTargetPath(m.TargetPath));
        }

        // 1. Add actions for planned links
        foreach (SquadLinkRecord record in mainListing)
        {
            if (record.Kind == SquadLinkKind.Directory)
            {
                string normDir = NormalizeRelativePath(record.RelativePath);
                if (outermostDirNorms.Contains(normDir))
                {
                    actions.Add(new SquadLinkAction.Add(record));
                    addedPaths.Add(normDir);
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

                bool coveredByLinkedDirectory = false;
                int slash = normFile.LastIndexOf('/');
                while (slash > 0)
                {
                    string ancestor = normFile[..slash];
                    if (outermostDirNorms.Contains(ancestor))
                    {
                        coveredByLinkedDirectory = true;
                        break;
                    }

                    slash = normFile.LastIndexOf('/', slash - 1);
                }

                if (!coveredByLinkedDirectory)
                {
                    actions.Add(new SquadLinkAction.Add(record));
                    addedPaths.Add(normFile);
                }
            }
        }

        // 1b. ExtraLinks from catalog row: guarantee an Add action for each extra link present in mainListing
        foreach (string extraLink in row.ExtraLinks)
        {
            string normExtra = NormalizeRelativePath(extraLink);
            if (!addedPaths.Contains(normExtra) && mainByRelative.TryGetValue(normExtra, out SquadLinkRecord? match))
            {
                actions.Add(new SquadLinkAction.Add(match));
                addedPaths.Add(normExtra);
            }
        }

        // 2. Remove actions for stale recorded links whose target in main home no longer exists
        foreach (SquadLinkRecord recorded in recordedLinks)
        {
            string normRecordedRelative = NormalizeRelativePath(recorded.RelativePath);
            string normRecordedTarget = NormalizeTargetPath(recorded.TargetPath);

            bool existsInMain = mainLookup.TryGetValue(normRecordedRelative, out string? target) &&
                string.Equals(target, normRecordedTarget, StringComparison.Ordinal);

            if (!existsInMain)
            {
                actions.Add(new SquadLinkAction.Remove(recorded));
            }
        }

        // 3. Keep actions for existing instance entries that are not recorded links
        HashSet<string> recordedLinksSet = new(recordedLinks.Select(r => NormalizeRelativePath(r.RelativePath)), StringComparer.Ordinal);
        HashSet<string> seenKept = new(StringComparer.Ordinal);
        foreach (string instanceEntry in instanceListing)
        {
            string normEntry = NormalizeRelativePath(instanceEntry);
            if (!recordedLinksSet.Contains(normEntry) && seenKept.Add(normEntry))
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
