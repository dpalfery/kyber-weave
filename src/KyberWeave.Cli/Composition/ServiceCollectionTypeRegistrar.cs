using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Composition;

/// <summary>
/// Lets Spectre.Console.Cli build commands from a Microsoft dependency-injection container.
/// </summary>
/// <remarks>
/// Spectre registers its own services through this type and asks it for every command and settings type. A type the
/// container does not know is constructed directly, with any constructor parameter the container can supply, so a command
/// with no dependencies needs no registration.
/// </remarks>
internal sealed class ServiceCollectionTypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    /// <inheritdoc />
    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);

    /// <inheritdoc />
    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);

    /// <inheritdoc />
    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());

    /// <inheritdoc />
    public ITypeResolver Build() => new ServiceProviderTypeResolver(services.BuildServiceProvider());
}

/// <summary>Resolves from the container first and constructs any concrete type it does not hold.</summary>
internal sealed class ServiceProviderTypeResolver(ServiceProvider provider) : ITypeResolver, IDisposable
{
    /// <inheritdoc />
    public object? Resolve(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        object? registered = provider.GetService(type);
        if (registered is not null)
        {
            return registered;
        }

        return type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            ? ActivatorUtilities.CreateInstance(provider, type)
            : null;
    }

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();
}
