using System.ComponentModel;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Search;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The documentation retrieval tools: a thin MCP adapter over <see cref="IDocsQueryService"/>,
/// <see cref="IPlanLifecycleService"/>, <see cref="IRunDeliveryService"/> and <see cref="IToolingCheckService"/>. It owns the tool names,
/// descriptions and schemas, and the per-call <c>projectPath</c> routing; every answer is composed
/// by the services.
/// </summary>
[McpServerToolType]
public sealed partial class DocsTools
{
    private readonly IDocsQueryService _query;
    private readonly IPlanLifecycleService _planLifecycle;
    private readonly IRunDeliveryService _delivery;
    private readonly IToolingCheckService _toolingCheck;
    private readonly ITaskPromptService _prompts;
    private readonly IDocsHostConfiguration _hostConfiguration;
    private readonly string _repositoryRoot;
    private readonly DocsRootRegistry? _roots;

    /// <summary>Tools over <paramref name="query"/> that also answer for other worktrees through <paramref name="roots"/>.</summary>
    /// <param name="query">The documentation query service for the root this server was started with.</param>
    /// <param name="planLifecycle">The plan lifecycle service for that root.</param>
    /// <param name="delivery">The run delivery service for that root.</param>
    /// <param name="toolingCheck">The tooling check service for that root.</param>
    /// <param name="prompts">Builds one task's prompt, so the conductor learns which agent to dispatch.</param>
    /// <param name="hostConfiguration">Loads that root's configuration, which holds the prompt budget.</param>
    /// <param name="repositoryRoot">The root these tools answer for.</param>
    /// <param name="roots">Resolves a per-call <c>projectPath</c>, or null when this host serves one root only.</param>
    public DocsTools(
        IDocsQueryService query,
        IPlanLifecycleService planLifecycle,
        IRunDeliveryService delivery,
        IToolingCheckService toolingCheck,
        ITaskPromptService prompts,
        IDocsHostConfiguration hostConfiguration,
        string repositoryRoot,
        DocsRootRegistry? roots = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _planLifecycle = planLifecycle ?? throw new ArgumentNullException(nameof(planLifecycle));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _toolingCheck = toolingCheck ?? throw new ArgumentNullException(nameof(toolingCheck));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _hostConfiguration = hostConfiguration ?? throw new ArgumentNullException(nameof(hostConfiguration));
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _roots = roots;
    }

    [McpServerTool(Name = "docs_answer_question_from_repo_documentation", ReadOnly = true, OpenWorld = false)]
    [Description("""
        DOCS FIRST — call before grepping or reading repository documentation under docs/ or other markdown.
        Searches this repository's guides, runbooks, decision records, and design
        notes and returns one answer with a confidence score and a single source line. Treat
        the returned passage as already read; do NOT reopen that file. A long section returns its
        best-matching passage and a cursor for the rest. Retired and archived
        pages are excluded.

        Use for: how X works, how to install/run/configure it, what a command, flag, or rule
        id means, why a design decision was made, is X documented, and before starting a task
        a runbook or design note might cover. Query is a plain question or a name: command,
        flag, rule id, component, or doc id.

        A miss says how many documents were searched: the subject is undocumented, so grep
        under docs/ is now fine. Do not use for what the code does right now; read the code instead.
        """)]
    public string Explore(
        [Description("The question in plain words, or a command, flag, rule id, feature or component name, or document id.")] string query,
        [Description("""
            Continuation token from a prior incomplete answer for the same question. Omit on
            the first call; repeat the same question with this value to read the next span.
            """)]
        string? cursor = null,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.Explore(query, cursor)) is { } redirected) return redirected;
        return _query.Explore(query, cursor);
    }

    [McpServerTool(Name = "docs_find_documents_citing_code_symbol", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when about to rename, move, delete, or change the signature of a class, method,
        property, or endpoint: call BEFORE the change. Returns every document that claims the symbol in its
        'code-refs' frontmatter, with each document's identity and code joins as
        symbol -> file:line: exactly the docs you must update with the change. Also use
        after a change to find stale docs, or to check whether a symbol is documented.

        Empty result: no document claims it, so nothing to update.
        """)]
    public string ForSymbol(
        [Description("A bare or fully qualified symbol name, e.g. 'DataProtectionHealthCheck' or 'KyberWeave.Core.Docs.Search.DocumentIndex'. Both forms resolve.")] string symbol,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.ForSymbol(symbol)) is { } redirected) return redirected;
        return _query.ForSymbol(symbol);
    }

    [McpServerTool(Name = "docs_find_duplicates_conflicts_ambiguous_terms", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Documentation quality audit. Returns a paged list of duplicate claims, conflicting
        statements, and ambiguous terms across the docs, each with line-addressable evidence.

        Use when docs may contradict themselves, when consolidating or deduplicating pages,
        when choosing which of two statements is canonical, or before a release or large
        docs edit. To answer a question, call docs_answer_question_from_repo_documentation instead.

        Candidates are review evidence only; nothing is applied. Empty: nothing pending.
        """)]
    public string AnalysisCandidates(
        [Description("Limits results to one kind of finding: 'duplicate', 'conflict', or 'terminology'. Omit to return every kind.")] string? kind = null,
        [Description("Opaque candidate id taken from the 'next cursor' line of a previous response, to continue after that item. Omit for the first page.")] string? cursor = null,
        [Description("Maximum candidates to return (1-20). Defaults to 20; larger values are capped at 20.")] int limit = 20,
        [Description("Maximum response characters (up to 12000). Defaults to 12000. Evidence per candidate is capped independently, so one candidate cannot consume the whole page.")] int charBudget = 12000,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.AnalysisCandidates(kind, cursor, limit, charBudget)) is { } redirected) return redirected;
        return _query.AnalysisCandidates(kind, cursor, limit, charBudget);
    }

    [McpServerTool(Name = "docs_look_up_glossary_term_definition", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Looks up ONE term in the managed glossary: its approved, proposed, and rejected
        senses with definitions, scopes, and aliases.

        Use when a word is overloaded or unclear in this codebase, before naming a new
        component or introducing vocabulary, and when writing docs that must match
        established wording. Pass the term alone, not a sentence.

        Unknown term: empty result, no sense declared.
        """)]
    public string Glossary(
        [Description("A single glossary term, matched case-insensitively. Pass the term alone, not a sentence.")] string term,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.Glossary(term)) is { } redirected) return redirected;
        return _query.Glossary(term);
    }

    [McpServerTool(Name = "docs_validate_governed_document_schema", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when you need the same schema conformance gate as kyber-weave docs validate after
        editing governed documentation — especially after writing or revising a plan under
        docs/plans — and before claiming PLAN_READY or PLAN_FINALIZED. Pass
        projectPath when you work in a linked worktree; compare the provenance root to your checkout
        before trusting the verdict. Default mergeReady false is the planning gate while a plan stays in the
        active plans folder. Returns provenance, pass or fail with counts, then findings.
        """)]
    public string DocsValidate(
        [Description("""
            When true, runs merge-ready checks including open-work rules such as
            KW-DOC-LIFECYCLE-003 and archive closeout. Default false for active plan work.
            """)]
        bool mergeReady = false,
        [Description("""
            Git ref for archive-closeout scope when mergeReady is true. Defaults to
            origin/HEAD, matching the CLI.
            """)]
        string @base = "origin/HEAD",
        [Description("""
            Total characters of finding text after the verdict line (1000-120000). Defaults
            to 12000. The provenance and verdict lines are always emitted whole.
            """)]
        int charBudget = DocumentIndex.DefaultCharBudget,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.DocsValidate(mergeReady, @base, charBudget)) is { } redirected) return redirected;
        return _query.DocsValidate(mergeReady, @base, charBudget);
    }

    [McpServerTool(Name = "docs_detect_drift_between_documents_and_code", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when you need the same entity-drift gate as kyber-weave docs drift after
        documentation edits that cite code symbols or API routes, alongside docs_validate_governed_document_schema
        before PLAN_READY or PLAN_FINALIZED. Pass projectPath when you work in
        a linked worktree; compare the provenance root to your checkout. A missing CodeGraph index fails rather than
        passing silently. Warnings such as KW-DOC-DRIFT-003 do not fail the gate but still
        appear. Returns provenance, pass or fail with counts, then findings.
        """)]
    public string DocsDrift(
        [Description("""
            Total characters of finding text after the verdict line (1000-120000). Defaults
            to 12000. The provenance and verdict lines are always emitted whole.
            """)]
        int charBudget = DocumentIndex.DefaultCharBudget,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.DocsDrift(charBudget)) is { } redirected) return redirected;
        return _query.DocsDrift(charBudget);
    }

    /// <summary>
    /// Reports the provenance of the corpus this server answers from, with no query. This is
    /// the cheap session-start check an agent calls before trusting any other docs tool.
    /// </summary>
    [McpServerTool(Name = "docs_get_source_repository_and_revision", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Which repository is this server answering from? Returns one line: absolute repo
        root, short HEAD revision (with dirty marker), and document count. Pass projectPath to ask about a linked worktree.

        Use when a docs answer names a file missing from your checkout, or when several
        servers may be bound to different repositories. Not needed otherwise: every docs
        tool already leads with this line.

        Zero documents is not an error; the line still reports the root.
        """)]
    public string DocsStatus(
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.DocsStatus()) is { } redirected) return redirected;
        return _query.DocsStatus();
    }

    /// <summary>
    /// Answers for one stack: which of this repository's written standards is the local answer
    /// for that technology, and what it says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller is an agent that ships with its own idea of how C# or tests are written. A
    /// confident answer that turns out to be another repository's standard is worse than a
    /// plain miss, because nothing in the response looks wrong — so the identity block names
    /// the corpus's own declared technology and the status is named in a warning rather than
    /// left for the reader to infer.
    /// </para>
    /// <para>
    /// The rule-id index is emitted ahead of the prose on purpose. Follow-up work hangs off
    /// the ids a standard states, and an index that depends on how much prose fitted into the
    /// caller's budget is an index nobody can rely on.
    /// </para>
    /// </remarks>
    [McpServerTool(Name = "docs_get_coding_standard_for_technology", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when you are about to write or review code in one of this repository's stacks and
        need the local answer rather than the one you shipped with: how C# is written here, how
        a test is written here, what a rule id in this repository means for the code in front of
        you. Returns one bundle for the technology: the rule ids it states, the shared rules, the
        parent technology's rules and skills when the standard names a parent, the technology's
        rules and skills, then the standard's sections that match the topic you name. The ids
        come first, so a budget can never cut them away; rules are never cut, skills are cut
        before standard sections, and anything cut is named in a closing note.

        Pass the technology ('csharp', 'test') or the property name the repository instructions
        use for it ('csharp-coding-standard'). A technology this repository declares no standard
        for is an explicit miss that names the ones it does declare, and a standard whose status
        is not current still answers and says so. A topic that matches no section still returns
        the standard, with a one-line note that the topic selected nothing, so the prose is
        there rather than a list of headings to go and read.
        """)]
    public string AgentCodingStandards(
        [Description("""
            The technology to answer for — 'csharp', 'test' — or the property name the repository
            instructions use for it, where 'csharp-coding-standard' and '<csharp-coding-standard>'
            are the same request as 'csharp'. Matched case-insensitively.
            """)] string technology,
        [Description("""
            What you are doing in that stack ('naming', 'error handling', 'pull request review'),
            to bring back the sections that answer it. Omit it for the standard itself.
            """)] string? topic = null,
        [Description("""
            Total characters of prose across the standards returned (1000-120000). Defaults to
            12000. A value outside the range is clamped to the nearer bound, so a budget below
            the floor behaves as the floor rather than as an empty answer.
            """)] int charBudget = DocumentIndex.DefaultCharBudget,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.AgentCodingStandards(technology, topic, charBudget)) is { } redirected) return redirected;
        return _query.AgentCodingStandards(technology, topic, charBudget);
    }

    /// <summary>
    /// Tells the conductor which agent to dispatch for one task, without returning the prompt text.
    /// </summary>
    /// <remarks>
    /// The conductor dispatches a task id and a few header lines; a hook on that dispatch replaces the
    /// task-id line with the whole prompt before the worker starts. The conductor therefore needs the
    /// agent's name, not the prompt, and this tool answers with exactly that: who to dispatch, the
    /// difficulty in use and who rated it, the technology, and anything the prompt budget left out.
    /// On a harness whose hook cannot rewrite the dispatch it also writes the prompt file and answers with
    /// its path, so this tool is not read-only.
    /// </remarks>
    [McpServerTool(Name = "docs_build_task_prompt_for_agent_dispatch", ReadOnly = false, OpenWorld = false)]
    [Description("""
        Use when you are the conductor about to dispatch a worker for a task: call it with the task id
        and it names the agent to dispatch. Answers with the agent, the difficulty in use and its
        source, the technology the task is written in, and anything the prompt budget left out. It
        never returns the prompt text: dispatch the task id, and the dispatch hook expands it into the
        full prompt for the worker. A prompt that cannot be built is refused with the command that
        repairs its task file, so no worker starts with only a task id.

        Name a technology instead of a task id for a dispatch that belongs to no task, such as a
        research request.

        Name the harness too. Where its hook cannot rewrite the dispatch, the prompt is written to one
        file under the run's scratch folder and this reply carries the path; name that path in the
        dispatch and tell the worker to read it first.
        """)]
    public string BuildTaskPrompt(
        [Description("""
            The task id from the plan or specification, written any natural way: T1, t1, task 1, 1.
            Omit it only when you name a technology for a dispatch that belongs to no task.
            """)] string? taskId = null,
        [Description("The technology a dispatch with no task names, such as 'csharp'. Ignored when a task id is given.")] string? technology = null,
        [Description("The plan or specification the task is in, when the checkout has more than one active plan.")] string? plan = null,
        [Description("A file holding the results earlier tasks reported, when this is a retry.")] string? priorResults = null,
        [Description("How many attempts at this task already failed; the retry's step-up reads it.")] int failedAttempts = 0,
        [Description("""
            The harness you dispatch from, such as 'antigravity' or 'claude'. On a harness whose hook cannot
            rewrite the dispatch, the prompt is written to the run's scratch folder and this reply gives its path.
            Omit it when unknown; no file is written then.
            """)] string? harness = null,
        [Description("Absolute path of your worktree; omit for the server's checkout.")] string? projectPath = null)
    {
        if (Redirect(projectPath, tools => tools.BuildTaskPrompt(taskId, technology, plan, priorResults, failedAttempts, harness)) is { } redirected)
            return redirected;

        DiagnosticReport report = new();
        if (!_hostConfiguration.TryLoad(_repositoryRoot, report, out KyberWeaveConfig config))
        {
            return _query.Provenance()
                   + "\nprompt: none\nreason: this checkout's configuration could not be loaded. Run 'kyber-weave docs validate .' in it.";
        }

        TaskPromptResult prompt = _prompts.Build(
            new TaskPromptRequest(
                _repositoryRoot,
                taskId,
                plan,
                PriorResultsPath: priorResults,
                Technology: technology,
                FailedAttempts: failedAttempts,
                Harness: harness),
            config);

        return FormatTaskPrompt(prompt);
    }

    private string FormatTaskPrompt(TaskPromptResult prompt)
    {
        StringBuilder text = new();
        text.Append(_query.Provenance()).Append('\n');
        if (!prompt.Built)
        {
            text.Append("prompt: none\nreason: ").Append(prompt.Refusal);
            return text.ToString();
        }

        text.Append("prompt: built\n");
        text.Append("agent: ").Append(prompt.Agent ?? "none").Append('\n');
        text.Append("difficulty: ").Append(prompt.Difficulty ?? "none");
        if (prompt.DifficultySource is not null)
            text.Append(" (source: ").Append(prompt.DifficultySource).Append(')');
        text.Append('\n');
        text.Append("technology: ").Append(prompt.Technology ?? "none").Append('\n');
        text.Append("left out: ").Append(prompt.LeftOut.Count == 0 ? "none" : string.Join("; ", prompt.LeftOut)).Append('\n');
        if (prompt.PromptFile is not null)
        {
            text.Append("prompt file: ").Append(prompt.PromptFile).Append('\n');
            text.Append("\nThe prompt text is not returned. This harness's hook cannot rewrite the dispatch, so the prompt is in the file above: name that path in the dispatch and tell the worker to read it first. A worker that starts without it runs 'kyber-weave docs packet <task id>'.");
            return text.ToString();
        }

        text.Append("prompt file: none\n");
        text.Append("\nThe prompt text is not returned. Dispatch the agent above with the task id and the dispatch hook inserts it; a worker that starts without it runs 'kyber-weave docs packet <task id>'.");
        return text.ToString();
    }

    /// <summary>
    /// Routes a per-call <c>projectPath</c> to the tools bound to that worktree, or refuses it. Null when the call is for
    /// this server's own root, so the caller answers it.
    /// </summary>
    private string? Redirect(string? projectPath, Func<DocsTools, string> call)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;

        if (_roots is null)
            return _query.Provenance() + "\nprojectPath was refused: this server answers for one checkout only.";

        if (!_roots.TryResolve(projectPath, out DocsTools? target, out string? refusal))
            return _query.Provenance() + "\nprojectPath was refused: " + refusal;

        return target is null ? null : call(target);
    }
}
