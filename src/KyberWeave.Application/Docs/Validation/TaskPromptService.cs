using System.Globalization;
using System.Text;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Text;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Builds one task's prompt: the task's own text, the host facts, the shared rules, the parent
/// technology's rules and skills, then the technology's rules, skills and standard, each inside a named tag,
/// cut to the host's prompt budget.
/// </summary>
/// <param name="tasks">Reads and checks the task file the prompt comes from.</param>
/// <param name="hostFacts">Builds the host facts part, with the task's gates resolved by technology.</param>
/// <param name="files">Reads the prior-results file a retry names.</param>
/// <param name="standardsFor">
/// The one standards lookup the docs tool also answers from: a technology to its shared rules, parent rules and
/// skills, rules, skills and standard, or null when the host has no folder for it.
/// </param>
public sealed class TaskPromptService(
    IPlanTaskService tasks,
    IPacketHostFacts hostFacts,
    IFileSystem files,
    Func<string, StandardsBundle?> standardsFor) : ITaskPromptService
{
    /// <summary>What the last part says when the budget cut nothing.</summary>
    public const string NothingLeftOut = "Nothing was left out.";

    /// <summary>The folder, relative to the repository root, a fallback harness's prompt file is written under.</summary>
    public const string ScratchFolder = ".agent_scratchpad";

    /// <summary>The prompt file's name inside the task's scratch folder.</summary>
    public const string PromptFileName = "prompt.md";

    private const string ParentSkillsUnit = "parent-skills";
    private const string SkillsUnit = "skills";
    private const string StandardUnitPrefix = "standard:";

    /// <summary>
    /// The developer tier for a task: <c>min(hard, rated difficulty + failed attempts)</c>. A task with no rating
    /// is a medium task, as the task file writes it, so a dispatch that belongs to no task also starts at medium.
    /// </summary>
    /// <param name="difficulty">The rating in use: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    /// <param name="failedAttempts">How many attempts at this task already failed.</param>
    public static string DeveloperTier(string? difficulty, int failedAttempts) =>
        PlanTaskAgentPick.DeveloperTier(difficulty, failedAttempts);

    private static IReadOnlyList<string> DeveloperTiers => PlanTaskAgentPick.DeveloperTiers;

    /// <summary>
    /// The agent a tiered-roster dispatch goes to. The developer kind — which the task file records, never a tier —
    /// resolves to the tier the rating and the failed attempts give. A dispatch that already names a tier never
    /// moves down: a failed attempt is never retried on a lower tier (Requirement 3.5). A name the removal phase
    /// deletes resolves to its replacement first, whatever tier the rating would choose (Requirement 13.5). Any other
    /// agent the dispatch, the plan or the pick names — <c>docs-dev</c>, <c>devops</c>, <c>platform-researcher</c> — is
    /// left as it is. A task that names neither an agent nor a technology is documentation or delivery work, so it
    /// keeps its empty agent.
    /// </summary>
    private static string? TieredAgent(
        string? requested,
        string? recorded,
        PlanTaskFileEntry? entry,
        string? technology,
        int failedAttempts,
        RemovedAgentResolution? removed)
    {
        if (removed is not null)
            return removed.Resolved;

        int tier = TierIndexOf(DeveloperTier(entry?.Difficulty, failedAttempts));
        if ((requested ?? recorded) is not { } named)
            return entry is null || technology is not null ? DeveloperTiers[tier] : null;

        if (string.Equals(named, PlanTaskAgentPick.DeveloperKind, StringComparison.Ordinal))
            return DeveloperTiers[tier];

        if (Raised(named, tier) is { } raised)
            return raised;

        // A name that ships nowhere is a hand edit, not a worker: a tiered dispatch falls back to the tier the
        // task's rating selects rather than handing an unknown agent to the harness.
        if (ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(named, out ArbiterSquadAgent? known)
            && !string.Equals(known.WorkerRoster, WorkerRosters.Technology, StringComparison.Ordinal))
        {
            return named;
        }

        return entry is null || technology is not null ? DeveloperTiers[tier] : null;
    }

    /// <summary>The higher of a named tier and the computed one, or null when the name is not a developer tier.</summary>
    private static string? Raised(string name, int computed)
    {
        int named = TierIndexOf(name);
        return named < 0 ? null : DeveloperTiers[Math.Max(named, computed)];
    }

    /// <summary>
    /// The refusal for a dispatch that names a technology developer while the run is on the tiered roster, or
    /// null when the name belongs to a role both rosters use. The tier the task belongs on is named in the
    /// message, so the caller can dispatch the right agent from the refusal alone.
    /// </summary>
    private static TaskPromptResult? RefusedTechnologyDeveloper(
        string? named,
        string? technology,
        PlanTaskFileEntry? entry,
        int failedAttempts)
    {
        if (named is null || technology is null
            || !PlanTaskAgentPick.RemovedAgents.TryGetValue(named, out string? replacement)
            || !string.Equals(replacement, PlanTaskAgentPick.DeveloperKind, StringComparison.Ordinal)
            || !ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(named, out ArbiterSquadAgent? agent)
            || !string.Equals(agent.WorkerRoster, WorkerRosters.Technology, StringComparison.Ordinal))
        {
            return null;
        }

        return TaskPromptResult.NotBuilt(
            "No prompt: " + PlanTaskAgentPick.TieredDeveloperRefusal(named, entry?.Difficulty, failedAttempts));
    }

    /// <summary>The tier's position on the ladder, or -1 when the name is not a developer tier.</summary>
    private static int TierIndexOf(string name)
    {
        for (int index = 0; index < DeveloperTiers.Count; index++)
        {
            if (string.Equals(DeveloperTiers[index], name, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    /// <inheritdoc />
    public TaskPromptResult Build(TaskPromptRequest request, KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepoRoot);

        string root = Path.GetFullPath(request.RepoRoot);
        string? taskId = Clean(request.TaskId);

        PlanTaskFileEntry? entry = null;
        string? fileRoster = null;
        if (taskId is not null)
        {
            string? plan = Clean(request.Plan);
            if (plan is null)
            {
                IReadOnlyList<string> active = tasks.ActivePlans(root, config);
                if (active.Count != 1)
                {
                    return TaskPromptResult.NotBuilt(active.Count == 0
                        ? "No prompt: no plan is Ready or In progress in this checkout. Approve a plan, or name the plan."
                        : $"No prompt: {active.Count} plans are Ready or In progress ({string.Join(", ", active)}). Name one plan.");
                }

                plan = active[0];
            }

            // The task file is checked against its plan before anything is read from it, so a stale file never
            // becomes half a prompt.
            PlanTaskFileOutcome outcome = tasks.Run(root, config, plan, PlanTaskFileMode.Check);
            if (outcome.File is null || outcome.Report.HasErrors)
            {
                return TaskPromptResult.NotBuilt(
                    $"No prompt: the task file for {plan} is missing or does not match the plan. Run " +
                    $"'kyber-weave docs tasks {plan} --write', then build the prompt again.");
            }

            entry = PlanTaskPacket.FindTask(outcome.File, taskId);
            if (entry is null)
            {
                string ids = string.Join(", ", (outcome.File.Tasks ?? []).Select(task => task.Id));
                return TaskPromptResult.NotBuilt(
                    $"No prompt: '{taskId}' names no one task in {plan}. Its tasks are: {ids}.");
            }

            // The roster is taken from the task file, so a file outlives a configuration change (Requirement 12.2).
            fileRoster = outcome.File.WorkerRoster;
        }

        string? technology = Clean(entry?.Technology) ?? Clean(request.Technology);
        if (entry is null && technology is null)
        {
            return TaskPromptResult.NotBuilt(
                "No prompt: name a task id, or a technology for a dispatch that belongs to no task.");
        }

        string? priorResults = null;
        if (Clean(request.PriorResultsPath) is { } priorPath)
        {
            if (!ScratchFile(root, priorPath, out string absolute))
            {
                return TaskPromptResult.NotBuilt(
                    $"No prompt: the prior-results file '{priorPath}' is outside this run's scratch area. " +
                    $"Write it under {ScratchFolder} in the checkout, name it relative to that folder, or leave it out.");
            }

            try
            {
                priorResults = files.ReadAllText(absolute);
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                return TaskPromptResult.NotBuilt(
                    $"No prompt: the prior-results file '{priorPath}' could not be read ({ex.Message}). " +
                    "Name a file that exists, or leave it out.");
            }
        }

        string? requested = Clean(request.Agent) ?? Clean(entry?.Role);
        string? recorded = Clean(entry?.Agent);
        string? agent = requested ?? recorded;
        RemovedAgentResolution? removed = null;
        if (string.Equals(fileRoster ?? config.WorkerRoster, WorkerRosters.Tiered, StringComparison.Ordinal))
        {
            // Only an explicit dispatch names a worker that must be refused: a stale role in the task file is a
            // removed name, and resolves to the tier its difficulty selects rather than blocking the prompt.
            if (RefusedTechnologyDeveloper(Clean(request.Agent), technology, entry, request.FailedAttempts) is { } refusal)
            {
                return refusal;
            }

            removed = PlanTaskAgentPick.ResolveRemovedAgent(
                requested ?? recorded, entry?.Difficulty, request.FailedAttempts);
            agent = TieredAgent(requested, recorded, entry, technology, request.FailedAttempts, removed);
        }
        string? taskText = entry is null ? null : PlanTaskPacket.Fill(entry, root, priorResults);
        // The dispatch header is routing metadata, so the count is put back where the worker reads it: a retry
        // starts knowing how many attempts already failed.
        if (taskText is not null && request.FailedAttempts > 0)
        {
            taskText = string.Create(
                CultureInfo.InvariantCulture,
                $"FAILED_ATTEMPTS: {request.FailedAttempts}\n\n{taskText}");
        }
        string? hostText = entry is null
            ? null
            : hostFacts.Render(root, config, agent, taskId, null, technology);

        PromptContent content = ContentOf(taskText, hostText, technology);
        int budget = config.TaskPromptBudget > 0 ? config.TaskPromptBudget : KyberWeaveConfig.DefaultTaskPromptBudget;
        // The retry's failure report is delivered in the same dispatch, after this prompt, so the budget reserves
        // room for it: cutting the prompt to the budget alone would overshoot by the report's length.
        string retryText = request.RetryText ?? string.Empty;
        int reserve = retryText.Length == 0 ? 0 : retryText.Length + 2;
        (string prompt, IReadOnlyList<string> leftOut) = Fit(content, budget, reserve);

        string? promptFile = null;
        string? harness = Clean(request.Harness);
        // A dispatch that names only a technology gets a file too, keyed by that technology: on a fallback harness
        // it has no other way to reach the worker, and the tool's reply must not claim a hook inserts it.
        string? slugSource = taskId ?? technology;
        if (slugSource is not null && harness is not null && !DispatchPromptInsertion.CanRewriteDispatch(harness))
        {
            TaskPromptResult? unwritten = WritePromptFile(root, slugSource, prompt, out promptFile);
            if (unwritten is not null)
            {
                return unwritten;
            }
        }

        return new TaskPromptResult(
            true,
            prompt,
            agent,
            entry?.Difficulty,
            entry?.DifficultySource,
            technology,
            leftOut,
            null,
            promptFile,
            removed?.Report);
    }

    /// <summary>
    /// Whether the path names a file inside the run's scratch area, the only place a prior-results file is read from.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="path">The path the dispatch named, relative to the root or absolute.</param>
    public static bool IsScratchFile(string root, string path) => ScratchFile(root, path, out _);

    /// <summary>
    /// Resolves a prior-results path and reports whether it stays inside the run's scratch area. Only that area is
    /// readable: the dispatch header names a file, and a header must not turn any readable file into prompt text.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="path">The path the dispatch named, relative to the root or absolute.</param>
    /// <param name="absolute">The resolved path, when it is inside the scratch area.</param>
    private static bool ScratchFile(string root, string path, out string absolute)
    {
        // The check is on the resolved path: a symlink lying inside the scratch area is a window onto its target,
        // and that target is what a read would return to the worker.
        absolute = RealPath(Path.GetFullPath(path, root));
        string scratch = Path.TrimEndingDirectorySeparator(
            RealPath(Path.GetFullPath(Path.Combine(root, ScratchFolder))));
        return string.Equals(absolute, scratch, StringComparison.Ordinal)
            ? false
            : absolute.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The path with every symlinked component resolved, so a check made on it describes the file a read returns.
    /// A component that does not exist is kept as written; the read then reports it as unreadable.
    /// </summary>
    /// <param name="full">An absolute path.</param>
    private static string RealPath(string full)
    {
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string segment in full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists || info.LinkTarget is null)
            {
                continue;
            }

            current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(current) ?? root);
        }

        return current;
    }

    /// <summary>
    /// Writes the prompt to a fallback harness's one file under the run's scratch folder, returning the path in
    /// <paramref name="relativePath"/>.
    /// </summary>
    /// <remarks>
    /// The file replaces the dispatch rewrite, so it is part of the delivery: a file that cannot be written is a
    /// refusal naming what to check, not a prompt the conductor dispatches without.
    /// </remarks>
    private static TaskPromptResult? WritePromptFile(
        string root,
        string taskId,
        string prompt,
        out string? relativePath)
    {
        relativePath = null;
        string slug = TaskSlug.Normalise(taskId);
        if (slug.Length == 0)
        {
            return TaskPromptResult.NotBuilt(
                $"No prompt: '{taskId}' does not make a task folder under {ScratchFolder}. Dispatch a task id with a " +
                "letter or digit in it.");
        }

        string relative = $"{ScratchFolder}/{slug}/{PromptFileName}";
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                _ = Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, prompt);
            relativePath = relative;
            return null;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return TaskPromptResult.NotBuilt(
                $"No prompt: the prompt file '{relative}' could not be written ({ex.Message}). Check that " +
                $"{ScratchFolder} is writable, then build the prompt again.");
        }
    }

    private PromptContent ContentOf(string? taskText, string? hostText, string? technology)
    {
        if (technology is null)
            return new PromptContent(taskText, hostText, null, null, false, null, null, null, null, null, null, []);

        StandardsBundle? bundle = standardsFor(technology);
        if (bundle is null)
            return new PromptContent(taskText, hostText, technology, null, false, null, null, null, null, null, null, []);

        string? parent = Clean(bundle.Standard?.Frontmatter.BuildsOn);
        return new PromptContent(
            taskText,
            hostText,
            technology,
            parent,
            true,
            BodyOf(bundle.SharedRules),
            BodyOf(bundle.ParentRules),
            BodyOf(bundle.ParentSkills),
            BodyOf(bundle.Rules),
            BodyOf(bundle.Skills),
            StandardLeadOf(bundle.Standard),
            bundle.Standard?.Sections ?? []);
    }

    /// <summary>
    /// Cuts the least relevant skills first, then the least relevant standard sections, until the prompt and the
    /// text delivered after it fit. Rules are never cut: they are the part a worker cannot recover from a topic
    /// lookup. When every cut is used and the prompt is still over the budget, the last part says so.
    /// </summary>
    /// <param name="content">Every part of the prompt before the budget is applied.</param>
    /// <param name="budget">The host's prompt budget in characters.</param>
    /// <param name="reserve">Characters delivered after the prompt, which the budget must also cover.</param>
    private static (string Prompt, IReadOnlyList<string> LeftOut) Fit(PromptContent content, int budget, int reserve)
    {
        IReadOnlyDictionary<string, double> taskTokens = TextVectorizer.Vectorize(
            string.Concat(content.Task ?? string.Empty, " ", content.Technology ?? string.Empty));

        HashSet<string> cut = new(StringComparer.Ordinal);
        List<string> leftOut = [];
        string prompt = Compose(content, budget, cut, leftOut);

        foreach (CutUnit unit in Units(content, taskTokens))
        {
            if (prompt.Length + reserve <= budget)
                break;

            cut.Add(unit.Id);
            leftOut.Add(unit.Label);
            prompt = Compose(content, budget, cut, leftOut);
        }

        if (prompt.Length + reserve > budget)
        {
            // Every cut is used, and the parts that stay whole are the ones the worker cannot recover. The last
            // part reports the overrun rather than returning an over-budget prompt silently.
            leftOut.Add(
                "the prompt is still over the "
                + budget.ToString(CultureInfo.InvariantCulture)
                + "-character prompt budget after every cut; the task text, host facts and rules stay whole, "
                + "so they cannot be cut further");
            prompt = Compose(content, budget, cut, leftOut);
        }

        return (prompt, leftOut);
    }

    private static IReadOnlyList<CutUnit> Units(PromptContent content, IReadOnlyDictionary<string, double> taskTokens)
    {
        List<CutUnit> units = [];

        // Kind 0 and 1 are the skills, so every skill goes before any standard section is touched.
        if (content.ParentSkills is not null)
            units.Add(new CutUnit(ParentSkillsUnit, $"parent skills ({content.Parent})", 0, 0, content.ParentSkills));
        if (content.Skills is not null)
            units.Add(new CutUnit(SkillsUnit, $"skills ({content.Technology})", 1, 0, content.Skills));

        for (int index = 0; index < content.StandardSections.Count; index++)
        {
            DocumentSection section = content.StandardSections[index];
            units.Add(new CutUnit(
                StandardUnitPrefix + section.Heading,
                $"standard section: {section.Heading}",
                2,
                index,
                section.Heading + "\n" + section.Body));
        }

        return
        [
            .. units
                .OrderBy(unit => unit.Kind)
                .ThenBy(unit => Relevance(unit.Text, taskTokens))
                .ThenByDescending(unit => unit.Index)
        ];
    }

    private static int Relevance(string text, IReadOnlyDictionary<string, double> taskTokens) =>
        TextVectorizer.Vectorize(text).Keys.Count(taskTokens.ContainsKey);

    private static string Compose(
        PromptContent content,
        int budget,
        IReadOnlySet<string> cut,
        IReadOnlyList<string> leftOut)
    {
        List<Part> parts = [];

        if (content.Task is not null)
            parts.Add(new Part("task", content.Task));
        if (content.HostFacts is not null)
            parts.Add(new Part("host-facts", content.HostFacts));

        if (content.Technology is not null)
        {
            if (content.HasFolder)
            {
                parts.Add(new Part("shared-rules", content.SharedRules ?? NoSharedRules));
                if (content.Parent is not null)
                    parts.Add(new Part("parent-rules", ParentPart(content, cut)));
                parts.Add(new Part("technology-rules", content.TechnologyRules ?? NoRulesFile));
                parts.Add(new Part("skills", SkillsPart(content, cut)));
                parts.Add(new Part("standard", StandardPart(content, cut)));
            }
            else
            {
                string note = string.Create(
                    CultureInfo.InvariantCulture,
                    $"No standards folder for technology '{content.Technology}' is installed in this repository, so this prompt carries no rules, skills or standard for it. Report this to the conductor rather than improvising a standard.");
                parts.Add(new Part("technology-rules", note));
                parts.Add(new Part("skills", note));
                parts.Add(new Part("standard", note));
            }
        }

        parts.Add(new Part("left-out", LeftOutPart(budget, leftOut)));
        return Join(parts);
    }

    private static string ParentPart(PromptContent content, IReadOnlySet<string> cut)
    {
        StringBuilder text = new();
        text.Append(content.ParentRules ?? string.Create(
            CultureInfo.InvariantCulture, $"The parent technology '{content.Parent}' has no rules file."));

        if (content.ParentSkills is not null && !cut.Contains(ParentSkillsUnit))
        {
            text.Append("\n\n## Parent skills (").Append(content.Parent).Append(")\n\n").Append(content.ParentSkills);
        }

        return text.ToString();
    }

    private static string SkillsPart(PromptContent content, IReadOnlySet<string> cut) => content.Skills is null
        ? "This technology's folder has no skills file."
        : cut.Contains(SkillsUnit)
            ? "The skills for this technology were left out: the prompt budget did not fit them. The last part names what was cut."
            : content.Skills;

    private static string StandardPart(PromptContent content, IReadOnlySet<string> cut)
    {
        StringBuilder text = new();
        if (content.StandardLead is not null)
            text.Append(content.StandardLead);

        foreach (DocumentSection section in content.StandardSections)
        {
            if (cut.Contains(StandardUnitPrefix + section.Heading))
                continue;

            if (text.Length > 0)
                text.Append("\n\n");

            text.Append("## ").Append(section.Heading).Append('\n').Append(section.Body.Trim());
        }

        return text.Length == 0
            ? "The standard for this technology was left out: the prompt budget cut every section. The last part names what was cut."
            : text.ToString();
    }

    private static string LeftOutPart(int budget, IReadOnlyList<string> leftOut)
    {
        if (leftOut.Count == 0)
            return NothingLeftOut;

        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"Left out over the {budget}-character prompt budget:");
        foreach (string label in leftOut)
            text.Append("\n- ").Append(label);

        return text.ToString();
    }

    private static string Join(IReadOnlyList<Part> parts)
    {
        StringBuilder text = new();
        foreach (Part part in parts)
        {
            if (text.Length > 0)
                text.Append("\n\n");

            text.Append('<').Append(part.Tag).Append(">\n")
                .Append(part.Body.Trim()).Append('\n')
                .Append("</").Append(part.Tag).Append('>');
        }

        return text.ToString();
    }

    private static string? BodyOf(DocumentModel? document) =>
        string.IsNullOrWhiteSpace(document?.Body) ? null : document.Body.Trim();

    /// <summary>The standard's own text before its first <c>##</c> section, which is not a section that can be cut.</summary>
    private static string? StandardLeadOf(DocumentModel? standard)
    {
        if (string.IsNullOrWhiteSpace(standard?.Body))
            return null;

        string body = standard.Body.Trim();
        int firstSection = body.IndexOf("\n## ", StringComparison.Ordinal);
        return firstSection < 0 ? body : body[..firstSection].TrimEnd();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string NoSharedRules =
        "This repository has no shared rules file (shared-rules.md), so this prompt carries none.";

    private const string NoRulesFile =
        "This technology's folder has no rules file, so this prompt carries none for it.";

    /// <summary>One part of the prompt, written between a tag that opens it and the same tag closing it.</summary>
    private sealed record Part(string Tag, string Body);

    /// <summary>One piece the budget may cut: its id, how the last part names it, and the text it would add.</summary>
    private sealed record CutUnit(string Id, string Label, int Kind, int Index, string Text);

    /// <summary>Every part of one prompt before the budget is applied, and which of them may be cut.</summary>
    private sealed record PromptContent(
        string? Task,
        string? HostFacts,
        string? Technology,
        string? Parent,
        bool HasFolder,
        string? SharedRules,
        string? ParentRules,
        string? ParentSkills,
        string? TechnologyRules,
        string? Skills,
        string? StandardLead,
        IReadOnlyList<DocumentSection> StandardSections);
}
