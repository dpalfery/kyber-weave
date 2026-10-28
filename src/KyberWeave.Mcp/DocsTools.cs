using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The documentation retrieval tools. Output is capped the way <c>codegraph_explore</c>
/// caps its own: a retrieval tool that returns a whole corpus is a slower grep.
/// </summary>
[McpServerToolType]
public sealed partial class DocsTools(DocumentIndexHost host)
{
    /// <summary>Hard ceiling on any single section, so one huge section cannot crowd out
    /// every other document in the response.</summary>
    private const int SectionCharCap = 6000;

    /// <summary>The range a caller's <c>charBudget</c> is clamped to on the coding-standard
    /// surface. Below the floor the answer would be an identity block with no prose, and
    /// above the ceiling one call could pull the corpus.</summary>
    private const int MinCharBudget = 1000;
    private const int MaxCharBudget = 120_000;

    /// <summary>Rule ids listed in one standard's index. A standard that cites more than this
    /// is summarized rather than dumped: a hundred ids is a slower grep, not an answer.</summary>
    private const int RuleIdCap = 50;

    /// <summary>What a citation of an id the index did not list is replaced with, so the cap
    /// the response states is a cap it keeps, and the omission is visible rather than silent.</summary>
    private const string WithheldRuleId = "[rule id withheld]";

    /// <summary>
    /// The reply to a call that named no technology: a caller error rather than a lookup miss,
    /// which is why it names both forms a caller may have in hand — the technology itself, and
    /// the configuration registry's property name for it that the repository instructions use.
    /// </summary>
    private const string StandardUsage = """
        Name the technology to answer for, or the property name this repository's instructions
        use for it. Both forms work: 'csharp' and 'csharp-coding-standard' are the same request,
        as is '<csharp-coding-standard>'.
        """;

    /// <summary>Code joins listed per document.</summary>
    private const int JoinCap = 20;

    /// <summary>Hard caps for the conversational analysis surface.</summary>
    private const int AnalysisCandidateCap = 20;
    private const int AnalysisCharCap = 12000;
    private const int AnalysisEvidenceCap = 8;

    private static readonly HashSet<string> AnalysisKindNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(AnalysisRuleKind.Duplicate),
        nameof(AnalysisRuleKind.Conflict),
        nameof(AnalysisRuleKind.Terminology)
    };

    private readonly DocumentIndexHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly IDocsAnalysisReader? _analysisReader;

    public DocsTools(DocumentIndexHost host, IDocsAnalysisReader analysisReader)
        : this(host)
    {
        _analysisReader = analysisReader ?? throw new ArgumentNullException(nameof(analysisReader));
    }

    [McpServerTool(Name = "docs_explore", ReadOnly = true, OpenWorld = false)]
    [Description("""
        DOCS FIRST — call before grepping or reading anything under docs/ or other markdown.
        Searches this repository documentation (guides, runbooks, decision records, design
        notes) and returns the best-matching sections of each document in ONE call, ranked, plus the code
        they reference as symbol -> file:line. Treat the returned sections as already read; do
        NOT reopen those files. Retired and archived pages are excluded, so the answer is
        current guidance.

        Use for: how X works, how to install/run/configure it, what a command, flag, or rule
        id means, why a design decision was made, is X documented, and before starting a task
        a runbook or design note might cover. Query is a plain question or a name: command,
        flag, rule id, component, or doc id.

        A miss says how many documents were searched: the subject is undocumented, so grep
        is now fine. Do not use for what the code does right now; read the code instead.
        """)]
    public string Explore(
        [Description("The question in plain words, or a command, flag, rule id, feature or component name, or document id.")] string query,
        [Description("""
            Maximum documents to return (1-20). Defaults to 5. Lowering this deepens each
            result rather than merely shortening the list, because charBudget is shared
            across the documents returned; maxDocs=1 usually fits a whole document.
            """)]
        int maxDocs = 5,
        [Description("""
            Total characters of prose across all returned documents (1000-120000). Defaults
            to 12000. Sections that did not fit are named in the response; re-query with a
            larger budget to retrieve them.
            """)]
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        IReadOnlyList<DocumentHit> hits = index.Explore(query, maxDocs, charBudget);

        if (hits.Count == 0)
        {
            // Saying so plainly is the whole point of the relevance floor. An agent told
            // to use this before grepping needs an explicit signal that it may now grep.
            string docsIndex = Path.Combine(_host.DocsRelativeRoot, "README.md").Replace('\\', '/');
            return Lead(provenance, $"""
                No document in the governed corpus scored above the relevance threshold for '{query}'.
                {index.DocumentCount} documents were considered.

                This is a real miss, not an empty corpus. Either the subject is undocumented, or the
                question uses vocabulary the documentation does not. Try naming a component, a doc-id
                ("webui/architecture"), or a code symbol; or fall back to reading {docsIndex}.
                """);
        }

        StringBuilder sb = new();
        sb.Append("Top ").Append(hits.Count).Append(" of ").Append(index.DocumentCount)
          .Append(" documents, best first (relevance ")
          .Append(hits[0].Score.ToString("0.00", CultureInfo.InvariantCulture))
          .Append(" … ")
          .Append(hits[^1].Score.ToString("0.00", CultureInfo.InvariantCulture))
          .AppendLine(").");
        if (!index.CodeGraphAvailable)
        {
            sb.AppendLine("Warning: no CodeGraph index was readable, so code joins are unresolved.");
        }

        foreach (DocumentHit hit in hits)
        {
            sb.AppendLine();
            // This surface states no cap and prints no index, so it withholds nothing: every
            // id the document cites is one the response is telling the caller about.
            AppendIdentity(sb, hit, WithheldRuleIds.None);
            AppendExcerpt(sb, hit.Excerpt);
            AppendJoins(sb, hit);
        }

        return Lead(provenance, sb.ToString());
    }

    [McpServerTool(Name = "docs_for_symbol", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when about to rename, move, delete, or change the signature of a class, method,
        property, or endpoint: call BEFORE the change. Returns every document that claims the symbol in its
        'code-refs' frontmatter, with each document's identity and code joins as
        symbol -> file:line: exactly the docs you must update with the change. Also use
        after a change to find stale docs, or to check whether a symbol is documented.

        Empty result: no document claims it, so nothing to update.
        """)]
    public string ForSymbol(
        [Description("A bare or fully qualified symbol name, e.g. 'DataProtectionHealthCheck' or 'KyberWeave.Core.Docs.Search.DocumentIndex'. Both forms resolve.")] string symbol)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        IReadOnlyList<DocumentHit> hits = index.ForSymbol(symbol);

        if (hits.Count == 0)
        {
            return Lead(
                provenance,
                $"No document declares '{symbol}' in its code-refs frontmatter. " +
                "It may be undocumented, or documented only in prose.");
        }

        StringBuilder sb = new();
        sb.Append(hits.Count).Append(hits.Count == 1 ? " document declares '" : " documents declare '")
          .Append(symbol).AppendLine("' in code-refs.");

        foreach (DocumentHit hit in hits)
        {
            sb.AppendLine();
            AppendIdentity(sb, hit, WithheldRuleIds.None);
            AppendJoins(sb, hit);
        }

        return Lead(provenance, sb.ToString());
    }

    [McpServerTool(Name = "docs_analysis_candidates", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Documentation quality audit. Returns a paged list of duplicate claims, conflicting
        statements, and ambiguous terms across the docs, each with line-addressable evidence.

        Use when docs may contradict themselves, when consolidating or deduplicating pages,
        when choosing which of two statements is canonical, or before a release or large
        docs edit. To answer a question, call docs_explore instead.

        Candidates are review evidence only; nothing is applied. Empty: nothing pending.
        """)]
    public string AnalysisCandidates(
        [Description("Limits results to one kind of finding: 'duplicate', 'conflict', or 'terminology'. Omit to return every kind.")] string? kind = null,
        [Description("Opaque candidate id taken from the 'next cursor' line of a previous response, to continue after that item. Omit for the first page.")] string? cursor = null,
        [Description("Maximum candidates to return (1-20). Defaults to 20; larger values are capped at 20.")] int limit = 20,
        [Description("Maximum response characters (up to 12000). Defaults to 12000. Evidence per candidate is capped independently, so one candidate cannot consume the whole page.")] int charBudget = 12000)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        int effectiveBudget = Math.Max(0, Math.Clamp(charBudget, 0, AnalysisCharCap) - provenance.Length - 1);

        if (_analysisReader is null)
            return Lead(provenance, "Documentation analysis is unavailable in this host.");

        if (!TryParseKind(kind, out AnalysisRuleKind? parsedKind))
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"Unknown documentation-analysis kind '{kind}'. Use duplicate, conflict, or terminology.",
                    effectiveBudget));
        }

        DocumentationAnalysisResult result;
        try
        {
            result = _analysisReader.Analyze();
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"Documentation analysis is unavailable: {exception.Message}",
                    effectiveBudget));
        }

        AnalysisCandidate[] ordered =
        [
            .. result.Candidates
                .Where(candidate => parsedKind is null || candidate.Kind == parsedKind)
                .OrderBy(candidate => candidate.Kind)
                .ThenBy(candidate => candidate.Term, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
        ];
        int start = ResolveCursor(ordered, cursor);
        if (start < 0)
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"The analysis cursor '{cursor}' is no longer present. Start again without a cursor.",
                    effectiveBudget));
        }

        int effectiveLimit = Math.Clamp(limit, 1, AnalysisCandidateCap);
        StringBuilder sb = new(Math.Min(effectiveBudget, 4096));
        AppendAnalysisMetrics(sb, result);

        if (start >= ordered.Length)
        {
            sb.AppendLine(parsedKind is null
                ? "No documentation-analysis candidates are pending."
                : $"No {parsedKind.Value.ToString().ToUpperInvariant()} candidates are pending.");
            return Lead(provenance, CapToBudget(sb.ToString(), effectiveBudget));
        }

        int emitted = 0;
        string? lastCursor = null;
        while (emitted < effectiveLimit && start + emitted < ordered.Length)
        {
            AnalysisCandidate candidate = ordered[start + emitted];
            bool moreAfter = start + emitted + 1 < ordered.Length;
            string cursorFooter = moreAfter ? $"next cursor: {candidate.Id}{Environment.NewLine}" : string.Empty;
            int available = effectiveBudget - sb.Length - cursorFooter.Length;
            if (available <= 0) break;

            string block = FormatCandidate(candidate);
            sb.Append(CapToBudget(block, available));
            emitted++;
            lastCursor = candidate.Id;

            if (block.Length > available) break;
        }

        if (emitted == 0)
            return Lead(
                provenance,
                CapToBudget(
                    sb.AppendLine("The response budget is too small for a candidate.").ToString(),
                    effectiveBudget));

        if (start + emitted < ordered.Length && lastCursor is not null)
        {
            string footer = $"next cursor: {lastCursor}{Environment.NewLine}";
            if (sb.Length + footer.Length <= effectiveBudget) sb.Append(footer);
        }

        return Lead(provenance, CapToBudget(sb.ToString(), effectiveBudget));
    }

    [McpServerTool(Name = "docs_glossary", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Looks up ONE term in the managed glossary: its approved, proposed, and rejected
        senses with definitions, scopes, and aliases.

        Use when a word is overloaded or unclear in this codebase, before naming a new
        component or introducing vocabulary, and when writing docs that must match
        established wording. Pass the term alone, not a sentence.

        Unknown term: empty result, no sense declared.
        """)]
    public string Glossary(
        [Description("A single glossary term, matched case-insensitively. Pass the term alone, not a sentence.")] string term)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        int effectiveBudget = Math.Max(0, AnalysisCharCap - provenance.Length - 1);

        if (_analysisReader is null)
            return Lead(provenance, "The managed documentation glossary is unavailable in this host.");
        if (string.IsNullOrWhiteSpace(term))
            return Lead(provenance, "Provide a glossary term to look up.");

        GlossaryLookupResult result;
        try
        {
            result = _analysisReader.LookupGlossary(term);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"The managed documentation glossary is unavailable: {exception.Message}",
                    effectiveBudget));
        }

        if (result.Senses.Count == 0)
        {
            return Lead(
                provenance,
                CapToBudget(
                    $"No glossary senses are declared for '{result.Term.ReplaceLineEndings(" ")}'.",
                    effectiveBudget));
        }

        StringBuilder sb = new();
        sb.Append(result.Senses.Count)
          .Append(result.Senses.Count == 1 ? " glossary sense for '" : " glossary senses for '")
          .Append(result.Term.ReplaceLineEndings(" "))
          .AppendLine("'.");
        foreach (GlossarySense sense in result.Senses
                     .OrderBy(sense => sense.Status)
                     .ThenBy(sense => sense.Id, StringComparer.Ordinal)
                     .Take(AnalysisCandidateCap))
        {
            sb.AppendLine();
            sb.Append("sense: ").AppendLine(sense.Id);
            sb.Append("status: ").AppendLine(sense.Status.ToString().ToUpperInvariant());
            sb.Append("definition: ").AppendLine(
                string.IsNullOrWhiteSpace(sense.Definition)
                    ? "(not yet defined)"
                    : CapToBudget(sense.Definition.ReplaceLineEndings(" "), 1000));
            sb.Append("scope: ").AppendLine(
                sense.Scopes.Count == 0
                    ? "(none)"
                    : CapToBudget(
                        string.Join("; ", sense.Scopes.Take(AnalysisEvidenceCap).Select(scope => CapToBudget(scope, 240))),
                        1000));
            sb.Append("aliases: ").AppendLine(
                sense.Aliases.Count == 0
                    ? "(none)"
                    : CapToBudget(
                        string.Join("; ", sense.Aliases.Take(AnalysisEvidenceCap).Select(alias => CapToBudget(alias, 240))),
                        1000));
        }

        if (result.Senses.Count > AnalysisCandidateCap)
            sb.AppendLine(CultureInfo.InvariantCulture, $"… {result.Senses.Count - AnalysisCandidateCap} more senses omitted by the hard cap.");
        return Lead(provenance, CapToBudget(sb.ToString(), effectiveBudget));
    }

    [McpServerTool(Name = "docs_validate", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when you need the same schema conformance gate as kyber-weave docs validate after
        editing governed documentation — especially after writing or revising a plan under
        docs/plans — and before claiming PLAN_READY or PLAN_FINALIZED. There is no
        repository-path argument: compare the provenance root to your checkout before trusting
        the verdict. Default mergeReady false is the planning gate while a plan stays in the
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
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        DiagnosticReport report = new();
        if (!TryLoadHostConfiguration(report, out KyberWeaveConfig config))
            return FormatGateResponse(provenance, report, charBudget);

        try
        {
            DocumentSet set = _host.LoadDocumentSet();
            report.AddRange(DocsSchemaGate.Validate(
                _host.RepositoryRoot,
                config,
                set,
                () => new DocumentLoader(_host.RepositoryRoot, config.Ontology),
                mergeReady,
                mergeReady ? @base : null).Items);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                exception.Message,
                "docs validate",
                Hint: "Fix the repository configuration or corpus access, then call docs_validate again."));
        }

        return FormatGateResponse(provenance, report, charBudget);
    }

    [McpServerTool(Name = "docs_drift", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when you need the same entity-drift gate as kyber-weave docs drift after
        documentation edits that cite code symbols or API routes, alongside docs_validate
        before PLAN_READY or PLAN_FINALIZED. There is no repository-path argument: compare
        the provenance root to your checkout. A missing CodeGraph index fails rather than
        passing silently. Warnings such as KW-DOC-DRIFT-003 do not fail the gate but still
        appear. Returns provenance, pass or fail with counts, then findings.
        """)]
    public string DocsDrift(
        [Description("""
            Total characters of finding text after the verdict line (1000-120000). Defaults
            to 12000. The provenance and verdict lines are always emitted whole.
            """)]
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);
        DiagnosticReport report = new();
        if (!TryLoadHostConfiguration(report, out KyberWeaveConfig _))
            return FormatGateResponse(provenance, report, charBudget);

        try
        {
            DocumentSet set = _host.LoadDocumentSet();
            report.AddRange(
                new DocDriftLinter(_host.CreateCodeGraphResolver()).Validate(set).Items);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                exception.Message,
                "docs drift",
                Hint: "Fix the repository configuration or corpus access, then call docs_drift again."));
        }

        return FormatGateResponse(provenance, report, charBudget);
    }

    /// <summary>
    /// Reports the provenance of the corpus this server answers from, with no query. This is
    /// the cheap session-start check an agent calls before trusting any other docs tool.
    /// </summary>
    [McpServerTool(Name = "docs_status", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Which repository is this server answering from? Returns one line: absolute repo
        root, short HEAD revision (with dirty marker), and document count. No arguments.

        Use when a docs answer names a file missing from your checkout, or when several
        servers may be bound to different repositories. Not needed otherwise: every docs
        tool already leads with this line.

        Zero documents is not an error; the line still reports the root.
        """)]
    public string DocsStatus()
    {
        DocumentIndex index = _host.Current();
        return Provenance(index);
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
    [McpServerTool(Name = "agent_coding_standards", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Use when you are about to write or review code in one of this repository's stacks and
        need the local answer rather than the one you shipped with: how C# is written here, how
        a test is written here, what a rule id in this repository means for the code in front of
        you. Returns the standard that answers for the technology — its title, path, id, declared
        technology, status, and the rule ids it states — with the sections that match the topic
        you name. The ids come first, so a budget can never cut them away.

        Pass the technology ('csharp', 'test') or the property name the repository instructions
        use for it ('csharp-coding-standard'). A technology this repository declares no standard
        for is an explicit miss that names the ones it does declare, and a standard whose status
        is not current still answers and says so.
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
            """)] int charBudget = DocumentIndex.DefaultCharBudget)
    {
        DocumentIndex index = _host.Current();
        string provenance = Provenance(index);

        // The index folds an unnamed technology into an empty result, so a caller error would
        // otherwise be answered with "no standard for ''". Usage prose is the honest reply to
        // a call that named nothing.
        if (string.IsNullOrWhiteSpace(technology))
            return Lead(provenance, StandardUsage);

        string requested = technology.Trim().ReplaceLineEndings(" ");
        IReadOnlyList<DocumentHit> hits = index.CodingStandards(
            requested,
            topic,
            Math.Clamp(charBudget, MinCharBudget, MaxCharBudget));

        if (hits.Count == 0)
            return Lead(provenance, StandardMiss(index, requested));

        StringBuilder sb = new();
        AppendStandardCount(sb, requested, hits);
        foreach (DocumentHit hit in hits)
        {
            sb.AppendLine();

            // The ids are read before the identity block rather than where they are printed,
            // because the identity block echoes the frontmatter title — document text the same
            // cap governs. The index still follows the identity, where the block order puts it.
            IReadOnlyList<string> ruleIds = RuleIds(hit.Document);
            WithheldRuleIds withheld = WithheldRuleIds.For(ruleIds);
            AppendStandardIdentity(sb, hit, withheld);
            AppendRuleIdIndex(sb, ruleIds);

            if (hit.Excerpt.Sections.Count == 0)
            {
                AppendEmptyExcerpt(sb, hit, topic, withheld);
                continue;
            }

            AppendExcerpt(sb, withheld.Elide(hit.Excerpt), "topic");
        }

        return Lead(provenance, sb.ToString());
    }

    /// <summary>
    /// The provenance header for a response answered from <paramref name="index"/>. Every
    /// user-visible return leads with this so a caller can tell which corpus answered.
    /// </summary>
    private string Provenance(DocumentIndex index) =>
        CorpusProvenance.Line(_host.RepositoryRoot, index.DocumentCount, _host.CorpusBuilds);

    /// <summary>
    /// Puts the provenance line first, then the body. The header is emitted whole and the
    /// body is what gets clipped, so the tell is never the part truncated away.
    /// </summary>
    private static string Lead(string provenance, string body) =>
        provenance + "\n" + body;

    private bool TryLoadHostConfiguration(DiagnosticReport report, out KyberWeaveConfig config)
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(_host.RepositoryRoot, null);
        if (!result.Success)
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                result.Error ?? "Failed to load kyber-weave.yml.",
                "kyber-weave.yml",
                result.ConfigPath));
            config = KyberWeaveConfig.ProductDefaults;
            return false;
        }

        config = result.Config!;
        foreach (string warning in config.Review.Warnings)
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigWarningCode,
                Severity.Warning,
                warning,
                "kyber-weave.yml",
                result.ConfigPath,
                Hint: "Check the spelling against the gate keys, or remove the key."));
        }

        return true;
    }

    private static string FormatGateResponse(string provenance, DiagnosticReport report, int charBudget)
    {
        int effectiveBudget = Math.Clamp(charBudget, MinCharBudget, MaxCharBudget);
        string verdict = report.HasErrors ? "fail" : "pass";
        string resultLine = string.Create(
            CultureInfo.InvariantCulture,
            $"result: {verdict} ({report.Errors} errors, {report.Warnings} warnings)");

        List<Diagnostic> errors =
        [
            .. report.Items
                .Where(item => item.Severity >= Severity.Error)
                .OrderByDescending(item => item.Severity)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.FilePath ?? string.Empty, StringComparer.Ordinal)
        ];
        List<Diagnostic> warnings =
        [
            .. report.Items
                .Where(item => item.Severity == Severity.Warning)
                .OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.FilePath ?? string.Empty, StringComparer.Ordinal)
        ];

        int reserveForWarning = 0;
        if (warnings.Count > 0)
            reserveForWarning = Math.Min(
                effectiveBudget / 3,
                FormatGateFinding(warnings[0]).Length + Environment.NewLine.Length);

        StringBuilder findingsText = new();
        int emitted = 0;
        int errorBudget = Math.Max(0, effectiveBudget - reserveForWarning);

        foreach (Diagnostic diagnostic in errors)
        {
            string line = FormatGateFinding(diagnostic) + Environment.NewLine;
            if (findingsText.Length + line.Length > errorBudget)
            {
                if (emitted > 0)
                    break;

                int remaining = errorBudget - findingsText.Length;
                if (remaining > 0)
                {
                    string finding = FormatGateFinding(diagnostic);
                    int maxFindingChars = remaining - Environment.NewLine.Length;
                    if (maxFindingChars > 0 && finding.Length > maxFindingChars)
                        finding = finding[..maxFindingChars];

                    if (finding.Length > 0)
                    {
                        findingsText.AppendLine(finding);
                        emitted++;
                    }
                }

                break;
            }

            findingsText.Append(line);
            emitted++;
        }

        foreach (Diagnostic diagnostic in warnings)
        {
            string line = FormatGateFinding(diagnostic) + Environment.NewLine;
            if (findingsText.Length + line.Length > effectiveBudget)
                break;

            findingsText.Append(line);
            emitted++;
        }

        int totalFindings = errors.Count + warnings.Count;
        int omitted = totalFindings - emitted;
        if (omitted > 0)
        {
            findingsText.Append(CultureInfo.InvariantCulture, $"{omitted} findings omitted.");
            findingsText.AppendLine();
        }

        string body = findingsText.Length == 0
            ? resultLine
            : resultLine + "\n" + findingsText.ToString().TrimEnd('\r', '\n');

        return Lead(provenance, body);
    }

    private static string FormatGateFinding(Diagnostic diagnostic)
    {
        StringBuilder sb = new();
        sb.Append(diagnostic.Code)
          .Append(" [")
          .Append(diagnostic.Severity.ToString().ToUpperInvariant())
          .Append("] ");
        if (!string.IsNullOrEmpty(diagnostic.FilePath))
        {
            sb.Append(diagnostic.FilePath.Replace('\\', '/'));
            sb.Append(": ");
        }

        sb.Append(diagnostic.Message.ReplaceLineEndings(" "));
        if (!string.IsNullOrWhiteSpace(diagnostic.Hint))
            sb.Append(CultureInfo.InvariantCulture, $" hint: {diagnostic.Hint.ReplaceLineEndings(" ")}");

        return sb.ToString();
    }

    private static bool TryParseKind(string? kind, out AnalysisRuleKind? parsedKind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            parsedKind = null;
            return true;
        }

        string trimmed = kind.Trim();
        if (AnalysisKindNames.Contains(trimmed)
            && Enum.TryParse(trimmed, ignoreCase: true, out AnalysisRuleKind parsed))
        {
            parsedKind = parsed;
            return true;
        }

        parsedKind = null;
        return false;
    }

    private static int ResolveCursor(IReadOnlyList<AnalysisCandidate> candidates, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;

        for (int index = 0; index < candidates.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(candidates[index].Id, cursor)) return index + 1;
        }

        return -1;
    }

    private static void AppendAnalysisMetrics(StringBuilder sb, DocumentationAnalysisResult result)
    {
        AnalysisMetrics metrics = result.Metrics;
        sb.AppendLine("metrics:");
        sb.Append("  extracted claims: ").AppendLine(metrics.ExtractedClaims.ToString(CultureInfo.InvariantCulture));
        sb.Append("  graph comparisons: ").AppendLine(metrics.GraphComparisons.ToString(CultureInfo.InvariantCulture));
        sb.Append("  lexical comparisons: ").AppendLine(metrics.LexicalComparisons.ToString(CultureInfo.InvariantCulture));
        sb.Append("  embedding comparisons: ").AppendLine(metrics.EmbeddingComparisons.ToString(CultureInfo.InvariantCulture));
        sb.Append("  candidates: graph ").Append(metrics.GraphCandidates.ToString(CultureInfo.InvariantCulture))
          .Append(", lexical ").Append(metrics.LexicalCandidates.ToString(CultureInfo.InvariantCulture))
          .Append(", embedding ").AppendLine(metrics.EmbeddingCandidates.ToString(CultureInfo.InvariantCulture));
        sb.Append("  truncated: ").AppendLine(metrics.Truncated ? "yes" : "no");
        AppendOptionalMetric(sb, result, "embeddingCacheHits", "embedding cache hits");
        AppendOptionalMetric(sb, result, "embeddingCacheMisses", "embedding cache misses");
        AppendOptionalMetric(sb, result, "embeddingPromptTokens", "embedding prompt tokens");
        AppendOptionalMetric(sb, result, "embeddingTotalTokens", "embedding total tokens");
        sb.Append("  diagnostics: ")
          .Append(result.Diagnostics.Errors.ToString(CultureInfo.InvariantCulture)).Append(" errors, ")
          .Append(result.Diagnostics.Warnings.ToString(CultureInfo.InvariantCulture)).Append(" warnings, ")
          .Append(result.Diagnostics.Infos.ToString(CultureInfo.InvariantCulture)).AppendLine(" info");
        foreach (Diagnostic diagnostic in result.Diagnostics.Items
                     .Where(item => item.Severity is Severity.Warning or Severity.Error or Severity.Critical)
                     .Take(3))
        {
            sb.Append("  ").Append(diagnostic.Code).Append(" [")
              .Append(diagnostic.Severity.ToString().ToUpperInvariant()).Append("]: ")
              .AppendLine(CapToBudget(diagnostic.Message.ReplaceLineEndings(" "), 240));
        }
        sb.AppendLine();
    }

    private static void AppendOptionalMetric(
        StringBuilder sb,
        DocumentationAnalysisResult result,
        string key,
        string label)
    {
        if (!result.Diagnostics.Metrics.TryGetValue(key, out object? value)) return;
        sb.Append("  ").Append(label).Append(": ").AppendLine(
            Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static string FormatCandidate(AnalysisCandidate candidate)
    {
        StringBuilder sb = new();
        sb.Append("candidate: ").AppendLine(candidate.Id);
        sb.Append("kind: ").AppendLine(candidate.Kind.ToString().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(candidate.Term))
            sb.Append("term: ").AppendLine(Truncate(candidate.Term.ReplaceLineEndings(" "), 240));
        sb.Append("evidence: ").AppendLine(string.Join(
            "; ",
            candidate.Claims
                .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
                .ThenBy(claim => claim.StartLine)
                .Take(AnalysisEvidenceCap)
                .Select(claim => $"{claim.FilePath}:{claim.StartLine}-{claim.EndLine}")));
        sb.Append("scores: lexical ")
          .Append(candidate.Score.Lexical.ToString("0.000", CultureInfo.InvariantCulture))
          .Append(", semantic ")
          .Append(candidate.Score.Semantic?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a")
          .Append(", graph ")
          .AppendLine(candidate.Score.Graph.ToString("0.000", CultureInfo.InvariantCulture));
        if (candidate.Sources.Count > 0)
        {
            sb.Append("sources: ").AppendLine(string.Join(
                ", ",
                candidate.Sources
                    .OrderBy(source => source)
                    .Select(source => source.ToString().ToUpperInvariant())));
        }
        if (candidate.Verdict is not null)
        {
            sb.Append("verdict: ").Append(candidate.Verdict.Label.ToString().ToUpperInvariant())
              .Append(" at ")
              .AppendLine(candidate.Verdict.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
        }

        foreach (Claim claim in candidate.Claims
                     .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
                     .ThenBy(claim => claim.StartLine)
                     .Take(AnalysisEvidenceCap))
        {
            sb.Append("  - ").Append(claim.FilePath).Append(':')
              .Append(claim.StartLine.ToString(CultureInfo.InvariantCulture)).Append('-')
              .Append(claim.EndLine.ToString(CultureInfo.InvariantCulture))
              .Append(" [").Append(claim.DocumentIdentity).AppendLine("]");
            sb.Append("    ").AppendLine(Truncate(claim.Text.ReplaceLineEndings(" "), 240));
        }
        if (candidate.Claims.Count > AnalysisEvidenceCap)
            sb.Append("  … ").Append(candidate.Claims.Count - AnalysisEvidenceCap).AppendLine(" more evidence locations.");
        sb.AppendLine();
        return sb.ToString();
    }

    private static bool IsExpectedReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException;

    private static string CapToBudget(string text, int cap) =>
        text.Length <= cap ? text : text[..cap];

    /// <summary>The shape a rule id is written in — <c>KW-DOC-SPEC-007</c> — which is what a
    /// coding standard cites and what suppressions and code-scanning alerts key on.</summary>
    [GeneratedRegex(@"\bKW-[A-Z0-9]+(?:-[A-Z0-9]+)*\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RuleIdPattern();

    /// <summary>
    /// Says which standard answered and how current it is, before anything else, so a draft
    /// answering for a technology is visible in the first line of the body rather than only in
    /// the identity block below it.
    /// </summary>
    private static void AppendStandardCount(
        StringBuilder sb,
        string requested,
        IReadOnlyList<DocumentHit> hits)
    {
        sb.Append(hits.Count)
          .Append(hits.Count == 1 ? " standard for technology '" : " standards for technology '")
          .Append(requested)
          .Append("' (status ")
          .Append(string.Join(", ", StatusNames(hits)))
          .AppendLine(").");
    }

    /// <summary>
    /// Each answering standard's status, deduped in document order, so the count line names
    /// every status present exactly once.
    /// </summary>
    private static List<string> StatusNames(IReadOnlyList<DocumentHit> hits)
    {
        List<string> names = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (DocumentHit hit in hits)
        {
            string name = StatusName(hit.Document.Status);
            if (seen.Add(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// A status in the spelling the ontology authors it in, so a caller can look the word up.
    /// </summary>
    /// <remarks>
    /// Every other member is one word and uppercases to its declared spelling, but
    /// <see cref="DocStatus.NeedsReview"/> is declared as <c>needs-review</c> in frontmatter,
    /// written that way by the scaffold, and reported that way by validation — so it would
    /// otherwise come back as <c>NEEDSREVIEW</c>, a state no document, rule id, or
    /// configuration key uses, and one a reader cannot look up.
    /// </remarks>
    private static string StatusName(DocStatus status) => status switch
    {
        DocStatus.NeedsReview => "NEEDS-REVIEW",
        _ => status.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// The identity of the standard that answered, with a warning when it is not current and
    /// the technology key it answers for.
    /// </summary>
    /// <remarks>
    /// The technology is printed as the corpus declares it rather than as the caller spelled
    /// it: that declared value is what a later lookup is defined against, and echoing the
    /// caller's spelling would hide a standard that answers under a different one. The status
    /// is named in a warning as well as in the identity line, because a caller about to follow
    /// a draft has to know it is not the settled answer, and a status field two lines down is
    /// the part of a response a reader skims past.
    /// </remarks>
    /// <param name="sb">The response being built; the identity block is appended to it.</param>
    /// <param name="hit">The standard that answered.</param>
    /// <param name="withheld">
    /// The ids this response may not print, applied to the echoed title — the frontmatter title
    /// is document text, and a standard that states more ids than the index lists can state one
    /// of them in its own title.
    /// </param>
    private static void AppendStandardIdentity(
        StringBuilder sb,
        DocumentHit hit,
        WithheldRuleIds withheld)
    {
        DocumentModel doc = hit.Document;
        if (doc.Status is not DocStatus.Current)
        {
            sb.Append("Warning: this standard's status is ").Append(StatusName(doc.Status))
              .AppendLine(", so it is not settled guidance for the stack yet — confirm it before following it.");
        }

        AppendIdentity(sb, hit, withheld);

        string declared = doc.Frontmatter.Technology?.Trim() ?? string.Empty;
        sb.Append("technology: ").AppendLine(
            declared.Length == 0 ? "(undeclared)" : declared.ReplaceLineEndings(" "));
    }

    /// <summary>
    /// The ids the standard states, ahead of the prose that cites them, capped at
    /// <see cref="RuleIdCap"/> with the remainder counted rather than listed.
    /// </summary>
    /// <remarks>
    /// Ahead of the prose because the ids are the part of a standard a caller acts on: they
    /// are what a finding cites, what a suppression names, and what the repository's own
    /// documentation indexes. Clipping the standard's text is recoverable by asking again with
    /// a larger budget; losing the ids is not, because the caller does not know they existed.
    /// </remarks>
    private static void AppendRuleIdIndex(StringBuilder sb, IReadOnlyList<string> ruleIds)
    {
        if (ruleIds.Count == 0)
        {
            // Naming the absence is the honest answer: an empty index line would read as a
            // standard that states ids the tool failed to find.
            sb.AppendLine("rule ids: (this standard states no rule ids)");
            return;
        }

        sb.Append("rule ids: ").AppendLine(string.Join(" · ", ruleIds.Take(RuleIdCap)));
        if (ruleIds.Count > RuleIdCap)
        {
            sb.Append("… ").Append(ruleIds.Count - RuleIdCap)
              .AppendLine(" more rule ids are stated in this standard and are not listed above.");
        }
    }

    /// <summary>
    /// The rule ids a standard states, deduped, in the order the document cites them.
    /// </summary>
    /// <remarks>
    /// Document order rather than a sort: the index is meant to read the way the standard
    /// reads, so a caller can find the cited id where the prose cites it. An id written twice
    /// is one entry, because the index answers "which ids does this standard state", not "how
    /// often does it say so".
    /// <para>
    /// The frontmatter title is scanned too, because the identity block echoes it and a standard
    /// can state an id there — an index built from the body alone would then omit an id the same
    /// response prints, and a caller keying a suppression from the index would miss it. Its ids
    /// are appended after the sections' rather than prepended, even though the title precedes
    /// the body in the file: the fifty-id listing window the cap opens is a window over the body
    /// the cap exists to summarize, and a title id taking a slot at the front would let
    /// frontmatter displace a section id from what the caller actually reads. An id the title
    /// repeats keeps its section position, so a title adds only ids nothing else states.
    /// </para>
    /// </remarks>
    private static List<string> RuleIds(DocumentModel document)
    {
        List<string> ids = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (DocumentSection section in document.Sections.OrderBy(section => section.LineNumber))
        {
            foreach (Match match in RuleIdPattern().Matches(section.Heading + "\n" + section.Body))
            {
                if (seen.Add(match.Value)) ids.Add(match.Value);
            }
        }

        foreach (Match match in RuleIdPattern().Matches(document.Frontmatter.Title ?? string.Empty))
        {
            if (seen.Add(match.Value)) ids.Add(match.Value);
        }

        return ids;
    }

    /// <summary>
    /// The explicit miss: no standard here answers for that technology, the caller learns what
    /// this repository does declare, and how many documents were considered — so the response
    /// is a real miss rather than a lookup that failed quietly.
    /// </summary>
    private static string StandardMiss(DocumentIndex index, string requested)
    {
        IReadOnlyList<string> declared = index.CodingStandardTechnologies();
        string offer = declared.Count == 0
            ? "This repository declares no coding standard for any technology."
            : "This repository declares a standard for: " + string.Join(", ", declared) + ".";

        return "No coding standard for '" + requested + "' is declared here. " + offer + "\n"
               + index.DocumentCount.ToString(CultureInfo.InvariantCulture)
               + " documents were considered, so this is a real miss rather than a lookup that "
               + "failed: that technology is not standardised in this repository, and the answer "
               + "for it is not written down here.";
    }

    /// <summary>The document's identity block. Every value it prints comes from the document.</summary>
    /// <param name="sb">The response being built; the identity block is appended to it.</param>
    /// <param name="hit">The document being identified.</param>
    /// <param name="withheld">
    /// The ids this response may not print, applied to the title. The title is document text
    /// like any other, so a response that states a cap on ids owes it the same elision — and
    /// the pass-through for a surface that caps nothing says so here rather than by omission.
    /// </param>
    private static void AppendIdentity(StringBuilder sb, DocumentHit hit, WithheldRuleIds withheld)
    {
        DocumentModel doc = hit.Document;
        sb.Append("### ").AppendLine(withheld.Elide(doc.Frontmatter.Title ?? doc.RelativePath));
        sb.Append("path: ").AppendLine(doc.RelativePath);
        sb.Append("id: ").AppendLine(doc.Frontmatter.Id ?? "(none)");
        sb.Append("doc-type: ").Append(DocTypeLabel(doc.DocType))
          .Append("  status: ").Append(StatusName(doc.Status))
          .Append("  component: ").AppendLine(doc.Frontmatter.Component ?? "(none)");
    }

    /// <summary>
    /// The document type in the spelling the ontology authors it in, so the identity block
    /// quotes the type rather than the enum member's name.
    /// </summary>
    /// <remarks>
    /// Every other member is one word and uppercases to its declared spelling, but a
    /// <see cref="DocType.CodingStandard"/> declared as <c>coding-standard</c> in frontmatter,
    /// written that way by the scaffold, and reported that way by validation would otherwise
    /// come back as <c>CODINGSTANDARD</c> — a type no document, rule id, or configuration key
    /// uses, and one a reader cannot look up.
    /// </remarks>
    private static string DocTypeLabel(DocType docType) => docType switch
    {
        DocType.CodingStandard => "CODING-STANDARD",
        _ => docType.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// Why a standard contributed no prose, said in the terms of what actually happened.
    /// </summary>
    /// <remarks>
    /// Two different things leave an empty excerpt, and the excerpt cannot tell them apart —
    /// the document's own section count can. A body of prose with no <c>##</c> headings has
    /// nothing to excerpt and is said to be exactly that. A body that <em>does</em> carry
    /// headings and returns none cleared the relevance gate is a topic that matched nothing,
    /// and blaming the document for it would send a caller away from a standard sitting right
    /// there — while returning the sections instead would answer a narrower question with
    /// everything. Naming the headings that did not match says what the document holds without
    /// giving up the topic as the question being answered.
    /// </remarks>
    /// <param name="sb">The response being built; the explanation is appended to it.</param>
    /// <param name="hit">The standard that returned no prose.</param>
    /// <param name="topic">The topic the caller named, when it named one.</param>
    /// <param name="withheld">
    /// The ids this response may not print, applied to the headings it names. They are read
    /// straight from the document here rather than from the excerpt — an excerpt with no
    /// sections carries no headings to elide — so this is the one echo site the excerpt's own
    /// elision would never reach.
    /// </param>
    private static void AppendEmptyExcerpt(
        StringBuilder sb,
        DocumentHit hit,
        string? topic,
        WithheldRuleIds withheld)
    {
        if (hit.Document.Sections.Count == 0)
        {
            // A standard that is prose and no ## headings: saying so beats an identity
            // block followed by nothing, which reads as an empty document.
            sb.AppendLine(
                "no sections: this standard's body carries no ## headings, so there is "
                + "nothing to excerpt from it.");
            return;
        }

        string requested = topic?.Trim().ReplaceLineEndings(" ") ?? string.Empty;
        sb.Append("no section matches the topic '").Append(requested)
          .AppendLine("', so nothing is excerpted for it.");

        List<string> headings = hit.Document.Sections
            .OrderBy(section => section.LineNumber)
            .Select(section => withheld.Elide(section.Heading))
            .Where(heading => heading.Length > 0)
            .ToList();

        if (headings.Count > 0) AppendOmittedHeadingsMarker(sb, headings, "topic");
    }

    /// <summary>
    /// Names headings the document holds that were not returned, so a caller learns what else
    /// is there without opening the file.
    /// </summary>
    /// <param name="sb">The response being built; the marker line is appended to it.</param>
    /// <param name="headings">
    /// The heading names to list, in document order. Eliding them is the caller's job: a heading
    /// is document text, and two of the three places this response echoes a heading — the
    /// excerpt's omitted list and the no-matching-topic path — do not pass through a section.
    /// </param>
    /// <param name="requestLabel">
    /// What the caller asked with — <c>query</c> from <see cref="Explore"/>, <c>topic</c> from
    /// <see cref="AgentCodingStandards"/> — so the marker names the thing the caller actually
    /// passed.
    /// </param>
    private static void AppendOmittedHeadingsMarker(
        StringBuilder sb,
        IEnumerable<string> headings,
        string requestLabel)
    {
        sb.Append("[also in this document, not matching this ").Append(requestLabel)
          .Append(": ").Append(string.Join(" · ", headings))
          .AppendLine("]");
    }

    /// <summary>
    /// Writes the prose, then names whatever did not fit. Naming the omissions is what
    /// lets a caller decide to ask for more instead of giving up and opening the file.
    /// </summary>
    /// <param name="sb">
    /// The response being built; the prose and any omission marker are appended to it.
    /// </param>
    /// <param name="excerpt">
    /// The sections this document's share of the caller's budget allowed, together with
    /// whatever was left out and why. A surface that caps rule ids hands over an excerpt already
    /// put through <see cref="WithheldRuleIds"/>, which is what keeps the headings named below —
    /// carried by the excerpt rather than by its sections — from printing an id the index
    /// declined to name.
    /// </param>
    /// <param name="requestLabel">
    /// What the caller asked with — <c>query</c> from <see cref="Explore"/>, <c>topic</c> from
    /// <see cref="AgentCodingStandards"/> — so the marker for an off-topic section names the
    /// thing the caller actually passed.
    /// </param>
    private static void AppendExcerpt(
        StringBuilder sb,
        DocumentExcerpt excerpt,
        string requestLabel = "query")
    {
        foreach (DocumentSection section in excerpt.Sections)
        {
            if (section.Heading.Length > 0) sb.Append("## ").AppendLine(section.Heading);
            sb.AppendLine(Truncate(section.Body, SectionCharCap));
        }

        if (excerpt.Sections.Count == 0) return;

        if (excerpt.IsComplete)
        {
            sb.AppendLine("[complete document]");
        }
        else if (excerpt.OmittedHeadings.Count > 0)
        {
            // Only suggest a bigger budget when the budget is what actually bit. A section
            // dropped for irrelevance will not come back however much budget is offered.
            if (excerpt.BudgetExhausted)
            {
                sb.Append("[omitted for space, ask again with a larger charBudget: ")
                  .Append(string.Join(" · ", excerpt.OmittedHeadings))
                  .AppendLine("]");
            }
            else
            {
                AppendOmittedHeadingsMarker(sb, excerpt.OmittedHeadings, requestLabel);
            }
        }
    }

    private static void AppendJoins(StringBuilder sb, DocumentHit hit)
    {
        if (hit.CodeJoins.Count == 0) return;

        sb.AppendLine("code joins:");
        foreach (CodeJoin join in hit.CodeJoins.Take(JoinCap))
        {
            sb.Append("  ").Append(join.Reference).Append(" -> ");

            if (join.Location.Length == 0)
            {
                sb.AppendLine("(unresolved)");
                continue;
            }

            sb.Append(join.Location).Append(" [").Append(join.Kind).Append(']');

            // A bare symbol name can name several symbols. Say when the choice was a
            // guess, so the reader verifies instead of trusting it.
            if (!join.InSourceRoot) sb.Append(" (outside this document's source-root)");
            if (join.OtherCandidates > 0) sb.Append(" (+").Append(join.OtherCandidates).Append(" other same-named)");

            sb.AppendLine();
        }

        if (hit.CodeJoins.Count > JoinCap)
        {
            sb.Append("  … ").Append(hit.CodeJoins.Count - JoinCap).AppendLine(" more.");
        }
    }

    private static string Truncate(string text, int cap) =>
        text.Length <= cap ? text : text[..cap] + $"\n… truncated at {cap} characters.";

    /// <summary>
    /// The ids a standard states that its own index declined to name, and the one place the rule
    /// for what the response may print is applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The index names <see cref="RuleIdCap"/> ids and counts the remainder rather than listing
    /// it, and the "… N more rule ids … are not listed above" line is a claim about the whole
    /// response. A standard that cites more ids than the cap is unusual, but a response repeats
    /// the document's own words in several places and only one of them is a section: the
    /// frontmatter title, the heading of a section the budget cut, and the headings of the
    /// sections a topic did not match are all document text. Left alone, any of them can print,
    /// a few hundred characters below that line, exactly the ids it says were not named — the
    /// omission would then be a claim the same response contradicts.
    /// </para>
    /// <para>
    /// So the elision is carried as one value through the standard's whole response rather than
    /// applied at the one site where a leak was noticed. Every member that echoes document text
    /// takes it, which is what makes the invariant hold rather than merely hold today: a new
    /// echo site has to hold this value to emit anything. The marker keeps the omission visible,
    /// so a reader sees that an id was cited rather than that a sentence lost a word.
    /// </para>
    /// <para>
    /// The frontmatter <c>id</c>, <c>path</c> and <c>technology</c> are emitted as they stand,
    /// deliberately: each is a key the caller reuses in a later call — a doc id to look up, a
    /// path to open, a technology to ask for again — and a key rewritten to
    /// <see cref="WithheldRuleId"/> would not resolve. None of the three can hold a rule id;
    /// they carry a document id, a filesystem path, and a stack name.
    /// </para>
    /// </remarks>
    private sealed class WithheldRuleIds
    {
        private readonly IReadOnlySet<string> _ids;

        private WithheldRuleIds(IReadOnlySet<string> ids) => _ids = ids;

        /// <summary>
        /// Nothing is withheld, so a citation prints as the document wrote it. The surfaces with
        /// no rule-id index — <see cref="Explore"/> and <see cref="ForSymbol"/> — name every id
        /// they echo and so have nothing to withhold; passing this says so at the call site
        /// instead of leaving it to a default the next echo site could inherit by accident.
        /// </summary>
        public static WithheldRuleIds None { get; } = new(new HashSet<string>(StringComparer.Ordinal));

        /// <summary>
        /// The ids <paramref name="ruleIds"/> states past <see cref="RuleIdCap"/> — the ones the
        /// index counted rather than listed — or nothing when it states no more than the cap.
        /// </summary>
        /// <remarks>
        /// Derived from the same deduped, document-ordered list the index was built from, so the
        /// ids withheld here and the ids counted by the "… N more" line are the same ids by
        /// construction rather than by two extractions that happen to agree.
        /// </remarks>
        public static WithheldRuleIds For(IReadOnlyList<string> ruleIds) =>
            ruleIds.Count <= RuleIdCap
                ? None
                : new WithheldRuleIds(new HashSet<string>(ruleIds.Skip(RuleIdCap), StringComparer.Ordinal));

        /// <summary>
        /// <paramref name="text"/> with every citation of a withheld id replaced by
        /// <see cref="WithheldRuleId"/>, so an id the index did not name is printed nowhere.
        /// </summary>
        public string Elide(string text) =>
            _ids.Count == 0
                ? text
                : RuleIdPattern().Replace(
                    text,
                    match => _ids.Contains(match.Value) ? WithheldRuleId : match.Value);

        /// <summary>
        /// The excerpt with the same rule applied to what it carries and to what it only names:
        /// the headings and prose of its sections, and the headings it reports as omitted.
        /// </summary>
        public DocumentExcerpt Elide(DocumentExcerpt excerpt) =>
            _ids.Count == 0
                ? excerpt
                : excerpt with
                {
                    Sections =
                    [
                        .. excerpt.Sections.Select(section => section with
                        {
                            Heading = Elide(section.Heading),
                            Body = Elide(section.Body)
                        })
                    ],
                    OmittedHeadings = [.. excerpt.OmittedHeadings.Select(heading => Elide(heading))]
                };
    }
}
