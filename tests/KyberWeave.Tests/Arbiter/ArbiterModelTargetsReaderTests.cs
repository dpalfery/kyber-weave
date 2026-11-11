using KyberWeave.Core.Arbiter.ModelTargets;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the Arbiter model-targets reader: <c>model-targets.&lt;harness&gt;.json</c> in the
/// arbiter directory. A valid file yields every field, with null profile tokens kept as null.
/// Any absent, unreadable, mis-schemed, or other-harness file yields null, and no exception escapes.
/// RED: <c>ArbiterModelTargets</c> does not exist yet.
/// </summary>
public sealed class ArbiterModelTargetsReaderTests : IDisposable
{
    private const string ValidJson =
        "{\"schema\":\"kyber-arbiter.model-targets/v1\",\"harness\":\"claude\"," +
        "\"preset\":{\"name\":\"work\",\"digest\":\"d\"},\"bundle-digest\":\"b\"," +
        "\"profiles\":{\"fast\":\"claude-haiku-5-5\",\"general\":\"claude-sonnet-5-5\"," +
        "\"architect\":\"opus\",\"orchestration\":null}," +
        "\"agents\":{\"csharp-dev\":{\"profile\":\"general\",\"token\":\"claude-sonnet-5-5\"}}}";

    private readonly TempDirectory _arbiterDirectory = new();

    public void Dispose() => _arbiterDirectory.Dispose();

    private void WriteTargets(string harness, string content) =>
        File.WriteAllText(Path.Combine(_arbiterDirectory.Path, $"model-targets.{harness}.json"), content);

    [Fact]
    public void ValidFileReadsEveryField()
    {
        WriteTargets("claude", ValidJson);

        ArbiterModelTargets? targets = ArbiterModelTargets.TryRead(_arbiterDirectory.Path, "claude");

        Assert.NotNull(targets);
        Assert.Equal("kyber-arbiter.model-targets/v1", targets.Schema);
        Assert.Equal("claude", targets.Harness);
        Assert.Equal("work", targets.Preset.Name);
        Assert.Equal("d", targets.Preset.Digest);
        Assert.Equal("b", targets.BundleDigest);
        Assert.Equal("claude-haiku-5-5", targets.Profiles["fast"]);
        Assert.Equal("claude-sonnet-5-5", targets.Profiles["general"]);
        Assert.Equal("opus", targets.Profiles["architect"]);
        Assert.Equal(4, targets.Profiles.Count);
        Assert.True(targets.Profiles.ContainsKey("orchestration"));
        Assert.Null(targets.Profiles["orchestration"]);
        Assert.Equal("general", targets.Agents["csharp-dev"].Profile);
        Assert.Equal("claude-sonnet-5-5", targets.Agents["csharp-dev"].Token);
    }

    [Fact]
    public void AgentWithNullTokenKeepsTokenAsNull()
    {
        WriteTargets("claude", ValidJson.Replace(
            "\"token\":\"claude-sonnet-5-5\"", "\"token\":null", StringComparison.Ordinal));

        ArbiterModelTargets? targets = ArbiterModelTargets.TryRead(_arbiterDirectory.Path, "claude");

        Assert.NotNull(targets);
        Assert.Equal("general", targets.Agents["csharp-dev"].Profile);
        Assert.Null(targets.Agents["csharp-dev"].Token);
    }

    [Fact]
    public void MissingFileReturnsNull()
    {
        ArbiterModelTargets? targets = ArbiterModelTargets.TryRead(_arbiterDirectory.Path, "claude");

        Assert.Null(targets);
    }

    [Fact]
    public void BadJsonReturnsNullWithoutThrowing()
    {
        WriteTargets("claude", "{\"schema\":\"kyber-arbiter.model-targets/v1\",");

        ArbiterModelTargets? targets = ArbiterModelTargets.TryRead(_arbiterDirectory.Path, "claude");

        Assert.Null(targets);
    }

    [Fact]
    public void WrongSchemaIdReturnsNull()
    {
        WriteTargets("claude", ValidJson.Replace(
            "kyber-arbiter.model-targets/v1", "kyber-arbiter.model-targets/v2", StringComparison.Ordinal));

        ArbiterModelTargets? targets = ArbiterModelTargets.TryRead(_arbiterDirectory.Path, "claude");

        Assert.Null(targets);
    }

    [Fact]
    public void HarnessInFileOtherThanRequestedReturnsNull()
    {
        WriteTargets("codex", ValidJson);

        ArbiterModelTargets? targets = ArbiterModelTargets.TryRead(_arbiterDirectory.Path, "codex");

        Assert.Null(targets);
    }
}
