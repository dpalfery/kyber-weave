using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Application.Configuration;

/// <summary>Loads a repository's configuration through the configuration repository and reports failures as diagnostics.</summary>
/// <param name="configs">Reads the repository's configuration file.</param>
public sealed class ConfigLoadService(IKyberWeaveConfigRepository configs) : IConfigLoadService
{
    private const string ConfigWarningCode = "KW-CONFIG-002";

    /// <inheritdoc />
    public bool TryLoad(
        string repoRoot,
        string? configPath,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out string? resolvedConfigPath)
    {
        ArgumentNullException.ThrowIfNull(report);

        KyberWeaveConfigLoadResult result = configs.TryLoad(repoRoot, configPath);
        resolvedConfigPath = result.ConfigPath;
        if (!result.Success)
        {
            report.Add(new Diagnostic(
                IDocsHostConfiguration.ConfigLoadErrorCode,
                Severity.Error,
                result.Error ?? "Failed to load kyber-weave.yml.",
                "kyber-weave.yml",
                result.ConfigPath));
            config = KyberWeaveConfig.ProductDefaults;
            return false;
        }

        config = result.Config!;

        // Unknown keys load but do nothing, so each one is surfaced rather than left to be noticed later.
        foreach (string warning in config.Review.Warnings)
        {
            report.Add(new Diagnostic(
                ConfigWarningCode,
                Severity.Warning,
                warning,
                "kyber-weave.yml",
                result.ConfigPath,
                Hint: "Check the spelling against the gate keys, or remove the key."));
        }

        return true;
    }
}
