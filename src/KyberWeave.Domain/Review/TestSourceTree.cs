namespace KyberWeave.Core.Review;

/// <summary>The repository root and the source files under it, as <c>TestClassPlanning</c> reads them.</summary>
/// <param name="Repository">The full path of the repository root.</param>
/// <param name="SourcesByName">Every <c>.cs</c> file under the root, keyed by its file name without the extension; the first found wins.</param>
public sealed record TestSourceTree(string Repository, IReadOnlyDictionary<string, string> SourcesByName);
