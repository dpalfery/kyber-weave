namespace KyberWeave.Core.Docs.Mcp;

/// <summary>Reads and writes the local undo record of the MCP wiring repair.</summary>
public interface IMcpWiringReceipts
{
    /// <summary>Reads the receipt, or answers with an empty one when none can be read.</summary>
    /// <param name="repositoryRoot">The repository whose receipt is read.</param>
    /// <returns>The receipt; empty when it is absent, unreadable or does not match the schema.</returns>
    McpWiringReceipt Load(string repositoryRoot);

    /// <summary>
    /// Writes the receipt, and reports why it could not be written instead of failing the run: a
    /// caller may already have restored files by the time it rewrites the record of them, and one that
    /// threw here would lose the report of the work it did.
    /// </summary>
    /// <param name="repositoryRoot">The repository the receipt belongs to.</param>
    /// <param name="records">Every record the receipt should hold.</param>
    /// <returns>Null when written, or the reason it was not.</returns>
    string? Save(string repositoryRoot, IReadOnlyList<McpWiringReceiptRecord> records);
}
