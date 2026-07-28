namespace KyberWeave.Core.CodeGraph;

/// <summary>One resolved code symbol from the CodeGraph index.</summary>
/// <param name="Id">Stable node id from the index.</param>
/// <param name="Kind">Indexed kind, e.g. <c>class</c>, <c>method</c>, or <c>route</c>.</param>
/// <param name="Name">Bare symbol name.</param>
/// <param name="QualifiedName">Fully qualified name when available.</param>
/// <param name="FilePath">Repository-relative path of the declaring file.</param>
/// <param name="Language">Indexed language, e.g. <c>csharp</c> or <c>typescript</c>. A bare
/// symbol name can collide across languages, so callers disambiguating a match need it.</param>
/// <param name="StartLine">1-based line the symbol is declared on.</param>
public sealed record CodeGraphNode(
    string Id,
    string Kind,
    string Name,
    string QualifiedName,
    string FilePath,
    string Language,
    int StartLine)
{
    /// <summary>The symbol's location as <c>file:line</c>.</summary>
    public string Location => StartLine > 0 ? $"{FilePath}:{StartLine}" : FilePath;
}
