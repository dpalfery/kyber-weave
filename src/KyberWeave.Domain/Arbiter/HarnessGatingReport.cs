namespace KyberWeave.Core.Arbiter;

/// <summary>One installed harness and the state its dispatches are gated in for this project.</summary>
/// <param name="Token">The harness token, as <c>--target</c> takes it.</param>
/// <param name="State">The gating state, in words an operator can act on.</param>
/// <param name="FixCommand">The command that gates it, or null when nothing needs fixing.</param>
/// <param name="Ungated">True when no hook gates this harness's dispatches for the project.</param>
public sealed record HarnessGatingRow(string Token, string State, string? FixCommand, bool Ungated);

/// <summary>The per-harness rows, and the harnesses whose global hook is missing from the global targets.</summary>
/// <param name="Rows">One row per installed harness, in catalogue order.</param>
/// <param name="UnlistedGlobalTokens">Harnesses the global Squad receipt holds an Arbiter hook for that <c>arbiter.global.targets</c> does not list.</param>
public sealed record HarnessGatingReport(
    IReadOnlyList<HarnessGatingRow> Rows,
    IReadOnlyList<string> UnlistedGlobalTokens);
