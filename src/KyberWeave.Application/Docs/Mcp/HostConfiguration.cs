using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>Loads the repository's host configuration for the tools that need it, reporting a failure as a finding.</summary>
internal sealed class HostConfiguration : IDocsHostConfiguration
{
    private readonly IKyberWeaveConfigRepository _config;

    /// <summary>Creates the loader over the configuration repository the container provides.</summary>
    /// <param name="config">Loads a repository's configuration.</param>
    public HostConfiguration(IKyberWeaveConfigRepository config) =>
        _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>
    /// Loads <c>kyber-weave.yml</c> for <paramref name="repositoryRoot"/>. A failure is added to <paramref name="report"/>
    /// and the product defaults are returned; warnings the configuration carries are added as findings.
    /// </summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="report">The report the findings are added to.</param>
    /// <param name="config">The configuration, or the product defaults when loading failed.</param>
    /// <returns>True when the configuration loaded.</returns>
    public bool TryLoad(string repositoryRoot, DiagnosticReport report, out KyberWeaveConfig config)
    {
        KyberWeaveConfigLoadResult result = _config.TryLoad(repositoryRoot);
        if (!result.Success)
        {
            report.Add(new Diagnostic(
                IDocsHostConfiguration.ConfigLoadErrorCode,
                Severity.Error,
                result.Error ?? "Failed to load kyber-weave.yml.",
                "kyber-weave.yml",
                result.ConfigPath));
            config = KyberWeaveConfigDefaults.Product;
            return false;
        }

        config = result.Config
            ?? throw new InvalidOperationException("A successful configuration load must produce a configuration.");
        foreach (string warning in config.Review.Warnings)
        {
            report.Add(new Diagnostic(
                IDocsHostConfiguration.ConfigWarningCode,
                Severity.Warning,
                warning,
                "kyber-weave.yml",
                result.ConfigPath,
                Hint: "Check the spelling against the gate keys, or remove the key."));
        }

        return true;
    }
}
