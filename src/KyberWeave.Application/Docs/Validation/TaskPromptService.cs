using System.Globalization;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Text;

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

    private const string ParentSkillsUnit = "parent-skills";
    private const string SkillsUnit = "skills";
    private const string StandardUnitPrefix = "standard:";

    /// <inheritdoc />
    public TaskPromptResult Build(TaskPromptRequest request, KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepoRoot);

        string root = Path.GetFullPath(request.RepoRoot);
        string? taskId = Clean(request.TaskId);

        PlanTaskFileEntry? entry = null;
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
            try
            {
                priorResults = files.ReadAllText(Path.GetFullPath(priorPath, root));
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                return TaskPromptResult.NotBuilt(
                    $"No prompt: the prior-results file '{priorPath}' could not be read ({ex.Message}). " +
                    "Name a file that exists, or leave it out.");
            }
        }

        string? agent = Clean(request.Agent) ?? Clean(entry?.Role) ?? Clean(entry?.Agent);
        string? taskText = entry is null ? null : PlanTaskPacket.Fill(entry, root, priorResults);
        string? hostText = entry is null
            ? null
            : hostFacts.Render(root, config, agent, taskId, null, technology);

        PromptContent content = ContentOf(taskText, hostText, technology);
        int budget = config.TaskPromptBudget > 0 ? config.TaskPromptBudget : KyberWeaveConfig.DefaultTaskPromptBudget;
        (string prompt, IReadOnlyList<string> leftOut) = Fit(content, budget);

        return new TaskPromptResult(
            true,
            prompt,
            agent,
            entry?.Difficulty,
            entry?.DifficultySource,
            technology,
            leftOut,
            null);
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
    /// Cuts the least relevant skills first, then the least relevant standard sections, until the prompt fits.
    /// Rules are never cut: they are the part a worker cannot recover from a topic lookup.
    /// </summary>
    private static (string Prompt, IReadOnlyList<string> LeftOut) Fit(PromptContent content, int budget)
    {
        IReadOnlyDictionary<string, double> taskTokens = TextVectorizer.Vectorize(
            string.Concat(content.Task ?? string.Empty, " ", content.Technology ?? string.Empty));

        HashSet<string> cut = new(StringComparer.Ordinal);
        List<string> leftOut = [];
        string prompt = Compose(content, budget, cut, leftOut);

        foreach (CutUnit unit in Units(content, taskTokens))
        {
            if (prompt.Length <= budget)
                break;

            cut.Add(unit.Id);
            leftOut.Add(unit.Label);
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
