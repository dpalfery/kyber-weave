using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Text;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task 3.2 contract: <c>arbiter setup | status | doctor</c> manage the per-user
/// provider override and diagnose the host installation.
/// RED: <c>setup</c>, <c>status</c> and <c>doctor</c> are not registered.
/// </summary>
public sealed class ArbiterSetupCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _home = new();
    private readonly FakeCredentialStore _store = new();
    private ICredentialStore? _storeOverride;

    private readonly ArbiterTestHost _host;

    private Func<HttpRequestMessage, HttpResponseMessage> _ollamaResponder = _ => NotFound();
    private string? _keyStdinValue;
    private string? _keyPromptValue;
    private Func<string, IReadOnlyList<string>, GitOutcome> _runGit =
        (_, _) => new GitOutcome(1, string.Empty, string.Empty);
    private ProcessResult _probeResult = new(1, string.Empty, "missing");

    public ArbiterSetupCommandTests()
    {
        // Every collaborator the commands take is registered here as a fake, so a command resolved from this host
        // reads the injected home, store and stubs and never the real home, keychain, network, git or PATH.
        _host = ArbiterTestHost.Create(services =>
        {
            services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(() => _home.Path));
            services.AddSingleton<ICredentialStore>(new ForwardingCredentialStore(() => _storeOverride ?? _store));
            services.AddSingleton<IArbiterKeyInput>(new FakeArbiterKeyInput(() => _keyStdinValue, () => _keyPromptValue));
            services.AddSingleton<IArbiterHttpHandlers>(new FakeArbiterHttpHandlers(() => new StubOllamaHandler(_ollamaResponder)));
            services.AddSingleton<IGitCommandRunner>(new FakeGitCommandRunner((directory, arguments) => _runGit(directory, arguments)));
            services.AddSingleton<IProcessExecutor>(new DelegateProbeExecutor(() => _probeResult));
        });
    }

    public void Dispose()
    {
        _host.Dispose();
        _temp.Dispose();
        _home.Dispose();
    }

    [Fact]
    public void Setup_WritesOverrideAndStore_NeverEchoing()
    {
        const string key = "TS-SETUP-KEY-7f3a9c1e";
        _keyStdinValue = key;
        string host = NewDir("setup-cloud");

        ArbiterSetupCommand command = _host.Command<ArbiterSetupCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Provider = "systemone",
                Endpoint = "https://api.typesafe.ai/v1",
                Model = "jev-1.13.0",
                KeyStdin = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        string written = File.ReadAllText(OverridePath());
        Assert.Contains("systemone", written, StringComparison.Ordinal);
        Assert.Contains("https://api.typesafe.ai/v1", written, StringComparison.Ordinal);
        Assert.DoesNotContain(key, written, StringComparison.Ordinal);
        Assert.Equal(key, _store.Read("https://api.typesafe.ai"));
        Assert.DoesNotContain(key, execution.Output, StringComparison.Ordinal);
        Assert.Contains("systemone", execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    // The model string went into the YAML verbatim, so a value carrying YAML syntax
    // either corrupted the file or wrote something the reader did not see. What matters
    // is the round trip: what setup accepts must be what the next run reads back.
    [InlineData("jev-1.13.0")]
    [InlineData("model:with:colons")]
    [InlineData("model \"quoted\"")]
    [InlineData("model #hash")]
    [InlineData("model # not-a-comment")]
    [InlineData("model 'single'")]
    [InlineData("model\\backslash")]
    [InlineData("  padded  ")]
    public void Setup_WritesAModelThatRoundTripsThroughTheUserSettings(string model)
    {
        string host = NewDir("setup-model");
        ArbiterSetupCommand command = _host.Command<ArbiterSetupCommand>();

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Provider = "none",
                Endpoint = "https://api.typesafe.ai/v1",
                Model = model,
            }));

        Assert.Equal(0, execution.ExitCode);
        ArbiterProviderConfig applied = _host.Get<IArbiterUserSettings>().ApplyTo(
            ArbiterConfigDefaults.Product.Provider,
            _home.Path);
        Assert.Equal(model.Trim(), applied.Model);
    }

    [Theory]
    // A newline cannot be quoted in a YAML scalar, so it is rejected rather than
    // written as a value that silently swallows the rest of the document.
    [InlineData("model\nendpoint: https://evil.example/v1")]
    [InlineData("model\r\nother: value")]
    [InlineData("model\ttab")]
    public void Setup_RejectsAModelCarryingControlCharacters(string model)
    {
        string host = NewDir("setup-control");

        CommandExecution execution = Capture(() => _host.Command<ArbiterSetupCommand>().Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Provider = "none",
                Endpoint = "https://api.typesafe.ai/v1",
                Model = model,
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.DoesNotContain("evil.example", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_MaskedPrompt_StoresKeyWithoutEchoing()
    {
        const string key = "TS-PROMPT-KEY-1a2b3c4d";
        _keyPromptValue = key;
        string host = NewDir("setup-prompt");

        CommandExecution execution = Capture(() => _host.Command<ArbiterSetupCommand>().Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Provider = "systemone",
                Endpoint = "https://api.typesafe.ai/v1",
                Model = "jev-1.13.0",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(key, _store.Read("https://api.typesafe.ai"));
        Assert.DoesNotContain(key, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_LocalOllama_SuggestsNimble()
    {
        _ollamaResponder = _ => JsonResponse("""{"version":"0.35.0"}""");
        string host = NewDir("setup-ollama");

        CommandExecution execution = Capture(() => _host.Command<ArbiterSetupCommand>().Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Provider = "systemone",
                Endpoint = "http://localhost:11434/v1",
                Model = "nimble",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("0.35", execution.Output, StringComparison.Ordinal);
        Assert.Contains("nimble", execution.Output, StringComparison.Ordinal);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public void Setup_Tev1_WarnsSmallInputBudget()
    {
        _ollamaResponder = _ => JsonResponse("""{"version":"0.35.0"}""");
        string host = NewDir("setup-tev1");

        CommandExecution execution = Capture(() => _host.Command<ArbiterSetupCommand>().Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Provider = "systemone",
                Endpoint = "http://localhost:11434/v1",
                Model = "tev1",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("tev1", execution.Output, StringComparison.Ordinal);
        Assert.Contains("2,000", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_ProviderNone_WritesNoneWithoutKey()
    {
        string host = NewDir("setup-none");

        CommandExecution execution = Capture(() => _host.Command<ArbiterSetupCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host, Provider = "none" }));

        Assert.Equal(0, execution.ExitCode);

        // Quoted: every value setup writes is a single-quoted YAML scalar.
        Assert.Contains("kind: 'none'", File.ReadAllText(OverridePath()), StringComparison.Ordinal);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public void Setup_UnknownProvider_ExitsOne()
    {
        string host = NewDir("setup-bad-provider");

        CommandExecution execution = Capture(() => _host.Command<ArbiterSetupCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host, Provider = "no-such-provider" }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-ARB-CONFIG-001", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ShowsProviderModelOriginAndKeyPresence_NeverValue()
    {
        const string key = "TS-STATUS-KEY-4b2d8f0a";
        WriteOverride("provider:\n  kind: systemone\n  endpoint: https://api.typesafe.ai/v1\n  model: jev-1.13.0\n");
        _store.Entries["https://api.typesafe.ai"] = key;
        string host = NewDir("status-found");

        CommandExecution execution = Capture(() => _host.Command<ArbiterStatusCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("systemone", execution.Output, StringComparison.Ordinal);
        Assert.Contains("jev-1.13.0", execution.Output, StringComparison.Ordinal);
        Assert.Contains("https://api.typesafe.ai", execution.Output, StringComparison.Ordinal);
        Assert.Contains("found", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(key, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_MissingKey_ReportsKey001WithoutValue()
    {
        WriteOverride("provider:\n  kind: systemone\n  endpoint: https://api.typesafe.ai/v1\n  model: jev-1.13.0\n");
        string host = NewDir("status-missing");

        CommandExecution execution = Capture(() => _host.Command<ArbiterStatusCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains(ArbiterStatusCommand.KeyMissing, execution.Output, StringComparison.Ordinal);
        Assert.Contains("missing", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_ProviderNone_RaisesConfig006()
    {
        string host = NewDir("doctor-006");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host, Format = "json" }));

        Assert.Contains("KW-ARB-CONFIG-006", execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("copilot-cli")]
    [InlineData("codex")]
    [InlineData("cursor")]
    public void Doctor_NamesEachAdvisoryPostDispatchTarget(string target)
    {
        string host = NewDir("doctor-advisory-" + target);

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        // The runbook promises doctor surfaces every target whose post-dispatch
        // findings are advisory; none may be silently missing.
        Assert.Contains("(" + target + ")", execution.Output, StringComparison.Ordinal);
        Assert.Contains("advisory", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_UntunedModel_RaisesConfig007()
    {
        WriteOverride("provider:\n  kind: systemone\n  endpoint: https://api.typesafe.ai/v1\n  model: mystery-model\n");
        string host = NewDir("doctor-007");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host, Format = "json" }));

        Assert.Contains("KW-ARB-CONFIG-007", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_BadOverride_RaisesConfig009()
    {
        WriteOverride("provider:\n  model: nimble\nextra: 1\n");
        string host = NewDir("doctor-009");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host, Format = "json" }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-ARB-CONFIG-009", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_PlainHttpEndpoint_RaisesConfig010()
    {
        WriteOverride("provider:\n  kind: systemone\n  endpoint: http://example.com/v1\n  model: jev-1.13.0\n");
        string host = NewDir("doctor-010");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host, Format = "json" }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-ARB-CONFIG-010", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_MissingKey_RaisesKey001()
    {
        WriteOverride("provider:\n  kind: systemone\n  endpoint: https://api.typesafe.ai/v1\n  model: jev-1.13.0\n");
        string host = NewDir("doctor-key");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains(ArbiterStatusCommand.KeyMissing, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_SecretToolMissing_ReportsKey001WithActionableText()
    {
        WriteOverride("provider:\n  kind: systemone\n  endpoint: https://api.typesafe.ai/v1\n  model: jev-1.13.0\n");
        _storeOverride = new SecretServiceCredentialStore(new ArbiterSecretToolMissingTests.MissingExecutableCredentialProcessRunner());
        string host = NewDir("doctor-no-secret-tool");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains(ArbiterStatusCommand.KeyMissing, execution.Output, StringComparison.Ordinal);
        Assert.Contains("TYPESAFE_API_KEY", execution.Output, StringComparison.Ordinal);
        Assert.Contains("libsecret-tools", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Key: found", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred trying to start process", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_SecretToolMissing_ExitsOneWithActionableText()
    {
        _keyStdinValue = "TS-NOTOOL-KEY-0c1d2e3f";
        _storeOverride = new SecretServiceCredentialStore(new ArbiterSecretToolMissingTests.MissingExecutableCredentialProcessRunner());
        string host = NewDir("setup-no-secret-tool");

        CommandExecution execution = Capture(() => _host.Command<ArbiterSetupCommand>().Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Provider = "systemone",
                Endpoint = "https://api.typesafe.ai/v1",
                Model = "jev-1.13.0",
                KeyStdin = true,
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("TYPESAFE_API_KEY", execution.Output, StringComparison.Ordinal);
        Assert.Contains("libsecret-tools", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_keyStdinValue, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_UnignoredLog_RaisesLog001()
    {
        _runGit = (_, arguments) =>
            arguments.Contains("check-ignore")
                ? new GitOutcome(1, string.Empty, string.Empty)
                : new GitOutcome(1, string.Empty, "not a repo");
        string host = NewDir("doctor-log");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains(ArbiterDoctorCommand.LogNotIgnored, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_MissingBinary_RaisesBin001()
    {
        _probeResult = new ProcessResult(1, string.Empty, "missing");
        string host = NewDir("doctor-bin");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains(ArbiterDoctorCommand.BinaryMissing, execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_NoIndexes_RaisesGuard001()
    {
        string host = NewDir("doctor-guard");

        CommandExecution execution = Capture(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains(ArbiterDoctorCommand.GuardWithoutIndex, execution.Output, StringComparison.Ordinal);
    }

    private string OverridePath() =>
        Path.Combine(_home.Path, ".config", "kyber-weave", "arbiter.yml");

    private void WriteOverride(string yaml)
    {
        string dir = Path.Combine(_home.Path, ".config", "kyber-weave");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "arbiter.yml"), yaml);
    }

    private string NewDir(string name)
    {
        string dir = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

        public List<(string Origin, string Key)> Writes { get; } = [];

        public string? Read(string origin)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            return Entries.TryGetValue(origin, out string? key) ? key : null;
        }

        public void Write(string origin, string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            ArgumentException.ThrowIfNullOrEmpty(key);
            Writes.Add((origin, key));
            Entries[origin] = key;
        }
    }

    private sealed class StubOllamaHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class DelegateProbeExecutor(Func<ProcessResult> next) : IProcessExecutor
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) => next();
    }
}
