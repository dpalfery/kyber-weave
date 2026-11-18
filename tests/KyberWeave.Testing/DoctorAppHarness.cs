using System.Reflection;
using KyberWeave.Cli.Commands.Doctor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Tests;

/// <summary>
/// The in-process <c>doctor</c> boundary: the real parser, strict so an option the command does not
/// declare is refused rather than ignored, with the tested <see cref="DoctorCommand"/> instance
/// supplied in place of the one the default resolver would construct.
/// </summary>
/// <remarks>
/// Strict parsing is the point. A lax app silently ignores an option it does not know, so a RED case
/// would quietly run a different verb and the failure would name the wrong cause.
/// </remarks>
internal static class DoctorAppHarness
{
    public static int Run(DoctorCommand command, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(args);

        CommandApp app = new(new DoctorCommandRegistrar(command));
        app.Configure(configuration =>
        {
            configuration.SetApplicationName("kyber-weave");
            configuration.UseStrictParsing();
            configuration.AddCommand<DoctorCommand>("doctor");
        });

        return ProcessConsoleCapture.Run(() => app.Run(args)).Result;
    }

    private sealed class DoctorCommandRegistrar(DoctorCommand command) : ITypeRegistrar
    {
        private readonly Dictionary<Type, object> _instances = new()
        {
            [typeof(DoctorCommand)] = command,
            [typeof(IAnsiConsole)] = AnsiConsole.Console
        };

        private readonly Dictionary<Type, Func<object>> _factories = [];

        public void Register(Type service, Type implementation) =>
            _factories.TryAdd(service, () => Activator.CreateInstance(implementation)!);

        public void RegisterInstance(Type service, object instance) => _instances[service] = instance;

        public void RegisterLazy(Type service, Func<object> factory) => _factories.TryAdd(service, factory);

        public ITypeResolver Build() => new DoctorCommandResolver(_instances, _factories);
    }

    private sealed class DoctorCommandResolver(
        Dictionary<Type, object> instances,
        Dictionary<Type, Func<object>> factories) : ITypeResolver
    {
        public object? Resolve(Type? type)
        {
            if (type is null)
            {
                return null;
            }

            if (instances.TryGetValue(type, out object? instance))
            {
                return instance;
            }

            if (factories.TryGetValue(type, out Func<object>? factory))
            {
                try
                {
                    object created = factory();
                    instances[type] = created;
                    return created;
                }
                catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
                {
                    // A registration this harness cannot satisfy is left unresolved, exactly as the
                    // default resolver leaves a type it has no registration for.
                    return null;
                }
            }

            // Spectre asks for every extension point it supports as an IEnumerable<T> and iterates the
            // answer while it composes the command tree. A null answer throws there, before Execute is
            // ever reached, and app.Run reports that as -1 — a value indistinguishable from the verb
            // refusing the call, which is why a harness that answered null made every case read as
            // "the option does not exist". The harness registers no extension point, so the honest
            // answer is an empty sequence rather than null.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);
            }

            if (!type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                return Activator.CreateInstance(type);
            }

            return null;
        }
    }
}
