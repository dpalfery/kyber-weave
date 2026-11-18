using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Loads the repository's host configuration for the documentation tools, reporting a failure as a finding.</summary>
public interface IDocsHostConfiguration
{
    /// <summary>The finding code for a host configuration that could not be loaded; the value <c>KyberWeaveConfigDiagnosticCodes.LoadError</c> carries.</summary>
    public const string ConfigLoadErrorCode = "KW-CONFIG-001";

    /// <summary>The finding code for a non-fatal problem in the host configuration, such as an unknown review key.</summary>
    public const string ConfigWarningCode = "KW-CONFIG-002";

    /// <summary>
    /// Loads <c>kyber-weave.yml</c> for <paramref name="repositoryRoot"/>. A failure is added to <paramref name="report"/>
    /// and the product defaults are returned; warnings the configuration carries are added as findings.
    /// </summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="report">The report the findings are added to.</param>
    /// <param name="config">The configuration, or the product defaults when loading failed.</param>
    /// <returns>True when the configuration loaded.</returns>
    bool TryLoad(string repositoryRoot, DiagnosticReport report, out KyberWeaveConfig config);
}
