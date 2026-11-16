using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>What the scaffolder did to one file.</summary>
public enum ScaffoldOutcome
{
    /// <summary>The file did not exist and was written whole.</summary>
    Created,

    /// <summary>The file existed and was edited in place, keeping the rest of its content.</summary>
    Updated,

    /// <summary>The file existed and was left alone; <c>force</c> would have overwritten it.</summary>
    Skipped,

    /// <summary>
    /// The file existed and was left alone because it is operator state rather than
    /// scaffolding. <c>force</c> does not reach it.
    /// </summary>
    Preserved
}

/// <summary>Where the documentation root used by <c>docs init</c> came from.</summary>
public enum DocsRootSource
{
    /// <summary>The operator supplied <c>--docs-root</c>.</summary>
    Explicit,

    /// <summary>An existing host configuration supplied the root.</summary>
    Configuration,

    /// <summary>The root was selected from the conventional directory names.</summary>
    Convention
}

/// <summary>One file the scaffolder considered writing.</summary>
/// <param name="RelativePath">Repository-relative path, forward-slashed.</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="Note">
/// Why, when the outcome alone would understate it — an edit that touched one key, or a
/// file held back from <c>force</c>. Plain text; the caller escapes it for its own output.
/// </param>
public sealed record ScaffoldedFile(string RelativePath, ScaffoldOutcome Outcome, string? Note = null)
{
    /// <summary>True when the file's content changed.</summary>
    public bool Written => Outcome is ScaffoldOutcome.Created or ScaffoldOutcome.Updated;
}

/// <summary>What the documentation scaffolder did.</summary>
/// <param name="DocsRoot">The documentation root the corpus was scaffolded into.</param>
/// <param name="DocsRootSource">Where the resolved documentation root came from.</param>
/// <param name="Files">Every file considered, written or skipped.</param>
public sealed record ScaffoldResult(
    string DocsRoot,
    DocsRootSource DocsRootSource,
    IReadOnlyList<ScaffoldedFile> Files)
{
    /// <summary>What <c>docs init</c> did about the catalogue review gates.</summary>
    public ReviewGatesOutcome? ReviewGates { get; init; }
}
