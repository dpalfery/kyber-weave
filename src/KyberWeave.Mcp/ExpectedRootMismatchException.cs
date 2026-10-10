namespace KyberWeave.Mcp;

/// <summary>
/// Thrown when a client-asserted <c>--expect-root</c> value does not match the resolved MCP root.
/// </summary>
/// <remarks>
/// Distinct from unbound-root failures so startup can report <c>KW-MCP-ROOT-002</c> without
/// matching message text that may appear in ordinary path diagnostics.
/// </remarks>
public sealed class ExpectedRootMismatchException : InvalidOperationException
{
    public ExpectedRootMismatchException()
    {
    }

    public ExpectedRootMismatchException(string message)
        : base(message)
    {
    }

    public ExpectedRootMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
