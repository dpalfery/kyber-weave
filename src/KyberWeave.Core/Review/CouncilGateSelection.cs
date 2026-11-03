using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>The repository-wide council gates that run for one change, and the ones that do not.</summary>
/// <param name="Run">The gates that will start, in declaration order.</param>
/// <param name="Skipped">The gates that do not apply to this host or to this change, and why.</param>
public sealed record CouncilGatePlan(
    IReadOnlyList<ReviewGate> Run,
    IReadOnlyList<SkippedTaskGate> Skipped);

/// <summary>
/// Decides which of a host's council gates run for one change.
/// </summary>
/// <remarks>
/// A gate is skipped only on evidence: a platform mismatch, or an <c>include</c> that matches no changed
/// file. When the changed set is unknown (<see langword="null"/>), no gate is skipped for <c>include</c>,
/// because a gate dropped on a guess would hide the evidence it exists to give. A gate with no
/// <c>include</c> runs on every change.
/// </remarks>
public static class CouncilGateSelection
{
    /// <summary>Resolves the council gates against the changed set and the host's operating system.</summary>
    /// <param name="gates">The gates the host declares.</param>
    /// <param name="changed">Repository-relative changed paths, or <see langword="null"/> when git could not list them.</param>
    /// <param name="currentOs"><c>windows</c> or <c>unix</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="currentOs"/> is neither <c>windows</c> nor <c>unix</c>. Any other host value would
    /// be read as unix, so it is refused rather than guessed at.
    /// </exception>
    public static CouncilGatePlan Plan(
        IReadOnlyList<ReviewGate> gates,
        IReadOnlyList<string>? changed,
        string currentOs)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentOs);
        if (currentOs is not ("windows" or "unix"))
            throw new ArgumentException($"currentOs must be 'windows' or 'unix', not '{currentOs}'.",
                nameof(currentOs));

        List<ReviewGate> run = [];
        List<SkippedTaskGate> skipped = [];

        foreach (ReviewGate gate in gates)
        {
            if (gate.Os is not null && gate.Os != currentOs)
            {
                skipped.Add(new SkippedTaskGate(gate.Id, $"it runs only on {gate.Os}, and this host is {currentOs}"));
                continue;
            }

            if (gate.Include.Count == 0 || changed is null)
            {
                run.Add(gate);
                continue;
            }

            bool anyMatch = changed.Any(path => gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path)));
            if (anyMatch)
                run.Add(gate);
            else
                skipped.Add(new SkippedTaskGate(gate.Id, "no changed file matches its include"));
        }

        return new CouncilGatePlan(run, skipped);
    }
}
