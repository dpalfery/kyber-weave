namespace KyberWeave.Tests;

/// <summary>Keeps <c>IgnoreMarkupTests</c> building until it calls <see cref="ClaimTestDocuments"/> directly; delete this file then.</summary>
internal static class ClaimExtractionTests
{
    internal static Core.Docs.Model.DocumentModel Document(string body, string? rawMarkdown = null, int bodyStartLine = 9) =>
        ClaimTestDocuments.Document(body, rawMarkdown, bodyStartLine);
}
