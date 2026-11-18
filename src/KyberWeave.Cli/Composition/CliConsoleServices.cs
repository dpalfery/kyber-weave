using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console;

namespace KyberWeave.Cli.Composition;

/// <summary>Registers the one console every command writes to.</summary>
internal static class CliConsoleServices
{
    /// <summary>
    /// Registers a console that forwards to the process-wide <see cref="AnsiConsole.Console"/> on every call, with
    /// <c>TryAdd</c>, so a test's own registration wins. This composition root is the one place allowed to name the
    /// static console; forwarding rather than snapshotting keeps a command bound to whichever console is current.
    /// </summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddCliConsole(this IServiceCollection services)
    {
        services.TryAddSingleton<IAnsiConsole>(_ => new ForwardingAnsiConsole(() => AnsiConsole.Console));
        return services;
    }
}
