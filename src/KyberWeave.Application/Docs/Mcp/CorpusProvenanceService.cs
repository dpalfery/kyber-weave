using System.Collections.Concurrent;
using System.Globalization;
using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Mcp;

/// <summary>
/// Formats the provenance header every docs tool leads with: the absolute corpus root, the
/// branch and git directory it belongs to, the short HEAD revision with a dirty marker, and the
/// live document count.
/// </summary>
/// <remarks>
/// <para>
/// The header is the tell that a same-named server is bound to a different checkout. A
/// mis-rooted process otherwise answers with the same confident prose as a correct one, so
/// the root and revision have to be visible on every response rather than inferred from a
/// document count alone (issue #162).
/// </para>
/// <para>
/// <c>branch=</c> and <c>repo=</c> are what separates the two ways a root can differ from the
/// caller's own. A second worktree of this repository has another root and the same git
/// directory; a foreign repository has another git directory. Comparing <c>root=</c> alone
/// reads the first case as the second, and refuses an answer that is in fact correct.
/// </para>
/// <para>
/// Values are cached per root and invalidated when either the host's corpus stamp changes or a
/// cheap fingerprint of the git tip and index moves, so a commit that does not touch documentation
/// still refreshes <c>rev</c>/<c>dirty</c> without shelling out on every tool call. The cache is
/// process-wide, as the header it serves is.
/// </para>
/// </remarks>
public sealed class CorpusProvenanceService(IGitCheckoutProbe probe, IPathResolver paths, IProcessEnvironment environment)
    : ICorpusProvenance
{
    private static readonly ConcurrentDictionary<string, CachedIdentity> Cache =
        new(StringComparer.Ordinal);

    private readonly IGitCheckoutProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <inheritdoc />
    public string Line(string repositoryRoot, int? documentCount, int corpusStamp)
    {
        string root = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        CachedIdentity identity = Identity(root, corpusStamp);
        string docs = documentCount is { } count
            ? count.ToString(CultureInfo.InvariantCulture)
            : CorpusProvenance.Unavailable;
        return "provenance: root=" + root
            + " branch=" + identity.Branch
            + " repo=" + identity.Repository
            + " rev=" + identity.Revision
            + " dirty=" + identity.Dirty
            + " documents=" + docs;
    }

    private CachedIdentity Identity(string root, int corpusStamp)
    {
        long gitStamp = _probe.Fingerprint(root);
        if (Cache.TryGetValue(root, out CachedIdentity cached)
            && cached.CorpusStamp == corpusStamp
            && cached.GitStamp == gitStamp)
        {
            return cached;
        }

        GitCheckoutLocation location = _probe.ReadLocation(root);
        GitCheckoutRevision revision = _probe.ReadRevision(root);
        CachedIdentity identity = new(
            corpusStamp,
            gitStamp,
            location.Branch,
            location.Repository,
            revision.Revision,
            revision.Dirty);
        Cache[root] = identity;
        return identity;
    }

    private readonly record struct CachedIdentity(
        int CorpusStamp,
        long GitStamp,
        string Branch,
        string Repository,
        string Revision,
        string Dirty);
}
