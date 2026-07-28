namespace KyberWeave.Core.Docs.Export;

/// <summary>Counts from one export run.</summary>
public sealed record DocGraphExportResult(int NodeCount, int EdgeCount, string NodesPath, string EdgesPath);
