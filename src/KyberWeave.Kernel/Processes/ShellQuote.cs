namespace KyberWeave.Core.Processes;

/// <summary>Quotes one argument so a command line printed for a person to paste runs as printed.</summary>
public static class ShellQuote
{
    /// <summary>
    ///     Quotes <paramref name="value" /> for the local shell. POSIX shells get single quotes, which keep every
    ///     character literal, with any single quote spliced in as <c>'\''</c>. Windows gets double quotes, which
    ///     cannot appear in a Windows path.
    /// </summary>
    /// <param name="value">The argument to quote.</param>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return OperatingSystem.IsWindows()
            ? "\"" + value + "\""
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
