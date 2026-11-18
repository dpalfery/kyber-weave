namespace KyberWeave.Core.Review;

/// <summary>The kind of change a path in the working tree carries.</summary>
public enum ChangeKind
{
    Unknown = 0,
    Added,
    Copied,
    Modified,
    Renamed,
    Deleted,
    Untracked
}

/// <summary>One changed path with the kind of change it carries.</summary>
/// <param name="Path">Repository-relative path, forward-slashed.</param>
/// <param name="Kind">How the path changed since the merge base.</param>
public sealed record ChangedPath(string Path, ChangeKind Kind);
