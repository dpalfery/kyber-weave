using KyberWeave.Application;
using KyberWeave.Cli.Commands.Arbiter;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Resolves the Arbiter's registered ports the way the entry apps do, for tests that build a command or evaluator directly.</summary>
internal static class ArbiterTestServices
{
    private static readonly Lazy<IServiceProvider> Provider = new(() => new ServiceCollection().AddWeaveApplication().BuildServiceProvider());

    private static readonly Lazy<ArbiterTestHost> Host = new(() => ArbiterTestHost.Create());

    /// <summary>Builds an Arbiter command from the container, over the real adapters, the way the host builds it.</summary>
    /// <typeparam name="T">The command type.</typeparam>
    internal static T Command<T>()
        where T : class =>
        Host.Value.Command<T>();

    /// <summary>Builds the comment command over the container, with the provider a test answers instead of a configured one.</summary>
    /// <param name="providerFactory">Builds the provider for a repository root.</param>
    internal static ArbiterCommentCommand CommentCommand(
        Func<string, KyberWeave.Core.Diagnostics.DiagnosticReport, KyberWeave.Core.Arbiter.Providers.IArbiterProvider?> providerFactory)
    {
        ArbiterCommentCommand command = Command<ArbiterCommentCommand>();
        command.ProviderFactory = providerFactory;
        return command;
    }

    /// <summary>Resolves <typeparamref name="T"/> from the application layer's registrations.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal static T Get<T>()
        where T : notnull =>
        Provider.Value.GetRequiredService<T>();
}
