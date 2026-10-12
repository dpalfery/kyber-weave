using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The server-level instructions a client puts in the model's context for the whole session.
/// A tool description is only read when the model is already weighing tools; this is read
/// before it starts, which is where "try the documentation first" has to be said.
/// </summary>
public static class DocsServerInstructions
{
    public const string Text = """
        Tools for this repository's written documentation: guides, runbooks, decision records,
        and design notes.

        For a question about how something in this repository works, how to run or configure
        it, what a command, flag, or rule means, or whether something is documented, call
        docs_explore first, before grepping or reading files under docs/ or other markdown. It
        returns the most relevant sections, ranked, with the code locations they refer to. If it
        reports a miss, the subject is likely undocumented and searching files directly is
        appropriate.

        Also available: docs_for_symbol (which documents describe a code symbol, before changing
        it), docs_glossary (what a term means here), docs_analysis_candidates (duplicated or
        contradictory documentation), and docs_status (which repository and revision the
        documentation comes from).
        """;

    /// <summary>Applies the instructions to the server options.</summary>
    public static void Configure(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServerInstructions = Text;
    }
}
