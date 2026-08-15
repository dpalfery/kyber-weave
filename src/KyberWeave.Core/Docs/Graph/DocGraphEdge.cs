namespace KyberWeave.Core.Docs.Graph;

/// <summary>One directed relationship in the documentation graph projection.</summary>
public sealed record DocGraphEdge(string Label, string From, string To);
