using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Link-reachability inside one inventory folder, shared by the validators that check an
/// inventory index accounts for the documents beside it.
/// </summary>
/// <remarks>
/// One algorithm, two inventories: the plan and todo rules ask the same question about
/// different folders. This is exactly the shared logic the repository's
/// duplicate-implementation review lens exists to keep out of private copies — copies
/// drift apart the first time one of them learns something the other does not.
/// </remarks>
internal static class InventoryReachability
{
    private const string FolderIndexName = "README.md";

    /// <summary>
    /// Every document inside <paramref name="folder"/> reachable from <paramref name="start"/>
    /// by relative body links, the start document included.
    /// </summary>
    public static HashSet<string> ReachableWithin(
        DocumentModel start,
        string folder,
        Dictionary<string, DocumentModel> byPath)
    {
        HashSet<string> reached = new HashSet<string>(DocsRootPath.PathComparer) { start.RelativePath };
        Queue<DocumentModel> pending = new Queue<DocumentModel>();
        pending.Enqueue(start);

        while (pending.TryDequeue(out DocumentModel? current))
        {
            foreach (string link in current.BodyLinks)
            {
                if (ResolveDocument(current.RelativePath, link, byPath) is not { } target
                    || !IsWithin(target.RelativePath, folder)
                    || !reached.Add(target.RelativePath))
                {
                    continue;
                }

                pending.Enqueue(target);
            }
        }

        return reached;
    }

    /// <summary>
    /// The document a link lands on. A link to a directory lands on that directory's README,
    /// which is how an inventory usually links the pack a document dispatches to.
    /// </summary>
    public static DocumentModel? ResolveDocument(
        string fromRelativePath,
        string link,
        Dictionary<string, DocumentModel> byPath)
    {
        string? target = DocGraphProjection.ResolveLink(fromRelativePath, link);
        if (target is null)
            return null;

        if (byPath.TryGetValue(target, out DocumentModel? document))
            return document;

        return byPath.GetValueOrDefault($"{target}/{FolderIndexName}");
    }

    public static bool IsWithin(string relativePath, string folder) =>
        folder.Length == 0
        || relativePath.StartsWith(
            folder + "/",
            DocsRootPath.PathComparer == StringComparer.OrdinalIgnoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// True when the document sits under the archive subtree, matched per segment the way
    /// the loader matches its exclusions.
    /// </summary>
    /// <remarks>
    /// The inventory rules protect the active folder, and their hints send closed work to
    /// <c>archive/</c> — so an archived document is not live whatever the retrieval policy.
    /// A host that lifts the loader's default exclusion of
    /// <see cref="OntologyConfig.ArchiveSegment"/> and overrides an index above the subtree
    /// makes the derived folder span the archive; without this check those closed documents
    /// would fail <c>docs validate</c> as unlisted live work.
    /// </remarks>
    public static bool IsArchived(string relativePath) =>
        relativePath.Split('/').Contains(OntologyConfig.ArchiveSegment, StringComparer.OrdinalIgnoreCase);
}
