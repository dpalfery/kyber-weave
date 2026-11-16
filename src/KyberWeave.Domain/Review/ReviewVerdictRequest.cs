namespace KyberWeave.Core.Review;

/// <summary>What one <c>review verdict</c> run reads and writes: every option the command takes.</summary>
/// <param name="Path">The repository path the verdict is computed in, as the option gave it.</param>
/// <param name="Config">An explicit configuration path, or null for the repository's own.</param>
/// <param name="Seats">The folder of seat output files, or null to use a findings document or the current cycle.</param>
/// <param name="Findings">A hand-built findings document, or null.</param>
/// <param name="Gates">The gate report the verdict is computed from, or null when no gate ran.</param>
/// <param name="Prior">The previous cycle's folder, or null for a first cycle.</param>
/// <param name="Out">Where the findings document is written, or null to write none.</param>
/// <param name="BaseRef">The git ref the change's base resolves against, or null for <c>origin/HEAD</c>.</param>
/// <param name="Task">The task id whose review folder holds the cycle, or null to pick it.</param>
/// <param name="Drop">Finding ids to drop, each as <c>id=reason</c>.</param>
/// <param name="NotSeated">Lenses that did not run, each as <c>lens=reason</c>.</param>
/// <param name="Candidates">Whether to list the refutation candidates saved so far instead of computing a verdict.</param>
/// <param name="NoDecisionModel">Whether to ask the decision model nothing.</param>
/// <param name="VerdictCommand">The command line a candidate run tells the reviewer to run next.</param>
public sealed record ReviewVerdictRequest(
    string Path,
    string? Config,
    string? Seats,
    string? Findings,
    string? Gates,
    string? Prior,
    string? Out,
    string? BaseRef,
    string? Task,
    IReadOnlyList<string> Drop,
    IReadOnlyList<string> NotSeated,
    bool Candidates,
    bool NoDecisionModel,
    string VerdictCommand);
