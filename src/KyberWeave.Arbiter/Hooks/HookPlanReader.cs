using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The hook's plan facts, read from the plan file the dispatch headers name.
/// </summary>
/// <remarks>
/// Plan and task identity come from <c>PLAN_FILE</c> and <c>TASK</c> only; nothing
/// is inferred from the plan index. A missing or unreadable plan is the
/// <c>plan.exists: false</c> fact, not a failure: the rules decide on the absence.
/// Parsing reuses <see cref="PlanDocumentParser"/>, the same parser every other
/// host reads plans through, rather than a hook-local one.
/// </remarks>
internal sealed class HookPlanReader(string repositoryRoot) : IArbiterPlanReader
{
    /// <inheritdoc/>
    public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(classification);

        if (!classification.Headers.TryGetValue("PLAN_FILE", out string? planFile)
            || string.IsNullOrWhiteSpace(planFile))
        {
            return facts;
        }

        string resolved = Path.IsPathRooted(planFile)
            ? planFile
            : Path.Combine(repositoryRoot, planFile);
        if (!File.Exists(resolved))
        {
            return facts.With("plan.exists", false, ArbiterFactLabel.Derived);
        }

        PlanDocument document;
        try
        {
            document = PlanDocumentParser.Parse(File.ReadAllText(resolved));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return facts.With("plan.exists", false, ArbiterFactLabel.Derived);
        }

        facts = facts.With("plan.exists", true, ArbiterFactLabel.Derived);
        if (document.Status is not null)
        {
            facts = facts.With("plan.status", document.Status, ArbiterFactLabel.Derived);
        }

        facts = facts.With("plan.development-mode", document.DevelopmentMode, ArbiterFactLabel.Derived);
        facts = facts.With("plan.tasks.count", document.Tasks.Count, ArbiterFactLabel.Derived);
        facts = facts.With("plan.out-of-scope", document.OutOfScope.ToList(), ArbiterFactLabel.Derived);

        if (classification.Headers.TryGetValue("TASK", out string? taskId)
            && !string.IsNullOrWhiteSpace(taskId))
        {
            PlanTask? task = document.Tasks.FirstOrDefault(
                candidate => string.Equals(candidate.Id, taskId, StringComparison.Ordinal));
            if (task is not null)
            {
                facts = facts.With("plan.task", task.Id, ArbiterFactLabel.Derived);
                facts = facts.With("plan.task.text", task.Text, ArbiterFactLabel.Derived);
                facts = facts.With("plan.task.files", task.Files.ToList(), ArbiterFactLabel.Derived);
                facts = facts.With("plan.task.depends-on", task.DependsOn.ToList(), ArbiterFactLabel.Derived);
                facts = facts.With("plan.task.skills", task.Skills.ToList(), ArbiterFactLabel.Derived);
                facts = facts.With(
                    "plan.test-contract.row",
                    document.ContractRows.Any(row => string.Equals(row.TaskId, task.Id, StringComparison.Ordinal)),
                    ArbiterFactLabel.Derived);
            }
        }

        return facts;
    }
}
