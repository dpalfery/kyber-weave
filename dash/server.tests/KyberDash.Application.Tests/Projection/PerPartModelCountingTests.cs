using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// A session that switches models counts each content part with the tokenizer of the model the
/// part was sent to, never with the session's first observed model (decision D17).
/// </summary>
public class PerPartModelCountingTests
{
    /// <summary>
    /// Two turns under two models: each turn's uncounted parts reach the tokenizer with that
    /// turn's own model, and the second turn's figures are the fake tokenizer's word counts.
    /// </summary>
    [Fact]
    public void BuildSessionSummary_CountsEachPartWithItsOwnModel()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        FakeTextTokenizer tokenizer = new();
        CanonSessionProjectorEngine projector = new(store, store, store, pricing, tokenizer);

        CanonicalRecord first = Turn("span-a", "2026-11-24T10:00:00Z", "model-alpha", "alpha one two");
        CanonicalRecord second = Turn("span-b", "2026-11-24T10:01:00Z", "model-beta", "beta one two three four");

        SessionSummaryRow row = projector.BuildSessionSummary("session-two-models", [first, second]);

        Assert.NotNull(row);
        IReadOnlyList<(string Text, string? Model)> calls = tokenizer.Calls;
        Assert.Contains(("alpha one two", (string?)"model-alpha"), calls);
        Assert.Contains(("beta one two three four", (string?)"model-beta"), calls);
        Assert.DoesNotContain(("beta one two three four", (string?)"model-alpha"), calls);
        Assert.DoesNotContain(("alpha one two", (string?)"model-beta"), calls);
    }

    private static CanonicalRecord Turn(string spanId, string timestamp, string model, string history) =>
        new()
        {
            SpanId = spanId,
            Source = "test",
            Harness = "claude-code",
            SessionId = "session-two-models",
            Name = "llm.invoke",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = timestamp,
            Status = "ok",
            Tokens = new TokenUsage(FreshInput: 1_000, CacheRead: 0, CacheCreation: 0, Output: 10, ReportedInput: 1_000, ReportedOutput: 10),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Parts = [new ContentPart("conversation_history", history)],
            Cost = new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.request.model"] = JsonSerializer.SerializeToElement(model),
            },
        };
}
