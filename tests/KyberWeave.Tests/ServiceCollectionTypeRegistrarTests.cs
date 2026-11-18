using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The type registrar that lets Spectre.Console.Cli build commands from the application container.</summary>
public sealed class ServiceCollectionTypeRegistrarTests
{
    [Fact]
    public void ARegisteredServiceComesFromTheContainer()
    {
        ServiceCollection services = [];
        FakeProcessEnvironment environment = new(currentDirectory: "/work", homeDirectory: "/home", tempDirectory: "/tmp");
        ITypeRegistrar registrar = new ServiceCollectionTypeRegistrar(services);
        registrar.RegisterInstance(typeof(IProcessEnvironment), environment);

        using ServiceProviderTypeResolver resolver = Assert.IsType<ServiceProviderTypeResolver>(registrar.Build());

        Assert.Same(environment, resolver.Resolve(typeof(IProcessEnvironment)));
    }

    [Fact]
    public void AConcreteTypeTheContainerDoesNotHoldIsConstructedWithWhatTheContainerCanSupply()
    {
        ServiceCollection services = [];
        FakeProcessEnvironment environment = new(currentDirectory: "/work", homeDirectory: "/home", tempDirectory: "/tmp");
        services.AddSingleton<IProcessEnvironment>(environment);
        ITypeRegistrar registrar = new ServiceCollectionTypeRegistrar(services);

        object? built = registrar.Build().Resolve(typeof(RegistrarCommandWithDependency));

        RegistrarCommandWithDependency command = Assert.IsType<RegistrarCommandWithDependency>(built);
        Assert.Same(environment, command.Environment);
        Assert.NotNull(registrar.Build().Resolve(typeof(RegistrarCommandWithoutDependencies)));
    }

    [Fact]
    public void AnUnregisteredInterfaceOrAbstractTypeResolvesToNull()
    {
        ITypeRegistrar registrar = new ServiceCollectionTypeRegistrar(new ServiceCollection());
        ITypeResolver resolver = registrar.Build();

        Assert.Null(resolver.Resolve(typeof(IProductRootLocator)));
        Assert.Null(resolver.Resolve(typeof(Stream)));
        Assert.Null(resolver.Resolve(null));
    }

    [Fact]
    public void ALazyRegistrationIsBuiltOnFirstUse()
    {
        ITypeRegistrar registrar = new ServiceCollectionTypeRegistrar(new ServiceCollection());
        int built = 0;
        registrar.RegisterLazy(typeof(IProcessEnvironment), () =>
        {
            built++;
            return new FakeProcessEnvironment(currentDirectory: "/work", homeDirectory: "/home", tempDirectory: "/tmp");
        });
        ITypeResolver resolver = registrar.Build();

        Assert.Equal(0, built);
        _ = resolver.Resolve(typeof(IProcessEnvironment));
        _ = resolver.Resolve(typeof(IProcessEnvironment));
        Assert.Equal(1, built);
    }
}

/// <summary>A command-shaped type with nothing to inject.</summary>
public sealed class RegistrarCommandWithoutDependencies;

/// <summary>A command-shaped type that takes the process environment.</summary>
/// <param name="environment">The injected environment.</param>
public sealed class RegistrarCommandWithDependency(IProcessEnvironment environment)
{
    /// <summary>Gets the injected environment.</summary>
    public IProcessEnvironment Environment { get; } = environment;
}
