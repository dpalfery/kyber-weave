using KyberWeave.Application.Review;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>Selects and expands one developer agent's task gates for one change, for the callers that have no container.</summary>
/// <remarks>
/// The logic is <see cref="TaskGateSelectionService"/> in the application layer behind <see cref="ITaskGateSelectionService"/>,
/// and the pure rules are <see cref="TaskGatePlanning"/>. This answers over the real file system, and stays for the review
/// command, the Docs packet test command and the tests that call it statically.
/// </remarks>
public static class TaskGateSelection
{
    /// <summary>The most test classes one <c>{tests}</c> filter may name.</summary>
    public const int MaxTestClassesInFilter = TaskGatePlanning.MaxTestClassesInFilter;

    private static readonly ITaskGateSelectionService Service =
        new TaskGateSelectionService(new FileRepositoryTreeProbe(), new FileTestClassSources());

    /// <inheritdoc cref="ITaskGateSelectionService.ValidateTask" />
    public static string? ValidateTask(string? task) => Service.ValidateTask(task);

    /// <inheritdoc cref="ITaskGateSelectionService.ValidatePath" />
    public static string? ValidatePath(string root, string path) => Service.ValidatePath(root, path);

    /// <inheritdoc cref="ITaskGateSelectionService.Plan" />
    public static TaskGatePlan Plan(
        IReadOnlyList<ReviewTaskGate> gates,
        IReadOnlyList<string> changed,
        string? task,
        string currentOs,
        string? root = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? testMap = null,
        IReadOnlyList<string>? declaredClasses = null) =>
        Service.Plan(gates, changed, task, currentOs, root, testMap, declaredClasses);
}
