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
        NeverLinkedMatcher neverLinked = new(row.NeverLinked);
        HashSet<string> renderedAncestors = CollectRenderedAncestors(normalizedRendered);

        List<SquadLinkRecord> candidateDirs = CollectCandidateDirs(mainListing, neverLinked, normalizedRendered, renderedAncestors);
        HashSet<string> candidateDirNorms = new(candidateDirs.Select(d => NormalizeRelativePath(d.RelativePath)), StringComparer.Ordinal);
        HashSet<string> outermostDirNorms = CollectOutermostDirNorms(candidateDirs, candidateDirNorms);

        List<SquadLinkAction> actions = [];
        HashSet<string> addedPaths = new(StringComparer.Ordinal);
        Dictionary<string, SquadLinkRecord> mainByRelative = new(mainListing.Count, StringComparer.Ordinal);
        Dictionary<string, string> mainLookup = new(mainListing.Count, StringComparer.Ordinal);
        BuildMainLookups(mainListing, mainByRelative, mainLookup);

        AddPlannedLinkActions(mainListing, neverLinked, normalizedRendered, outermostDirNorms, actions, addedPaths);
        AddExtraLinkActions(row.ExtraLinks, mainByRelative, actions, addedPaths);
        AddRemoveActions(recordedLinks, mainLookup, actions);
        AddKeepActions(recordedLinks, instanceListing, actions);

        return actions;
    }

    private static HashSet<string> CollectRenderedAncestors(HashSet<string> normalizedRendered)
    {
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

        return renderedAncestors;
    }

    private static List<SquadLinkRecord> CollectCandidateDirs(
        IReadOnlyList<SquadLinkRecord> mainListing,
        NeverLinkedMatcher neverLinked,
        HashSet<string> normalizedRendered,
        HashSet<string> renderedAncestors)
    {
        List<SquadLinkRecord> candidateDirs = [];
        foreach (SquadLinkRecord record in mainListing)
        {
            if (record.Kind == SquadLinkKind.Directory &&
                !neverLinked.IsNeverLinked(record.RelativePath) &&
                !ContainsRendered(record.RelativePath, normalizedRendered, renderedAncestors))
            {
                candidateDirs.Add(record);
            }
        }

        return candidateDirs;
    }

    private static bool ContainsRendered(
        string dirRelativePath,
        HashSet<string> normalizedRendered,
        HashSet<string> renderedAncestors)
    {
        string normalizedDir = NormalizeRelativePath(dirRelativePath);
        return normalizedRendered.Contains(normalizedDir) || renderedAncestors.Contains(normalizedDir);
    }

    private static HashSet<string> CollectOutermostDirNorms(
        List<SquadLinkRecord> candidateDirs,
        HashSet<string> candidateDirNorms)
    {
        HashSet<string> outermostDirNorms = new(StringComparer.Ordinal);
        foreach (SquadLinkRecord dir in candidateDirs)
        {
            string normDir = NormalizeRelativePath(dir.RelativePath);
            if (!HasAncestorIn(normDir, candidateDirNorms))
            {
                outermostDirNorms.Add(normDir);
            }
        }

        return outermostDirNorms;
    }

    private static bool HasAncestorIn(string normalizedPath, HashSet<string> ancestors)
    {
        int slash = normalizedPath.LastIndexOf('/');
        while (slash > 0)
        {
            if (ancestors.Contains(normalizedPath[..slash]))
            {
                return true;
            }

            slash = normalizedPath.LastIndexOf('/', slash - 1);
        }

        return false;
    }

    private static void BuildMainLookups(
        IReadOnlyList<SquadLinkRecord> mainListing,
        Dictionary<string, SquadLinkRecord> mainByRelative,
        Dictionary<string, string> mainLookup)
    {
        foreach (SquadLinkRecord m in mainListing)
        {
            string key = NormalizeRelativePath(m.RelativePath);
            mainByRelative.TryAdd(key, m);
            mainLookup.TryAdd(key, NormalizeTargetPath(m.TargetPath));
        }
    }

    private static void AddPlannedLinkActions(
        IReadOnlyList<SquadLinkRecord> mainListing,
        NeverLinkedMatcher neverLinked,
        HashSet<string> normalizedRendered,
        HashSet<string> outermostDirNorms,
        List<SquadLinkAction> actions,
        HashSet<string> addedPaths)
    {
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

                if (neverLinked.IsNeverLinked(record.RelativePath))
                {
                    continue;
                }

                if (!HasAncestorIn(normFile, outermostDirNorms))
                {
                    actions.Add(new SquadLinkAction.Add(record));
                    addedPaths.Add(normFile);
                }
            }
        }
    }

    private static void AddExtraLinkActions(
        IReadOnlyList<string> extraLinks,
        Dictionary<string, SquadLinkRecord> mainByRelative,
        List<SquadLinkAction> actions,
        HashSet<string> addedPaths)
    {
        // 1b. ExtraLinks from catalog row: guarantee an Add action for each extra link present in mainListing
        foreach (string extraLink in extraLinks)
        {
            string normExtra = NormalizeRelativePath(extraLink);
            if (!addedPaths.Contains(normExtra) && mainByRelative.TryGetValue(normExtra, out SquadLinkRecord? match))
            {
                actions.Add(new SquadLinkAction.Add(match));
                addedPaths.Add(normExtra);
            }
        }
    }

    private static void AddRemoveActions(
        IReadOnlyList<SquadLinkRecord> recordedLinks,
        Dictionary<string, string> mainLookup,
        List<SquadLinkAction> actions)
    {
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
    }

    private static void AddKeepActions(
        IReadOnlyList<SquadLinkRecord> recordedLinks,
        IReadOnlyList<string> instanceListing,
        List<SquadLinkAction> actions)
    {
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
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string NormalizeTargetPath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }

    private sealed class NeverLinkedMatcher
    {
        private readonly HashSet<string> _exact;
        private readonly List<string> _prefixes;

        public NeverLinkedMatcher(IEnumerable<string> neverLinked)
        {
            _exact = new HashSet<string>(StringComparer.Ordinal);
            _prefixes = [];
            foreach (string never in neverLinked)
            {
                string norm = NormalizeRelativePath(never);
                _exact.Add(norm);
                _prefixes.Add(norm + "/");
            }
        }

        public bool IsNeverLinked(string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (_exact.Contains(normalized))
            {
                return true;
            }

            foreach (string prefix in _prefixes)
            {
                if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
