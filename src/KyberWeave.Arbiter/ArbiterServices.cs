using KyberWeave.Application;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Arbiter;

/// <summary>The hook host's container: a plain service collection, built on first use and never a Host builder.</summary>
/// <remarks>
/// The hook path must start fast and must not write to stdout, so nothing here starts a host, a logger or a
/// background service. Callers resolve inside the hook's fail-open try, so a registration fault is logged and allowed.
/// </remarks>
internal static class ArbiterServices
{
    /// <summary>Builds the container over the application layer's registrations.</summary>
    /// <returns>A provider that resolves lazily.</returns>
    internal static IServiceProvider Create()
    {
        ServiceCollection services = new();
        _ = services.AddWeaveApplication();
        return services.BuildServiceProvider();
    }
}
