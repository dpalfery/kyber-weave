using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Selects and expands one developer agent's task gates for one change, checking every path before it can reach argv.</summary>
public interface ITaskGateSelectionService
{
    /// <summary>Refuses a <c>--task</c> value that is not a lookup name.</summary>
    /// <param name="task">The value given, or null when none was.</param>
    /// <returns>The reason it is refused, or null when the value is acceptable.</returns>
    string? ValidateTask(string? task);

    /// <summary>Checks one changed path, whether given by <c>--paths</c> or listed by git, before it can reach argv.</summary>
    /// <param name="root">The repository root the path must stay inside.</param>
    /// <param name="path">The repository-relative path to check.</param>
    /// <returns>The reason it is refused, or null when the path is safe to pass on.</returns>
    string? ValidatePath(string root, string path);

    /// <summary>Resolves the agent's gates against the changed set and the host's operating system.</summary>
    /// <param name="gates">The agent's gates, in declaration order.</param>
    /// <param name="changed">The repository-relative changed paths, already validated with <see cref="ValidatePath"/>.</param>
    /// <param name="task">The validated <c>--task</c> slug, or null when none was given.</param>
    /// <param name="currentOs"><c>windows</c> or <c>unix</c>, for the gate's <c>os</c> filter.</param>
    /// <param name="root">The repository root the <c>{tests}</c> token resolves its class files under, or null for the nearest enclosing repository of this process.</param>
    /// <param name="testMap">The host's <c>review.test-map</c>, or null when it declares none.</param>
    /// <param name="declaredClasses">Test classes the task's plan names in its Test contract; they fill <c>{tests}</c> first.</param>
    /// <exception cref="ArgumentException"><paramref name="currentOs"/> is neither <c>windows</c> nor <c>unix</c>.</exception>
    TaskGatePlan Plan(
        IReadOnlyList<ReviewTaskGate> gates,
        IReadOnlyList<string> changed,
        string? task,
        string currentOs,
        string? root = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? testMap = null,
        IReadOnlyList<string>? declaredClasses = null);

    /// <summary>Resolves the fully qualified names of the test classes that cover the changed files.</summary>
    /// <param name="root">The repository root the class files are looked up under; null uses the nearest enclosing repository of this process.</param>
    /// <param name="changed">The repository-relative changed paths, already validated.</param>
    /// <param name="declared">The host's <c>review.test-map</c>, or null when it declares none.</param>
    /// <returns>The names in resolution order, without duplicates; empty means nothing covers the change.</returns>
    /// <exception cref="InvalidOperationException">The root exists but is not accessible.</exception>
    IReadOnlyList<string> ResolveTestClasses(
        string? root,
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declared = null);
}
