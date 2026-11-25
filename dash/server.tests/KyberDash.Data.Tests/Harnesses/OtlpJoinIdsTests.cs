using System.Text.Json;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Data.Repositories;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="OtlpJoinIds"/>: which OTLP attributes become the projector's join keys.
/// </summary>
public class OtlpJoinIdsTests
{
    [Fact]
    public void Read_ClaudeIds_AreTheJoinKeys()
    {
        (string? responseId, string? requestId) = OtlpJoinIds.Read(
            TranscriptReaderRepository.ClaudeHarnessId,
            Attributes(("message.id", "msg_01ABC"), ("request_id", "req_01XYZ")));

        Assert.Equal("msg_01ABC", responseId);
        Assert.Equal("req_01XYZ", requestId);
    }

    [Fact]
    public void Read_IdLongerThanTheBound_IsNoJoinKey()
    {
        string tooLong = new('a', OtlpJoinIds.MaxIdCharacters + 1);
        string atBound = new('b', OtlpJoinIds.MaxIdCharacters);

        (string? responseId, string? requestId) = OtlpJoinIds.Read(
            TranscriptReaderRepository.ClaudeHarnessId,
            Attributes(("message.id", tooLong), ("request_id", atBound)));

        Assert.Null(responseId);
        Assert.Equal(atBound, requestId);
    }

    private static Dictionary<string, JsonElement> Attributes(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(
            static pair => pair.Key,
            static pair => JsonSerializer.SerializeToElement(pair.Value),
            StringComparer.Ordinal);
}
