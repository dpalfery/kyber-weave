namespace KyberWeave.Core.Review.Catalogue;

/// <summary>How marker files that sit inside one another are reduced to roots.</summary>
public enum ProjectRootNesting
{
    /// <summary>Only the outermost marker directory is a root; the directories it covers are not.</summary>
    Outermost,

    /// <summary>Every directory that holds a marker is a root.</summary>
    Each
}

/// <summary>One project root of a family, with the marker that proves it and the slug its gate ids carry.</summary>
/// <param name="RootDirectory">Repository-relative, forward-slashed. <c>.</c> is the repository root.</param>
/// <param name="MarkerPath">Repository-relative path of the marker that made this directory a root.</param>
/// <param name="Slug">Lookup-safe identifier part, unique among the roots of one family.</param>
public sealed record ProjectRoot(string RootDirectory, string MarkerPath, string Slug);

/// <summary>The roots one family has in a repository, and whether there are too many to write out in full.</summary>
/// <param name="Roots">Every root in ordinal path order, including when the family is capped.</param>
/// <param name="Capped">True when the family has more roots than its gates are written for.</param>
public sealed record ProjectRootSet(IReadOnlyList<ProjectRoot> Roots, bool Capped);
