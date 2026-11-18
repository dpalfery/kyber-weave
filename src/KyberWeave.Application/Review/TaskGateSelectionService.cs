using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>Selects and expands one developer agent's task gates for one change.</summary>
/// <remarks>
/// <para>
/// This is the only place a <c>{changed}</c>, <c>{task}</c> or <c>{tests}</c> token becomes a value. All three are
/// substituted into argv elements, never into a command line, so a changed file name cannot introduce an option or a
/// second command. Paths are checked before expansion: an absolute path, a <c>..</c> escape, a leading <c>-</c>, or a
/// symbolic link leading outside the root refuses the whole run.
/// </para>
/// <para>
/// Every decision here is made before a process starts. The rules are <see cref="TaskGatePlanning"/> and
/// <see cref="TestClassPlanning"/>; this class asks the file system what they need to know.
/// </para>
/// </remarks>
/// <param name="tree">Probes the repository root and the links under it.</param>
/// <param name="tests">Reads the source files the test classes are looked up in.</param>
/// <param name="paths">Resolves and contains a changed path through the path seam.</param>
/// <param name="environment">Names the working directory a relative root resolves against.</param>
public sealed class TaskGateSelectionService(
    IRepositoryTreeProbe tree,
    ITestClassSources tests,
    IPathResolver paths,
    IProcessEnvironment environment) : ITaskGateSelectionService
{
    /// <inheritdoc />
    public string? ValidateTask(string? task) => TaskGatePlanning.ValidateTask(task);

    /// <inheritdoc />
    public string? ValidatePath(string root, string path) =>
        TaskGatePlanning.ValidatePath(paths, environment, root, path, tree.LeadsOutsideThroughSymlink);

    /// <inheritdoc />
    public TaskGatePlan Plan(
        IReadOnlyList<ReviewTaskGate> gates,
        IReadOnlyList<string> changed,
        string? task,
        string currentOs,
        string? root = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? testMap = null,
        IReadOnlyList<string>? declaredClasses = null) =>
        TaskGatePlanning.Plan(
            gates,
            changed,
            task,
            currentOs,
            root,
            declaredClasses,
            root is null ? null : tree.ProbeRoot(root),
            matches => ResolveTestClasses(root, matches, testMap));

    /// <inheritdoc />
    public IReadOnlyList<string> ResolveTestClasses(
        string? root,
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declared = null)
    {
        ArgumentNullException.ThrowIfNull(changed);

        if (!TestClassPlanning.AnyPossibleCoverage(changed, declared))
            return [];

        return tests.Open(root) is { } source
            ? TestClassPlanning.Resolve(source, changed, declared, tests.ReadDeclaredNamespace)
            : [];
    }
}
