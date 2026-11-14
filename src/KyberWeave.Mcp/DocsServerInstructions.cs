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
        docs_answer_question_from_repo_documentation first, before grepping or reading files under docs/ or other markdown. It
        returns one answer with a confidence score and a single source line. If it
        reports a miss, the subject is likely undocumented and searching files directly is
        appropriate.

        Also available: docs_find_documents_citing_code_symbol (which documents describe a code symbol, before changing
        it), docs_get_coding_standard_for_technology (how this repository writes code in a named technology, and
        the rule ids that standard states, before writing or reviewing code in it),
        docs_look_up_glossary_term_definition (what a term means here), docs_find_duplicates_conflicts_ambiguous_terms (duplicated or
        contradictory documentation), docs_validate_governed_document_schema and docs_detect_drift_between_documents_and_code (the same schema and drift
        gates as kyber-weave docs validate and docs drift, without a shell), and docs_get_source_repository_and_revision
        (which repository and revision the documentation comes from).

        Two tools change files, and only plan files: docs_finalize_approved_plan_and_write_task_file
        sets an approved Draft plan to Ready and writes its task file, and
        docs_archive_plan_after_closeout_is_complete archives a plan once its closeout evidence is
        complete. One tool delivers a finished run: docs_push_run_branch_and_open_draft_pull_request
        pushes the run branch and opens or updates its draft pull request on GitHub. All three need
        projectPath and refuse, changing nothing, when their checks fail.

        Every response starts with a provenance line naming the repository root, its branch, its
        git directory, the short revision, whether the working tree is dirty, and the document
        count. Two answers with the same git directory are the same repository, even when their
        roots differ because they are separate worktrees.

        Responses also report a broken code index rather than guessing around it. If a response
        carries the line STATUS: TOOL_FAILURE, the tool could not read the CodeGraph index its
        code joins come from: stop and fix that instead of answering without it. A repository that
        sets stop-on-tool-failure: false gets a warning line instead, and the answer still comes
        back.
        """;

    /// <summary>Applies the instructions to the server options.</summary>
    public static void Configure(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServerInstructions = Text;
    }
}
