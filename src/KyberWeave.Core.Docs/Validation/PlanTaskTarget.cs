using KyberWeave.Application.Review;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>Finds a task by the id a worker typed, for the callers that have no container.</summary>
/// <remarks>
/// The logic is <see cref="PlanTaskTargetService"/> in the application layer behind
/// <see cref="IPlanTaskTargetService"/>, over the plan the <c>docs packet</c> command reads. This answers over the real
/// file system, and stays for <c>docs packet</c> and the tests that call it statically. Delete it when those take the
/// service from the container.
/// </remarks>
public static partial class PlanTaskTarget
{
    private static readonly IPlanTaskTargetService Service = new PlanTaskTargetService(
        new PlanTaskService(LegacyPorts.Files),
        new TaskGateSelectionService(new FileRepositoryTreeProbe(), new FileTestClassSources()));

    /// <inheritdoc cref="IPlanTaskTargetService.Find" />
    public static PlanTaskTargetMatch? Find(string root, KyberWeaveConfig config, string typedTask, out string? reason) =>
        Service.Find(root, config, typedTask, out reason);

    /// <inheritdoc cref="IPlanTaskTargetService.ContractRow" />
    public static PlanContractRow? ContractRow(string root, string plan, string? taskId) =>
        Service.ContractRow(root, plan, taskId);

    /// <inheritdoc cref="IPlanTaskTargetService.Files" />
    public static IReadOnlyList<string> Files(string root, PlanTaskFileEntry entry, PlanContractRow? row = null) =>
        Service.Files(root, entry, row);

    /// <inheritdoc cref="IPlanTaskTargetService.DeclaredClasses" />
    public static IReadOnlyList<string> DeclaredClasses(PlanContractRow? row) =>
        Service.DeclaredClasses(row);
}
