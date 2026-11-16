using KyberWeave.Application;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Resolves the Arbiter's registered ports the way the entry apps do, for tests that build a command or evaluator directly.</summary>
internal static class ArbiterTestServices
{
    private static readonly Lazy<IServiceProvider> Provider = new(() => new ServiceCollection().AddWeaveApplication().BuildServiceProvider());

    /// <summary>Resolves <typeparamref name="T"/> from the application layer's registrations.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal static T Get<T>()
        where T : notnull =>
        Provider.Value.GetRequiredService<T>();
}
