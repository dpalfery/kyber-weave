using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Core.Docs.Validation;
using Microsoft.Extensions.DependencyInjection;
using KyberWeave.Application;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the Docs commands from the container the real host builds, with whichever collaborators a test replaces
/// registered first. No test run reaches a paid decision model unless a test registers a picker for it.
/// </summary>
internal static class DocsTestCommands
{
    /// <summary>A picker factory that configures no decision model.</summary>
    internal sealed class NoPickerFactory : IPlanTaskAgentPickerFactory
    {
        public IPlanTaskAgentPicker? ForConfig(KyberWeaveConfig config) => null;
    }

    /// <summary>A picker factory that always returns <paramref name="picker"/>.</summary>
    internal sealed class FixedPickerFactory(Func<IPlanTaskAgentPicker> picker) : IPlanTaskAgentPickerFactory
    {
        public IPlanTaskAgentPicker? ForConfig(KyberWeaveConfig config) => picker();
    }

    /// <summary>The container, with <paramref name="configure"/> applied before the application's own registrations.</summary>
    public static IServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IPlanTaskAgentPickerFactory>(new NoPickerFactory());
        configure?.Invoke(services);
        return services.AddWeaveApplication().BuildServiceProvider();
    }

    public static DocsCloseoutCommand Closeout(IServiceProvider? services = null) =>
        new((services ?? Services()).GetRequiredService<IPlanCloseoutService>());

    public static DocsFinalizeCommand Finalize(IServiceProvider? services = null) =>
        new((services ?? Services()).GetRequiredService<IPlanFinalizeService>());

    public static DocsValidateCommand Validate(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsValidateCommand(provider.GetRequiredService<IDocsSchemaGate>(), provider.GetRequiredService<IDocumentSourceFactory>());
    }

    public static DocsPacketCommand Packet(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsPacketCommand(provider.GetRequiredService<IPlanTaskService>(), provider.GetRequiredService<IPacketHostFacts>());
    }

    public static DocsTasksCommand Tasks(IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? Services();
        return new DocsTasksCommand(provider.GetRequiredService<IPlanTaskService>(), provider.GetRequiredService<IPlanTaskAgentPickerFactory>());
    }

    public static IDocsSchemaGate SchemaGate => Services().GetRequiredService<IDocsSchemaGate>();

    public static IPacketHostFacts HostFacts => Services().GetRequiredService<IPacketHostFacts>();

    public static RunDeliveryPorts DeliveryPorts => Services().GetRequiredService<RunDeliveryPorts>();
}
