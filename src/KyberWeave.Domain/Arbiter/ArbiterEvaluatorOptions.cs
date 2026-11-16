using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter;

/// <summary>Options for one the evaluator call.</summary>
/// <param name="DryRun">When true, the evaluator reads the ledger and writes nothing.</param>
/// <param name="Source">Who observed the event: <c>hook</c>, <c>serve</c> or <c>eval</c>.</param>
/// <param name="ConfirmModelWrite">Writes the model token into the harness input and reports whether it held. Asked
/// before a delivered model is recorded, so the decision says delivered only for a write that happened. Null when no
/// harness writes the model (serve, eval), in which case the decision is recorded as resolved.</param>
public sealed record ArbiterEvaluatorOptions(
    bool DryRun = false,
    string Source = ArbiterSources.Hook,
    Func<string, bool>? ConfirmModelWrite = null);
