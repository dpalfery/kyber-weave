namespace KyberDash.Application;

/// <summary>
/// Wire strings for enum values the application layer emits, kept local so the layer does not depend
/// on conversion helpers that are internal to <c>KyberDash.Domain</c>.
/// </summary>
internal static class CanonModelWireStrings
{
    /// <summary>Renders a <see cref="KyberDash.Domain.ProblemSeverity"/> as <c>error</c> or <c>warning</c>.</summary>
    /// <param name="severity">Problem severity.</param>
    /// <returns>The wire string.</returns>
    public static string ProblemWireString(KyberDash.Domain.ProblemSeverity severity) =>
        severity == KyberDash.Domain.ProblemSeverity.Warning ? "warning" : "error";
}
