namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// Decides whether a resolved path stays inside the repository root.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, not one: a path is inside the root when it starts with the root's boundary, and the
/// comparison carries the rules of the target platform rather than of the process. Windows
/// compares its paths case-insensitively, so <c>c:\work\repo\docs</c> is the same file as
/// <c>C:\work\repo\docs</c>; comparing ordinally there would refuse a write the host itself made.
/// Unix compares case-sensitively, so the same comparison must not be case-folded there.
/// </para>
/// <para>
/// A root that is itself a filesystem root (<c>/</c>, <c>C:\</c>) has no trailing separator to
/// keep, so trimming leaves the separator itself as the boundary, and every absolute path on that
/// drive is inside it. Both rules are kept here rather than in each caller so they stay testable
/// on every CI host, whatever the host's own path rules are.
/// </para>
/// </remarks>
internal static class RepositoryBoundary
{
    /// <summary>
    /// The prefix an inside path must carry: <paramref name="repositoryRoot"/> with exactly one
    /// trailing <paramref name="separator"/>.
    /// </summary>
    internal static string For(string repositoryRoot, char separator) =>
        repositoryRoot.TrimEnd(separator) + separator;

    /// <summary>
    /// True when <paramref name="absolutePath"/> lies inside the boundary <see cref="For"/>
    /// produced, graded by <paramref name="isWindows"/>'s casing rules.
    /// </summary>
    internal static bool IsWithin(string absolutePath, string boundary, bool isWindows) =>
        absolutePath.StartsWith(
            boundary,
            isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
