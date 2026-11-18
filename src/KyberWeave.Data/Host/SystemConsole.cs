using KyberWeave.Contracts.Host;

namespace KyberWeave.Data.Host;

/// <summary>The real console: the process's own standard output, standard error and standard input.</summary>
/// <remarks>
/// Every member reads the current <see cref="Console"/> writer or reader rather than caching it, so a test that
/// redirects the console sees its own writers through this same adapter.
/// </remarks>
public sealed class SystemConsole : IConsole
{
    /// <inheritdoc />
    public TextWriter Out => Console.Out;

    /// <inheritdoc />
    public TextWriter Error => Console.Error;

    /// <inheritdoc />
    public bool IsInputRedirected => Console.IsInputRedirected;

    /// <inheritdoc />
    public string? ReadLine() => Console.ReadLine();
}
