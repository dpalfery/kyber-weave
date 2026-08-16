using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Squad.Validation;

/// <summary>Raised when canonical Squad source cannot be loaded safely.</summary>
public sealed class SquadSourceValidationException : Exception
{
    public SquadSourceValidationException()
        : this(new DiagnosticReport())
    {
    }

    public SquadSourceValidationException(string message)
        : base(message)
    {
        Diagnostics = new DiagnosticReport();
    }

    public SquadSourceValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Diagnostics = new DiagnosticReport();
    }

    public SquadSourceValidationException(DiagnosticReport diagnostics)
        : base(CreateMessage(diagnostics))
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostics = diagnostics;
    }

    /// <summary>Actionable source diagnostics that prevented loading.</summary>
    public DiagnosticReport Diagnostics { get; }

    private static string CreateMessage(DiagnosticReport diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Items.Count == 0
            ? "Squad source validation failed."
            : $"Squad source validation failed: {diagnostics.Items[0].Message}";
    }
}
