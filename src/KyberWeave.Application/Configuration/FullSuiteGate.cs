namespace KyberWeave.Core.Configuration;

/// <summary>
/// Decides which review gates a run may execute. Only the review council and CI run the full product suite,
/// so a gate marked <see cref="ReviewGate.FullSuite"/> is refused for any other caller.
/// </summary>
/// <remarks>
/// The refusal is a rule about who runs the expensive gate, not about what the change contains. A dev agent
/// that refuses to run a full suite still has every targeted check, so the refusal names those alternatives.
/// A council run passes <c>--council</c>, and a person at a terminal is the operator, so neither is refused.
/// </remarks>
public static class FullSuiteGate
{
    /// <summary>Splits gates into those the run may execute and those it must refuse.</summary>
    /// <param name="gates">The gates the run selected, in declaration order.</param>
    /// <param name="council">True when the caller declared itself the review council or CI.</param>
    /// <param name="interactive">True when stdin is an interactive terminal.</param>
    /// <returns>The admitted gates, in their original order, and one refusal per refused gate.</returns>
    public static FullSuiteSelection Select(IEnumerable<ReviewGate> gates, bool council, bool interactive)
    {
        ArgumentNullException.ThrowIfNull(gates);

        List<ReviewGate> admitted = [];
        List<FullSuiteRefusal> refused = [];

        foreach (ReviewGate gate in gates)
        {
            if (council || interactive || !gate.FullSuite)
            {
                admitted.Add(gate);
                continue;
            }

            refused.Add(new FullSuiteRefusal(gate.Id, RefusalMessage(gate.Id)));
        }

        return new FullSuiteSelection(admitted, refused);
    }

    private static string RefusalMessage(string gateId) =>
        $"Gate '{gateId}' is a full-suite gate. Only the review council and CI run the full test suite, " +
        "so it was refused: this run has no --council and stdin is not a terminal. To check your change, run " +
        "'kyber-weave review gates . --agent <name> --paths <files>' for your task gates, or " +
        "'scripts/fast-loop.sh --class \"KyberWeave.Tests.<Area>*\"' for the one area you changed.";
}

/// <summary>The outcome of <see cref="FullSuiteGate.Select"/>.</summary>
/// <param name="Admitted">The gates the run may execute, in their original order.</param>
/// <param name="Refused">The full-suite gates the run must not execute, with the reason for each.</param>
public sealed record FullSuiteSelection(IReadOnlyList<ReviewGate> Admitted, IReadOnlyList<FullSuiteRefusal> Refused);

/// <summary>A full-suite gate the run was not allowed to execute.</summary>
/// <param name="GateId">The gate's id.</param>
/// <param name="Message">A sentence the operator can act on, naming the targeted alternatives.</param>
public sealed record FullSuiteRefusal(string GateId, string Message);
