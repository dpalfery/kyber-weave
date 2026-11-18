using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for the review fix to the preset rewriter: the model targets table records the digest of
/// the bundle its tokens were resolved from. The profiles and the bundle digest must describe the same
/// bundle, so a table rebuilt after a bundle change never claims the old bundle.
/// </summary>
public sealed class SquadPresetRewriterBundleDigestTests
{
    private const string TablePath = "artifacts/arbiter/model-targets.claude.json";
    private const string StaleDigest = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void ClearingAStaleTableRecordsTheDigestOfTheBundleItWasRebuiltFrom()
    {
        SquadModelProfiles bundle = CreateBundle("claude-plan-model");

        SquadPresetRewriteResult result = Rewrite(CreateStaleContents(), bundle);

        Assert.Equal(ArbiterModelTargetsFile.BundleDigest(bundle, "claude"), RecordedBundleDigest(result));
    }

    [Fact]
    public void PresetUseRecordsTheDigestOfTheBundleNotOfThePresetMergedProfiles()
    {
        SquadModelProfiles bundle = CreateBundle("claude-plan-model");
        SquadModelProfiles merged = CreateBundle("claude-preset-model");

        SquadPresetRewriteResult result = Rewrite(CreateStaleContents(), merged, bundle);

        string digest = RecordedBundleDigest(result);
        Assert.Equal(ArbiterModelTargetsFile.BundleDigest(bundle, "claude"), digest);
        Assert.NotEqual(ArbiterModelTargetsFile.BundleDigest(merged, "claude"), digest);
    }

    private static Dictionary<string, byte[]> CreateStaleContents()
    {
        string staleTable =
            "{\"schema\":\"kyber-arbiter.model-targets/v1\",\"harness\":\"claude\"," +
            "\"preset\":{\"name\":\"old\",\"digest\":\"0\"},\"bundle-digest\":\"" + StaleDigest + "\"," +
            "\"profiles\":{\"deep-planning\":\"stale\"}," +
            "\"agents\":{\"architect\":{\"profile\":\"deep-planning\",\"token\":\"stale\"}}}";
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [TablePath] = Encoding.UTF8.GetBytes(staleTable),
        };
    }

    private static SquadPresetRewriteResult Rewrite(
        Dictionary<string, byte[]> contents,
        SquadModelProfiles profiles,
        SquadModelProfiles? bundled = null)
    {
        List<SquadOwnedFile> files =
        [
            new SquadOwnedFile(TablePath, StaleDigest, "claude", false),
        ];
        return SquadPresetFileRewriter.Rewrite(
            files,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude" },
            profiles,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            file => contents.TryGetValue(file.RelativePath, out byte[]? content) ? content : null,
            bundled: bundled);
    }

    private static string RecordedBundleDigest(SquadPresetRewriteResult result)
    {
        SquadDeploymentFile table = result.Files.Single(file => file.RelativePath == TablePath);
        using JsonDocument document = JsonDocument.Parse(table.Content.ToArray());
        return document.RootElement.GetProperty("bundle-digest").GetString()!;
    }

    private static SquadModelProfiles CreateBundle(string planningToken)
    {
        Dictionary<string, SquadModelProfile> profiles = new(StringComparer.Ordinal)
        {
            ["deep-planning"] = new SquadModelProfile(
                "plan-default-model",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = planningToken }),
            ["general"] = new SquadModelProfile(
                "general-default-model",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "general-claude-model" }),
        };
        return new SquadModelProfiles("kyber-squad.model-profiles/v1", profiles, "test");
    }
}
