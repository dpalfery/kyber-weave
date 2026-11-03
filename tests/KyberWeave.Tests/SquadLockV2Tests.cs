using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the lock v2 specification: locks with model presets upgrade to schema kyber-squad.lock/v2
///     with model-presets written last in ordinal order, while locks without presets remain byte-identical v1.
/// </summary>
public sealed class SquadLockV2Tests
{
    private const string LockSchemaV1 = "kyber-squad.lock/v1";
    private const string LockSchemaV2 = "kyber-squad.lock/v2";
    private const string ValidDigest1 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ValidDigest2 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void SquadAppliedPresetExposesNameAndDigestProperties()
    {
        SquadAppliedPreset preset = new("zai", ValidDigest1);

        Assert.Equal("zai", preset.Name);
        Assert.Equal(ValidDigest1, preset.Digest);
    }

    [Fact]
    public void SquadAppliedPresetSupportsValueEqualityAndHashCodeComparison()
    {
        SquadAppliedPreset first = new("zai", ValidDigest1);
        SquadAppliedPreset second = new("zai", ValidDigest1);
        SquadAppliedPreset differentName = new("other", ValidDigest1);
        SquadAppliedPreset differentDigest = new("zai", ValidDigest2);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, differentName);
        Assert.NotEqual(first, differentDigest);
    }

    [Fact]
    public void LockWithNoPresetsSerializesAsV1WithoutModelPresetsKey()
    {
        SquadStateStore store = CreateStore();
        SquadLock squadLock = CreateLock(LockSchemaV1, null);

        string yaml = store.SerializeLock(squadLock);

        Assert.Contains("schema: kyber-squad.lock/v1", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("model-presets", yaml, StringComparison.Ordinal);
        Assert.Null(squadLock.ModelPresets);
    }

    [Fact]
    public void LockWithPresetsSerializesAsV2WithModelPresetsWrittenLastInOrdinalOrder()
    {
        SquadStateStore store = CreateStore();
        // Insert in reverse ordinal order (Cursor before Claude) to verify serialization orders keys ordinally.
        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Cursor] = new SquadAppliedPreset("fast", ValidDigest2),
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", ValidDigest1)
        };
        SquadLock squadLock = CreateLock(LockSchemaV2, presets);

        string yaml = store.SerializeLock(squadLock);

        string normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("schema: kyber-squad.lock/v2\n", normalized, StringComparison.Ordinal);

        // Verify model-presets is written last (after apm section) and targets are in ordinal order (claude before cursor).
        int apmIndex = normalized.IndexOf("apm:\n", StringComparison.Ordinal);
        int modelPresetsIndex = normalized.IndexOf("model-presets:\n", StringComparison.Ordinal);
        int claudeIndex = normalized.IndexOf("  claude:\n", StringComparison.Ordinal);
        int cursorIndex = normalized.IndexOf("  cursor:\n", StringComparison.Ordinal);

        Assert.True(apmIndex >= 0, "YAML must contain apm section.");
        Assert.True(modelPresetsIndex > apmIndex, "model-presets must be written after apm.");
        Assert.True(claudeIndex > modelPresetsIndex, "claude must be under model-presets.");
        Assert.True(cursorIndex > claudeIndex, "claude must precede cursor in ordinal order.");

        string expectedModelPresetsBlock =
            """
                model-presets:
                  claude:
                    name: zai
                    digest: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                  cursor:
                    name: fast
                    digest: fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210
                """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(expectedModelPresetsBlock, normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void LockV2WithPresetsRoundTripsThroughSerializationAndDeserialization()
    {
        SquadStateStore store = CreateStore();
        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", ValidDigest1),
            [SquadTarget.Codex] = new SquadAppliedPreset("standard", ValidDigest2)
        };
        SquadLock original = CreateLock(LockSchemaV2, presets);

        string yaml = store.SerializeLock(original);
        SquadLock deserialized = store.DeserializeLock(yaml);

        Assert.Equal(LockSchemaV2, deserialized.Schema);
        Assert.NotNull(deserialized.ModelPresets);
        Assert.Equal(2, deserialized.ModelPresets.Count);
        Assert.Equal(new SquadAppliedPreset("zai", ValidDigest1), deserialized.ModelPresets[SquadTarget.Claude]);
        Assert.Equal(new SquadAppliedPreset("standard", ValidDigest2), deserialized.ModelPresets[SquadTarget.Codex]);
        AssertLockEqual(original, deserialized);
    }

    [Fact]
    public void DeserializeLockReadsV1LockWithoutModelPresetsKey()
    {
        SquadStateStore store = CreateStore();
        string v1Yaml =
            $$"""
                  schema: kyber-squad.lock/v1
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                    - cursor
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        SquadLock deserialized = store.DeserializeLock(v1Yaml);

        Assert.Equal(LockSchemaV1, deserialized.Schema);
        Assert.True(deserialized.ModelPresets is null || deserialized.ModelPresets.Count == 0);
    }

    [Fact]
    public void DeserializeLockRejectsV2LockWithEmptyModelPresetsMapping()
    {
        SquadStateStore store = CreateStore();
        string v2EmptyPresetsYaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets: {}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(v2EmptyPresetsYaml));
    }

    [Fact]
    public void DeserializeLockRejectsV2LockMissingModelPresetsKey()
    {
        SquadStateStore store = CreateStore();
        string v2MissingPresetsYaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(v2MissingPresetsYaml));
    }

    [Fact]
    public void DeserializeLockRejectsV1LockCarryingModelPresetsKey()
    {
        SquadStateStore store = CreateStore();
        string v1WithPresetsYaml =
            $$"""
                  schema: kyber-squad.lock/v1
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets:
                    claude:
                      name: zai
                      digest: {{ValidDigest1}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(v1WithPresetsYaml));
    }

    [Fact]
    public void SerializeLockRejectsV1LockCarryingModelPresets()
    {
        SquadStateStore store = CreateStore();
        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", ValidDigest1)
        };
        SquadLock v1WithPresets = CreateLock(LockSchemaV1, presets);

        Assert.Throws<InvalidDataException>(() => store.SerializeLock(v1WithPresets));
    }

    [Fact]
    public void SerializeLockRejectsV2LockWithNullOrEmptyModelPresets()
    {
        SquadStateStore store = CreateStore();
        SquadLock v2NullPresets = CreateLock(LockSchemaV2, null);
        SquadLock v2EmptyPresets = CreateLock(
            LockSchemaV2,
            new Dictionary<SquadTarget, SquadAppliedPreset>());

        Assert.Throws<InvalidDataException>(() => store.SerializeLock(v2NullPresets));
        Assert.Throws<InvalidDataException>(() => store.SerializeLock(v2EmptyPresets));
    }

    [Theory]
    [InlineData("not-a-hex-digest")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void DeserializeLockRejectsModelPresetWithInvalidDigest(string invalidDigest)
    {
        SquadStateStore store = CreateStore();
        string yaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets:
                    claude:
                      name: zai
                      digest: {{invalidDigest}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(yaml));
    }

    [Theory]
    [InlineData("not-a-hex-digest")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void SerializeLockRejectsModelPresetWithInvalidDigest(string invalidDigest)
    {
        SquadStateStore store = CreateStore();
        Dictionary<SquadTarget, SquadAppliedPreset> presets = new()
        {
            [SquadTarget.Claude] = new SquadAppliedPreset("zai", invalidDigest)
        };
        SquadLock squadLock = CreateLock(LockSchemaV2, presets);

        Assert.Throws<InvalidDataException>(() => store.SerializeLock(squadLock));
    }

    [Fact]
    public void DeserializeLockRejectsUnknownLockSchema()
    {
        SquadStateStore store = CreateStore();
        string unknownSchemaYaml =
            $$"""
                  schema: kyber-squad.lock/v3
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(unknownSchemaYaml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeserializeLockRejectsModelPresetWithBlankName(string blankName)
    {
        SquadStateStore store = CreateStore();
        string yaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets:
                    claude:
                      name: "{{blankName}}"
                      digest: {{ValidDigest1}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(yaml));
    }

    [Fact]
    public void DeserializeLockRejectsUnknownTargetTokens()
    {
        SquadStateStore store = CreateStore();
        string yaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets:
                    not-a-target:
                      name: fast
                      digest: {{ValidDigest1}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeLock(yaml));
        Assert.Contains("unknown target", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeLockRejectsBlankOrNonScalarTargetKey()
    {
        SquadStateStore store = CreateStore();
        string yaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets:
                    "   ":
                      name: fast
                      digest: {{ValidDigest1}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(yaml));
    }

    [Fact]
    public void DeserializeLockRejectsDuplicateTargetKeyInModelPresets()
    {
        SquadStateStore store = CreateStore();
        string yaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets:
                    claude:
                      name: fast
                      digest: {{ValidDigest1}}
                    claude:
                      name: slow
                      digest: {{ValidDigest2}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(yaml));
    }

    [Fact]
    public void DeserializeLockRejectsNonMappingUnderTargetInModelPresets()
    {
        SquadStateStore store = CreateStore();
        string yaml =
            $$"""
                  schema: kyber-squad.lock/v2
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - claude
                  exclusions: []
                  translation: best-effort
                  bundle-digest: {{ValidDigest1}}
                  asset-digest: {{ValidDigest1}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{ValidDigest1}}
                  model-presets:
                    claude: "not-a-mapping"

                  """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => store.DeserializeLock(yaml));
    }

    private static SquadStateStore CreateStore()
    {
        return new SquadStateStore(new FakeSquadUserPaths(Path.GetTempPath()));
    }

    private static SquadLock CreateLock(
        string schema,
        IReadOnlyDictionary<SquadTarget, SquadAppliedPreset>? modelPresets)
    {
        return new SquadLock(
            schema,
            "1.2.3",
            "1.2.3",
            "1.2.3",
            "full",
            ["claude", "cursor"],
            [],
            "best-effort",
            ValidDigest1,
            ValidDigest1,
            new SquadApmIdentity("1.2.3", "0123456789abcdef", ValidDigest1),
            modelPresets);
    }

    private static void AssertLockEqual(SquadLock expected, SquadLock actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.SquadVersion, actual.SquadVersion);
        Assert.Equal(expected.CliVersion, actual.CliVersion);
        Assert.Equal(expected.McpVersion, actual.McpVersion);
        Assert.Equal(expected.Bundle, actual.Bundle);
        Assert.Equal(expected.Targets, actual.Targets);
        Assert.Equal(expected.Exclusions, actual.Exclusions);
        Assert.Equal(expected.Translation, actual.Translation);
        Assert.Equal(expected.BundleDigest, actual.BundleDigest);
        Assert.Equal(expected.AssetDigest, actual.AssetDigest);
        Assert.Equal(expected.Apm, actual.Apm);
        if (expected.ModelPresets is null)
        {
            Assert.Null(actual.ModelPresets);
        }
        else
        {
            Assert.NotNull(actual.ModelPresets);
            Assert.Equal(expected.ModelPresets.Count, actual.ModelPresets.Count);
            foreach ((SquadTarget target, SquadAppliedPreset preset) in expected.ModelPresets)
            {
                Assert.True(actual.ModelPresets.TryGetValue(target, out SquadAppliedPreset? actualPreset));
                Assert.Equal(preset, actualPreset);
            }
        }
    }
}
