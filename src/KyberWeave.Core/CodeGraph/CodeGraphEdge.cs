namespace KyberWeave.Core.CodeGraph;

/// <summary>One directed relationship from the CodeGraph index.</summary>
/// <param name="SourceId">Stable id of the source code node.</param>
/// <param name="TargetId">Stable id of the target code node.</param>
/// <param name="Kind">Indexed relationship kind, such as <c>calls</c> or <c>references</c>.</param>
public sealed record CodeGraphEdge(string SourceId, string TargetId, string Kind);
