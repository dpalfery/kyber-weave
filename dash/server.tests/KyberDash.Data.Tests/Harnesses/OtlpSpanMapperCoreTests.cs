using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="OtlpSpanMapperCore.ResolveGenericOp"/> — the operation
/// classification every OTLP span mapper shares, so one harness cannot misclassify a span
/// another classifies correctly.
/// </summary>
public class OtlpSpanMapperCoreTests
{
    [Fact]
    public void ResolveGenericOp_ToolNameAttribute_MapsToolInvoke()
    {
        OtlpSpanNative native = Native(("gen_ai.tool.name", "\"Bash\""));

        Assert.Equal(RecordOp.ToolInvoke, OtlpSpanMapperCore.ResolveGenericOp(native));
    }

    [Fact]
    public void ResolveGenericOp_InvokeAgentOperationName_MapsAgentInvoke()
    {
        OtlpSpanNative native = Native(("gen_ai.operation.name", "\"invoke_agent\""));

        Assert.Equal(RecordOp.AgentInvoke, OtlpSpanMapperCore.ResolveGenericOp(native));
    }

    [Fact]
    public void ResolveGenericOp_InvokeAgentSpanNameWithSuffix_MapsAgentInvoke()
    {
        OtlpSpanNative native = Native(name: "invoke_agent:writer");

        Assert.Equal(RecordOp.AgentInvoke, OtlpSpanMapperCore.ResolveGenericOp(native));
    }

    [Fact]
    public void ResolveGenericOp_TokenAttributesWithoutToolOrAgentSignals_MapsLlmInvoke()
    {
        OtlpSpanNative native = Native(("gen_ai.usage.input_tokens", "100"), ("gen_ai.usage.output_tokens", "30"));

        Assert.Equal(RecordOp.LlmInvoke, OtlpSpanMapperCore.ResolveGenericOp(native));
    }

    [Fact]
    public void ResolveGenericOp_NoRecognizedSignal_MapsUnspecified()
    {
        OtlpSpanNative native = Native(("gen_ai.session.id", "\"s-1\""));

        Assert.Equal(RecordOp.Unspecified, OtlpSpanMapperCore.ResolveGenericOp(native));
    }

    private static OtlpSpanNative Native(params (string Key, string Json)[] attributes) =>
        Native("test.span", attributes);

    private static OtlpSpanNative Native(string name, params (string Key, string Json)[] attributes)
    {
        OtlpSpan span = new(
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "aabbccddeeff0011",
            ParentSpanId: "1122334455667788",
            Name: name,
            Kind: "client",
            StartTimeUnixNano: "1763550000000000000",
            EndTimeUnixNano: "1763550001250000000",
            Timestamp: "2026-11-19T11:00:00.000Z",
            DurationMs: 10.0,
            Status: new OtlpStatus("ok"),
            Attributes: attributes.ToDictionary(
                attribute => attribute.Key,
                attribute => JsonSerializer.Deserialize<JsonElement>(attribute.Json),
                StringComparer.Ordinal),
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope("test", "1.0.0"));

        return OtlpSpanNative.Parse(span);
    }
}
