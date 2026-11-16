using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>Forwards the severity-override validation the Docs schema gate still makes statically to the layered source.</summary>
/// <remarks>Delete once the schema gate takes <c>ISeverityOverrideSource</c> from the container.</remarks>
public static class SeverityOverrideFiles
{
    /// <inheritdoc cref="KyberWeave.Contracts.Repositories.ISeverityOverrideSource.Validate" />
    public static DiagnosticReport Validate(string root, KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new FileSeverityOverrideSource().Validate(root, config.ConfigReg);
    }
}
