namespace KyberWeave.Arbiter;

/// <summary>
/// The harness token <c>serve</c> evaluations are recorded under: an
/// <c>arbiter_evaluate</c> call has no harness hook, so it is recorded under a token
/// of its own and the classifier treats it as project-wide, like any harness that
/// does not fire per-agent hooks. Shared by the serve tools and the decision engine,
/// which labels the caller of a serve event <c>asserted</c> rather than
/// <c>harness</c>.
/// </summary>
internal static class ArbiterServeToken
{
    /// <summary>The harness token recorded for <c>arbiter_evaluate</c> events.</summary>
    public const string Harness = "serve";
}
