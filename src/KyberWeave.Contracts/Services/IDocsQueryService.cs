namespace KyberWeave.Contracts.Services;

/// <summary>
/// The documentation retrieval and gate answers the docs MCP tools return, for the one repository root the service is
/// bound to. Every method returns the tool's complete response text; the MCP adapter owns tool names, descriptions and
/// the per-call <c>projectPath</c> routing.
/// </summary>
public interface IDocsQueryService
{
    /// <summary>Answers a question from the governed corpus: one passage, a confidence score and a source line.</summary>
    /// <param name="query">The question, or a command, flag, rule id, component or document id.</param>
    /// <param name="cursor">The continuation token of a prior incomplete answer, or null.</param>
    string Explore(string query, string? cursor);

    /// <summary>Lists the documents that cite a code symbol.</summary>
    /// <param name="symbol">A bare or fully qualified symbol name.</param>
    string ForSymbol(string symbol);

    /// <summary>Pages through duplicate, conflict and terminology findings.</summary>
    /// <param name="kind">One kind of finding, or null for every kind.</param>
    /// <param name="cursor">The candidate id to continue after, or null.</param>
    /// <param name="limit">Maximum candidates, capped by the service.</param>
    /// <param name="charBudget">Maximum response characters, capped by the service.</param>
    string AnalysisCandidates(string? kind, string? cursor, int limit, int charBudget);

    /// <summary>Looks up every managed glossary sense for one term.</summary>
    /// <param name="term">The term, matched case-insensitively.</param>
    string Glossary(string term);

    /// <summary>Runs the schema conformance gate over the corpus.</summary>
    /// <param name="mergeReady">True to include the merge-ready checks.</param>
    /// <param name="baseRef">The git ref for archive-closeout scope when <paramref name="mergeReady"/> is true.</param>
    /// <param name="charBudget">Total characters of finding text after the verdict line.</param>
    string DocsValidate(bool mergeReady, string baseRef, int charBudget);

    /// <summary>Runs the entity-drift gate between the documents and the code.</summary>
    /// <param name="charBudget">Total characters of finding text after the verdict line.</param>
    string DocsDrift(int charBudget);

    /// <summary>Reports the provenance line of the corpus, with no query.</summary>
    string DocsStatus();

    /// <summary>Answers for one technology with the repository's own written standard.</summary>
    /// <param name="technology">The technology or the configuration property name that names it.</param>
    /// <param name="topic">What the caller is doing in that stack, or null for the standard itself.</param>
    /// <param name="charBudget">Total characters of prose, clamped by the service.</param>
    string AgentCodingStandards(string technology, string? topic, int charBudget);

    /// <summary>Loads one KW skill: its body, the folder it sits in, and the paths of its reference files.</summary>
    /// <param name="name">The skill's name, as a task prompt's KW skills list or a coding standard's gives it.</param>
    string LoadSkill(string name);

    /// <summary>The provenance line every response leads with, without loading the corpus when it is not yet loaded.</summary>
    string Provenance();
}
