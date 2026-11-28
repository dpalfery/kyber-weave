using KyberWeave.Application;
using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.RunStart;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the Arbiter commands over the container the way the real host builds them, with the collaborators a test
/// replaces registered before the host's own. The container is the one <c>CliApplication.Create</c> assembles.
/// </summary>
internal sealed class ArbiterTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    private ArbiterTestHost(ServiceProvider provider) => _provider = provider;

    /// <summary>Builds a host. The fakes are registered first, so a registration made here wins over the real adapters.</summary>
    /// <param name="configure">Registers the test's fakes.</param>
    internal static ArbiterTestHost Create(Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = [];
        configure?.Invoke(services);
        services.TryAddSingleton<IRepositoryToolSettings, KyberWeave.Core.Doctor.ConfigFileToolSettings>();
        _ = services.AddSquadCliServices();
        _ = services.AddArbiterCliServices();
        _ = services.AddWeaveApplication();
        return new ArbiterTestHost(services.BuildServiceProvider());
    }

    /// <summary>Resolves a registered service.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal T Get<T>()
        where T : notnull =>
        _provider.GetRequiredService<T>();

    /// <summary>Builds a command, resolving each constructor parameter from the container.</summary>
    /// <typeparam name="T">The command type.</typeparam>
    internal T Command<T>()
        where T : class =>
        ActivatorUtilities.CreateInstance<T>(_provider);

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}

/// <summary>The key input a test scripts: what stdin yields and what the masked prompt yields.</summary>
/// <param name="stdin">Gets the stdin line.</param>
/// <param name="prompt">Gets the prompted key.</param>
internal sealed class FakeArbiterKeyInput(Func<string?> stdin, Func<string?> prompt) : IArbiterKeyInput
{
    /// <inheritdoc />
    public string? ReadFromStandardInput() => stdin();

    /// <inheritdoc />
    public string? PromptHidden() => prompt();
}

/// <summary>A transport factory that hands out whatever handler a test supplies.</summary>
/// <param name="create">Creates the handler.</param>
internal sealed class FakeArbiterHttpHandlers(Func<HttpMessageHandler> create) : IArbiterHttpHandlers
{
    /// <inheritdoc />
    public HttpMessageHandler Create() => create();
}

/// <summary>A git runner a test scripts, in place of starting git.</summary>
/// <param name="run">Answers one git invocation.</param>
internal sealed class FakeGitCommandRunner(Func<string, IReadOnlyList<string>, GitOutcome> run) : IGitCommandRunner
{
    /// <inheritdoc />
    public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments) => run(workingDirectory, arguments);

    /// <inheritdoc />
    public Task<GitOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(run(workingDirectory, arguments));
}

/// <summary>A credential store that forwards to whichever store a test currently holds.</summary>
/// <param name="current">Gets the store to forward to.</param>
internal sealed class ForwardingCredentialStore(Func<ICredentialStore> current) : ICredentialStore
{
    /// <inheritdoc />
    public string? Read(string origin) => current().Read(origin);

    /// <inheritdoc />
    public void Write(string origin, string key) => current().Write(origin, key);

    /// <inheritdoc />
    public void Delete(string origin) => current().Delete(origin);
}
