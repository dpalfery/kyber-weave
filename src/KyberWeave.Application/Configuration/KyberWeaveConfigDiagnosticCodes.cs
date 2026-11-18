namespace KyberWeave.Core.Configuration;

/// <summary>The stable rule codes a host raises while it loads <c>kyber-weave.yml</c>.</summary>
/// <remarks>
/// A repository read reports its outcome through <see cref="KyberWeaveConfigLoadResult"/> and does not name a rule id;
/// the entry point that turns the outcome into a diagnostic names the code. They live together so two hosts cannot
/// spell the same condition differently.
/// </remarks>
public static class KyberWeaveConfigDiagnosticCodes
{
    /// <summary>Stable rule code for invalid or unreadable <c>kyber-weave.yml</c>.</summary>
    public const string LoadError = "KW-CONFIG-001";

    /// <summary>Stable rule code for a non-fatal problem in <c>kyber-weave.yml</c>, such as an unknown key.</summary>
    public const string Warning = "KW-CONFIG-002";
}
