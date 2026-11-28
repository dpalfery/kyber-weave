using System.Globalization;
using System.Text;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Mcp;

/// <summary>Formatting and failure classification the docs tool services share.</summary>
public static class DocsToolSupport
{
    /// <summary>One finding as a single line: code, severity, file, message and hint.</summary>
    /// <param name="diagnostic">The finding.</param>
    public static string FormatGateFinding(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        StringBuilder sb = new();
        sb.Append(diagnostic.Code)
          .Append(" [")
          .Append(diagnostic.Severity.ToString().ToUpperInvariant())
          .Append("] ");
        if (!string.IsNullOrEmpty(diagnostic.FilePath))
        {
            sb.Append(diagnostic.FilePath.Replace('\\', '/'));
            sb.Append(": ");
        }

        sb.Append(diagnostic.Message.ReplaceLineEndings(" "));
        if (!string.IsNullOrWhiteSpace(diagnostic.Hint))
            sb.Append(CultureInfo.InvariantCulture, $" hint: {diagnostic.Hint.ReplaceLineEndings(" ")}");

        return sb.ToString();
    }

    /// <summary>Whether <paramref name="exception"/> is a read failure a tool reports as a finding rather than crashing on.</summary>
    /// <param name="exception">The exception a corpus or configuration read threw.</param>
    public static bool IsExpectedReadFailure(Exception exception) =>
        FileFailure.Is(exception) || exception is InvalidDataException;
}
