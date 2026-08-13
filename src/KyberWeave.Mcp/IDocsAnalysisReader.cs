using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Mcp;

/// <summary>Read-only repository analysis used by the conversational MCP tools.</summary>
public interface IDocsAnalysisReader
{
    /// <summary>Runs bounded analysis over the repository's current configured corpus.</summary>
    DocumentationAnalysisResult Analyze();

    /// <summary>Looks up every managed glossary sense for one term.</summary>
    GlossaryLookupResult LookupGlossary(string term);
}
