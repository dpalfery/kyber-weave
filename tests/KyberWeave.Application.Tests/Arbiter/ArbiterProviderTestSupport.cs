using System.Net;
using System.Text;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>The one question and fact set the provider tests ask, and the handler and clock that answer it offline.</summary>
internal static class ArbiterProviderTestSupport
{
    internal const string RuleId = "KW-ARB-SCOPE-002";
    internal const string Model = "jev-1.13.0";

    internal static ArbiterFactSet Facts() => new ArbiterFactSet()
        .With("plan.task.text", "Migrate the login flow.", ArbiterFactLabel.Asserted)
        .With("delegation.prompt", "Migrate the login flow to the new SDK.", ArbiterFactLabel.Asserted);

    internal static ArbiterRule Rule() => new(
        RuleId,
        "delegate",
        "Does the delegation ask for work the plan task does not describe?",
        ["within-task", "adds-work"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["within-task"] = RuleEffects.Allow,
            ["adds-work"] = RuleEffects.Escalate,
        },
        Ask: new RuleAsk(
            "choice",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["task"] = "plan.task.text",
                ["delegation"] = "delegation.prompt",
            },
            Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["within-task"] = "Every piece of work the delegation asks for is described by the task.",
                ["adds-work"] = "The delegation asks for work the task does not describe.",
            },
            ConfidenceAtLeast: 0.8,
            TunedFor: [Model]));

    internal static HttpResponseMessage Answer() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"model\":\"" + Model + "\",\"answers\":{\"" + RuleId + "\":" +
            "{\"type\":\"choice\",\"choice\":\"within-task\"," +
            "\"probabilities\":{\"within-task\":0.9,\"adds-work\":0.1},\"confidence\":0.9}}}",
            Encoding.UTF8,
            "application/json"),
    };
}

/// <summary>A transport that answers from a queue and records what it was sent.</summary>
internal sealed class QueuedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    internal List<System.Net.Http.Headers.AuthenticationHeaderValue?> Authorizations { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Authorizations.Add(request.Headers.Authorization);
        return Task.FromResult(_responses.Dequeue());
    }
}

/// <summary>A clock that stands still at one instant and fires every timer at once, so a retry wait costs no time.</summary>
/// <param name="now">The instant the clock reports.</param>
internal sealed class ImmediateClock(DateTimeOffset now) : TimeProvider
{
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => now;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _ = Task.Run(() => callback(state));
        return new SpentTimer();
    }

    private sealed class SpentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A credential store that answers one key, or none.</summary>
/// <param name="key">The key every origin answers.</param>
internal sealed class FixedCredentialStore(string? key) : ICredentialStore
{
    /// <inheritdoc />
    public string? Read(string origin) => key;

    /// <inheritdoc />
    public void Write(string origin, string key1) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Delete(string origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(string origin, CancellationToken cancellationToken = default) =>
        new(key);

    /// <inheritdoc />
    public ValueTask WriteAsync(string origin, string key1, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public ValueTask DeleteAsync(string origin, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
