using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The configured provider factory over its seams: the environment answers the key, the handler factory supplies the
/// transport and the credential store is a fake, so no variable, socket or keychain is touched.
/// </summary>
public sealed class ConfiguredArbiterProviderFactoryTests
{
    private const string Remote = "https://api.typesafe.ai/v1";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);

    private readonly CountingHandlers _handlers = new();

    private ConfiguredArbiterProviderFactory Factory(
        string? environmentKey = null,
        string? storedKey = null,
        Func<ICredentialStore>? store = null) =>
        new(
            new RepositoryProviderSettings(),
            new FakeProcessEnvironment(readVariable: name => name == ArbiterKeyResolver.EnvVarName ? environmentKey : null),
            _handlers,
            new ImmediateClock(Now),
            store ?? (() => new FixedCredentialStore(storedKey)));

    private static ArbiterProviderConfig Config(string endpoint, ArbiterProviderKind kind = ArbiterProviderKind.Systemone) =>
        new(kind, endpoint, ArbiterProviderTestSupport.Model, 5000);

    [Fact]
    public void ProviderNone_AnswersNullAndCreatesNoTransport()
    {
        Assert.Null(Factory(environmentKey: "k").TryCreate(Config(Remote, ArbiterProviderKind.None), "/kw/home"));
        Assert.Equal(0, _handlers.Created);
    }

    [Fact]
    public void ARemoteEndpointWithNoKey_AnswersNullAndCreatesNoTransport()
    {
        Assert.Null(Factory().TryCreate(Config(Remote), "/kw/home"));
        Assert.Equal(0, _handlers.Created);
    }

    [Fact]
    public void ACredentialStoreThatCannotBeBuilt_AnswersNullForARemoteEndpointWithNoEnvironmentKey()
    {
        ConfiguredArbiterProviderFactory factory = Factory(store: () => throw new InvalidOperationException("no store"));

        Assert.Null(factory.TryCreate(Config(Remote), "/kw/home"));
    }

    [Fact]
    public void ALoopbackEndpoint_NeedsNoKeyAndRunsOverTheTransportTheSeamSupplies()
    {
        IArbiterProvider? provider = Factory(store: () => throw new InvalidOperationException("no store"))
            .TryCreate(Config("http://127.0.0.1:11434"), "/kw/home");

        Assert.NotNull(provider);
        Assert.Equal(1, _handlers.Created);
        (provider as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task TheKeyTheEnvironmentSeamAnswersTravelsInTheAuthorizationHeader()
    {
        QueuedHandler transport = new(ArbiterProviderTestSupport.Answer());
        _handlers.Transport = transport;
        IArbiterProvider provider = Factory(environmentKey: "env-key").TryCreate(Config(Remote), "/kw/home")!;

        _ = await provider.AskStep1Async([ArbiterProviderTestSupport.Rule()], ArbiterProviderTestSupport.Facts());
        (provider as IDisposable)?.Dispose();

        Assert.Equal("env-key", Assert.Single(transport.Authorizations)!.Parameter);
        Assert.Equal(1, _handlers.Created);
    }

    private sealed class RepositoryProviderSettings : IArbiterUserSettings
    {
        public ArbiterProviderConfig ApplyTo(ArbiterProviderConfig repository, string homeDirectory) => repository;
    }

    private sealed class CountingHandlers : IArbiterHttpHandlers
    {
        internal int Created { get; private set; }

        internal HttpMessageHandler Transport { get; set; } = new QueuedHandler();

        public HttpMessageHandler Create()
        {
            Created++;
            return Transport;
        }
    }
}
