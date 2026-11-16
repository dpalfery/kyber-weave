using System.Diagnostics.CodeAnalysis;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The file system kind of a Squad link target.</summary>
public enum SquadLinkKind
{
    /// <summary>A regular file link target.</summary>
    File,

    /// <summary>A directory link target.</summary>
    Directory
}

/// <summary>A link entry mapping a relative path in an instance to its target path in the main harness home.</summary>
/// <param name="RelativePath">Path relative to the instance root.</param>
/// <param name="Kind">File or Directory link kind.</param>
/// <param name="TargetPath">Absolute target path in the main harness home.</param>
public sealed record SquadLinkRecord(string RelativePath, SquadLinkKind Kind, string TargetPath);

/// <summary>Action to perform when synchronizing instance links.</summary>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification =
        "Discriminated union variants are nested within the base action record to model closed action types.")]
public abstract record SquadLinkAction
{
    private protected SquadLinkAction()
    {
    }

    /// <summary>Action to create or update a symbolic link.</summary>
    /// <param name="Record">The link record to establish.</param>
    public sealed record Add(SquadLinkRecord Record) : SquadLinkAction;

    /// <summary>Action to remove a previously established link whose target no longer exists.</summary>
    /// <param name="Record">The link record to remove.</param>
    public sealed record Remove(SquadLinkRecord Record) : SquadLinkAction;

    /// <summary>Action indicating an unmanaged instance copy is preserved rather than overwritten.</summary>
    /// <param name="RelativePath">Path relative to the instance root.</param>
    /// <param name="Warning">Warning description explaining why the instance copy was kept.</param>
    public sealed record Keep(string RelativePath, string Warning) : SquadLinkAction;
}
