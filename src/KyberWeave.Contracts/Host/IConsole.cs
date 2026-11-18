using System.Diagnostics.CodeAnalysis;

namespace KyberWeave.Contracts.Host;

/// <summary>The console a command writes its report to and reads operator answers from.</summary>
/// <remarks>
/// Code above the data layer asks this instead of calling <see cref="System.Console"/>, so a unit test reads what was
/// written and supplies the answers without a person at a terminal.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Error names the diagnostic stream, as Console.Error does, and every caller reads it that way; a consumer in a language where Error is reserved sees only the property name it implements.")]
public interface IConsole
{
    /// <summary>Gets the writer for ordinary output.</summary>
    TextWriter Out { get; }

    /// <summary>Gets the writer for diagnostics.</summary>
    TextWriter Error { get; }

    /// <summary>Whether standard input is redirected, so no person is typing at the other end of it.</summary>
    bool IsInputRedirected { get; }

    /// <summary>Reads one line of input.</summary>
    /// <returns>The line without its terminator, or null when the input has ended.</returns>
    string? ReadLine();
}
