using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the ownership boundary for Squad deployment. A receipt grants authority over only the
///     byte-identical paths it records; it is never a general overwrite or delete permission.
/// </summary>
public sealed class SquadDeploymentStateTests(ITestOutputHelper output)
{
    private const string GuidanceFile = SquadResponseGuidanceDeployer.FileName;

    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    public static TheoryData<string, string, string> MalformedHomeFilesJson
    {
        get
        {
            string digest = Digest("guidance");
            return new TheoryData<string, string, string>
            {
                { "object-instead-of-array", "{}", "homeFiles must be an array" },
                { "string-instead-of-array", "\"response-guidance.md\"", "homeFiles must be an array" },
                { "number-instead-of-array", "7", "homeFiles must be an array" },
                { "null-instead-of-array", "null", "field 'homeFiles' cannot be null" },
                { "null-entry", "[null]", "home file must be an object" },
                { "string-entry", "[\"response-guidance.md\"]", "home file must be an object" },
                { "array-entry", "[[]]", "home file must be an object" },
                {
                    "missing-digest",
                    """[{"relativePath":"response-guidance.md"}]""",
                    "Missing: sha256"
                },
                {
                    "missing-path",
                    $$"""[{"sha256":"{{digest}}"}]""",
                    "Missing: relativePath"
                },
                {
                    "unknown-field",
                    $$"""[{"relativePath":"response-guidance.md","sha256":"{{digest}}","target":"claude"}]""",
                    "unknown: target"
                },
                {
                    "duplicate-field",
                    $$"""[{"relativePath":"response-guidance.md","relativePath":"other.md","sha256":"{{digest}}"}]""",
                    "duplicate field 'relativePath'"
                },
                {
                    "null-digest",
                    """[{"relativePath":"response-guidance.md","sha256":null}]""",
                    "field 'sha256' cannot be null"
                },
                {
                    "numeric-path",
                    $$"""[{"relativePath":5,"sha256":"{{digest}}"}]""",
                    "JSON is invalid"
                },
                {
                    "escaping-path",
                    $$"""[{"relativePath":"../response-guidance.md","sha256":"{{digest}}"}]""",
                    "is not portable"
                },
                {
                    "invalid-digest",
                    """[{"relativePath":"response-guidance.md","sha256":"not-a-digest"}]""",
                    "home file digest"
                },
                {
                    "duplicate-path",
                    $$"""[{"relativePath":"response-guidance.md","sha256":"{{digest}}"},{"relativePath":"Response-Guidance.md","sha256":"{{digest}}"}]""",
                    "not a unique portable path"
                }
            };
        }
    }

    [Fact]
    public void ExecuteWithDurableWritesEnabledDeploysWhatItWrote()
    {
        // The test assembly turns SyncToDisk off for speed (SquadTestDurability), so the branch
        // that ships would otherwise never run. Exercise it once in an isolated async flow scope.
        using (SquadTransaction.UseSyncToDiskScope(true))
        {
            using TempDirectory fixture = new();
            string targetRoot = Path.Combine(fixture.Path, "target");
            Directory.CreateDirectory(targetRoot);
            SquadStateStore store = Store(Path.Combine(fixture.Path, "application-data"));
            SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
                targetRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/durable.toml", "durable")],
                [],
                false,
                new FixedTimeProvider(InstalledAt));

            new SquadTransaction(store).Execute(plan);

            Assert.Equal(
                "durable",
                File.ReadAllText(Path.Combine(targetRoot, ".codex", "agents", "durable.toml")));
        }
    }

    [Fact]
    public void SerializeProjectStateIsStablePortableSecretFreeAndRoundTrips()
    {
        using TempDirectory fixture = new();
        string secretApplicationData = Path.Combine(fixture.Path, "TOP-SECRET-USER-PATH");
        SquadStateStore store = Store(secretApplicationData);
        SquadLock squadLock = Lock();
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor"),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".warp/skills/conductor/SKILL.md",
                    Digest("role skill"),
                    "warp",
                    true)
            ]);

        string lockYaml = store.SerializeLock(squadLock);
        string receiptJson = store.SerializeReceipt(receipt);

        Assert.Equal(
            $$"""
                  schema: kyber-squad.lock/v1
                  squad-version: 1.2.3
                  cli-version: 1.2.3
                  mcp-version: 1.2.3
                  bundle: full
                  targets:
                    - codex
                    - warp
                  exclusions:
                    - cursor
                  translation: best-effort
                  bundle-digest: {{Digest("bundle")}}
                  asset-digest: {{Digest("asset")}}
                  apm:
                    version: 1.2.3
                    tag-commit: 0123456789abcdef
                    asset-sha256: {{Digest("apm")}}

                  """.Replace("\r\n", "\n", StringComparison.Ordinal),
            lockYaml.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(
            $$"""
                  {
                    "schema": "kyber-squad.receipt/v1",
                    "scope": "project",
                    "targetRoot": ".",
                    "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                    "degradations": [
                      {
                        "target": "warp",
                        "subject": "conductor",
                        "code": "role-skill-fallback"
                      }
                    ],
                    "files": [
                      {
                        "relativePath": ".codex/agents/conductor.toml",
                        "sha256": "{{Digest("conductor")}}",
                        "target": "codex",
                        "adopted": false
                      },
                      {
                        "relativePath": ".warp/skills/conductor/SKILL.md",
                        "sha256": "{{Digest("role skill")}}",
                        "target": "warp",
                        "adopted": true
                      }
                    ]
                  }

                  """.Replace("\r\n", "\n", StringComparison.Ordinal),
            receiptJson.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Path, lockYaml, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Path, receiptJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", lockYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", receiptJson, StringComparison.Ordinal);
        AssertLockEqual(squadLock, store.DeserializeLock(lockYaml));
        AssertReceiptEqual(receipt, store.DeserializeReceipt(receiptJson));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("bundle-digest")]
    [InlineData("asset-digest")]
    [InlineData("apm-digest")]
    public void SerializeLockInvalidSchemaOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadLock valid = Lock();
        SquadLock invalid = invalidField switch
        {
            "schema" => valid with { Schema = "kyber-squad.lock/v999" },
            "bundle-digest" => valid with { BundleDigest = "not-a-sha256" },
            "asset-digest" => valid with { AssetDigest = "ABCDEF" },
            "apm-digest" => valid with
            {
                Apm = valid.Apm with { AssetSha256 = new string('0', 63) }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.SerializeLock(invalid));

        Assert.Contains(invalidField.Split('-')[0], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("bundle-digest")]
    [InlineData("asset-digest")]
    [InlineData("apm-digest")]
    public void DeserializeLockInvalidSchemaOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string yaml = store.SerializeLock(Lock());
        string invalidYaml = invalidField switch
        {
            "schema" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: kyber-squad.lock/v999",
                StringComparison.Ordinal),
            "bundle-digest" => yaml.Replace(
                $"bundle-digest: {Digest("bundle")}",
                "bundle-digest: not-a-sha256",
                StringComparison.Ordinal),
            "asset-digest" => yaml.Replace(
                $"asset-digest: {Digest("asset")}",
                "asset-digest: ABCDEF",
                StringComparison.Ordinal),
            "apm-digest" => yaml.Replace(
                $"asset-sha256: {Digest("apm")}",
                $"asset-sha256: {new string('0', 63)}",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeLock(invalidYaml));

        Assert.Contains(invalidField.Split('-')[0], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A receipt records the canonical physical root it was written against, so an absolute root is the
    ///     normal form and must survive a round trip. It was once rejected, which predates recording roots.
    /// </summary>
    [Fact]
    public void ReceiptRecordsAbsolutePhysicalRootAndRoundTrips()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string physicalRoot = Path.Combine(fixture.Path, "physical-root");
        SquadOwnedFile validFile = new(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            "codex",
            false);
        SquadReceipt receipt = Receipt(validFile) with { TargetRoot = physicalRoot };

        SquadReceipt restored = store.DeserializeReceipt(store.SerializeReceipt(receipt));

        Assert.Equal(physicalRoot, restored.TargetRoot);
    }

    /// <summary>
    ///     Only an absolute physical root or the legacy <c>.</c> marker is a valid target root. A relative root
    ///     is not a location and would be resolved against whatever directory later reads it.
    /// </summary>
    [Theory]
    [InlineData("relative/root")]
    [InlineData("../escape")]
    public void ReceiptRejectsRelativeTargetRootOtherThanTheLegacyMarker(string targetRoot)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt invalid = Receipt(new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("conductor"),
                "codex",
                false)) with
        {
            TargetRoot = targetRoot
        };

        InvalidDataException serialized = Assert.Throws<InvalidDataException>(() => store.SerializeReceipt(invalid));
        Assert.Contains("target root", serialized.Message, StringComparison.OrdinalIgnoreCase);
        string json = store.SerializeReceipt(Receipt(new SquadOwnedFile(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            "codex",
            false)));
        string invalidJson = json.Replace("\"targetRoot\": \".\"", $"\"targetRoot\": \"{targetRoot}\"",
            StringComparison.Ordinal);

        InvalidDataException deserialized =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidJson));
        Assert.Contains("target root", deserialized.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("absolute-path")]
    [InlineData("file-digest")]
    public void SerializeReceiptInvalidSchemaRootPathOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadOwnedFile validFile = new(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            "codex",
            false);
        SquadReceipt valid = Receipt(validFile);
        SquadReceipt invalid = invalidField switch
        {
            "schema" => valid with { Schema = "kyber-squad.receipt/v999" },
            "absolute-path" => valid with
            {
                Files = [validFile with { RelativePath = "/private/conductor.toml" }]
            },
            "file-digest" => valid with
            {
                Files = [validFile with { Sha256 = "not-a-sha256" }]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.SerializeReceipt(invalid));

        Assert.Contains(invalidField.Split('-')[^1], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("absolute-path")]
    [InlineData("file-digest")]
    public void DeserializeReceiptInvalidSchemaRootPathOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string json = store.SerializeReceipt(Receipt(new SquadOwnedFile(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            "codex",
            false)));
        string invalidJson = invalidField switch
        {
            "schema" => json.Replace(
                "kyber-squad.receipt/v1",
                "kyber-squad.receipt/v999",
                StringComparison.Ordinal),
            "absolute-path" => json.Replace(
                ".codex/agents/conductor.toml",
                "/private/conductor.toml",
                StringComparison.Ordinal),
            "file-digest" => json.Replace(
                Digest("conductor"),
                "not-a-sha256",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidJson));

        Assert.Contains(invalidField.Split('-')[^1], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A global receipt records the files Squad owns in the user's home beside the files it owns in each
    ///     harness root. The record has to survive a write and a read: a receipt that lost it would make the
    ///     next update treat Squad's own guidance file as unowned and stop refreshing it.
    /// </summary>
    [Fact]
    public void GlobalReceiptHomeFilesRoundTrip()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt receipt = GlobalReceiptWithHomeFiles(GuidanceHomeFile());

        string json = store.SerializeReceipt(receipt);

        Assert.Contains("\"homeFiles\"", json, StringComparison.Ordinal);
        Assert.Equal(receipt.HomeFiles, store.DeserializeReceipt(json).HomeFiles);
    }

    /// <summary>
    ///     The field is additive. A receipt written before home-owned files existed has none, and a receipt
    ///     that owns none writes none, so neither an earlier CLI's receipts nor an existing deployment is
    ///     disturbed by the field's arrival.
    /// </summary>
    [Fact]
    public void GlobalReceiptHomeFilesAreOmittedWhenThereAreNoneAndReadAsEmpty()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);

        string json = store.SerializeReceipt(GlobalReceiptWithHomeFiles());

        Assert.DoesNotContain("homeFiles", json, StringComparison.Ordinal);
        Assert.Empty(store.DeserializeReceipt(json).HomeFiles);
    }

    /// <summary>
    ///     A home-file record is an ownership claim over a path beneath the user's home, so it is held to the
    ///     standard a harness file is: a global receipt to carry it, a portable path that stays inside the
    ///     root, a lowercase SHA-256 digest, and a path that appears once. Each case is refused on its own
    ///     ground, which the message names, so a check that stops running cannot hide behind another.
    /// </summary>
    [Theory]
    [InlineData("project-scope", "only on a global receipt")]
    [InlineData("instance-scope", "only on a global receipt")]
    [InlineData("null-list", "home files are required")]
    [InlineData("null-entry", "empty home file entry")]
    [InlineData("parent-traversal", "is not portable")]
    [InlineData("rooted-path", "is not portable")]
    [InlineData("reserved-device-name", "is not portable")]
    [InlineData("backslash-separator", "is not portable")]
    [InlineData("short-digest", "home file digest")]
    [InlineData("uppercase-digest", "home file digest")]
    [InlineData("non-hex-digest", "home file digest")]
    [InlineData("duplicate-path", "not a unique portable path")]
    [InlineData("case-variant-duplicate", "not a unique portable path")]
    public void SerializeReceiptHomeFilesThatBreakTheReceiptContractAreRejected(string invalidCase, string reason)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt invalid = ReceiptWithInvalidHomeFiles(invalidCase);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.SerializeReceipt(invalid));

        Assert.True(
            exception.Message.Contains(reason, StringComparison.Ordinal),
            $"'{invalidCase}' was refused, but with '{exception.Message}' rather than a message naming '{reason}'.");
    }

    /// <summary>
    ///     The raw JSON is checked before any object is bound, so a shape the binder would quietly coerce
    ///     (a missing digest read as null, an unknown field ignored, a repeated field resolved to one value)
    ///     is refused instead of becoming an ownership record nobody wrote.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedHomeFilesJson))]
    public void DeserializeReceiptHomeFilesWithAMalformedShapeAreRejected(
        string malformedCase,
        string homeFilesJson,
        string reason)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string json = ReplaceHomeFiles(
            store.SerializeReceipt(GlobalReceiptWithHomeFiles(GuidanceHomeFile())),
            homeFilesJson);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(json));

        Assert.True(
            exception.Message.Contains(reason, StringComparison.Ordinal),
            $"'{malformedCase}' was refused, but with '{exception.Message}' rather than a message naming '{reason}'.");
    }

    /// <summary>
    ///     The control for the theory above: swapping in a well-formed value leaves the document valid, so
    ///     each rejection there comes from the value it injects and not from the way the document is rebuilt.
    /// </summary>
    [Fact]
    public void ReceiptHomeFilesReplacementLeavesAWellFormedDocumentValid()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt receipt = GlobalReceiptWithHomeFiles(GuidanceHomeFile());
        string rewritten = ReplaceHomeFiles(
            store.SerializeReceipt(receipt),
            $"[{HomeFileJson(GuidanceFile, Digest("guidance"))}]");

        Assert.Equal(receipt.HomeFiles, store.DeserializeReceipt(rewritten).HomeFiles);
    }

    /// <summary>
    ///     A property declared twice would be read as whichever occurrence the reader reaches, so an
    ///     ownership claim could hide behind an empty list. The reader refuses the repeat outright.
    /// </summary>
    [Fact]
    public void DeserializeReceiptHomeFilesDeclaredTwiceAreRejected()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string twice = InsertBeforeRootClose(
            store.SerializeReceipt(GlobalReceiptWithHomeFiles(GuidanceHomeFile())),
            "  \"homeFiles\": []");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(twice));

        Assert.Contains("duplicate field 'homeFiles'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Only a global receipt, written on the v2 schema, can own files in the home. A receipt on the v1
    ///     schema, or one that names another scope, was never written by this code with that field, so
    ///     reading it as ownership would let a hand edit give Squad authority it never recorded.
    /// </summary>
    [Theory]
    [InlineData("project-receipt-on-v1", "unknown: homeFiles")]
    [InlineData("global-receipt-on-v1", "unknown: homeFiles")]
    [InlineData("project-scope-on-v2", "global scope only")]
    public void DeserializeReceiptHomeFilesOutsideAGlobalV2ReceiptAreRejected(string carrier, string reason)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string homeFiles = $"  \"homeFiles\": [{HomeFileJson(GuidanceFile, Digest("guidance"))}]";
        string json = carrier switch
        {
            "project-receipt-on-v1" => InsertBeforeRootClose(store.SerializeReceipt(Receipt()), homeFiles),
            "global-receipt-on-v1" => InsertBeforeRootClose(
                SerializeAsLegacyV1Json(Receipt() with { Scope = SquadDeploymentScope.Global }),
                homeFiles),
            "project-scope-on-v2" => store
                .SerializeReceipt(GlobalReceiptWithHomeFiles(GuidanceHomeFile()))
                .Replace("\"scope\": \"global\"", "\"scope\": \"project\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(json));

        Assert.True(
            exception.Message.Contains(reason, StringComparison.Ordinal),
            $"'{carrier}' was refused, but with '{exception.Message}' rather than a message naming '{reason}'.");
    }

    /// <summary>
    ///     Global state lives beneath the injected home at <c>.kyber-weave/squad/global</c>, not beneath
    ///     the platform application data directory, and the receipt names the home as its anchor.
    /// </summary>
    [Fact]
    public void ResolveStateGlobalScopeLivesUnderHomeAnchoredSquadState()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(home);
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "body")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        string physicalHome = SquadPhysicalRootIdentity.Resolve(home).PhysicalPath;

        // The planner cannot know the home anchor, so it records the unanchored marker. The transaction
        // stamps the physical home on the plan as it writes, which is what this test asserts afterwards.
        Assert.Equal(SquadStateStore.UnanchoredTargetRoot, plan.Receipt.TargetRoot);
        new SquadTransaction(store).Execute(plan);

        string stateDirectory = store.ResolveStateDirectory(
            fixture.Path,
            SquadDeploymentScope.Global);

        AssertSameLocation(
            Path.Combine(physicalHome, ".kyber-weave", "squad", "global"),
            stateDirectory);
        Assert.Equal(InstalledAt, plan.Receipt.InstalledAtUtc);
        Assert.Equal(physicalHome, plan.Receipt.TargetRoot);
        string receiptJson = store.SerializeReceipt(plan.Receipt);
        Assert.Contains("\"scope\": \"global\"", receiptJson, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Global state is one machine-wide record anchored on the home, so two unrelated target roots
    ///     address the same lock and receipt rather than each keeping a private binding and a sibling
    ///     receipt. The sibling-ownership check that used to read those siblings is retired with it.
    /// </summary>
    [Fact]
    public void GlobalStateForUnrelatedTargetRootsSharesOneHomeAnchoredReceipt()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(home);
        string firstRoot = Path.Combine(fixture.Path, "customer-alpha");
        string secondRoot = Path.Combine(fixture.Path, "customer-beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        SquadStateStore store = Store(applicationData);
        // Bare paths, not dot-prefixed: a fresh CreateInstall always renders through the modern
        // per-target layout (NormalizeRenderedFiles never treats a new render as legacy), so a
        // dot-prefixed path here would only manufacture a self-contradicting receipt of this
        // test's own making against the read-side layout cross-check (#91), not exercise
        // anything CreateInstall itself does.
        SquadDeploymentPlan firstPlan = SquadDeploymentPlan.CreateInstall(
            firstRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered("agents/first.toml", "first")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));

        new SquadTransaction(store).Execute(firstPlan);

        SquadReceipt firstReceipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(
            firstRoot,
            SquadDeploymentScope.Global));
        SquadReceipt sharedReceipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(
            secondRoot,
            SquadDeploymentScope.Global));
        Assert.Equal("agents/first.toml", Assert.Single(firstReceipt.Files).RelativePath);
        Assert.Equal(firstReceipt.InstalledAtUtc, sharedReceipt.InstalledAtUtc);
        Assert.Equal("agents/first.toml", Assert.Single(sharedReceipt.Files).RelativePath);

        string globalState = Path.Combine(
            SquadPhysicalRootIdentity.Resolve(home).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global");
        AssertSameLocation(globalState, store.ResolveStateDirectory(firstRoot, SquadDeploymentScope.Global));
        AssertSameLocation(globalState, store.ResolveStateDirectory(secondRoot, SquadDeploymentScope.Global));
        Assert.False(
            Directory.Exists(Path.Combine(Path.GetDirectoryName(globalState)!, "roots")),
            "A per-root binding directory must not exist once global state is home-anchored.");
        string[] statePaths = Directory.EnumerateFileSystemEntries(
                globalState,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(globalState, path).Replace(
                Path.DirectorySeparatorChar,
                '/'))
            .ToArray();
        Assert.NotEmpty(statePaths);
        Assert.All(statePaths, relativePath =>
        {
            Assert.DoesNotContain("customer-alpha", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer-beta", relativePath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(firstRoot, Store(applicationData).SerializeReceipt(firstReceipt),
            StringComparison.Ordinal);
        Assert.DoesNotContain(secondRoot, Store(applicationData).SerializeReceipt(sharedReceipt),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     Global renderers strip the target directory prefix because each target deploys beneath
    ///     its own root, so one relative path legitimately appears once per target. State store
    ///     validation must key uniqueness on (target, path), not on the path alone.
    /// </summary>
    [Fact]
    public void GlobalReceiptSharingOnePathAcrossTargetsRoundTrips()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile("agents/conductor.md", Digest("claude conductor"), "claude", false),
                new SquadOwnedFile("agents/conductor.md", Digest("codex conductor"), "codex", false)
            ]);

        string json = store.SerializeReceipt(receipt);

        // A Global receipt always serializes on the v2 schema (A3), even when constructed
        // directly with the v1 literal, so the round-tripped schema is expected to differ from
        // what was passed to SerializeReceipt.
        AssertReceiptEqual(
            receipt with { Schema = SquadStateStore.ReceiptSchemaV2 },
            store.DeserializeReceipt(json));
    }

    [Fact]
    public void SerializeReceiptDuplicatePathForOneTargetIsRejected()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt duplicate = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile("agents/conductor.md", Digest("first"), "claude", false),
                new SquadOwnedFile("agents/conductor.md", Digest("second"), "claude", false)
            ]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.SerializeReceipt(duplicate));

        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Project scope writes every target beneath the one deployment root, so two targets
    ///     claiming one relative path would claim one physical file; the receipt must refuse it.
    /// </summary>
    [Fact]
    public void SerializeReceiptProjectScopeSharedPathAcrossTargetsIsRejected()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt collision = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile("agents/conductor.md", Digest("claude conductor"), "claude", false),
                new SquadOwnedFile("agents/conductor.md", Digest("codex conductor"), "codex", false)
            ]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.SerializeReceipt(collision));

        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A global install across two targets that render the same relative path must deploy to
    ///     each target's own root, and status must verify the bytes there — not against the
    ///     deployment root, which holds none of the files.
    /// </summary>
    [Fact]
    public void ExecuteGlobalInstallSharingPathsAcrossTargetsDeploysAndStatusVerifiesPerTargetRoots()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string claudeRoot = Path.Combine(fixture.Path, "claude-home", ".claude");
        string codexRoot = Path.Combine(fixture.Path, "codex-home", ".codex");
        Directory.CreateDirectory(claudeRoot);
        Directory.CreateDirectory(codexRoot);
        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Claude] = claudeRoot,
            [SquadTarget.Codex] = codexRoot
        });
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Global,
            Lock(),
            [
                Rendered("agents/conductor.md", "claude conductor", "claude"),
                Rendered("agents/conductor.md", "codex conductor")
            ],
            [],
            false,
            new FixedTimeProvider(InstalledAt),
            globalRoots);

        new SquadTransaction(store).Execute(plan);

        Assert.Equal("claude conductor", Read(claudeRoot, "agents/conductor.md"));
        Assert.Equal("codex conductor", Read(codexRoot, "agents/conductor.md"));
        // A Global receipt always serializes on the v2 schema (A3), so what SquadTransaction
        // persisted and re-read differs from the plan's own v1-labeled in-memory receipt only
        // in that one field.
        AssertReceiptEqual(
            plan.Receipt with { Schema = SquadStateStore.ReceiptSchemaV2 },
            Assert.IsType<SquadReceipt>(store.ReadReceipt(fixture.Path, SquadDeploymentScope.Global)));

        SquadStatusCommand command = new(stateStore: store, globalRoots: globalRoots);
        CapturedConsoleExecution<int> clean = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = true
            }));
        Assert.Equal(0, clean.Result);
        Assert.Contains("(claude)", clean.Output, StringComparison.Ordinal);
        Assert.Contains("(codex)", clean.Output, StringComparison.Ordinal);

        Write(claudeRoot, "agents/conductor.md", "operator edit");
        CapturedConsoleExecution<int> drifted = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = true
            }));

        Assert.Equal(1, drifted.Result);
        Assert.Contains("drift", drifted.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(claude)", drifted.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An uninstall plan's receipt carries only retained files; the dry-run report must list
    ///     the planned removals with their target, and remove nothing.
    /// </summary>
    [Fact]
    public void UninstallDryRunListsPlannedRemovalsWithTargetsAndRemovesNothing()
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(fixture.Path, relativePath, "installed body");
        WriteState(fixture.Path, Lock(), receipt);
        SquadStateStore store = Store(fixture.Path);
        SquadUninstallCommand command = new(
            stateStore: store,
            lifecycleService: SquadCommandComposition.CreateLifecycleService(stateStore: store));

        CapturedConsoleExecution<int> dryRun = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadUninstallSettings
            {
                Path = fixture.Path,
                DryRun = true
            }));

        Assert.Equal(0, dryRun.Result);
        Assert.Contains("would uninstall 1 files", dryRun.Output, StringComparison.Ordinal);
        Assert.Contains(relativePath, dryRun.Output, StringComparison.Ordinal);
        Assert.Contains("(codex)", dryRun.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(ToPlatformPath(fixture.Path, relativePath)));
    }

    [Fact]
    public void ExecuteGlobalScopeKeepsIntentStagesAndBackupsInHomeJournalNotBesideTargetOrInState()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(Path.Combine(applicationData, "home"));
        string targetRoot = Path.Combine(fixture.Path, "global-target");
        Directory.CreateDirectory(targetRoot);
        Write(targetRoot, ".codex/agents/conductor.toml", "installed body");
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("installed body"),
                    "codex",
                    false)
            ]);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        SquadStateStore store = Store(applicationData);
        GlobalTransactionLocationObserver observer = new(
            targetRoot,
            store.ResolveStateDirectory(targetRoot, SquadDeploymentScope.Global),
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global));

        new SquadTransaction(store, observer).Execute(plan);

        Assert.True(observer.SawDurableIntentInJournal);
        Assert.True(observer.SawStagingInJournal);
        Assert.True(observer.SawBackupInJournal);
        Assert.False(observer.SawStagingOrBackupOnTargetRoot);
        Assert.False(observer.SawStagingOrBackupInState);
        Assert.Equal("updated body", Read(targetRoot, ".codex/agents/conductor.toml"));
    }

    [Fact]
    public void CreateInstallIdenticalUnmanagedFileWithAdoptRecordsOwnershipWithoutRewriting()
    {
        using TempDirectory fixture = new();
        Write(fixture.Path, ".codex/agents/conductor.toml", "same bytes");
        DateTime before = File.GetLastWriteTimeUtc(
            Path.Combine(fixture.Path, ".codex/agents/conductor.toml"));
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "same bytes")],
            [],
            true,
            new FixedTimeProvider(InstalledAt));

        Transaction(fixture.Path).Execute(plan);

        SquadOwnedFile owned = Assert.Single(plan.Receipt.Files);
        Assert.True(owned.Adopted);
        Assert.Equal(Digest("same bytes"), owned.Sha256);
        Assert.Equal(before, File.GetLastWriteTimeUtc(
            Path.Combine(fixture.Path, owned.RelativePath)));
    }

    [Fact]
    public void ExecuteInstallPathCreatedAfterPreflightRefusesToOverwriteRacedUnmanagedFile()
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(relativePath, "generated body")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Write(fixture.Path, relativePath, "raced unmanaged body");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced unmanaged body", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("same bytes", false)]
    [InlineData("different bytes", true)]
    [InlineData("different bytes", false)]
    public void CreateInstallUnmanagedCollisionRefusesWithoutChangingTree(
        string existingContent,
        bool adopt)
    {
        using TempDirectory fixture = new();
        Write(fixture.Path, ".codex/agents/conductor.toml", existingContent);
        SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/conductor.toml", "same bytes")],
                [],
                adopt,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
        AssertSnapshotsEqual(before, Snapshot(fixture.Path));
    }

    [Theory]
    [InlineData(".codex/agents/Reviewer.toml", ".codex/agents/reviewer.toml")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml.")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml ")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents./reviewer.toml")]
    public void CreateInstallPortablePathAliasesAreRejectedBeforeWrites(
        string firstPath,
        string aliasPath)
    {
        using TempDirectory fixture = new();

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "second")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains("portable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aliasPath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    [Theory]
    [InlineData(".codex/agents/Reviewer.toml", ".codex/agents/reviewer.toml")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml.")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml ")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents./reviewer.toml")]
    public void CreateUninstallReceiptPortablePathAliasesAreRejected(
        string firstPath,
        string aliasPath)
    {
        using TempDirectory fixture = new();
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("second"), "codex", false));

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                receipt));

        Assert.Contains("portable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aliasPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUpdateLocallyEditedManagedFileWithoutReplacePreservesFileAndReceiptHash()
    {
        using TempDirectory fixture = new();
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(fixture.Path, ".codex/agents/conductor.toml", "operator edit");
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            oldReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Transaction(fixture.Path).Execute(plan);

        Assert.Equal(
            "operator edit",
            Read(fixture.Path, ".codex/agents/conductor.toml"));
        SquadOwnedFile retained = Assert.Single(plan.Receipt.Files);
        Assert.Equal(Digest("installed body"), retained.Sha256);
        Assert.False(retained.Adopted);
    }

    /// <summary>
    ///     Progressively disclosed references are ordinary deployment files. Their rendered paths
    ///     must therefore receive the same exact-byte ownership and status guarantees as principals.
    /// </summary>
    [Fact]
    public void RenderedResourcesAreReceiptOwnedWithExactHashesAndStatusDetectsTheirDrift()
    {
        using TempDirectory fixture = new();
        const string agentResource = ".codex/agents/conductor/references/plan-path.md";
        const string skillResource = ".codex/skills/product-owner/references/requirements.md";
        const string agentContent = "# Plan path\n";
        const string skillContent = "# Requirements phase\n";
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [
                Rendered(agentResource, agentContent),
                Rendered(skillResource, skillContent)
            ],
            [],
            false,
            new FixedTimeProvider(InstalledAt));

        new SquadTransaction(store).Execute(plan);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(
            fixture.Path,
            SquadDeploymentScope.Project));
        Assert.Equal(
            [agentResource, skillResource],
            receipt.Files.Select(file => file.RelativePath));
        Assert.Equal(
            [Digest(agentContent), Digest(skillContent)],
            receipt.Files.Select(file => file.Sha256));
        Assert.All(receipt.Files, file => Assert.Equal("codex", file.Target));

        SquadStatusCommand command = new(stateStore: store);
        CapturedConsoleExecution<int> clean = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = false
            }));
        Assert.Equal(0, clean.Result);

        Write(fixture.Path, agentResource, "operator edit\n");
        CapturedConsoleExecution<int> drifted = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = false
            }));

        Assert.Equal(1, drifted.Result);
        Assert.Contains(agentResource, drifted.Output, StringComparison.Ordinal);
        Assert.Contains("drift", drifted.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resource removal cannot broaden update authority: unchanged obsolete resources may be
    ///     deleted, while locally edited current or obsolete resources remain owned at their prior hash.
    /// </summary>
    [Fact]
    public void ResourceUpdateAndRemovalPreserveLocallyEditedReceiptOwnedFiles()
    {
        using TempDirectory fixture = new();
        const string currentResource = ".codex/agents/conductor/references/plan-path.md";
        const string editedObsoleteResource = ".codex/agents/conductor/references/legacy.md";
        const string unchangedObsoleteResource = ".codex/skills/product-owner/references/legacy.md";
        SquadOwnedFile currentOwned = new(currentResource, Digest("installed plan\n"), "codex", false);
        SquadOwnedFile editedObsoleteOwned = new(
            editedObsoleteResource,
            Digest("installed legacy agent\n"),
            "codex",
            false);
        SquadOwnedFile unchangedObsoleteOwned = new(
            unchangedObsoleteResource,
            Digest("installed legacy skill\n"),
            "codex",
            false);
        SquadReceipt receipt = Receipt(currentOwned, editedObsoleteOwned, unchangedObsoleteOwned);
        Write(fixture.Path, currentResource, "operator plan edit\n");
        Write(fixture.Path, editedObsoleteResource, "operator legacy edit\n");
        Write(fixture.Path, unchangedObsoleteResource, "installed legacy skill\n");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(currentResource, "updated plan\n")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Transaction(fixture.Path).Execute(plan);

        Assert.Equal("operator plan edit\n", Read(fixture.Path, currentResource));
        Assert.Equal("operator legacy edit\n", Read(fixture.Path, editedObsoleteResource));
        Assert.False(File.Exists(ToPlatformPath(fixture.Path, unchangedObsoleteResource)));
        SquadReceipt persisted = Assert.IsType<SquadReceipt>(Store(fixture.Path).ReadReceipt(
            fixture.Path,
            SquadDeploymentScope.Project));
        Assert.Equal(
            [currentOwned, editedObsoleteOwned],
            persisted.Files);
    }

    /// <summary>
    ///     When updating an existing deployment from Antigravity's fallback role-skill lowering
    ///     shape (.agents/skills/&lt;name&gt;/SKILL.md) to its native agent shape (.agents/agents/&lt;name&gt;/agent.md),
    ///     receipt-diff logic in <see cref="SquadDeploymentPlan.CreateUpdate" /> plans a Delete mutation
    ///     for orphaned fallback skill files whose on-disk bytes still match the previous receipt digest,
    ///     and retains orphaned skill files whose bytes were locally edited by an operator.
    /// </summary>
    [Fact]
    public void CreateUpdateReceiptDiffRetiresFallbackSkillPathsOnNativeAntigravityUpdateAndRetainsLocallyEdited()
    {
        using TempDirectory fixture = new();
        const string target = "antigravity";
        const string retiredSkillPath = ".agents/skills/worker/SKILL.md";
        const string editedSkillPath = ".agents/skills/reviewer/SKILL.md";
        const string installedWorkerContent =
            "---\nname: worker\ndescription: fallback worker skill\n---\nFallback worker instructions\n";
        const string installedReviewerContent =
            "---\nname: reviewer\ndescription: fallback reviewer skill\n---\nFallback reviewer instructions\n";
        const string locallyEditedReviewerContent =
            "---\nname: reviewer\ndescription: operator edited reviewer\n---\nCustom operator instructions\n";

        const string nativeWorkerAgentPath = ".agents/agents/worker/agent.md";
        const string nativeReviewerAgentPath = ".agents/agents/reviewer/agent.md";
        const string nativeWorkerAgentContent =
            "---\nname: worker\ndescription: native worker agent\nenable_write_tools: true\n---\nNative worker instructions\n";
        const string nativeReviewerAgentContent =
            "---\nname: reviewer\ndescription: native reviewer agent\nenable_write_tools: true\n---\nNative reviewer instructions\n";

        SquadOwnedFile retiredSkillOwned = new(
            retiredSkillPath,
            Digest(installedWorkerContent),
            target,
            false);
        SquadOwnedFile editedSkillOwned = new(
            editedSkillPath,
            Digest(installedReviewerContent),
            target,
            false);
        SquadReceipt previousReceipt = Receipt(retiredSkillOwned, editedSkillOwned);

        Write(fixture.Path, retiredSkillPath, installedWorkerContent);
        Write(fixture.Path, editedSkillPath, locallyEditedReviewerContent);
        WriteState(fixture.Path, Lock(), previousReceipt);

        // Verify pre-update on-disk state: fallback files exist with their initial contents, native paths do not yet exist
        Assert.True(File.Exists(ToPlatformPath(fixture.Path, retiredSkillPath)));
        Assert.Equal(installedWorkerContent, Read(fixture.Path, retiredSkillPath));
        Assert.True(File.Exists(ToPlatformPath(fixture.Path, editedSkillPath)));
        Assert.Equal(locallyEditedReviewerContent, Read(fixture.Path, editedSkillPath));
        Assert.False(File.Exists(ToPlatformPath(fixture.Path, nativeWorkerAgentPath)));
        Assert.False(File.Exists(ToPlatformPath(fixture.Path, nativeReviewerAgentPath)));

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [
                Rendered(nativeWorkerAgentPath, nativeWorkerAgentContent, target),
                Rendered(nativeReviewerAgentPath, nativeReviewerAgentContent, target)
            ],
            previousReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        // Verify planned changes: unmodified fallback path is planned for Delete; native paths are planned for Write; locally edited path is not touched
        Assert.Contains(
            plan.PlannedFileChanges,
            change => change.RelativePath == retiredSkillPath &&
                      change.Target == target &&
                      change.Kind == SquadFileMutationKind.Delete);
        Assert.Contains(
            plan.PlannedFileChanges,
            change => change.RelativePath == nativeWorkerAgentPath &&
                      change.Target == target &&
                      change.Kind == SquadFileMutationKind.Write);
        Assert.Contains(
            plan.PlannedFileChanges,
            change => change.RelativePath == nativeReviewerAgentPath &&
                      change.Target == target &&
                      change.Kind == SquadFileMutationKind.Write);
        Assert.DoesNotContain(
            plan.PlannedFileChanges,
            change => change.RelativePath == editedSkillPath);

        // Verify planned mutations and preconditions
        SquadFileMutation deleteMutation = Assert.Single(
            plan.FileMutations,
            m => m.RelativePath == retiredSkillPath);
        Assert.Equal(SquadFileMutationKind.Delete, deleteMutation.Kind);
        Assert.Equal(target, deleteMutation.Target);

        SquadFileMutation workerWrite = Assert.Single(
            plan.FileMutations,
            m => m.RelativePath == nativeWorkerAgentPath);
        Assert.Equal(SquadFileMutationKind.Write, workerWrite.Kind);
        Assert.Equal(target, workerWrite.Target);
        Assert.Equal(nativeWorkerAgentContent, Encoding.UTF8.GetString(workerWrite.Content!));

        SquadFileMutation reviewerWrite = Assert.Single(
            plan.FileMutations,
            m => m.RelativePath == nativeReviewerAgentPath);
        Assert.Equal(SquadFileMutationKind.Write, reviewerWrite.Kind);
        Assert.Equal(target, reviewerWrite.Target);
        Assert.Equal(nativeReviewerAgentContent, Encoding.UTF8.GetString(reviewerWrite.Content!));

        Assert.DoesNotContain(plan.FileMutations, m => m.RelativePath == editedSkillPath);

        SquadFilePrecondition deletePrecondition = Assert.Single(
            plan.FilePreconditions,
            p => p.RelativePath == retiredSkillPath);
        Assert.Equal(SquadFilePreconditionKind.ExactFile, deletePrecondition.Kind);
        Assert.Equal(Digest(installedWorkerContent), deletePrecondition.Sha256);

        SquadFilePrecondition workerPrecondition = Assert.Single(
            plan.FilePreconditions,
            p => p.RelativePath == nativeWorkerAgentPath);
        Assert.Equal(SquadFilePreconditionKind.Missing, workerPrecondition.Kind);

        SquadFilePrecondition reviewerPrecondition = Assert.Single(
            plan.FilePreconditions,
            p => p.RelativePath == nativeReviewerAgentPath);
        Assert.Equal(SquadFilePreconditionKind.Missing, reviewerPrecondition.Kind);

        // Verify planned receipt: retired skill is omitted; edited skill is retained at original digest; new native agents are owned
        Assert.DoesNotContain(plan.Receipt.Files, f => f.RelativePath == retiredSkillPath);

        SquadOwnedFile retainedSkill = Assert.Single(
            plan.Receipt.Files,
            f => f.RelativePath == editedSkillPath);
        Assert.Equal(Digest(installedReviewerContent), retainedSkill.Sha256);
        Assert.False(retainedSkill.Adopted);
        Assert.Equal(target, retainedSkill.Target);

        SquadOwnedFile plannedWorkerAgent = Assert.Single(
            plan.Receipt.Files,
            f => f.RelativePath == nativeWorkerAgentPath);
        Assert.Equal(Digest(nativeWorkerAgentContent), plannedWorkerAgent.Sha256);
        Assert.False(plannedWorkerAgent.Adopted);
        Assert.Equal(target, plannedWorkerAgent.Target);

        SquadOwnedFile plannedReviewerAgent = Assert.Single(
            plan.Receipt.Files,
            f => f.RelativePath == nativeReviewerAgentPath);
        Assert.Equal(Digest(nativeReviewerAgentContent), plannedReviewerAgent.Sha256);
        Assert.False(plannedReviewerAgent.Adopted);
        Assert.Equal(target, plannedReviewerAgent.Target);

        Assert.Equal(3, plan.Receipt.Files.Count);

        // Execute transaction and verify physical filesystem and receipt store outcomes
        Transaction(fixture.Path).Execute(plan);

        // Unmodified fallback skill was deleted from disk
        Assert.False(File.Exists(ToPlatformPath(fixture.Path, retiredSkillPath)));

        // Locally edited fallback skill was retained untouched on disk with operator modifications intact
        Assert.True(File.Exists(ToPlatformPath(fixture.Path, editedSkillPath)));
        Assert.Equal(locallyEditedReviewerContent, Read(fixture.Path, editedSkillPath));

        // Native agent files now exist on disk with rendered content
        Assert.True(File.Exists(ToPlatformPath(fixture.Path, nativeWorkerAgentPath)));
        Assert.Equal(nativeWorkerAgentContent, Read(fixture.Path, nativeWorkerAgentPath));
        Assert.True(File.Exists(ToPlatformPath(fixture.Path, nativeReviewerAgentPath)));
        Assert.Equal(nativeReviewerAgentContent, Read(fixture.Path, nativeReviewerAgentPath));

        // Persisted receipt matches planned receipt and omits retired fallback skill
        SquadReceipt persisted = Assert.IsType<SquadReceipt>(Store(fixture.Path).ReadReceipt(
            fixture.Path,
            SquadDeploymentScope.Project));
        AssertReceiptEqual(plan.Receipt, persisted);
        Assert.DoesNotContain(persisted.Files, f => f.RelativePath == retiredSkillPath);
    }

    /// <summary>
    ///     Prevents a host that deployed <c>create-pull-request-github</c> before it was retired
    ///     into <c>create-pull-request</c> from keeping the retired skill forever after
    ///     <c>squad update</c>, or from losing an operator's edited copy of it. The rendered set is
    ///     the real canonical Claude render, so the test fails while the retired skill is still
    ///     canonical.
    /// </summary>
    [Fact]
    public async Task CreateUpdateRetiresCreatePullRequestGithubFromAPriorReleaseUnlessLocallyEdited()
    {
        const string retiredSkillPath = ".claude/skills/create-pull-request-github/SKILL.md";
        const string installedContent =
            "---\nname: create-pull-request-github\ndescription: Prior release\n---\nPrior release guidance\n";
        const string editedContent =
            "---\nname: create-pull-request-github\ndescription: Operator edited\n---\nOperator guidance\n";
        SquadRenderResult render = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Claude);
        Assert.True(render.Success, string.Join("; ", render.Errors));
        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            retiredSkillPath,
            Digest(installedContent),
            "claude",
            false));

        using (TempDirectory fixture = new())
        {
            Write(fixture.Path, retiredSkillPath, installedContent);
            WriteState(fixture.Path, Lock(), previousReceipt);

            SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                render.Files,
                previousReceipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));

            Assert.Contains(
                plan.PlannedFileChanges,
                change => change.RelativePath == retiredSkillPath &&
                          change.Kind == SquadFileMutationKind.Delete);
            Transaction(fixture.Path).Execute(plan);

            Assert.False(File.Exists(ToPlatformPath(fixture.Path, retiredSkillPath)));
            SquadReceipt persisted = Assert.IsType<SquadReceipt>(Store(fixture.Path).ReadReceipt(
                fixture.Path,
                SquadDeploymentScope.Project));
            Assert.DoesNotContain(persisted.Files, file => file.RelativePath == retiredSkillPath);
        }

        foreach (bool replaceManaged in new[] { false, true })
        {
            using TempDirectory fixture = new();
            Write(fixture.Path, retiredSkillPath, editedContent);
            WriteState(fixture.Path, Lock(), previousReceipt);

            SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                render.Files,
                previousReceipt,
                [],
                replaceManaged,
                new FixedTimeProvider(InstalledAt.AddDays(1)));

            Assert.DoesNotContain(plan.PlannedFileChanges, change => change.RelativePath == retiredSkillPath);
            Transaction(fixture.Path).Execute(plan);

            Assert.Equal(editedContent, Read(fixture.Path, retiredSkillPath));
            SquadReceipt persisted = Assert.IsType<SquadReceipt>(Store(fixture.Path).ReadReceipt(
                fixture.Path,
                SquadDeploymentScope.Project));
            Assert.Contains(persisted.Files, file => file.RelativePath == retiredSkillPath);
        }
    }

    /// <summary>
    ///     A failed resource update must restore the exact prior bytes and ownership receipt, even
    ///     when the failure happens after the new resource after-image has been published.
    /// </summary>
    [Fact]
    public void ResourceUpdateFailureRollsBackPriorBytesAndReceipt()
    {
        using TempDirectory fixture = new();
        const string resourcePath = ".codex/agents/conductor/references/plan-path.md";
        const string installedContent = "installed plan reference\n";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            resourcePath,
            Digest(installedContent),
            "codex",
            false));
        Write(fixture.Path, resourcePath, installedContent);
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(resourcePath, "updated plan reference\n")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        LifecycleStepFailingObserver failure = new(
            fixture.Path,
            SquadTransactionStepKind.FileApplied);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(fixture.Path, failure).Execute(plan));

        Assert.Equal(installedContent, Read(fixture.Path, resourcePath));
        SquadReceipt restored = Assert.IsType<SquadReceipt>(Store(fixture.Path).ReadReceipt(
            fixture.Path,
            SquadDeploymentScope.Project));
        AssertReceiptEqual(receipt, restored);
    }

    [Fact]
    public void CreateUpdateLocallyEditedManagedFileWithReplaceReplacesOnlyReceiptOwnedPath()
    {
        using TempDirectory fixture = new();
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(fixture.Path, ".codex/agents/conductor.toml", "operator edit");
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            oldReceipt,
            [],
            true,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Transaction(fixture.Path).Execute(plan);

        Assert.Equal(
            "updated body",
            Read(fixture.Path, ".codex/agents/conductor.toml"));
        SquadOwnedFile replaced = Assert.Single(plan.Receipt.Files);
        Assert.Equal(Digest("updated body"), replaced.Sha256);
    }

    [Fact]
    public void ExecuteUpdateFileChangedAfterExactHashPreflightRefusesToOverwriteRacedEdit()
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(fixture.Path, relativePath, "installed body");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(relativePath, "updated body")],
            receipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        Write(fixture.Path, relativePath, "raced operator edit");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced operator edit", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Fact]
    public void CreateUpdateReplaceManagedDoesNotAuthorizeUnmanagedCollision()
    {
        using TempDirectory fixture = new();
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(fixture.Path, ".codex/agents/conductor.toml", "installed body");
        Write(fixture.Path, ".codex/agents/reviewer.toml", "unmanaged file");
        SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated body"),
                    Rendered(".codex/agents/reviewer.toml", "generated body")
                ],
                oldReceipt,
                [],
                true,
                new FixedTimeProvider(InstalledAt.AddDays(1))));

        Assert.Contains(
            ".codex/agents/reviewer.toml",
            exception.Message,
            StringComparison.Ordinal);
        AssertSnapshotsEqual(before, Snapshot(fixture.Path));
    }

    [Fact]
    public void CreateUninstallUnchangedOwnedFileIsRemovedAndEditedOwnedFileIsRetained()
    {
        using TempDirectory fixture = new();
        SquadOwnedFile unchanged = new(
            ".codex/agents/conductor.toml",
            Digest("installed conductor"),
            "codex",
            false);
        SquadOwnedFile edited = new(
            ".codex/agents/reviewer.toml",
            Digest("installed reviewer"),
            "codex",
            false);
        SquadReceipt receipt = Receipt(unchanged, edited);
        Write(fixture.Path, unchanged.RelativePath, "installed conductor");
        Write(fixture.Path, edited.RelativePath, "operator reviewer edit");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);

        Transaction(fixture.Path).Execute(plan);

        Assert.False(File.Exists(Path.Combine(fixture.Path, unchanged.RelativePath)));
        Assert.Equal("operator reviewer edit", Read(fixture.Path, edited.RelativePath));
        SquadReceipt? retainedReceipt = Store(fixture.Path).ReadReceipt(
            fixture.Path,
            SquadDeploymentScope.Project);
        SquadOwnedFile retained = Assert.Single(Assert.IsType<SquadReceipt>(retainedReceipt).Files);
        Assert.Equal(edited, retained);
    }

    [Fact]
    public void ExecuteUninstallFileChangedAfterExactHashPreflightRefusesToDeleteRacedEdit()
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(fixture.Path, relativePath, "installed body");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);
        Write(fixture.Path, relativePath, "raced operator edit");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced operator edit", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData(".codex/../../outside.md")]
    public void CreateInstallLexicalPathEscapeIsRejectedBeforeWrites(string relativePath)
    {
        using TempDirectory fixture = new();

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(relativePath, "escape")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    [Fact]
    public void CreateInstallPathThatResolvesThroughSymlinkOutsideRootIsRejected()
    {
        using TempDirectory fixture = new();
        using TempDirectory outside = new();
        string codex = Path.Combine(fixture.Path, ".codex");
        Directory.CreateDirectory(codex);
        Directory.CreateSymbolicLink(Path.Combine(codex, "agents"), outside.Path);

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/conductor.toml", "escape")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public void CreateUninstallReceiptPathThatResolvesOutsideRootIsRejected()
    {
        using TempDirectory fixture = new();
        using TempDirectory outside = new();
        string codex = Path.Combine(fixture.Path, ".codex");
        Directory.CreateDirectory(codex);
        Directory.CreateSymbolicLink(Path.Combine(codex, "agents"), outside.Path);
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("outside"),
                "codex",
                false));

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                receipt));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteActiveTransactionHoldsAtomicExclusiveLease()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction transaction = Transaction(fixture.Path, blocker);
        SquadDeploymentPlan plan = fixture.CreateUpdatePlan();
        Task execution = Task.Run(() => transaction.Execute(plan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The transaction did not reach its durable intent checkpoint.");

        bool heldExclusiveLease;
        try
        {
            // The lease lives in the project journal beside the root, not in the home-anchored
            // state directory, so that is where an exclusive hold must be observable.
            string leaseDirectory = Store(fixture.Path).ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project);
            heldExclusiveLease = Directory.EnumerateFiles(
                    leaseDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Any(IsHeldWithExclusiveFileShare);
        }
        finally
        {
            blocker.Release.Set();
        }

        await execution;
        Assert.True(
            heldExclusiveLease,
            "An active transaction must own an atomically acquired exclusive lease, not rely on a check-then-create directory race.");
    }

    [Fact]
    public async Task RecoverWhileExecuteOwnsLeaseRefusesToInterleaveWithActiveTransaction()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = Transaction(fixturePath, blocker);
        SquadDeploymentPlan plan = fixture.CreateUpdatePlan();
        Task execution = Task.Run(() => activeTransaction.Execute(plan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The transaction did not reach its durable intent checkpoint.");

        Exception? recoveryException;
        try
        {
            SquadTransaction recoveryTransaction = Transaction(fixturePath);
            recoveryException = Record.Exception(() =>
                recoveryTransaction.Recover(
                    fixturePath,
                    SquadDeploymentScope.Project));
        }
        finally
        {
            blocker.Release.Set();
        }

        await execution;
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(recoveryException);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("updated body", Read(fixturePath, fixture.RelativePath));
    }

    [Fact]
    public void ExecuteStagesBacksUpAndJournalsBeforeApplyingStateLast()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        RecordingObserver observer = new(fixture.Path);
        SquadTransaction transaction = Transaction(fixture.Path, observer);

        transaction.Execute(fixture.CreateUpdatePlan());

        Assert.Equal(
            [
                SquadTransactionStepKind.IntentWritten,
                SquadTransactionStepKind.FileStaged,
                SquadTransactionStepKind.FileBackedUp,
                SquadTransactionStepKind.FileApplied,
                SquadTransactionStepKind.LockApplied,
                SquadTransactionStepKind.ReceiptApplied
            ],
            observer.Steps.Select(step => step.Kind));
        Assert.True(observer.SawIntentJournal);
        Assert.True(observer.SawStagedFile);
        Assert.True(observer.SawBackupFile);
        Assert.Equal("updated body", Read(fixture.Path, fixture.RelativePath));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Path,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecutePlainObserverReceivesOnlyLegacyLifecycleSteps()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        RecordingObserver observer = new(fixture.Path);

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        SquadTransactionStepKind[] expected =
        [
            SquadTransactionStepKind.IntentWritten,
            SquadTransactionStepKind.FileStaged,
            SquadTransactionStepKind.FileBackedUp,
            SquadTransactionStepKind.FileApplied,
            SquadTransactionStepKind.LockApplied,
            SquadTransactionStepKind.ReceiptApplied
        ];
        Assert.Equal(expected, Enum.GetValues<SquadTransactionStepKind>());
        Assert.Equal(expected, observer.Steps.Select(step => step.Kind));
        Assert.Equal(
            Enumerable.Range(1, expected.Length),
            observer.Steps.Select(step => step.Sequence));
    }

    [Fact]
    public void ExecuteCheckpointObserverOptsIntoPreparedAndTransitionEvents()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        CheckpointRecordingObserver observer = new();

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        Assert.Equal(
            [
                SquadTransactionStepKind.IntentWritten,
                SquadTransactionStepKind.FileStaged,
                SquadTransactionStepKind.FileBackedUp,
                SquadTransactionStepKind.FileApplied,
                SquadTransactionStepKind.LockApplied,
                SquadTransactionStepKind.ReceiptApplied
            ],
            observer.Steps.Select(step => step.Kind));
        Assert.Equal(
            Enumerable.Range(1, 6),
            observer.Steps.Select(step => step.Sequence));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.Prepared,
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            Enum.GetValues<SquadTransactionCheckpointKind>());
        Assert.Equal(
            Enumerable.Range(1, observer.Checkpoints.Count),
            observer.Checkpoints.Select(checkpoint => checkpoint.Sequence));
        Assert.Equal(
            new SquadTransactionCheckpoint(
                1,
                SquadTransactionCheckpointKind.Prepared),
            observer.Checkpoints[0]);
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    fixture.RelativePath,
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    "lock",
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    "receipt",
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
    }

    [Fact]
    public void ExecuteFailureAfterEveryFilesystemStepRestoresOriginalTreeAndRecoveryIsIdempotent()
    {
        using TransactionFixture probeFixture = TransactionFixture.Create();
        RecordingObserver probe = new(probeFixture.Path);
        Transaction(probeFixture.Path, probe).Execute(probeFixture.CreateUpdatePlan());
        int stepCount = probe.Steps.Count;

        Assert.True(stepCount > 0);
        for (int failureSequence = 1; failureSequence <= stepCount; failureSequence++)
        {
            using TransactionFixture fixture = TransactionFixture.Create();
            SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);
            FailingObserver failure = new(fixture.Path, failureSequence);
            SquadTransaction transaction = Transaction(
                fixture.Path,
                failure);

            InjectedSquadTransactionFailure exception = Assert.Throws<InjectedSquadTransactionFailure>(() =>
                transaction.Execute(fixture.CreateUpdatePlan()));

            Assert.Equal(failureSequence, exception.Sequence);
            AssertSnapshotsEqual(before, Snapshot(fixture.Path));

            // Rehydrate the exact tree visible at the injection boundary. This models a process
            // terminating before its catch block can run, while the first assertion above still
            // independently proves that an ordinary caught failure rolls back immediately.
            RestoreSnapshot(fixture.Path, Assert.IsType<SortedDictionary<string, byte[]>>(
                failure.InterruptedSnapshot));
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
            SortedDictionary<string, byte[]> afterFirstRecovery = Snapshot(fixture.Path);
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

            AssertSnapshotsEqual(before, afterFirstRecovery);
            AssertSnapshotsEqual(afterFirstRecovery, Snapshot(fixture.Path));
        }
    }

    [Fact]
    public void ExecuteFailureAfterEachFileMutationRestoresFilesDirectoriesSymlinksAndFreshParents()
    {
        using RichTransactionFixture probeFixture = RichTransactionFixture.Create();
        RecordingObserver probe = new(probeFixture.Path);
        Transaction(probeFixture.Path, probe).Execute(probeFixture.CreateUpdatePlan());
        int stepCount = probe.Steps.Count;

        Assert.True(stepCount >= 10, "The fixture must exercise multiple stage, backup, apply, and state mutations.");
        for (int failureSequence = 1; failureSequence <= stepCount; failureSequence++)
        {
            using RichTransactionFixture fixture = RichTransactionFixture.Create();
            SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
            FailingObserver failure = new(fixture.Path, failureSequence);
            SquadTransaction transaction = Transaction(fixture.Path, failure);

            InjectedSquadTransactionFailure exception = Assert.Throws<InjectedSquadTransactionFailure>(() =>
                transaction.Execute(fixture.CreateUpdatePlan()));

            Assert.Equal(failureSequence, exception.Sequence);
            AssertTreesEqual(before, CaptureTree(fixture.Path));

            RestoreTree(
                fixture.Path,
                Assert.IsType<SortedDictionary<string, TreeEntry>>(
                    failure.InterruptedTreeSnapshot));
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
            SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixture.Path);
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

            AssertTreesEqual(before, afterFirstRecovery);
            AssertTreesEqual(afterFirstRecovery, CaptureTree(fixture.Path));
        }
    }

    [Theory]
    [InlineData(SquadTransactionStepKind.FileApplied)]
    [InlineData(SquadTransactionStepKind.LockApplied)]
    [InlineData(SquadTransactionStepKind.ReceiptApplied)]
    public void ExecutePostIntentExternalReplacementPreservesRaceAndRetainsRecoverableConflict(
        SquadTransactionStepKind replacementCheckpoint)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SquadStateStore store = Store(fixturePath);
        PostIntentReplacementObserver observer = new(
            fixturePath,
            store,
            SquadDeploymentScope.Project,
            fixture.RelativePath,
            replacementCheckpoint);
        SquadTransaction transaction = new(store, observer);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();

        Exception? exception = Record.Exception(() => transaction.Execute(updatePlan));

        SquadDeploymentConflictException
            conflict = Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.Contains("changed", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(observer.ExternalBytes, File.ReadAllBytes(observer.ReplacedPath));
        if (replacementCheckpoint != SquadTransactionStepKind.FileApplied)
            Assert.Equal("installed body", Read(fixturePath, fixture.RelativePath));
        if (replacementCheckpoint != SquadTransactionStepKind.LockApplied)
            AssertLockEqual(Lock(), Assert.IsType<SquadLock>(store.ReadLock(
                fixturePath,
                SquadDeploymentScope.Project)));
        if (replacementCheckpoint != SquadTransactionStepKind.ReceiptApplied)
            AssertReceiptEqual(fixture.Receipt, Assert.IsType<SquadReceipt>(store.ReadReceipt(
                fixturePath,
                SquadDeploymentScope.Project)));
        Assert.True(
            Directory.Exists(store.ResolveTransactionDirectory(
                fixturePath,
                SquadDeploymentScope.Project)),
            "A compare-and-restore conflict must retain its verified journal and backup authority.");

        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        Exception? recoveryException = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsAssignableFrom<SquadDeploymentConflictException>(recoveryException);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.Equal(observer.ExternalBytes, File.ReadAllBytes(observer.ReplacedPath));

        Exception? repeatedRecoveryException = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsAssignableFrom<SquadDeploymentConflictException>(repeatedRecoveryException);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void ExecutePostIntentExternalChildInTransactionCreatedParentPreservesParentAndReportsConflict()
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        const string generatedPath = ".warp/roles/reviewer.md";
        const string externalPath = ".warp/roles/operator-note.md";
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixturePath,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(generatedPath, "generated reviewer", "warp")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        ExternalChildObserver observer = new(fixturePath, generatedPath, externalPath);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(plan));

        Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.False(File.Exists(ToPlatformPath(fixturePath, generatedPath)));
        Assert.Equal("external operator note", Read(fixturePath, externalPath));
        Assert.True(Directory.Exists(ToPlatformPath(fixturePath, ".warp/roles")));
        Assert.True(Directory.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecutePreparedArtifactsAreAtomicAndDurableBeforeFirstTargetMutation()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        PreparedArtifactsObserver observer = new(
            fixture.Path,
            fixture.RelativePath);

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        Assert.True(observer.ObservedPreparedCheckpoint);
    }

    [Theory]
    [InlineData("truncated-journal")]
    [InlineData("duplicate-journal-field")]
    [InlineData("partial-backup")]
    [InlineData("wrong-backup-digest")]
    public void RecoverTruncatedOrDigestMismatchedPreparedArtifactRefusesAuthority(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixturePath, interrupted);
        CorruptPreparedArtifact(fixturePath, fixture.RelativePath, corruption);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.True(File.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json")));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("staging")]
    [InlineData("backup")]
    public void RecoverInterruptedInsideArtifactWriteRefusesPartialAuthority(string artifact)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixturePath, interrupted);
        ReplacePublishedArtifactWithPartialTemporaryFile(
            fixturePath,
            fixture.RelativePath,
            artifact);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    // A project lease is keyed by the physical root, a global lease by the home. So a project and
    // a global transaction on one root hold independent leases, while two transactions of the same
    // scope contend when they reach the same key, through a symlink alias or directly.
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Global, false, false)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Project, false, false)]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Global, true, false)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Project, true, false)]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Project, false, true)]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Project, true, true)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Global, false, true)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Global, true, true)]
    public async Task ExecuteActiveLeaseOnOnePhysicalRootContendsOnlyWithItsOwnScope(
        SquadDeploymentScope activeScope,
        SquadDeploymentScope contenderScope,
        bool useSymlinkAlias,
        bool expectContention)
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string aliasRoot = Path.Combine(fixture.Path, "target-alias");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateSymbolicLink(aliasRoot, targetRoot);
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan activePlan = SquadDeploymentPlan.CreateInstall(
            targetRoot,
            activeScope,
            Lock(),
            [Rendered(AgentRelativePath(activeScope, "active.toml"), "active")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        string contenderRoot = useSymlinkAlias ? aliasRoot : targetRoot;
        string contenderFile = AgentRelativePath(contenderScope, "contender.toml");
        SquadDeploymentPlan contenderPlan = SquadDeploymentPlan.CreateInstall(
            contenderRoot,
            contenderScope,
            Lock(),
            [Rendered(contenderFile, "contender")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = new(store, blocker);
        Task activeExecution = Task.Run(() =>
            activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The active transaction did not acquire its lease before contention was tested.");
        SortedDictionary<string, TreeEntry> beforeContention = CaptureTree(fixture.Path);

        Exception? recoveryException = null;
        Exception? executeException;
        IReadOnlyDictionary<string, TreeEntry>? afterContention;
        try
        {
            SquadTransaction storeTransaction = new(store);
            if (expectContention)
                recoveryException = Record.Exception(() => storeTransaction.Recover(
                    contenderRoot,
                    contenderScope));

            executeException = Record.Exception(() =>
                storeTransaction.Execute(contenderPlan));
            afterContention = CaptureTree(fixture.Path);
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        if (expectContention)
        {
            AssertActiveLeaseConflict(recoveryException);
            AssertActiveLeaseConflict(executeException);
            AssertTreesEqual(
                beforeContention,
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, TreeEntry>>(afterContention));
            Assert.False(File.Exists(ToPlatformPath(targetRoot, contenderFile)));
        }
        else
        {
            Assert.Null(executeException);
            Assert.Equal("contender", Read(targetRoot, contenderFile));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverFailedFirstGlobalInstallRestoresApplicationDataTopology(
        bool preserveSharedApplicationData)
    {
        int probeStepCount = ProbeGlobalInstallStepCount();

        for (int failureSequence = 1; failureSequence <= probeStepCount; failureSequence++)
        {
            using TempDirectory fixture = new();
            string targetRoot = Path.Combine(fixture.Path, "target");
            string applicationData = Path.Combine(fixture.Path, "application-data");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(Path.Combine(applicationData, "home"));
            if (preserveSharedApplicationData)
            {
                Write(applicationData, "home/.kyber-weave/shared.keep", "shared home store");
                Write(applicationData, "home/.kyber-weave/squad/shared.keep", "shared squad");
            }

            SquadStateStore store = Store(applicationData);
            SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
            SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
            FailingObserver failure = new(fixture.Path, failureSequence);
            SquadTransaction transaction = new(store, failure);

            Assert.Throws<InjectedSquadTransactionFailure>(() => transaction.Execute(plan));
            AssertTreesEqual(before, CaptureTree(fixture.Path));

            RestoreTree(
                fixture.Path,
                Assert.IsType<SortedDictionary<string, TreeEntry>>(
                    failure.InterruptedTreeSnapshot));
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);
            SortedDictionary<string, TreeEntry> recovered = CaptureTree(fixture.Path);
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);

            AssertTreesEqual(before, recovered);
            AssertTreesEqual(recovered, CaptureTree(fixture.Path));
        }
    }

    /// <summary>
    ///     Global state no longer keys on the target root at all. Lexical, symlink and unrelated roots
    ///     all resolve to the one home-anchored lock, receipt and journal, so the alias and binding
    ///     machinery that used to keep them apart has nothing left to decide.
    /// </summary>
    [Fact]
    public void ResolveGlobalStateIgnoresTargetRootAndAnchorsOnHomeForEveryAlias()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string aliasRoot = Path.Combine(fixture.Path, "target-alias");
        string unrelatedRoot = Path.Combine(fixture.Path, "unrelated");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(unrelatedRoot);
        Directory.CreateDirectory(home);
        Directory.CreateSymbolicLink(aliasRoot, targetRoot);
        SquadStateStore store = Store(applicationData);
        string globalState = Path.Combine(
            SquadPhysicalRootIdentity.Resolve(home).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global");

        AssertSameLocation(
            Path.Combine(globalState, "squad.lock.yml"),
            store.ResolveLockPath(targetRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveLockPath(targetRoot, SquadDeploymentScope.Global),
            store.ResolveLockPath(aliasRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            store.ResolveTransactionDirectory(aliasRoot, SquadDeploymentScope.Global));
        AssertSameLocation(
            Path.Combine(globalState, "squad.receipt.json"),
            store.ResolveReceiptPath(unrelatedRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(Path.Combine(targetRoot, "."), SquadDeploymentScope.Global));
    }

    /// <summary>
    ///     On a case-insensitive filesystem the two spellings name one directory, so they must resolve to
    ///     the same home-anchored receipt. On a case-sensitive filesystem the probe cannot be built and the
    ///     test returns without asserting; that case is covered by the Windows sibling test.
    /// </summary>
    [Fact]
    public void ResolveGlobalStateCaseAliasesShareHomeAnchoredReceiptOnCaseInsensitiveFilesystem()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "CaseSensitiveProbe");
        string caseAlias = Path.Combine(fixture.Path, "casesensitiveprobe");
        Directory.CreateDirectory(targetRoot);
        if (!Directory.Exists(caseAlias))
            return;

        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(home);
        SquadStateStore store = Store(applicationData);
        string expected = Path.Combine(
            SquadPhysicalRootIdentity.Resolve(home).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global",
            "squad.receipt.json");

        AssertSameLocation(expected, store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global));
        AssertSameLocation(expected, store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));
    }

    [Fact]
    public async Task ResolvePhysicalRootCaseDistinctSiblingsRemainDistinctWhenSupported()
    {
        using TempDirectory fixture = new();
        string upperRoot = Path.Combine(fixture.Path, "Root");
        string lowerRoot = Path.Combine(fixture.Path, "root");
        string symlinkRoot = Path.Combine(fixture.Path, "root-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(upperRoot);
        if (Directory.Exists(lowerRoot))
        {
            Assert.Equal(
                SquadPhysicalRootIdentity.Resolve(upperRoot),
                SquadPhysicalRootIdentity.Resolve(lowerRoot));
            return;
        }

        Directory.CreateDirectory(lowerRoot);
        Directory.CreateSymbolicLink(symlinkRoot, upperRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentity.Resolve(upperRoot);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentity.Resolve(lowerRoot);
        SquadPhysicalRootIdentity symlinkIdentity = SquadPhysicalRootIdentity.Resolve(symlinkRoot);
        SquadStateStore store = Store(applicationData);

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        Assert.NotEqual(
            store.ResolveTransactionDirectory(upperRoot, SquadDeploymentScope.Project),
            store.ResolveTransactionDirectory(lowerRoot, SquadDeploymentScope.Project));
        Assert.Equal(upperIdentity, symlinkIdentity);

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = SquadDeploymentPlan.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadDeploymentPlan siblingPlan = SquadDeploymentPlan.CreateInstall(
            lowerRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/lower.toml", "lower")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = new(store, blocker);
        Task activeExecution = Task.Run(() => activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The first case-distinct root did not acquire its physical-root lease.");

        Exception? siblingException;
        try
        {
            SquadTransaction siblingTransaction = new(store);
            siblingException = Record.Exception(() => siblingTransaction.Execute(siblingPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        Assert.Null(siblingException);
        Assert.Equal("upper", Read(upperRoot, ".codex/agents/upper.toml"));
        Assert.Equal("lower", Read(lowerRoot, ".codex/agents/lower.toml"));
    }

    [Fact]
    public async Task ResolvePhysicalRootCaseAliasesConvergeWhenFilesystemIsInsensitive()
    {
        using TempDirectory fixture = new();
        string actualRoot = Path.Combine(fixture.Path, "CaseProbe");
        string caseAlias = Path.Combine(fixture.Path, "caseprobe");
        string symlinkAlias = Path.Combine(fixture.Path, "case-probe-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(actualRoot);
        Directory.CreateSymbolicLink(symlinkAlias, actualRoot);
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentity.Resolve(actualRoot);

        Assert.Equal(actualIdentity, SquadPhysicalRootIdentity.Resolve(symlinkAlias));
        if (!Directory.Exists(caseAlias))
        {
            Assert.NotEqual(
                Path.GetFullPath(actualRoot),
                Path.GetFullPath(caseAlias));
            return;
        }

        SquadPhysicalRootIdentity aliasIdentity = SquadPhysicalRootIdentity.Resolve(caseAlias);
        SquadStateStore store = Store(applicationData);
        Assert.Equal(actualIdentity.PhysicalPath, aliasIdentity.PhysicalPath);
        Assert.Equal(actualIdentity.Key, aliasIdentity.Key);
        Assert.Equal(
            store.ResolveReceiptPath(actualRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = SquadDeploymentPlan.CreateInstall(
            actualRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/actual.toml", "actual")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        // Project scope, not global: the lease of a project is its physical root, so a case alias of that
        // root contends. Global state is leased on the home, which this root is not, so a global alias
        // would correctly not contend with a project at an unrelated root.
        SquadDeploymentPlan aliasPlan = SquadDeploymentPlan.CreateInstall(
            caseAlias,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".warp/roles/alias.md", "alias", "warp")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = new(store, blocker);
        Task activeExecution = Task.Run(() => activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The physical-root alias did not acquire its shared lease.");

        Exception? aliasException;
        try
        {
            SquadTransaction aliasTransaction = new(store);
            aliasException = Record.Exception(() => aliasTransaction.Execute(aliasPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        AssertActiveLeaseConflict(aliasException);
    }

    [Fact]
    public void ExecuteRetargetedRootAliasFailsBeforeWrites()
    {
        using TempDirectory fixture = new();
        string firstRoot = Path.Combine(fixture.Path, "first");
        string secondRoot = Path.Combine(fixture.Path, "second");
        string aliasRoot = Path.Combine(fixture.Path, "root-alias");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateSymbolicLink(aliasRoot, firstRoot);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            aliasRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "generated")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Directory.Delete(aliasRoot);
        Directory.CreateSymbolicLink(aliasRoot, secondRoot);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        Exception? exception = Record.Exception(() =>
            new SquadTransaction(Store(applicationData)).Execute(plan));

        SquadDeploymentConflictException
            conflict = Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.Contains("root", conflict.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("lock", "unknown")]
    [InlineData("lock", "duplicate")]
    [InlineData("lock", "missing")]
    [InlineData("lock", "null")]
    [InlineData("lock", "misspelled")]
    [InlineData("lock", "wrong-case")]
    [InlineData("lock", "wrong-type")]
    [InlineData("lock", "yaml-alias")]
    [InlineData("lock", "yaml-merge")]
    [InlineData("lock", "yaml-tag")]
    [InlineData("receipt", "unknown")]
    [InlineData("receipt", "duplicate")]
    [InlineData("receipt", "missing")]
    [InlineData("receipt", "null")]
    [InlineData("receipt", "misspelled")]
    [InlineData("receipt", "wrong-case")]
    [InlineData("receipt", "wrong-type")]
    [InlineData("receipt", "wrong-enum")]
    [InlineData("receipt", "invalid-timestamp")]
    [InlineData("receipt", "invalid-digest")]
    [InlineData("receipt", "portable-duplicate")]
    public void DeserializeStateUnknownDuplicateMissingNullOrMisspelledFieldsAreRejected(
        string documentKind,
        string corruption)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string document = documentKind == "lock"
            ? CorruptLockDocument(store.SerializeLock(Lock()), corruption)
            : CorruptReceiptDocument(store.SerializeReceipt(Receipt(
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor"),
                    "codex",
                    false))), corruption);

        Exception? exception = Record.Exception(() =>
        {
            if (documentKind == "lock")
                _ = store.DeserializeLock(document);
            else
                _ = store.DeserializeReceipt(document);
        });

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Message));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("null")]
    public void RecoverInvalidPreparedJournalIsNonAuthoritative(string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixturePath, interrupted);
        string intentPath = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");
        string journal = File.ReadAllText(intentPath, Encoding.UTF8);
        File.WriteAllText(
            intentPath,
            CorruptJournalDocument(journal, corruption),
            new UTF8Encoding(false));
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [MemberData(nameof(PortableInvalidPaths))]
    public void CreateLifecyclePlanWindowsForbiddenDeviceControlAndUnicodeAliasesAreRejected(
        string invalidPath)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        SquadOwnedFile owned = new(invalidPath, Digest("owned"), "codex", false);
        SquadReceipt receipt = Receipt(owned);

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(invalidPath, "generated")],
                [],
                false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUpdate(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(invalidPath, "updated")],
                receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixturePath,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixturePath));
    }

    [Theory]
    [InlineData(".codex/agents/\u00e9.md", ".codex/agents/e\u0301.md")]
    [InlineData(".codex/agents/Reviewer.md", ".codex/agents/reviewer.md")]
    public void CreateLifecyclePlanUnicodeAndCaseOnlyPortablePairsAreRejected(
        string firstPath,
        string aliasPath)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("alias"), "codex", false));

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                [],
                false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUpdate(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixturePath,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixturePath));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("lock-state")]
    [InlineData("receipt-state")]
    public void ExecuteStagedArtifactChangedAfterPreparedBeforeApplyIsRejected(string artifact)
    {
        using TempDirectory fixture = new();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(relativePath, "generated conductor")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        StagedArtifactTamperingObserver observer = new(
            fixture.Path,
            relativePath,
            artifact);

        Exception? exception = Record.Exception(() => new SquadTransaction(store, observer).Execute(plan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        string reportedArtifact = artifact == "target-file"
            ? relativePath
            : artifact.Split('-')[0];
        Assert.Contains(reportedArtifact, invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ToPlatformPath(fixture.Path, relativePath)));
        Assert.False(File.Exists(store.ResolveLockPath(fixture.Path, SquadDeploymentScope.Project)));
        Assert.False(File.Exists(store.ResolveReceiptPath(fixture.Path, SquadDeploymentScope.Project)));
        Assert.False(observer.SawAffectedApplyCheckpoint);
        Assert.True(
            Directory.Exists(store.ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project)),
            "A changed prepared stage is non-authoritative evidence and must be retained.");
    }

    [Theory]
    [InlineData("missing-write", "file")]
    [InlineData("missing-write", "directory")]
    [InlineData("missing-write", "link")]
    [InlineData("existing-write", "file")]
    [InlineData("existing-write", "directory")]
    [InlineData("existing-write", "link")]
    [InlineData("existing-delete", "file")]
    [InlineData("existing-delete", "directory")]
    [InlineData("existing-delete", "link")]
    public void LeafClaimChangedPreconditionIsNeverOverwrittenOrDeleted(
        string operation,
        string racedNodeKind)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan;
        if (operation == "missing-write")
        {
            plan = SquadDeploymentPlan.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(relativePath, "generated")],
                [],
                false,
                new FixedTimeProvider(InstalledAt));
        }
        else
        {
            Write(fixturePath, relativePath, "installed");
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest("installed"),
                "codex",
                false));
            WriteState(fixturePath, Lock(), receipt);
            plan = operation == "existing-write"
                ? SquadDeploymentPlan.CreateUpdate(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [Rendered(relativePath, "updated")],
                    receipt,
                    [],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1)))
                : SquadDeploymentPlan.CreateUninstall(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    receipt);
        }

        LeafRaceObserver observer = new(fixturePath, relativePath, racedNodeKind);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() => transaction.Execute(plan));

        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains(Path.GetFileName(relativePath), conflict.Message, StringComparison.Ordinal);
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.RacedEntry),
            CaptureTree(fixturePath)[relativePath]);
        Assert.True(File.Exists(observer.ExternalCanaryPath));
        Assert.True(Directory.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecuteClaimPublicationUsesNoOverwrite()
    {
        string sourcePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs");
        string source = File.ReadAllText(sourcePath, Encoding.UTF8);
        int applyStart = source.IndexOf("private void ApplyFiles", StringComparison.Ordinal);
        int restoreStart =
            source.IndexOf("private static IReadOnlyList<string> RestoreIntent", StringComparison.Ordinal);
        Assert.True(applyStart >= 0 && restoreStart > applyStart);
        string liveApplySource = source[applyStart..restoreStart];

        Assert.Contains("ClaimExistingNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("PublishStageNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("VerifyAfterImage", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("overwrite: true", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteEntry(targetPath", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "ClaimExistingNoOverwrite"), StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "PublishStageNoOverwrite"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("undeclared-file")]
    [InlineData("undeclared-link")]
    [InlineData("undeclared-directory")]
    [InlineData("duplicate-identity")]
    [InlineData("swapped-area")]
    [InlineData("swapped-role")]
    [InlineData("swapped-lifecycle")]
    [InlineData("invalid-transition")]
    [InlineData("well-digested-unexpected")]
    public void RecoverPreparedArtifactAuthorityMustExactlyDescribeTransactionTree(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixturePath, interrupted);
        string transactionDirectory = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        Assert.NotEmpty(artifacts);
        AssertPreparedArtifactAuthority(artifacts);
        CorruptPreparedAuthority(intent, transactionDirectory, corruption);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("wrong-area")]
    [InlineData("non-ancestor")]
    [InlineData("duplicate")]
    [InlineData("unordered")]
    [InlineData("absolute")]
    [InlineData("traversal")]
    [InlineData("sibling")]
    [InlineData("outside-root")]
    [InlineData("legacy-count")]
    [InlineData("negative-count")]
    [InlineData("maximum-count")]
    public void RecoverCorruptCreatedDirectoryAuthorityCannotEscapeBoundRoots(string corruption)
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Write(fixture.Path, "target-canary.txt", "target boundary canary");
        Write(fixture.Path, "application-canary.txt", "application boundary canary");
        Write(fixture.Path, "beside-target/canary.txt", "sibling canary");
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
        CheckpointFailingObserver observer = new(
            fixture.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            new SquadTransaction(store, observer).Execute(plan));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string intentPath = Path.Combine(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        Assert.False(intent.ContainsKey("missingTransactionDirectories"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        Assert.NotEmpty(createdDirectories);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);
        CorruptCreatedDirectoryAuthority(intent, createdDirectories, fixture.Path, corruption);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixture.Path);

        Exception? exception = Record.Exception(() => new SquadTransaction(store).Recover(
            targetRoot,
            SquadDeploymentScope.Global));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixture.Path));
        Assert.Equal("target boundary canary", Read(fixture.Path, "target-canary.txt"));
        Assert.Equal("application boundary canary", Read(fixture.Path, "application-canary.txt"));
        Assert.Equal("sibling canary", Read(fixture.Path, "beside-target/canary.txt"));
    }

    [Fact]
    public void RecoverValidCreatedDirectoryAuthorityRemovesOnlyDeclaredEmptyDirectoriesDeepestFirst()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Write(fixture.Path, "beside-target/canary.txt", "sibling canary");
        Write(applicationData, "shared/canary.txt", "application canary");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        SquadStateStore store = Store(applicationData);
        CheckpointFailingObserver observer = new(
            fixture.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            new SquadTransaction(store, observer).Execute(CreateGlobalInstallPlan(targetRoot)));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string intentPath = Path.Combine(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);

        new SquadTransaction(store).Recover(targetRoot, SquadDeploymentScope.Global);

        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Fact]
    public void DeserializeAuthorityIntegerEnumTokensAreRejected()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        List<string> failures = new();
        string receiptJson = store.SerializeReceipt(fixture.Receipt);
        foreach (int numericValue in new[] { 0, 1, int.MaxValue })
        {
            string numericReceipt = ReplaceJsonStringPropertyWithNumber(
                receiptJson,
                "scope",
                numericValue,
                out int replacements);
            Assert.Equal(1, replacements);
            if (Record.Exception(() => store.DeserializeReceipt(numericReceipt)) is not InvalidDataException)
                failures.Add($"receipt.scope={numericValue}");
        }

        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        string fixturePath = fixture.Path;
        SquadTransaction transaction = Transaction(fixturePath);
        Dictionary<string, IReadOnlyList<int>> journalCases = new(StringComparer.Ordinal)
        {
            ["phase"] = [0, 1, int.MaxValue],
            ["originalKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["afterKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["lockKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["lockAfterKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["receiptKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["receiptAfterKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["area"] = [0, 1, 2, int.MaxValue],
            ["role"] = [0, 1, 2, 3, 4, 5, 6, 7, int.MaxValue],
            ["nodeKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["lifecycleState"] = [0, 1, 2, int.MaxValue]
        };
        foreach ((string? property, IReadOnlyList<int>? numericValues) in journalCases)
            foreach (int numericValue in numericValues)
            {
                RestoreTree(fixturePath, interrupted);
                string intentPath = Path.Combine(
                    fixturePath,
                    ".kyber-weave",
                    ".squad-transaction",
                    "intent.json");
                string numericJournal = ReplaceJsonStringPropertyWithNumber(
                    File.ReadAllText(intentPath, Encoding.UTF8),
                    property,
                    numericValue,
                    out int replacements);
                if (replacements == 0)
                {
                    failures.Add($"journal.{property}=<missing>");
                    continue;
                }

                File.WriteAllText(intentPath, numericJournal, new UTF8Encoding(false));
                SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
                Exception? exception = Record.Exception(() => transaction.Recover(
                    fixturePath,
                    SquadDeploymentScope.Project));
                if (exception is not InvalidDataException)
                    failures.Add($"journal.{property}={numericValue}");
                if (!TreesEqual(beforeRecovery, CaptureTree(fixturePath)))
                    failures.Add($"journal.{property}={numericValue}:filesystem-access");
            }

        Assert.True(
            failures.Count == 0,
            $"Integer enum tokens were accepted or reached filesystem recovery: {string.Join(", ", failures)}");
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("prepared")]
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "The serializer emits scope tokens in lowercase; asserting an "
                        + "uppercased token would assert a format it never writes.")]
    public void SerializeAuthorityStrictReaderRoundTripsEverySupportedShape(string journalPhase)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        SquadLock[] locks =
        [
            Lock(),
            Lock() with { Targets = [], Exclusions = [] }
        ];
        foreach (SquadLock squadLock in locks)
            AssertLockEqual(squadLock, store.DeserializeLock(store.SerializeLock(squadLock)));

        foreach (SquadDeploymentScope scope in Enum.GetValues<SquadDeploymentScope>())
        {
            // The fixture's file is dot-prefixed (it doubles as Project-scope fixture data
            // elsewhere), so forcing Scope to Global here must also carry a Layout that agrees
            // with that path's own classification (#91's read-side cross-check) — otherwise this
            // round-trip would hand the v2 writer a self-contradicting receipt of the test's own
            // making, not the serializer's.
            SquadReceiptLayout globalLayout = SquadDeploymentPlan.ClassifyGlobalLayout(fixture.Receipt.Files);
            SquadReceipt[] receipts =
            [
                fixture.Receipt with
                {
                    Scope = scope,
                    Layout = scope == SquadDeploymentScope.Global ? globalLayout : fixture.Receipt.Layout
                },
                fixture.Receipt with
                {
                    Scope = scope,
                    Degradations = [],
                    Files = []
                }
            ];
            foreach (SquadReceipt receipt in receipts)
            {
                string serialized = store.SerializeReceipt(receipt);
                Assert.Contains($"\"scope\": \"{scope.ToString().ToLowerInvariant()}\"", serialized,
                    StringComparison.Ordinal);

                // A Global receipt always serializes on the v2 schema (A3); Project stays on
                // whatever schema the fixture already carries.
                SquadReceipt expected = scope == SquadDeploymentScope.Global
                    ? receipt with { Schema = SquadStateStore.ReceiptSchemaV2 }
                    : receipt;
                AssertReceiptEqual(expected, store.DeserializeReceipt(serialized));
            }
        }

        SortedDictionary<string, TreeEntry> original = CaptureTree(fixture.Path);
        SortedDictionary<string, TreeEntry> interrupted = journalPhase == "preparing"
            ? InterruptTransactionAfter(
                fixture,
                SquadTransactionStepKind.IntentWritten)
            : InterruptTransactionAfterCheckpoint(
                fixture,
                SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixture.Path, interrupted);
        string intentPath = Path.Combine(
            fixture.Path,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.Equal(journalPhase, intent["phase"]?.GetValue<string>());
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);
        AssertJsonEnumTokensAreStrings(intent);
        if (journalPhase == "prepared")
        {
            JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
            AssertPreparedArtifactAuthority(artifacts);
        }

        Transaction(fixture.Path).Recover(fixture.Path, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mutated")]
    [InlineData("extra")]
    public void ExecuteClosedArtifactVerificationOccursBeforeFirstLeafMutation(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        PreparedBoundaryCorruptionObserver observer = new(
            fixturePath,
            fixture.RelativePath,
            corruption);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(updatePlan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(observer.CorruptedPreparedArtifacts);
        Assert.False(
            observer.SawLeafOrStateMutation,
            "Closed artifact verification must run before an active transition, leaf claim, or state mutation.");
        // The retained journal creates <root>/.kyber-weave before verification runs. Its evidence is
        // excluded from the comparison, so the directory itself is the only expected addition, and no
        // leaf artifact may differ.
        SortedDictionary<string, TreeEntry> expectedAfterRejection = new(before, StringComparer.Ordinal)
        {
            [".kyber-weave"] = new TreeEntry(TreeEntryKind.Directory, null, null)
        };
        AssertTreesEqual(expectedAfterRejection, CaptureTreeWithoutTransactionEvidence(fixturePath));
        Assert.True(
            Directory.Exists(Path.Combine(
                fixturePath,
                ".kyber-weave",
                ".squad-transaction")),
            "A rejected prepared generation must be retained as diagnostic evidence.");
    }

    [Theory]
    [InlineData("remove", "target-stage")]
    [InlineData("remove", "target-backup")]
    [InlineData("remove", "target-link-metadata")]
    [InlineData("remove", "state-stage")]
    [InlineData("remove", "state-original")]
    [InlineData("add", "target-stage")]
    [InlineData("add", "target-backup")]
    [InlineData("add", "target-link-metadata")]
    [InlineData("add", "state-stage")]
    [InlineData("add", "state-original")]
    [InlineData("add", "claimed-original")]
    [InlineData("add", "discarded-after-image")]
    public void ExecuteSemanticArtifactSetCannotBeChangedByEditingManifestAndTreeTogether(
        string edit,
        string semanticRole)
    {
        using RichTransactionFixture fixture = RichTransactionFixture.Create();
        string fixturePath = fixture.Path;
        CheckpointFailingObserver observer = new(
            fixturePath,
            SquadTransactionCheckpointKind.Prepared);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(updatePlan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string transactionDirectory = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        EditSemanticArtifactSet(
            intent,
            transactionDirectory,
            edit,
            semanticRole);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("file-write", "ActiveTransitionWritten")]
    [InlineData("file-write", "OriginalClaimed")]
    [InlineData("file-write", "AfterImagePublished")]
    [InlineData("link-write", "ActiveTransitionWritten")]
    [InlineData("link-write", "OriginalClaimed")]
    [InlineData("link-write", "AfterImagePublished")]
    [InlineData("file-delete", "ActiveTransitionWritten")]
    [InlineData("file-delete", "OriginalClaimed")]
    [InlineData("file-delete", "AfterImagePublished")]
    [InlineData("link-delete", "ActiveTransitionWritten")]
    [InlineData("link-delete", "OriginalClaimed")]
    [InlineData("link-delete", "AfterImagePublished")]
    [InlineData("lock-write", "ActiveTransitionWritten")]
    [InlineData("lock-write", "OriginalClaimed")]
    [InlineData("lock-write", "AfterImagePublished")]
    [InlineData("lock-delete", "ActiveTransitionWritten")]
    [InlineData("lock-delete", "OriginalClaimed")]
    [InlineData("lock-delete", "AfterImagePublished")]
    [InlineData("receipt-write", "ActiveTransitionWritten")]
    [InlineData("receipt-write", "OriginalClaimed")]
    [InlineData("receipt-write", "AfterImagePublished")]
    [InlineData("receipt-delete", "ActiveTransitionWritten")]
    [InlineData("receipt-delete", "OriginalClaimed")]
    [InlineData("receipt-delete", "AfterImagePublished")]
    public void RecoverActiveClaimTransitionRestoresOriginalWithoutConflict(
        string scenario,
        string crashPoint)
    {
        Assert.True(
            Enum.TryParse(crashPoint, out SquadTransactionCheckpointKind checkpoint),
            $"K4q requires the opt-in '{crashPoint}' crash boundary.");
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        ActiveTransitionScenario recoveryScenario = CreateActiveTransitionScenario(
            fixture.Path,
            scenario);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        ActiveTransitionCrashObserver observer = new(
            fixture.Path,
            checkpoint,
            recoveryScenario.Subject);
        SquadTransaction transaction = new(store, observer);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(recoveryScenario.Plan));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixture.Path);
        transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

        AssertTreesEqual(before, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixture.Path));
    }

    [Fact]
    public void ResolvePhysicalRootUsesActualOnDiskSpellingAndDirectoryCaseSemantics()
    {
        using TempDirectory fixture = new();
        string actualRoot = Path.Combine(fixture.Path, "ActualSpelling");
        string caseVariant = Path.Combine(fixture.Path, "actualspelling");
        Directory.CreateDirectory(actualRoot);
        DirectoryInfo actualEntry = new DirectoryInfo(fixture.Path).EnumerateDirectories()
            .Single(directory => string.Equals(
                directory.Name,
                "ActualSpelling",
                StringComparison.Ordinal));
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentity.Resolve(actualRoot);

        Assert.Equal(
            actualEntry.Name,
            Path.GetFileName(actualIdentity.PhysicalPath));
        if (Directory.Exists(caseVariant))
        {
            Assert.Equal(
                actualIdentity,
                SquadPhysicalRootIdentity.Resolve(caseVariant));
            return;
        }

        Directory.CreateDirectory(caseVariant);
        SquadPhysicalRootIdentity distinctIdentity = SquadPhysicalRootIdentity.Resolve(caseVariant);
        Assert.NotEqual(actualIdentity.PhysicalPath, distinctIdentity.PhysicalPath);
        Assert.NotEqual(actualIdentity.Key, distinctIdentity.Key);
    }

    [Fact]
    public void ResolvePhysicalRootComparisonPolicyIsNotOperatingSystemWideCaseFolding()
    {
        string identitySource = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadPhysicalRootIdentity.cs"), Encoding.UTF8);
        string transactionSource = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);

        Assert.DoesNotContain("physicalPath.ToUpperInvariant()", identitySource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
            transactionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvePhysicalRootWindowsCaseSensitiveSiblingsRemainDistinct()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine(
                "Windows per-directory case-sensitivity branch is exercised only on Windows; " +
                "the host volume semantics remain covered by the paired cross-platform probes.");
            return;
        }

        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        string caseSensitiveParent = Path.Combine(fixturePath, "case-sensitive-parent");
        Directory.CreateDirectory(caseSensitiveParent);
        if (!TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent, out string diagnostic))
        {
            output.WriteLine(
                $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
            return;
        }

        string upperRoot = Path.Combine(caseSensitiveParent, "Root");
        string lowerRoot = Path.Combine(caseSensitiveParent, "root");
        Directory.CreateDirectory(upperRoot);
        Directory.CreateDirectory(lowerRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentity.Resolve(upperRoot);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentity.Resolve(lowerRoot);
        SquadStateStore store = Store(Path.Combine(fixturePath, "application-data"));

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        // Global state is home-anchored, so case-distinct roots share one global receipt. The
        // project leases below still separate them, which is the part this test pins.
        Assert.Equal(
            store.ResolveReceiptPath(upperRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(lowerRoot, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = new(store, blocker);
        SquadDeploymentPlan upperPlan = SquadDeploymentPlan.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Task active = Task.Run(() => activeTransaction.Execute(upperPlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The first Windows case-sensitive sibling did not acquire its lease.");
        Exception? siblingFailure;
        try
        {
            SquadTransaction siblingTransaction = new(store);
            SquadDeploymentPlan lowerPlan = SquadDeploymentPlan.CreateInstall(
                lowerRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/lower.toml", "lower")],
                [],
                false,
                new FixedTimeProvider(InstalledAt));
            siblingFailure = Record.Exception(() => siblingTransaction.Execute(lowerPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await active;
        Assert.Null(siblingFailure);
    }

    [Theory]
    [InlineData("schema", "")]
    [InlineData("schema", " \t\r\n")]
    [InlineData("schema", "\u00a0\u2003")]
    [InlineData("squad-version", "")]
    [InlineData("squad-version", " \t\r\n")]
    [InlineData("squad-version", "\u00a0\u2003")]
    [InlineData("cli-version", "")]
    [InlineData("cli-version", " \t\r\n")]
    [InlineData("cli-version", "\u00a0\u2003")]
    [InlineData("mcp-version", "")]
    [InlineData("mcp-version", " \t\r\n")]
    [InlineData("mcp-version", "\u00a0\u2003")]
    [InlineData("bundle", "")]
    [InlineData("bundle", " \t\r\n")]
    [InlineData("bundle", "\u00a0\u2003")]
    [InlineData("translation", "")]
    [InlineData("translation", " \t\r\n")]
    [InlineData("translation", "\u00a0\u2003")]
    [InlineData("apm-version", "")]
    [InlineData("apm-version", " \t\r\n")]
    [InlineData("apm-version", "\u00a0\u2003")]
    [InlineData("apm-tag-commit", "")]
    [InlineData("apm-tag-commit", " \t\r\n")]
    [InlineData("apm-tag-commit", "\u00a0\u2003")]
    [InlineData("target", "")]
    [InlineData("target", " \t\r\n")]
    [InlineData("target", "\u00a0\u2003")]
    [InlineData("exclusion", "")]
    [InlineData("exclusion", " \t\r\n")]
    [InlineData("exclusion", "\u00a0\u2003")]
    public void SerializeLockBlankRequiredValuesAreRejected(
        string field,
        string blankValue)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadLock invalid = ReplaceRequiredLockValue(Lock(), field, blankValue);
        string? emitted = null;

        Exception? exception = Record.Exception(() => emitted = store.SerializeLock(invalid));

        Assert.IsType<InvalidDataException>(exception);
        Assert.Null(emitted);
    }

    [Fact]
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Derives the canonical lowercase token the serializer emits, which "
                        + "is the baseline the non-canonical variants are generated from.")]
    public void DeserializeAuthorityNoncanonicalEnumCaseIsRejected()
    {
        using RichTransactionFixture fixture = RichTransactionFixture.Create();
        string fixturePath = fixture.Path;
        SquadStateStore store = Store(fixturePath);
        List<string> failures = new();
        foreach (SquadDeploymentScope scope in Enum.GetValues<SquadDeploymentScope>())
        {
            string canonical = store.SerializeReceipt(fixture.Receipt with { Scope = scope });
            string token = scope.ToString().ToLowerInvariant();
            foreach (string variant in NoncanonicalCaseVariants(token))
            {
                string changed = canonical.Replace(
                    $"\"scope\": \"{token}\"",
                    $"\"scope\": \"{variant}\"",
                    StringComparison.Ordinal);
                if (Record.Exception(() => store.DeserializeReceipt(changed)) is not InvalidDataException)
                    failures.Add($"receipt.scope={variant}");
            }
        }

        CheckpointFailingObserver observer = new(
            fixturePath,
            SquadTransactionCheckpointKind.Prepared);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(updatePlan));
        SortedDictionary<string, TreeEntry> interrupted = Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
        RestoreTree(fixturePath, interrupted);
        string intentPath = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");
        JsonObject canonicalIntent = ReadJsonObject(intentPath);
        (string Property, string Value, int Occurrence)[] enumTokens =
            EnumerateAuthorityEnumTokens(canonicalIntent).ToArray();
        Assert.NotEmpty(enumTokens);
        foreach ((string Property, string Value, int Occurrence) token in enumTokens)
            foreach (string variant in NoncanonicalCaseVariants(token.Value))
            {
                RestoreTree(fixturePath, interrupted);
                JsonObject changed = Assert.IsType<JsonObject>(canonicalIntent.DeepClone());
                Assert.True(
                    ReplaceAuthorityEnumOccurrence(
                        changed,
                        token.Property,
                        token.Value,
                        token.Occurrence,
                        variant),
                    $"Could not replace {token.Property}[{token.Occurrence}].");
                WriteJsonObject(intentPath, changed);
                SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
                SquadTransaction recoveryTransaction = Transaction(fixturePath);
                Exception? exception = Record.Exception(() => recoveryTransaction.Recover(
                    fixturePath,
                    SquadDeploymentScope.Project));
                if (exception is not InvalidDataException)
                    failures.Add($"journal.{token.Property}={variant}");
                if (!TreesEqual(beforeRecovery, CaptureTree(fixturePath)))
                    failures.Add($"journal.{token.Property}={variant}:filesystem-access");
            }

        Assert.True(
            failures.Count == 0,
            "Noncanonical authority enum spellings were accepted or reached filesystem " +
            $"recovery: {string.Join(", ", failures)}");
    }

    [Fact]
    public void ResolveFileWindowsCaseSensitiveSiblingSymlinkEscapeIsRejected()
    {
        using TempDirectory fixture = new();
        AssertHomeStoreSymlinkEscapesAreRejected(fixture.Path);

        string caseSensitiveParent = Path.Combine(fixture.Path, "case-sensitive-parent");
        Directory.CreateDirectory(caseSensitiveParent);
        if (OperatingSystem.IsWindows() &&
            !TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent, out string diagnostic))
        {
            output.WriteLine(
                $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
            return;
        }

        string upperRoot = Path.Combine(caseSensitiveParent, "Root");
        string lowerRoot = Path.Combine(caseSensitiveParent, "root");
        Directory.CreateDirectory(upperRoot);
        if (Directory.Exists(lowerRoot))
        {
            output.WriteLine(
                "The temporary volume is case-insensitive; the case-distinct sibling branch " +
                "is covered by the Linux and capable Windows filesystem-contract runners.");
            return;
        }

        Directory.CreateDirectory(lowerRoot);
        const string canaryRelativePath = "outside-canary.txt";
        Write(lowerRoot, canaryRelativePath, "outside canary");
        SortedDictionary<string, TreeEntry> outsideBefore = CaptureTree(lowerRoot);

        string directAlias = Path.Combine(upperRoot, "escape");
        Directory.CreateSymbolicLink(directAlias, lowerRoot);
        Assert.Throws<SquadPathContainmentException>(() =>
            SquadPathPolicy.ResolveFile(upperRoot, $"escape/{canaryRelativePath}"));

        Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateInstall(
                upperRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered("escape/generated.md", "must remain contained")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Directory.Delete(directAlias);
        Directory.CreateDirectory(Path.Combine(upperRoot, ".kyber-weave"));
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "contained")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Directory.Delete(Path.Combine(upperRoot, ".kyber-weave"));
        Directory.CreateSymbolicLink(Path.Combine(upperRoot, ".kyber-weave"), lowerRoot);

        // Project state lives under the home store, so the in-root link is reached only through the
        // project transaction directory: the journal and its work directory beside the root.
        SquadStateStore projectStore = Store(Path.Combine(fixture.Path, "application-data"));
        Assert.Throws<SquadPathContainmentException>(() =>
            Transaction(upperRoot).Execute(plan));
        Assert.Throws<SquadPathContainmentException>(() =>
            projectStore.ResolveTransactionDirectory(upperRoot, SquadDeploymentScope.Project));
        Assert.Throws<SquadPathContainmentException>(() =>
            projectStore.ResolveTransactionWorkDirectory(upperRoot, SquadDeploymentScope.Project));
        Assert.Throws<SquadPathContainmentException>(() =>
            Transaction(upperRoot).Recover(upperRoot, SquadDeploymentScope.Project));
        AssertTreesEqual(outsideBefore, CaptureTree(lowerRoot));
    }

    /// <summary>
    ///     A symlink planted inside the home store that leads out of the home must stop every home-store
    ///     resolution it sits on. Planted at <c>.kyber-weave</c> it covers the state files and the global
    ///     journal, which lives beside <c>squad</c>. Planted at <c>.kyber-weave/squad</c> it covers the
    ///     state files only, so the journal is not asserted to reject there. Nothing here depends on
    ///     letter case, so it runs on every filesystem.
    /// </summary>
    private static void AssertHomeStoreSymlinkEscapesAreRejected(string workspace)
    {
        const string canaryRelativePath = "outside-canary.txt";
        string project = Path.Combine(workspace, "home-escape-project");
        Directory.CreateDirectory(project);
        (string Label, string LinkRelativePath, bool JournalOnPath)[] plants =
        [
            ("kyber-weave", ".kyber-weave", true),
            ("squad", Path.Combine(".kyber-weave", "squad"), false)
        ];

        foreach ((string label, string linkRelativePath, bool journalOnPath) in plants)
        {
            string applicationData = Path.Combine(workspace, $"home-escape-{label}");
            string home = Path.Combine(applicationData, "home");
            string outside = Path.Combine(workspace, $"home-outside-{label}");
            Directory.CreateDirectory(Path.Combine(home, Path.GetDirectoryName(linkRelativePath)!));
            Directory.CreateDirectory(outside);
            Write(outside, canaryRelativePath, "outside canary");
            SortedDictionary<string, TreeEntry> outsideBefore = CaptureTree(outside);
            Directory.CreateSymbolicLink(Path.Combine(home, linkRelativePath), outside);

            SquadStateStore store = Store(applicationData);
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveReceiptPath(project, SquadDeploymentScope.Project));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveLockPath(project, SquadDeploymentScope.Project));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveReceiptPath(project, SquadDeploymentScope.Global));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveLockPath(project, SquadDeploymentScope.Global));
            if (journalOnPath)
                Assert.Throws<SquadPathContainmentException>(() =>
                    store.ResolveTransactionDirectory(project, SquadDeploymentScope.Global));

            AssertTreesEqual(outsideBefore, CaptureTree(outside));
        }
    }

    [Fact]
    public void AllContainmentHelpersUseCanonicalFileSystemSemantics()
    {
        string deploymentRoot = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment");
        string semanticsPath = Path.Combine(
            deploymentRoot,
            "SquadFileSystemPathSemantics.cs");

        Assert.True(
            File.Exists(semanticsPath),
            "Containment must be centralized in SquadFileSystemPathSemantics.");

        List<string> offenders = new();
        foreach (string sourcePath in Directory.EnumerateFiles(deploymentRoot, "*.cs"))
        {
            if (string.Equals(sourcePath, semanticsPath, StringComparison.Ordinal))
                continue;

            string source = File.ReadAllText(sourcePath, Encoding.UTF8);
            if (source.Contains("private static StringComparison PathComparison", StringComparison.Ordinal) ||
                source.Contains("private static bool IsWithin(", StringComparison.Ordinal) ||
                source.Contains("private static bool PathsEqual(", StringComparison.Ordinal) ||
                source.Contains(
                    "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
                    StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(sourcePath));
        }

        Assert.True(
            offenders.Count == 0,
            "Deployment containment helpers must delegate to " +
            $"SquadFileSystemPathSemantics: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RestoreIntentBackupChangedAfterObserverBoundaryIsNeverConsumed(
        string artifactRole)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        RestoreArtifactTamperingObserver observer = new(
            scenario.ArtifactPath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("artifact", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(observer.MutatedArtifact);
        Assert.True(File.Exists(scenario.ArtifactPath));
        Assert.Equal(observer.CorruptBytes, File.ReadAllBytes(scenario.ArtifactPath));
        Assert.True(File.Exists(scenario.IntentPath));
        AssertTreeEntryEqual(
            scenario.ExpectedLiveAfterImage,
            CaptureTree(fixturePath)[scenario.LiveRelativePath]);

        SortedDictionary<string, TreeEntry> retainedEvidence = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsType<InvalidDataException>(firstRecovery);
        AssertTreesEqual(retainedEvidence, CaptureTree(fixturePath));
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(retainedEvidence, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RecoverRestoreArtifactChangedBeforeRehydratedConsumptionIsNonAuthoritative(
        string artifactRole)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new(
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        File.WriteAllBytes(scenario.ArtifactPath, "corrupt restore evidence"u8.ToArray());
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);

        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.False(TreesEqual(before, CaptureTree(fixturePath)));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RecoverUnchangedRestoreArtifactRestoresExactly(string artifactRole)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new(
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        Transaction(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Transaction(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);

        AssertTreesEqual(before, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void RestoreIntentRestoreSourcesAreReverifiedAtAdjacentUseBoundary()
    {
        string transactionSource = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);
        string restoreIntent = ExtractMethod(transactionSource, "RestoreIntent");
        string restoreEntry = ExtractMethod(transactionSource, "RestoreEntry");

        Assert.Contains("VerifyRestore", restoreIntent, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", restoreEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReceiptNullBlankOrNoncanonicalNestedValuesAreRejected()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string canonicalToken = SquadTargetCatalog.GetToken(SquadTarget.Codex);
        SquadOwnedFile validFile = new(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            canonicalToken,
            false);
        SquadDegradation validDegradation = new(
            canonicalToken,
            "conductor",
            "role-skill-fallback");
        SquadReceipt valid = Receipt(validFile) with { Degradations = [validDegradation] };
        List<(string Name, SquadReceipt Receipt)> invalidReceipts =
        [
            ("null file entry", valid with
            {
                Files = [null!]
            }),
            ("null degradation entry", valid with
            {
                Degradations = [null!]
            })
        ];
        string?[] blankValues = [null, string.Empty, " \t\r\n", "\u00a0\u2003"];
        foreach (string? blank in blankValues)
        {
            invalidReceipts.Add(($"degradation target '{blank}'", valid with
            {
                Degradations = [validDegradation with { Target = blank! }]
            }));
            invalidReceipts.Add(($"degradation subject '{blank}'", valid with
            {
                Degradations = [validDegradation with { Subject = blank! }]
            }));
            invalidReceipts.Add(($"degradation code '{blank}'", valid with
            {
                Degradations = [validDegradation with { Code = blank! }]
            }));
            invalidReceipts.Add(($"owned-file target '{blank}'", valid with
            {
                Files = [validFile with { Target = blank! }]
            }));
        }

        string[] noncanonicalTargets = SquadTargetCatalog.All
            .Select(SquadTargetCatalog.GetToken)
            .SelectMany(NoncanonicalCaseVariants)
            .Concat(["github-copilot", "factory-droids", "unknown-target"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string target in noncanonicalTargets)
        {
            invalidReceipts.Add(($"degradation target '{target}'", valid with
            {
                Degradations = [validDegradation with { Target = target }]
            }));
            invalidReceipts.Add(($"owned-file target '{target}'", valid with
            {
                Files = [validFile with { Target = target }]
            }));
        }

        List<string> accepted = new();
        foreach ((string? name, SquadReceipt? invalidReceipt) in invalidReceipts)
        {
            string? emitted = null;
            Exception? exception = Record.Exception(() => emitted = store.SerializeReceipt(invalidReceipt));
            if (exception is not InvalidDataException || emitted is not null)
                accepted.Add(name);
        }

        Assert.True(
            accepted.Count == 0,
            "Receipt writer accepted invalid nested values: " + string.Join(", ", accepted));
    }

    [Fact]
    public void SerializeReceiptStrictReaderAcceptsEveryWriterOutput()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            string token = SquadTargetCatalog.GetToken(target);
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                    $".agents/{token}/conductor.md",
                    Digest(token),
                    token,
                    false)) with
            {
                Degradations = [new SquadDegradation(token, "conductor", "fallback")]
            };

            string emitted = store.SerializeReceipt(receipt);
            SquadReceipt roundTripped = store.DeserializeReceipt(emitted);

            AssertReceiptEqual(receipt, roundTripped);
            Assert.Contains($"\"target\": \"{token}\"", emitted, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("target", "edit-bytes")]
    [InlineData("target", "replacement-link")]
    [InlineData("target", "replacement-directory")]
    [InlineData("target", "unchanged")]
    [InlineData("lock", "edit-bytes")]
    [InlineData("lock", "replacement-link")]
    [InlineData("lock", "replacement-directory")]
    [InlineData("lock", "unchanged")]
    [InlineData("receipt", "edit-bytes")]
    [InlineData("receipt", "replacement-link")]
    [InlineData("receipt", "replacement-directory")]
    [InlineData("receipt", "unchanged")]
    public void ExecuteTargetAndStateStageChangedAtOriginalClaimedCheckpointIsRejectedBeforePublish(
        string subject,
        string mutation)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        string liveRelativePath = subject switch
        {
            "target" => fixture.RelativePath,
            "lock" => StateRelativePath(
                fixture.Path,
                store.ResolveLockPath(fixture.Path, SquadDeploymentScope.Project)),
            "receipt" => StateRelativePath(
                fixture.Path,
                store.ResolveReceiptPath(fixture.Path, SquadDeploymentScope.Project)),
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };
        TreeEntry originalLiveEntry = CaptureTree(fixture.Path)[liveRelativePath];
        OriginalClaimStageTamperingObserver observer = new(
            fixture.Path,
            fixture.RelativePath,
            subject,
            mutation);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = new(store, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(updatePlan));

        Assert.True(observer.SawOriginalClaimed);
        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Assert.Null(exception);
            Assert.True(observer.SawAffectedAfterImage);
            Assert.True(observer.SawAffectedPublicApply);
            Assert.False(Directory.Exists(store.ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project)));
            return;
        }

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains(
            subject == "target" ? Path.GetFileName(fixture.RelativePath) : subject,
            invalid.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            observer.SawAffectedAfterImage,
            "A changed stage must be rejected before its after-image checkpoint.");
        Assert.False(
            observer.SawAffectedPublicApply,
            "A changed stage must be rejected before its public apply event.");
        AssertTreeEntryEqual(originalLiveEntry, CaptureTree(fixture.Path)[liveRelativePath]);
        Assert.True(File.Exists(observer.IntentPath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedStageEntry),
            CaptureTree(fixture.Path)[observer.StageRelativePath]);
    }

    [Fact]
    public void PublishStageNoOverwriteVerifiesAuthorityAtAdjacentMoveBoundary()
    {
        string source = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);
        string declaration = ExtractMethodDeclaration(source, "PublishStageNoOverwrite");
        string method = ExtractMethod(source, "PublishStageNoOverwrite");

        Assert.Contains("IntentDocument", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactArea", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactRole", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactFingerprintMatches", method, StringComparison.Ordinal);
        Assert.Contains("MoveEntryNoOverwrite", method, StringComparison.Ordinal);

        int fingerprint = method.LastIndexOf("ArtifactFingerprintMatches", StringComparison.Ordinal);
        int move = method.IndexOf("MoveEntryNoOverwrite", fingerprint, StringComparison.Ordinal);
        Assert.True(fingerprint >= 0 && move > fingerprint);
        string adjacentBoundary = method[fingerprint..move];
        Assert.DoesNotContain("Notify", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("await", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIntent", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", adjacentBoundary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("target", "file", "edit-bytes")]
    [InlineData("target", "file-link", "retarget-file-link")]
    [InlineData("target", "file", "replace-directory")]
    [InlineData("target", "file", "unchanged")]
    [InlineData("lock", "file", "edit-bytes")]
    [InlineData("lock", "directory-link", "retarget-directory-link")]
    [InlineData("lock", "directory", "replace-file")]
    [InlineData("lock", "file", "unchanged")]
    [InlineData("receipt", "file", "edit-bytes")]
    [InlineData("receipt", "file-link", "retarget-file-link")]
    [InlineData("receipt", "directory", "replace-file")]
    [InlineData("receipt", "file", "unchanged")]
    public void RecoverClaimChangedAtOriginalClaimedCheckpointIsNeverMovedLive(
        string subject,
        string originalKind,
        string mutation)
    {
        AssertCaughtClaimMutation(subject, originalKind, mutation);
        AssertRehydratedClaimMutation(subject, originalKind, mutation);
    }

    [Theory]
    [InlineData("target-backup")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("lock-link-metadata")]
    [InlineData("receipt-original")]
    [InlineData("receipt-link-metadata")]
    public void RestoreIntentAfterImagePublishedFailureRestoresFilesAndLinksFromVerifiedPayload(
        string artifactRole)
    {
        using TempDirectory caughtFixture = new();
        AfterImageRestoreScenario caughtScenario = CreateAfterImageRestoreScenario(caughtFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> caughtBefore = CaptureTree(caughtFixture.Path);
        AfterImageCrashObserver caughtObserver = new(
            caughtFixture.Path,
            caughtScenario.Subject);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(caughtFixture.Path, caughtObserver).Execute(caughtScenario.Plan));

        AssertTreesEqual(caughtBefore, CaptureTree(caughtFixture.Path));

        using TempDirectory recoveryFixture = new();
        AfterImageRestoreScenario recoveryScenario =
            CreateAfterImageRestoreScenario(recoveryFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> recoveryBefore = CaptureTree(recoveryFixture.Path);
        AfterImageCrashObserver recoveryObserver = new(
            recoveryFixture.Path,
            recoveryScenario.Subject);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(recoveryFixture.Path, recoveryObserver).Execute(recoveryScenario.Plan));
        RestoreTree(
            recoveryFixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                recoveryObserver.InterruptedTreeSnapshot));

        Transaction(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(recoveryFixture.Path);
        Transaction(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);

        AssertTreesEqual(recoveryBefore, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(recoveryFixture.Path));
    }

    [Fact]
    public void RestoreIntentUsesSingleVerifiedImmutablePayload()
    {
        string source = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);
        string capture = ExtractMethod(source, "CaptureVerifiedRestorePayload");
        string compare = ExtractMethod(source, "CompareAndRestoreEntry");
        string restore = ExtractMethod(source, "RestoreEntry");

        Assert.Contains("ReadOnlyMemory<byte>", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(compare, "CaptureVerifiedRestorePayload("));
        Assert.DoesNotContain("File.ReadAll", compare, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAll", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyRestoreArtifact", restore, StringComparison.Ordinal);
        Assert.Contains("payload", restore, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetEntryKind", capture, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(capture, "GetEntryKind") >= 2,
            "Restore payload capture must verify the source node kind before and after its single read.");
        Assert.Equal(1, CountOccurrences(capture, "File.ReadAllBytes(backupPath)"));
        Assert.Equal(1, CountOccurrences(capture, "File.ReadAllBytes(linkMetadataPath)"));
        Assert.Contains("FileMode.CreateNew", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create,", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create)", restore, StringComparison.Ordinal);
        Assert.Contains("CreateSymbolicLink", restore, StringComparison.Ordinal);
        Assert.Contains("GetEntryKind(targetPath)", restore, StringComparison.Ordinal);
        int liveRecheck = restore.LastIndexOf(
            "GetEntryKind(targetPath)",
            StringComparison.Ordinal);
        int liveRemoval = restore.IndexOf(
            "DeleteEntry(targetPath",
            liveRecheck,
            StringComparison.Ordinal);
        Assert.True(
            liveRecheck >= 0 && liveRemoval > liveRecheck,
            "The live after-image must be revalidated immediately before removal.");
        string adjacentRemoval = restore[liveRecheck..liveRemoval];
        Assert.DoesNotContain("File.ReadAll", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("await", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIntent", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("await", restore, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Behavior (1): Receipt layout marker tests — v2 with layout: per-target-roots for global
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SerializeReceiptLayout_GlobalInstall_WritesV2WithPerTargetRootsLayout()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        SquadStateStore store = Store(applicationData);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(".codex/agents/conductor.toml", Digest("conductor"), "codex", false)
            ]);

        string json = store.SerializeReceipt(receipt);

        Assert.Contains("\"schema\": \"kyber-squad.receipt/v2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"layout\": \"per-target-roots\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReceiptLayout_ProjectInstall_StaysV1WithoutLayout()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        SquadStateStore store = Store(applicationData);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(".codex/agents/conductor.toml", Digest("conductor"), "codex", false)
            ]);

        string json = store.SerializeReceipt(receipt);

        Assert.Contains("\"schema\": \"kyber-squad.receipt/v1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"layout\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scope\": \"project\"", json, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Behavior (2): Receipt validation — v2 rejected when invalid, v1 mixed paths rejected
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DeserializeReceiptLayout_V2GlobalReceiptRoundTrips()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string validV2Json = """
                             {
                               "schema": "kyber-squad.receipt/v2",
                               "scope": "global",
                               "layout": "per-target-roots",
                               "targetRoot": ".",
                               "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                               "degradations": [],
                               "files": []
                             }
                             """;

        SquadReceipt receipt = store.DeserializeReceipt(validV2Json);

        Assert.Equal("kyber-squad.receipt/v2", receipt.Schema);
    }

    [Fact]
    public void DeserializeReceiptLayout_V2ProjectReceipt_RejectedWithInvalidDataException()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string invalidV2Json = """
                               {
                                 "schema": "kyber-squad.receipt/v2",
                                 "scope": "project",
                                 "layout": "per-target-roots",
                                 "targetRoot": ".",
                                 "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                 "degradations": [],
                                 "files": []
                               }
                               """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidV2Json));

        Assert.Contains("v2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeReceiptLayout_V2MissingLayout_RejectedWithInvalidDataException()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string invalidJson = """
                             {
                               "schema": "kyber-squad.receipt/v2",
                               "targetRoot": ".",
                               "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                               "degradations": [],
                               "files": []
                             }
                             """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidJson));

        Assert.Contains("layout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeReceiptLayout_V2UnknownLayout_RejectedWithInvalidDataException()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string invalidJson = """
                             {
                               "schema": "kyber-squad.receipt/v2",
                               "layout": "unknown-layout",
                               "targetRoot": ".",
                               "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                               "degradations": [],
                               "files": []
                             }
                             """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidJson));

        Assert.Contains("layout", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown-layout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("per-target-roots", ".codex/agents/conductor.toml")]
    [InlineData("single-root", "agents/conductor.toml")]
    public void DeserializeReceiptLayout_V2LayoutContradictsPaths_RejectedWithInvalidDataException(
        string declaredLayout,
        string relativePath)
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string contradictingJson = $$"""
                                     {
                                       "schema": "kyber-squad.receipt/v2",
                                       "layout": "{{declaredLayout}}",
                                       "targetRoot": ".",
                                       "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                       "degradations": [],
                                       "files": [
                                         {
                                           "relativePath": "{{relativePath}}",
                                           "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                           "target": "codex",
                                           "adopted": false
                                         }
                                       ]
                                     }
                                     """;

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(contradictingJson));

        Assert.Contains("layout", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(declaredLayout, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeReceiptLayout_V1LegacyMixedPrefixedAndBarePaths_RejectedWithInvalidDataException()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string mixedJson = """
                           {
                             "schema": "kyber-squad.receipt/v1",
                             "scope": "global",
                             "targetRoot": ".",
                             "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                             "degradations": [],
                             "files": [
                               {
                                 "relativePath": ".codex/agents/conductor.toml",
                                 "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                 "target": "codex",
                                 "adopted": false
                               },
                               {
                                 "relativePath": "agents/copilot.md",
                                 "sha256": "def456def456def456def456def456def456def456def456def456def456deef",
                                 "target": "copilot",
                                 "adopted": false
                               }
                             ]
                           }
                           """;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(mixedJson));

        Assert.Contains("mix", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeReceiptLayout_V1GlobalWithBarePaths_AcceptedAsPerTargetLayout()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string barePathsJson = """
                               {
                                 "schema": "kyber-squad.receipt/v1",
                                 "scope": "global",
                                 "targetRoot": ".",
                                 "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                 "degradations": [],
                                 "files": [
                                   {
                                     "relativePath": "agents/conductor.toml",
                                     "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                     "target": "codex",
                                     "adopted": false
                                   }
                                 ]
                               }
                               """;

        SquadReceipt receipt = store.DeserializeReceipt(barePathsJson);

        Assert.Equal("kyber-squad.receipt/v1", receipt.Schema);
        Assert.Single(receipt.Files);
        Assert.Equal("agents/conductor.toml", receipt.Files[0].RelativePath);

        string reserialized = store.SerializeReceipt(receipt);
        Assert.Contains("\"layout\": \"per-target-roots\"", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeReceiptLayout_V1GlobalWithPrefixedPaths_AcceptedAsSingleRootLayout()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string prefixedPathsJson = """
                                   {
                                     "schema": "kyber-squad.receipt/v1",
                                     "scope": "global",
                                     "targetRoot": ".",
                                     "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
                                     "degradations": [],
                                     "files": [
                                       {
                                         "relativePath": ".codex/agents/conductor.toml",
                                         "sha256": "abc123abc123abc123abc123abc123abc123abc123abc123abc123abc123abcd",
                                         "target": "codex",
                                         "adopted": false
                                       },
                                       {
                                         "relativePath": ".github/agents/reviewer.agent.md",
                                         "sha256": "def456def456def456def456def456def456def456def456def456def456deef",
                                         "target": "copilot",
                                         "adopted": false
                                       }
                                     ]
                                   }
                                   """;

        SquadReceipt receipt = store.DeserializeReceipt(prefixedPathsJson);

        Assert.Equal("kyber-squad.receipt/v1", receipt.Schema);
        Assert.Equal(2, receipt.Files.Count);

        string reserialized = store.SerializeReceipt(receipt);
        Assert.Contains("\"layout\": \"single-root\"", reserialized, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Behavior (3): Uninstall of legacy v1 global receipt with target-prefixed paths
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void UninstallReceiptLayout_LegacySingleRootReceipt_DeletesCleanFilesUnderRecordedRoot()
    {
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");
        string fakeCodexRoot = Path.Combine(fixture.Path, "fake-codex");
        string fakeCopilotRoot = Path.Combine(fixture.Path, "fake-copilot");
        Directory.CreateDirectory(fakeCodexRoot);
        Directory.CreateDirectory(fakeCopilotRoot);

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor clean");
        Write(recordedRoot, ".github/agents/reviewer.agent.md", "copilot clean");
        Write(recordedRoot, ".codex/agents/edited.toml", "edited content");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor clean"),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".github/agents/reviewer.agent.md",
                    Digest("copilot clean"),
                    "copilot",
                    false),
                new SquadOwnedFile(
                    ".codex/agents/edited.toml",
                    Digest("original installed content"),
                    "codex",
                    false)
            ]);

        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = fakeCodexRoot,
            [SquadTarget.Copilot] = fakeCopilotRoot
        });

        SquadDeploymentPlan uninstallPlan = SquadDeploymentPlan.CreateUninstall(
            recordedRoot,
            SquadDeploymentScope.Global,
            legacyReceipt,
            globalRoots);

        new SquadTransaction(Store(applicationData)).Execute(uninstallPlan);

        Assert.False(File.Exists(Path.Combine(recordedRoot, ".codex/agents/conductor.toml")));
        Assert.False(File.Exists(Path.Combine(recordedRoot, ".github/agents/reviewer.agent.md")));
        Assert.True(File.Exists(Path.Combine(recordedRoot, ".codex/agents/edited.toml")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCodexRoot, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCopilotRoot, "*", SearchOption.AllDirectories));

        SquadReceipt? retainedReceipt = Store(applicationData).ReadReceipt(recordedRoot, SquadDeploymentScope.Global);
        Assert.NotNull(retainedReceipt);
        Assert.Single(retainedReceipt.Files);
        Assert.Equal(".codex/agents/edited.toml", retainedReceipt.Files[0].RelativePath);
        Assert.Equal(SquadReceiptLayout.SingleRoot, retainedReceipt.Layout);

        string retainedJson = Store(applicationData).SerializeReceipt(retainedReceipt);
        Assert.Contains("\"schema\": \"kyber-squad.receipt/v2\"", retainedJson, StringComparison.Ordinal);
        Assert.Contains("\"layout\": \"single-root\"", retainedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallReceiptLayout_LegacyReceiptAllClean_DeletesEverythingThenLockAndReceipt()
    {
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");
        string fakeCodexRoot = Path.Combine(fixture.Path, "fake-codex");
        string fakeCopilotRoot = Path.Combine(fixture.Path, "fake-copilot");
        Directory.CreateDirectory(fakeCodexRoot);
        Directory.CreateDirectory(fakeCopilotRoot);

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor clean");
        Write(recordedRoot, ".github/agents/reviewer.agent.md", "copilot clean");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor clean"),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".github/agents/reviewer.agent.md",
                    Digest("copilot clean"),
                    "copilot",
                    false)
            ]);

        SquadStateStore stateStore = Store(applicationData);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        string lockPath = stateStore.ResolveLockPath(recordedRoot, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, stateStore.SerializeReceipt(legacyReceipt), Encoding.UTF8);
        File.WriteAllText(lockPath, stateStore.SerializeLock(Lock()), Encoding.UTF8);

        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = fakeCodexRoot,
            [SquadTarget.Copilot] = fakeCopilotRoot
        });

        SquadDeploymentPlan uninstallPlan = SquadDeploymentPlan.CreateUninstall(
            recordedRoot,
            SquadDeploymentScope.Global,
            legacyReceipt,
            globalRoots);

        new SquadTransaction(stateStore).Execute(uninstallPlan);

        Assert.False(File.Exists(Path.Combine(recordedRoot, ".codex/agents/conductor.toml")));
        Assert.False(File.Exists(Path.Combine(recordedRoot, ".github/agents/reviewer.agent.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCodexRoot, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeCopilotRoot, "*", SearchOption.AllDirectories));

        Assert.False(File.Exists(receiptPath));
        Assert.False(File.Exists(lockPath));
        Assert.Null(stateStore.ReadReceipt(recordedRoot, SquadDeploymentScope.Global));
    }

    // ---------------------------------------------------------------------------------------
    // Behavior (4): Update and install refuse legacy receipts before download
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReceiptLayout_LegacySingleRootReceipt_ThrowsSquadDeploymentConflictException(bool dryRun)
    {
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor content");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadStateStore stateStore = Store(applicationData);
        string stateDir = stateStore.ResolveStateDirectory(recordedRoot, SquadDeploymentScope.Global);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllTextAsync(
            receiptPath,
            SerializeAsLegacyV1Json(legacyReceipt),
            Encoding.UTF8);

        SortedDictionary<string, byte[]> receiptSnapshotBefore = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotBefore = Snapshot(recordedRoot);

        using RecordingReleaseSource releaseSource = new();
        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = Path.Combine(fixture.Path, "fake-codex")
        });
        SquadLifecycleService service = new(releaseSource, SquadCommandComposition.ResolveRenderer(), stateStore,
            globalRoots: globalRoots);

        SquadDeploymentConflictException exception = await Assert.ThrowsAsync<SquadDeploymentConflictException>(() =>
            service.UpdateAsync(new SquadUpdateRequest(
                recordedRoot,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex],
                DryRun: dryRun)));

        Assert.Contains("squad uninstall --global", exception.Message, StringComparison.Ordinal);
        Assert.Contains("squad install --global", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, releaseSource.CallCount);

        SortedDictionary<string, byte[]> receiptSnapshotAfter = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotAfter = Snapshot(recordedRoot);
        AssertSnapshotsEqual(receiptSnapshotBefore, receiptSnapshotAfter);
        AssertSnapshotsEqual(deploymentSnapshotBefore, deploymentSnapshotAfter);
    }

    [Fact]
    public async Task InstallReceiptLayout_ExistingLegacySingleRootReceipt_ThrowsSquadDeploymentConflictException()
    {
        using TempDirectory fixture = new();
        string recordedRoot = Path.Combine(fixture.Path, "legacy-install");
        Directory.CreateDirectory(recordedRoot);
        string applicationData = Path.Combine(fixture.Path, "app-data");

        Write(recordedRoot, ".codex/agents/conductor.toml", "conductor content");

        SquadReceipt legacyReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadStateStore stateStore = Store(applicationData);
        string stateDir = stateStore.ResolveStateDirectory(recordedRoot, SquadDeploymentScope.Global);
        string receiptPath = stateStore.ResolveReceiptPath(recordedRoot, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        await File.WriteAllTextAsync(
            receiptPath,
            SerializeAsLegacyV1Json(legacyReceipt),
            Encoding.UTF8);

        SortedDictionary<string, byte[]> receiptSnapshotBefore = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotBefore = Snapshot(recordedRoot);

        using RecordingReleaseSource releaseSource = new();
        FakeGlobalRootResolver globalRoots = new(new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Codex] = Path.Combine(fixture.Path, "fake-codex")
        });
        SquadLifecycleService service = new(releaseSource, SquadCommandComposition.ResolveRenderer(), stateStore,
            globalRoots: globalRoots);

        SquadDeploymentConflictException exception = await Assert.ThrowsAsync<SquadDeploymentConflictException>(() =>
            service.InstallAsync(new SquadInstallRequest(
                recordedRoot,
                SquadDeploymentScope.Global,
                [SquadTarget.Codex])));

        Assert.Contains("squad uninstall --global", exception.Message, StringComparison.Ordinal);
        Assert.Contains("squad install --global", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, releaseSource.CallCount);

        SortedDictionary<string, byte[]> receiptSnapshotAfter = Snapshot(stateDir);
        SortedDictionary<string, byte[]> deploymentSnapshotAfter = Snapshot(recordedRoot);
        AssertSnapshotsEqual(receiptSnapshotBefore, receiptSnapshotAfter);
        AssertSnapshotsEqual(deploymentSnapshotBefore, deploymentSnapshotAfter);
    }

    // ---------------------------------------------------------------------------------------
    // Behavior (5): v1 receipt with bare paths updates normally and rewrites as v2 per-target
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void UpdateReceiptLayout_V1BarePaths_RoundTripsAsV2PerTargetRoots()
    {
        using TempDirectory fixture = new();
        string applicationData = Path.Combine(fixture.Path, "app-data");
        string targetRoot = fixture.Path;
        SquadStateStore store = Store(applicationData);

        Write(targetRoot, "agents/conductor.toml", "conductor content");

        SquadReceipt barePathsReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            targetRoot,
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    "agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadDeploymentPlan updatePlan = SquadDeploymentPlan.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered("agents/conductor.toml", "updated content")],
            barePathsReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        string receiptJson = store.SerializeReceipt(updatePlan.Receipt);

        Assert.Contains("\"schema\": \"kyber-squad.receipt/v2\"", receiptJson, StringComparison.Ordinal);
        Assert.Contains("\"layout\": \"per-target-roots\"", receiptJson, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Behavior (6): CreateUpdate stamps its output layout from what it actually retains, even
    // with no resolver supplied — closing the gap where a mismatched label could otherwise be
    // persisted and later trusted by a run that does supply one (F1).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void
        CreateUpdateReceiptLayout_NullResolverMixedRetainedAndRenderedPaths_ThrowsSquadDeploymentConflictException()
    {
        using TempDirectory fixture = new();
        string targetRoot = fixture.Path;

        // The prefixed codex entry is re-rendered under its own old identity and is locally
        // edited, so it is retained as-is (still prefixed) rather than replaced. The claude
        // entry is a brand-new render with a modern bare path. Combined, the update's own
        // output would mix prefixed and bare paths.
        Write(targetRoot, ".codex/agents/conductor.toml", "edited content");

        SquadReceipt previousReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("original content"),
                    "codex",
                    false)
            ]);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateUpdate(
                targetRoot,
                SquadDeploymentScope.Global,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated content"),
                    Rendered("agents/reviewer.md", "reviewer body", "claude")
                ],
                previousReceipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1))));

        Assert.Contains("squad uninstall --global", exception.Message, StringComparison.Ordinal);
        Assert.Contains("squad install --global", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUpdateReceiptLayout_NullResolverAllCleanLegacyReceipt_LayoutMatchesClassification()
    {
        using TempDirectory fixture = new();
        string targetRoot = fixture.Path;

        Write(targetRoot, ".codex/agents/conductor.toml", "conductor content");

        SquadReceipt previousReceipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor content"),
                    "codex",
                    false)
            ]);

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated conductor content")],
            previousReceipt,
            [],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Assert.Equal(
            SquadDeploymentPlan.ClassifyGlobalLayout(plan.Receipt.Files),
            plan.Receipt.Layout);
        Assert.Equal(SquadReceiptLayout.SingleRoot, plan.Receipt.Layout);
    }

    private static void AssertCaughtClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new(
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        Assert.True(observer.SawOriginalClaimed);
        Assert.False(observer.SawAffectedAfterImage);
        Assert.False(observer.SawAffectedPublicApply);
        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Assert.IsType<InjectedSquadTransactionFailure>(exception);
            AssertTreesEqual(original, CaptureTree(fixturePath));
            Assert.False(Directory.Exists(Path.Combine(
                fixturePath,
                ".kyber-weave",
                ".squad-transaction")));
            return;
        }

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("claim", invalid.Message, StringComparison.OrdinalIgnoreCase);
        SortedDictionary<string, TreeEntry> caughtTree = CaptureTree(fixturePath);
        Assert.False(caughtTree.ContainsKey(scenario.LiveRelativePath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedClaimEntry),
            caughtTree[observer.ClaimRelativePath]);
        Assert.True(File.Exists(observer.IntentPath));

        SquadTransaction recoveryTransaction = Transaction(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(caughtTree, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    private static void AssertRehydratedClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new(
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        _ = Record.Exception(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Transaction(fixturePath).Recover(
                fixturePath,
                SquadDeploymentScope.Project);
            SortedDictionary<string, TreeEntry> controlAfterFirstRecovery = CaptureTree(fixturePath);
            Transaction(fixturePath).Recover(
                fixturePath,
                SquadDeploymentScope.Project);

            AssertTreesEqual(original, controlAfterFirstRecovery);
            AssertTreesEqual(controlAfterFirstRecovery, CaptureTree(fixturePath));
            return;
        }

        SortedDictionary<string, TreeEntry> interrupted = CaptureTree(fixturePath);
        Assert.False(interrupted.ContainsKey(scenario.LiveRelativePath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedClaimEntry),
            interrupted[observer.ClaimRelativePath]);

        SquadTransaction recoveryTransaction = Transaction(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(interrupted, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    private static ClaimMutationScenario CreateClaimMutationScenario(
        string root,
        string subject,
        string originalKind)
    {
        const string targetRelativePath = ".codex/agents/conductor.toml";
        const string installedBody = "installed claim body";
        Write(root, targetRelativePath, installedBody);
        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            targetRelativePath,
            Digest(installedBody),
            "codex",
            false));
        WriteState(root, Lock(), previousReceipt);
        SquadStateStore store = Store(root);
        string livePath = subject switch
        {
            "target" => ToPlatformPath(root, targetRelativePath),
            "lock" => store.ResolveLockPath(root, SquadDeploymentScope.Project),
            "receipt" => store.ResolveReceiptPath(root, SquadDeploymentScope.Project),
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };
        ConfigureOriginalNode(root, livePath, subject, originalKind);
        string renderedBody = string.Equals(subject, "target", StringComparison.Ordinal)
            ? "updated claim body"
            : installedBody;
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(targetRelativePath, renderedBody)],
            previousReceipt,
            [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        return new ClaimMutationScenario(
            plan,
            string.Equals(subject, "target", StringComparison.Ordinal)
                ? targetRelativePath
                : subject,
            Path.GetRelativePath(root, livePath).Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static AfterImageRestoreScenario CreateAfterImageRestoreScenario(
        string root,
        string artifactRole)
    {
        (string subject, string originalKind) = artifactRole switch
        {
            "target-backup" => ("target", "file"),
            "target-link-metadata" => ("target", "file-link"),
            "lock-original" => ("lock", "file"),
            "lock-link-metadata" => ("lock", "file-link"),
            "receipt-original" => ("receipt", "file"),
            "receipt-link-metadata" => ("receipt", "file-link"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(artifactRole), artifactRole, null)
        };
        ClaimMutationScenario scenario = CreateClaimMutationScenario(root, subject, originalKind);
        return new AfterImageRestoreScenario(scenario.Plan, scenario.Subject);
    }

    private static void ConfigureOriginalNode(
        string root,
        string livePath,
        string subject,
        string originalKind)
    {
        if (string.Equals(originalKind, "file", StringComparison.Ordinal))
            return;

        DeleteLeafEntry(livePath);
        string fixtureDirectory = string.Equals(subject, "target", StringComparison.Ordinal)
            ? Path.Combine(root, ".claim-fixtures")
            : Path.Combine(Path.GetDirectoryName(livePath)!, ".claim-fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        switch (originalKind)
        {
            case "file-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-file.txt");
                    string content = string.Equals(subject, "target", StringComparison.Ordinal)
                        ? "installed claim body"
                        : $"original {subject} state";
                    File.WriteAllText(linkTargetPath, content, new UTF8Encoding(false));
                    Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    File.CreateSymbolicLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-directory");
                    Directory.CreateDirectory(linkTargetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    Directory.CreateSymbolicLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory":
                Directory.CreateDirectory(livePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(originalKind), originalKind, null);
        }
    }

    private static void DeleteLeafEntry(string path)
    {
        DirectoryInfo directory = new(path);
        if (directory.LinkTarget is not null)
        {
            Directory.Delete(path);
            return;
        }

        FileInfo file = new(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            File.Delete(path);
            return;
        }

        if (directory.Exists)
            Directory.Delete(path, true);
    }

    private static string ExtractMethodDeclaration(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int declarationStart = source.LastIndexOf(
                "    private ",
                methodNameIndex,
                StringComparison.Ordinal);
            int bodyStart = source.IndexOf('{', methodNameIndex);
            Assert.True(declarationStart >= 0 && bodyStart > methodNameIndex);
            string declaration = source[declarationStart..bodyStart];
            if (declaration.Contains(methodName, StringComparison.Ordinal))
                return declaration;

            searchFrom = methodNameIndex + methodName.Length;
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static RestoreArtifactScenario CreateRestoreArtifactScenario(
        string root,
        string artifactRole)
    {
        const string targetRelativePath = ".codex/agents/conductor.toml";
        const string installedBody = "installed body";
        string targetPath = ToPlatformPath(root, targetRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (string.Equals(artifactRole, "target-link-metadata", StringComparison.Ordinal))
        {
            const string canonicalRelativePath = "canonical/conductor.toml";
            string canonicalPath = Path.Combine(root, "canonical", "conductor.toml");
            Write(root, canonicalRelativePath, installedBody);
            File.CreateSymbolicLink(
                targetPath,
                Path.GetRelativePath(Path.GetDirectoryName(targetPath)!, canonicalPath));
        }
        else
        {
            File.WriteAllText(targetPath, installedBody, new UTF8Encoding(false));
        }

        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            targetRelativePath,
            Digest(installedBody),
            "codex",
            false));
        WriteState(root, Lock(), previousReceipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(targetRelativePath, "updated body")],
            previousReceipt,
            [new SquadDegradation("codex", "conductor", "model-profile-narrowed")],
            false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        string transactionDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction");
        SquadStateStore store = Store(root);
        (SquadTransactionStepKind boundary, string artifactPath, string liveRelativePath, byte[] expectedBytes) =
            artifactRole switch
            {
                "target-file" => (
                    SquadTransactionStepKind.FileApplied,
                    ToPlatformPath(
                        Path.Combine(transactionDirectory, "backups"),
                        $"codex/{targetRelativePath}"),
                    targetRelativePath,
                    "updated body"u8.ToArray()),
                "target-link-metadata" => (
                    SquadTransactionStepKind.FileApplied,
                    ToPlatformPath(
                        Path.Combine(transactionDirectory, "links"),
                        $"codex/{targetRelativePath}"),
                    targetRelativePath,
                    "updated body"u8.ToArray()),
                "lock-original" => (
                    SquadTransactionStepKind.LockApplied,
                    Path.Combine(transactionDirectory, "state-originals", "lock"),
                    StateRelativePath(root, store.ResolveLockPath(root, SquadDeploymentScope.Project)),
                    Encoding.UTF8.GetBytes(store.SerializeLock(
                        plan.Lock ?? throw new InvalidOperationException("The update plan has no lock.")))),
                "receipt-original" => (
                    SquadTransactionStepKind.ReceiptApplied,
                    Path.Combine(transactionDirectory, "state-originals", "receipt"),
                    StateRelativePath(root, store.ResolveReceiptPath(root, SquadDeploymentScope.Project)),
                    Encoding.UTF8.GetBytes(store.SerializeReceipt(plan.Receipt))),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(artifactRole), artifactRole, "Unknown restore artifact role.")
            };
        return new RestoreArtifactScenario(
            plan,
            boundary,
            artifactPath,
            Path.Combine(transactionDirectory, "intent.json"),
            liveRelativePath,
            new TreeEntry(TreeEntryKind.File, expectedBytes, null));
    }

    private static string ExtractMethod(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int lineStart = source.LastIndexOf('\n', methodNameIndex) + 1;
            string declarationPrefix = source[lineStart..methodNameIndex];
            if (declarationPrefix.Contains("private", StringComparison.Ordinal))
            {
                int bodyStart = source.IndexOf('{', methodNameIndex);
                Assert.True(bodyStart >= 0);
                int depth = 0;
                for (int index = bodyStart; index < source.Length; index++)
                {
                    depth += source[index] switch
                    {
                        '{' => 1,
                        '}' => -1,
                        _ => 0
                    };
                    if (depth == 0)
                        return source[bodyStart..(index + 1)];
                }

                throw new XunitException(
                    $"Method '{methodName}' has no complete body.");
            }

            searchFrom = methodNameIndex + methodName.Length;
        }
    }

    private static SortedDictionary<string, TreeEntry> CaptureTreeWithoutTransactionEvidence(
        string root)
    {
        SortedDictionary<string, TreeEntry> result = CaptureTree(root);
        foreach (string path in result.Keys.Where(path =>
                     path.Equals(
                         ".kyber-weave/.squad-transaction",
                         StringComparison.Ordinal) ||
                     path.StartsWith(
                         ".kyber-weave/.squad-transaction/",
                         StringComparison.Ordinal)).ToArray())
            result.Remove(path);

        return result;
    }

    private static void EditSemanticArtifactSet(
        JsonObject intent,
        string transactionDirectory,
        string edit,
        string semanticRole)
    {
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        if (edit == "remove")
        {
            (JsonObject Artifact, int Index) match = artifacts
                .Select((node, index) => (
                    Artifact: Assert.IsType<JsonObject>(node),
                    Index: index))
                .First(pair => string.Equals(
                    pair.Artifact["role"]?.GetValue<string>(),
                    semanticRole,
                    StringComparison.Ordinal));
            string removedRelativePath = Assert.IsType<string>(
                match.Artifact["path"]?.GetValue<string>());
            DeleteArtifactNode(ToPlatformPath(transactionDirectory, removedRelativePath));
            artifacts.RemoveAt(match.Index);
            RemoveEmptyTestArtifactParents(
                ToPlatformPath(transactionDirectory, removedRelativePath),
                transactionDirectory);
            return;
        }

        if (edit != "add")
            throw new ArgumentOutOfRangeException(nameof(edit), edit, null);

        string transactionId = Assert.IsType<string>(intent["transactionId"]?.GetValue<string>());
        string suffix = Digest($"{transactionId}:{semanticRole}");
        (string area, string relativePath) = semanticRole switch
        {
            "target-stage" => ("work", $"staging/semantic-extra-{suffix}.bin"),
            "target-backup" => ("work", $"backups/semantic-extra-{suffix}.bin"),
            "target-link-metadata" => ("work", $"links/semantic-extra-{suffix}.txt"),
            "state-stage" => ("journal", $"state-staging/semantic-extra-{suffix}"),
            "state-original" => ("work", $"state-originals/semantic-extra-{suffix}"),
            "claimed-original" => ("work", $"claimed-{transactionId}-{suffix}"),
            "discarded-after-image" => ("work", $"discarded-{transactionId}-{suffix}"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(semanticRole),
                semanticRole,
                null)
        };
        byte[] bytes = Encoding.UTF8.GetBytes($"role-valid semantic extra {semanticRole}");
        string fullPath = ToPlatformPath(transactionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
        artifacts.Add(new JsonObject
        {
            ["transactionId"] = transactionId,
            ["area"] = area,
            ["role"] = semanticRole,
            ["path"] = relativePath,
            ["nodeKind"] = "file",
            ["byteLength"] = bytes.LongLength,
            ["sha256"] = Digest(bytes),
            ["linkTarget"] = string.Empty,
            ["lifecycleState"] = "pre-operation"
        });
    }

    private static void DeleteArtifactNode(string path)
    {
        FileInfo file = new(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            file.Delete();
            return;
        }

        DirectoryInfo directory = new(path);
        if (directory.LinkTarget is not null || directory.Exists)
            directory.Delete(false);
    }

    private static void RemoveEmptyTestArtifactParents(string artifactPath, string root)
    {
        string? current = Path.GetDirectoryName(artifactPath);
        while (current is not null &&
               !string.Equals(current, root, StringComparison.Ordinal) &&
               Directory.Exists(current) &&
               !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static ActiveTransitionScenario CreateActiveTransitionScenario(
        string root,
        string scenario)
    {
        const string relativePath = ".codex/agents/conductor.toml";
        if (scenario is "file-write" or "file-delete")
        {
            const string installed = "installed file body";
            Write(root, relativePath, installed);
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest(installed),
                "codex",
                false));
            WriteState(root, Lock(), receipt);
            SquadDeploymentFile[] rendered = scenario == "file-write"
                ? [Rendered(relativePath, "updated file body")]
                : [];
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    rendered,
                    receipt,
                    [],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                relativePath);
        }

        if (scenario is "link-write" or "link-delete")
        {
            const string installed = "installed link target";
            const string targetRelativePath = "canonical/conductor.toml";
            string canonicalPath = Path.Combine(root, "canonical", "conductor.toml");
            Write(root, targetRelativePath, installed);
            string fullLinkPath = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullLinkPath)!);
            File.CreateSymbolicLink(
                fullLinkPath,
                Path.GetRelativePath(Path.GetDirectoryName(fullLinkPath)!, canonicalPath));
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest(installed),
                "codex",
                false));
            WriteState(root, Lock(), receipt);
            SquadDeploymentFile[] rendered = scenario == "link-write"
                ? [Rendered(relativePath, "updated link body")]
                : [];
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    rendered,
                    receipt,
                    [],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                relativePath);
        }

        SquadReceipt emptyReceipt = Receipt();
        WriteState(root, Lock(), emptyReceipt);
        if (scenario is "lock-write" or "receipt-write")
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [],
                    emptyReceipt,
                    [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
                    false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                scenario.StartsWith("lock", StringComparison.Ordinal) ? "lock" : "receipt");

        if (scenario is "lock-delete" or "receipt-delete")
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUninstall(
                    root,
                    SquadDeploymentScope.Project,
                    emptyReceipt),
                scenario.StartsWith("lock", StringComparison.Ordinal) ? "lock" : "receipt");

        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
    }

    private static bool TryEnableWindowsDirectoryCaseSensitivity(
        string directory,
        out string diagnostic)
    {
        if (!OperatingSystem.IsWindows())
        {
            diagnostic = "host is not Windows";
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new("fsutil.exe")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("file");
            startInfo.ArgumentList.Add("setCaseSensitiveInfo");
            startInfo.ArgumentList.Add(directory);
            startInfo.ArgumentList.Add("enable");
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                diagnostic = "fsutil did not start";
                return false;
            }

            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            diagnostic = string.Join(
                " ",
                new[] { standardOutput, standardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()));
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    private static SquadLock ReplaceRequiredLockValue(
        SquadLock value,
        string field,
        string replacement)
    {
        return field switch
        {
            "schema" => value with { Schema = replacement },
            "squad-version" => value with { SquadVersion = replacement },
            "cli-version" => value with { CliVersion = replacement },
            "mcp-version" => value with { McpVersion = replacement },
            "bundle" => value with { Bundle = replacement },
            "translation" => value with { Translation = replacement },
            "apm-version" => value with { Apm = value.Apm with { Version = replacement } },
            "apm-tag-commit" => value with { Apm = value.Apm with { TagCommit = replacement } },
            "target" => value with { Targets = [replacement] },
            "exclusion" => value with { Exclusions = [replacement] },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    private static IEnumerable<(string Property, string Value, int Occurrence)>
        EnumerateAuthorityEnumTokens(JsonNode root)
    {
        HashSet<string> enumProperties = new(StringComparer.Ordinal)
        {
            "phase", "originalKind", "afterKind", "lockKind", "lockAfterKind",
            "receiptKind", "receiptAfterKind", "area", "role", "nodeKind",
            "lifecycleState", "allowedState"
        };
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        return Enumerate(root);

        IEnumerable<(string Property, string Value, int Occurrence)> Enumerate(JsonNode node)
        {
            if (node is JsonObject jsonObject)
                foreach ((string? property, JsonNode? child) in jsonObject)
                {
                    if (child is JsonValue value &&
                        enumProperties.Contains(property) &&
                        value.GetValueKind() == JsonValueKind.String)
                    {
                        string token = value.GetValue<string>();
                        string key = $"{property}\0{token}";
                        occurrences.TryGetValue(key, out int occurrence);
                        occurrences[key] = occurrence + 1;
                        yield return (property, token, occurrence);
                    }

                    if (child is not null)
                        foreach ((string Property, string Value, int Occurrence) nested in Enumerate(child))
                            yield return nested;
                }
            else if (node is JsonArray jsonArray)
                foreach (JsonNode? child in jsonArray)
                {
                    if (child is null)
                        continue;
                    foreach ((string Property, string Value, int Occurrence) nested in Enumerate(child))
                        yield return nested;
                }
        }
    }

    private static bool ReplaceAuthorityEnumOccurrence(
        JsonNode root,
        string propertyName,
        string canonicalValue,
        int targetOccurrence,
        string replacement)
    {
        int occurrence = 0;
        return Replace(root);

        bool Replace(JsonNode node)
        {
            if (node is JsonObject jsonObject)
                foreach ((string? property, JsonNode? child) in jsonObject.ToArray())
                {
                    if (string.Equals(property, propertyName, StringComparison.Ordinal) &&
                        child is JsonValue value &&
                        value.GetValueKind() == JsonValueKind.String &&
                        string.Equals(
                            value.GetValue<string>(),
                            canonicalValue,
                            StringComparison.Ordinal))
                    {
                        if (occurrence == targetOccurrence)
                        {
                            jsonObject[property] = replacement;
                            return true;
                        }

                        occurrence++;
                    }

                    if (child is not null && Replace(child))
                        return true;
                }
            else if (node is JsonArray jsonArray)
                foreach (JsonNode? child in jsonArray)
                    if (child is not null && Replace(child))
                        return true;

            return false;
        }
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Case variance is the subject under test. The lowercase variant is "
                        + "one of the inputs being asserted on, not a normalization to be made safe.")]
    private static IReadOnlyList<string> NoncanonicalCaseVariants(string canonical)
    {
        return new[]
            {
                canonical.ToLowerInvariant(),
                canonical.ToUpperInvariant(),
                char.ToUpperInvariant(canonical[0]) + canonical[1..],
                ToggleAsciiCase(canonical)
            }
            .Where(value => !string.Equals(value, canonical, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static JsonObject ReadJsonObject(string path)
    {
        return Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)));
    }

    private static void WriteJsonObject(string path, JsonObject document)
    {
        File.WriteAllText(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

    private static void AssertPreparedArtifactAuthority(JsonArray artifacts)
    {
        foreach (JsonNode? node in artifacts)
        {
            JsonObject artifact = Assert.IsType<JsonObject>(node);
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["transactionId"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["area"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["role"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["nodeKind"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["lifecycleState"]).GetValue<string>()));
        }
    }

    private static void CorruptPreparedAuthority(
        JsonObject intent,
        string transactionDirectory,
        string corruption)
    {
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        JsonObject first = Assert.IsType<JsonObject>(artifacts[0]);
        switch (corruption)
        {
            case "missing-required":
                artifacts.RemoveAt(0);
                break;
            case "undeclared-file":
                Write(transactionDirectory, "undeclared.bin", "undeclared file");
                break;
            case "undeclared-link":
                File.CreateSymbolicLink(
                    Path.Combine(transactionDirectory, "undeclared-link"),
                    "missing-link-target");
                break;
            case "undeclared-directory":
                Directory.CreateDirectory(Path.Combine(transactionDirectory, "undeclared-directory"));
                break;
            case "duplicate-identity":
                artifacts.Add(first.DeepClone());
                break;
            case "swapped-area":
                first["area"] = first["area"]?.GetValue<string>() == "journal"
                    ? "work"
                    : "journal";
                break;
            case "swapped-role":
                first["role"] = "state-stage";
                break;
            case "swapped-lifecycle":
                first["lifecycleState"] = "active-transition";
                break;
            case "invalid-transition":
                first["lifecycleState"] = "post-operation";
                intent["activeTransition"] = new JsonObject
                {
                    ["artifactIdentity"] = "not-the-mutated-artifact",
                    ["allowedState"] = "pre-operation"
                };
                break;
            case "well-digested-unexpected":
                const string unexpectedPath = "unexpected-authority.bin";
                byte[] unexpectedBytes = "well-digested but undeclared"u8.ToArray();
                File.WriteAllBytes(Path.Combine(transactionDirectory, unexpectedPath), unexpectedBytes);
                JsonObject unexpected = Assert.IsType<JsonObject>(first.DeepClone());
                unexpected["area"] = "journal";
                unexpected["path"] = unexpectedPath;
                unexpected["byteLength"] = unexpectedBytes.LongLength;
                unexpected["sha256"] = Digest(unexpectedBytes);
                artifacts.Add(unexpected);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static void AssertCreatedDirectoryAuthorityIsCanonical(JsonArray createdDirectories)
    {
        int previousDepth = int.MaxValue;
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (JsonNode? node in createdDirectories)
        {
            JsonObject authority = Assert.IsType<JsonObject>(node);
            string area = Assert.IsAssignableFrom<JsonValue>(authority["area"]).GetValue<string>();
            string path = Assert.IsAssignableFrom<JsonValue>(authority["path"]).GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(area));
            Assert.False(Path.IsPathRooted(path));
            Assert.DoesNotContain("..", path.Split('/'), StringComparer.Ordinal);
            Assert.True(identities.Add($"{area}:{path}"));
            int depth = path.Count(character => character == '/');
            Assert.True(
                depth <= previousDepth,
                "Created-directory authority must be serialized deepest first.");
            previousDepth = depth;
        }
    }

    private static void CorruptCreatedDirectoryAuthority(
        JsonObject intent,
        JsonArray createdDirectories,
        string fixtureRoot,
        string corruption)
    {
        JsonObject first = Assert.IsType<JsonObject>(createdDirectories[0]);
        switch (corruption)
        {
            case "wrong-area":
                first["area"] = "outside";
                break;
            case "non-ancestor":
                first["path"] = "unrelated/not-an-ancestor";
                break;
            case "duplicate":
                createdDirectories.Add(first.DeepClone());
                break;
            case "unordered":
                Assert.True(createdDirectories.Count > 1);
                JsonObject last = Assert.IsType<JsonObject>(createdDirectories[^1]);
                createdDirectories.RemoveAt(createdDirectories.Count - 1);
                createdDirectories.Insert(0, last);
                break;
            case "absolute":
                first["path"] = Path.Combine(fixtureRoot, "absolute");
                break;
            case "traversal":
                first["path"] = "../escape";
                break;
            case "sibling":
                first["path"] = "beside-target";
                break;
            case "outside-root":
                first["path"] = Path.GetFullPath(Path.Combine(fixtureRoot, "..", "outside"));
                break;
            case "legacy-count":
                intent["missingJournalDirectoryCount"] = 1;
                break;
            case "negative-count":
                intent["missingJournalDirectoryCount"] = -1;
                break;
            case "maximum-count":
                intent["missingJournalDirectoryCount"] = int.MaxValue;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static string ReplaceJsonStringPropertyWithNumber(
        string json,
        string propertyName,
        int numericValue,
        out int replacementCount)
    {
        JsonNode root = JsonNode.Parse(json)
                        ?? throw new XunitException("Expected a JSON authority document.");
        replacementCount = ReplaceJsonStringPropertyWithNumber(
            root,
            propertyName,
            numericValue);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static int ReplaceJsonStringPropertyWithNumber(
        JsonNode node,
        string propertyName,
        int numericValue)
    {
        int replacements = 0;
        if (node is JsonObject jsonObject)
            foreach ((string? name, JsonNode? value) in jsonObject.ToArray())
            {
                if (string.Equals(name, propertyName, StringComparison.Ordinal) &&
                    value is JsonValue &&
                    value.GetValueKind() == JsonValueKind.String)
                {
                    jsonObject[name] = numericValue;
                    replacements++;
                    continue;
                }

                if (value is not null)
                    replacements += ReplaceJsonStringPropertyWithNumber(
                        value,
                        propertyName,
                        numericValue);
            }
        else if (node is JsonArray jsonArray)
            foreach (JsonNode? child in jsonArray)
                if (child is not null)
                    replacements += ReplaceJsonStringPropertyWithNumber(
                        child,
                        propertyName,
                        numericValue);

        return replacements;
    }

    private static void AssertJsonEnumTokensAreStrings(JsonNode node)
    {
        HashSet<string> enumProperties = new(StringComparer.Ordinal)
        {
            "phase", "originalKind", "afterKind", "lockKind", "lockAfterKind",
            "receiptKind", "receiptAfterKind", "area", "role", "nodeKind",
            "lifecycleState"
        };
        AssertJsonEnumTokensAreStrings(node, enumProperties);
    }

    private static void AssertJsonEnumTokensAreStrings(
        JsonNode node,
        IReadOnlySet<string> enumProperties)
    {
        if (node is JsonObject jsonObject)
            foreach ((string? name, JsonNode? value) in jsonObject)
            {
                if (enumProperties.Contains(name))
                {
                    JsonValue token = Assert.IsAssignableFrom<JsonValue>(value);
                    Assert.Equal(JsonValueKind.String, token.GetValueKind());
                }

                if (value is not null)
                    AssertJsonEnumTokensAreStrings(value, enumProperties);
            }
        else if (node is JsonArray jsonArray)
            foreach (JsonNode? child in jsonArray)
                if (child is not null)
                    AssertJsonEnumTokensAreStrings(child, enumProperties);
    }

    private static bool TreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual)
    {
        if (!expected.Keys.SequenceEqual(actual.Keys, StringComparer.Ordinal))
            return false;

        foreach ((string? path, TreeEntry? expectedEntry) in expected)
        {
            TreeEntry actualEntry = actual[path];
            if (expectedEntry.Kind != actualEntry.Kind ||
                !string.Equals(expectedEntry.LinkTarget, actualEntry.LinkTarget, StringComparison.Ordinal))
                return false;

            if (expectedEntry.Content is null != actualEntry.Content is null)
                return false;
            if (expectedEntry.Content is not null &&
                !expectedEntry.Content.SequenceEqual(Assert.IsType<byte[]>(actualEntry.Content)))
                return false;
        }

        return true;
    }

    private static void AssertTreeEntryEqual(TreeEntry expected, TreeEntry actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.LinkTarget, actual.LinkTarget);
        if (expected.Content is null)
        {
            Assert.Null(actual.Content);
            return;
        }

        Assert.NotNull(actual.Content);
        Assert.True(
            expected.Content.SequenceEqual(actual.Content),
            "The raced leaf bytes changed during claim/publication.");
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Generates a deliberately case-varied path fixture; the lowercasing "
                        + "is the test input, not a normalization.")]
    public static IEnumerable<object[]> PortableInvalidPaths()
    {
        foreach (char forbidden in "<>:\"|?*")
            yield return [$".codex/agents/bad{forbidden}name.md"];

        yield return [".codex//agents/repeated-separator.md"];
        yield return [".codex\\agents\\noncanonical-separators.md"];
        yield return [".codex/agents/trailing-dot."];
        yield return [".codex/agents/trailing-space "];
        yield return [".codex/agents/e\u0301.md"];

        for (int value = 0; value <= 0x1f; value++)
            yield return [$".codex/agents/control{(char)value}name.md"];
        for (int value = 0x7f; value <= 0x9f; value++)
            yield return [$".codex/agents/control{(char)value}name.md"];

        string[] deviceNames =
        [
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3"
        ];
        foreach (string deviceName in deviceNames)
        {
            yield return [$".codex/agents/{deviceName}/SKILL.md"];
            yield return [$".codex/agents/{deviceName.ToLowerInvariant()}.txt"];
            yield return [$".codex/agents/{ToggleAsciiCase(deviceName)}.agent.md"];
        }
    }

    private static string ToggleAsciiCase(string value)
    {
        return new string(value.Select(character =>
            char.IsAsciiLetterUpper(character)
                ? char.ToLowerInvariant(character)
                : char.IsAsciiLetterLower(character)
                    ? char.ToUpperInvariant(character)
                    : character).ToArray());
    }

    private static SortedDictionary<string, TreeEntry> InterruptTransactionAfter(
        TransactionFixture fixture,
        SquadTransactionStepKind checkpoint)
    {
        LifecycleStepFailingObserver observer = new(fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    private static SortedDictionary<string, TreeEntry> InterruptTransactionAfterCheckpoint(
        TransactionFixture fixture,
        SquadTransactionCheckpointKind checkpoint)
    {
        CheckpointFailingObserver observer = new(fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    private static void CorruptPreparedArtifact(
        string root,
        string relativePath,
        string corruption)
    {
        string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        string backupPath = ToPlatformPath(
            Path.Combine(transactionDirectory, "backups"),
            $"codex/{relativePath}");
        switch (corruption)
        {
            case "truncated-journal":
                byte[] journal = File.ReadAllBytes(intentPath);
                File.WriteAllBytes(intentPath, journal[..Math.Max(1, journal.Length / 2)]);
                break;
            case "duplicate-journal-field":
                File.WriteAllText(
                    intentPath,
                    CorruptJournalDocument(
                        File.ReadAllText(intentPath, Encoding.UTF8),
                        "duplicate"),
                    new UTF8Encoding(false));
                break;
            case "partial-backup":
                byte[] backup = File.ReadAllBytes(backupPath);
                File.WriteAllBytes(backupPath, backup[..Math.Max(1, backup.Length / 2)]);
                break;
            case "wrong-backup-digest":
                byte[] wrong = File.ReadAllBytes(backupPath);
                wrong[0] ^= 0xff;
                File.WriteAllBytes(backupPath, wrong);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static void ReplacePublishedArtifactWithPartialTemporaryFile(
        string root,
        string relativePath,
        string artifact)
    {
        string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        string publishedPath = artifact switch
        {
            "journal" => Path.Combine(transactionDirectory, "intent.json"),
            "staging" => ToPlatformPath(
                Path.Combine(transactionDirectory, "staging"),
                $"codex/{relativePath}"),
            "backup" => ToPlatformPath(
                Path.Combine(transactionDirectory, "backups"),
                $"codex/{relativePath}"),
            _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null)
        };
        byte[] bytes = File.ReadAllBytes(publishedPath);
        string temporaryPath = publishedPath + ".tmp";
        File.Move(publishedPath, temporaryPath);
        File.WriteAllBytes(temporaryPath, bytes[..Math.Max(1, bytes.Length / 2)]);
    }

    private static SquadDeploymentPlan CreateGlobalInstallPlan(string targetRoot)
    {
        return SquadDeploymentPlan.CreateInstall(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "global conductor")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
    }

    private static int ProbeGlobalInstallStepCount()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        Directory.CreateDirectory(targetRoot);
        RecordingObserver observer = new(targetRoot);
        new SquadTransaction(
            Store(Path.Combine(fixture.Path, "application-data")),
            observer).Execute(CreateGlobalInstallPlan(targetRoot));
        return observer.Steps.Count;
    }

    private static void AssertActiveLeaseConflict(Exception? exception)
    {
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CorruptLockDocument(string yaml, string corruption)
    {
        return corruption switch
        {
            "unknown" => yaml + "unknown-field: true\n",
            "duplicate" => yaml + "schema: kyber-squad.lock/v1\n",
            "missing" => yaml.Replace(
                "schema: kyber-squad.lock/v1\n",
                string.Empty,
                StringComparison.Ordinal),
            "null" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: null",
                StringComparison.Ordinal),
            "misspelled" => yaml.Replace("schema:", "schemma:", StringComparison.Ordinal),
            "wrong-case" => yaml.Replace("schema:", "Schema:", StringComparison.Ordinal),
            "wrong-type" => yaml.Replace(
                "targets:\n  - codex\n  - warp",
                "targets: 42",
                StringComparison.Ordinal),
            "yaml-alias" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: &schema kyber-squad.lock/v1",
                StringComparison.Ordinal),
            "yaml-merge" => "defaults: &defaults\n  schema: kyber-squad.lock/v1\n<<: *defaults\n" +
                            yaml.Replace(
                                "schema: kyber-squad.lock/v1\n",
                                string.Empty,
                                StringComparison.Ordinal),
            "yaml-tag" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: !!str kyber-squad.lock/v1",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    private static string CorruptReceiptDocument(string json, string corruption)
    {
        return corruption switch
        {
            "unknown" => InsertBeforeRootClose(json, "  \"unknownField\": true"),
            "duplicate" => InsertBeforeRootClose(
                json,
                "  \"schema\": \"kyber-squad.receipt/v1\""),
            "missing" => json
                .Replace("  \"schema\": \"kyber-squad.receipt/v1\",\r\n", string.Empty, StringComparison.Ordinal)
                .Replace("  \"schema\": \"kyber-squad.receipt/v1\",\n", string.Empty, StringComparison.Ordinal),
            "null" => json.Replace(
                "\"schema\": \"kyber-squad.receipt/v1\"",
                "\"schema\": null",
                StringComparison.Ordinal),
            "misspelled" => json.Replace("\"schema\":", "\"schemma\":", StringComparison.Ordinal),
            "wrong-case" => json.Replace("\"schema\":", "\"Schema\":", StringComparison.Ordinal),
            "wrong-type" => json.Replace("\"files\": [", "\"files\": 42, \"ignoredFiles\": [",
                StringComparison.Ordinal),
            "wrong-enum" => json.Replace("\"scope\": \"project\"", "\"scope\": \"workspace\"",
                StringComparison.Ordinal),
            "invalid-timestamp" => json.Replace(
                "2026-08-14T12:34:56.0000000Z",
                "not-a-timestamp",
                StringComparison.Ordinal),
            "invalid-digest" => json.Replace(Digest("conductor"), "not-a-digest", StringComparison.Ordinal),
            "portable-duplicate" => InsertDuplicateReceiptFile(json),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    private static string CorruptJournalDocument(string json, string corruption)
    {
        return corruption switch
        {
            "unknown" => InsertBeforeRootClose(json, "  \"unknownField\": true"),
            "duplicate" => InsertBeforeRootClose(
                json,
                "  \"schema\": \"kyber-squad.transaction/v1\""),
            "missing" => json
                .Replace("  \"schema\": \"kyber-squad.transaction/v1\",\r\n", string.Empty, StringComparison.Ordinal)
                .Replace("  \"schema\": \"kyber-squad.transaction/v1\",\n", string.Empty, StringComparison.Ordinal),
            "null" => json.Replace(
                "\"schema\": \"kyber-squad.transaction/v1\"",
                "\"schema\": null",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    private static string InsertBeforeRootClose(string json, string property)
    {
        int rootClose = json.LastIndexOf('}');
        Assert.True(rootClose >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return json.Insert(rootClose, $",{newline}{property}{newline}");
    }

    private static string InsertDuplicateReceiptFile(string json)
    {
        int insertion = json.LastIndexOf("  ]", StringComparison.Ordinal);
        Assert.True(insertion >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string duplicate =
            $",{newline}    {{{newline}      \"relativePath\": \".codex/agents/Conductor.toml\",{newline}      \"sha256\": \"{Digest("conductor")}\",{newline}      \"target\": \"codex\",{newline}      \"adopted\": false{newline}    }}";
        return json.Insert(insertion + 3, duplicate);
    }

    private static SquadHomeFile GuidanceHomeFile()
    {
        return new SquadHomeFile(GuidanceFile, Digest("guidance"));
    }

    private static SquadReceipt GlobalReceiptWithHomeFiles(params SquadHomeFile[] homeFiles)
    {
        return new SquadReceipt(
            SquadStateStore.ReceiptSchemaV2,
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [])
        {
            HomeFiles = homeFiles
        };
    }

    private static string HomeFileJson(string relativePath, string sha256)
    {
        return $$"""{"relativePath":"{{relativePath}}","sha256":"{{sha256}}"}""";
    }

    /// <summary>
    ///     A receipt that breaks exactly one rule of the home-file record, built from a valid global receipt
    ///     so the only thing wrong with it is the thing the case names.
    /// </summary>
    private static SquadReceipt ReceiptWithInvalidHomeFiles(string invalidCase)
    {
        SquadHomeFile valid = GuidanceHomeFile();
        SquadReceipt globalReceipt = GlobalReceiptWithHomeFiles(valid);
        return invalidCase switch
        {
            "project-scope" => Receipt() with { HomeFiles = [valid] },
            "instance-scope" => Receipt() with { Scope = SquadDeploymentScope.Instance, HomeFiles = [valid] },
            "null-list" => globalReceipt with { HomeFiles = null! },
            "null-entry" => globalReceipt with { HomeFiles = [null!] },
            "parent-traversal" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "../response-guidance.md" }]
            },
            "rooted-path" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "/etc/response-guidance.md" }]
            },
            "reserved-device-name" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "con.md" }]
            },
            "backslash-separator" => globalReceipt with
            {
                HomeFiles = [valid with { RelativePath = "guidance\\response-guidance.md" }]
            },
            "short-digest" => globalReceipt with { HomeFiles = [valid with { Sha256 = "abc123" }] },
            "uppercase-digest" => globalReceipt with
            {
                HomeFiles = [valid with { Sha256 = valid.Sha256.ToUpperInvariant() }]
            },
            "non-hex-digest" => globalReceipt with
            {
                HomeFiles = [valid with { Sha256 = new string('g', 64) }]
            },
            "duplicate-path" => globalReceipt with
            {
                HomeFiles = [valid, valid with { Sha256 = Digest("other") }]
            },
            "case-variant-duplicate" => globalReceipt with
            {
                HomeFiles = [valid, valid with { RelativePath = "Response-Guidance.md" }]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null)
        };
    }

    /// <summary>
    ///     Swaps the value of a serialized global receipt's <c>homeFiles</c> property for raw JSON. The store
    ///     writes it as the last property, so everything from its name to the root's closing brace is its value.
    /// </summary>
    private static string ReplaceHomeFiles(string json, string homeFilesJson)
    {
        int start = json.IndexOf("\"homeFiles\":", StringComparison.Ordinal);
        int rootClose = json.LastIndexOf('}');
        Assert.True(start >= 0 && rootClose > start, "The fixture receipt must end with its homeFiles property.");
        return $"{json[..start]}\"homeFiles\": {homeFilesJson}\n{json[rootClose..]}";
    }

    private static SquadStateStore Store(string applicationData)
    {
        return new SquadStateStore(new FakeSquadUserPaths(applicationData));
    }

    private static SquadTransaction Transaction(
        string applicationData,
        ISquadTransactionObserver? observer = null)
    {
        return new SquadTransaction(Store(applicationData), observer);
    }

    private static bool IsHeldWithExclusiveFileShare(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool HasDirectoryNamed(string root, string name)
    {
        return Directory.Exists(root) && Directory.EnumerateDirectories(
                root,
                "*",
                SearchOption.AllDirectories)
            .Any(path => string.Equals(
                Path.GetFileName(path),
                name,
                StringComparison.Ordinal));
    }

    private static SquadLock Lock(string version = "1.2.3")
    {
        return new SquadLock(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex", "warp"],
            ["cursor"],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));
    }

    private static SquadReceipt Receipt(params SquadOwnedFile[] files)
    {
        return new SquadReceipt(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            files);
    }

    private static SquadDeploymentFile Rendered(
        string relativePath,
        string content,
        string target = "codex")
    {
        return new SquadDeploymentFile(relativePath, Encoding.UTF8.GetBytes(content), target);
    }

    private static string Digest(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    ///     Serializes <paramref name="receipt" /> as authentic legacy v1 JSON — the shape a genuine
    ///     pre-#91 CLI wrote, with no explicit <c>layout</c> field — rather than through
    ///     <see cref="SquadStateStore.SerializeReceipt" />, which always upgrades a Global receipt
    ///     to v2 regardless of the schema string already on the in-memory object. A directly
    ///     constructed receipt with prefixed paths but an unset (default)
    ///     <see cref="SquadReceipt.Layout" /> would otherwise round-trip through the v2 writer as a
    ///     self-contradicting payload — exactly what the v2 read-side cross-check (#91) now refuses.
    /// </summary>
    private static string SerializeAsLegacyV1Json(SquadReceipt receipt)
    {
        JsonObject json = new()
        {
            ["schema"] = "kyber-squad.receipt/v1",
            ["scope"] = receipt.Scope == SquadDeploymentScope.Global ? "global" : "project",
            ["targetRoot"] = receipt.TargetRoot,
            ["installedAtUtc"] = receipt.InstalledAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["degradations"] = new JsonArray(
            [
                .. receipt.Degradations.Select(degradation => (JsonNode)new JsonObject
                {
                    ["target"] = degradation.Target,
                    ["subject"] = degradation.Subject,
                    ["code"] = degradation.Code
                })
            ]),
            ["files"] = new JsonArray(
            [
                .. receipt.Files.Select(file => (JsonNode)new JsonObject
                {
                    ["relativePath"] = file.RelativePath,
                    ["sha256"] = file.Sha256,
                    ["target"] = file.Target,
                    ["adopted"] = file.Adopted
                })
            ])
        };

        return json.ToJsonString() + "\n";
    }

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Read(string root, string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    ///     Writes a project's committed lock and receipt where the store resolves them, so a fixture
    ///     stays correct when the state layout moves rather than pinning the layout it was born with.
    /// </summary>
    private static void WriteState(string root, SquadLock squadLock, SquadReceipt receipt)
    {
        SquadStateStore store = Store(root);
        string lockPath = store.ResolveLockPath(root, SquadDeploymentScope.Project);
        string receiptPath = store.ResolveReceiptPath(root, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, store.SerializeLock(squadLock), new UTF8Encoding(false));
        File.WriteAllText(receiptPath, store.SerializeReceipt(receipt), new UTF8Encoding(false));
    }

    /// <summary>
    ///     Expresses a store-resolved state file as the '/'-separated key <see cref="CaptureTree" /> uses,
    ///     measured from the physical root. Both sides are canonical so a symlinked temp root still matches.
    /// </summary>
    private static string StateRelativePath(string root, string statePath)
    {
        return Path.GetRelativePath(SquadPhysicalRootIdentity.Resolve(root).PhysicalPath, statePath)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    ///     Compares two locations by where they physically are. A path may legitimately be spelled through
    ///     a symlinked temp root (<c>/tmp</c> resolving to <c>/private/tmp</c>) and still name one place, so
    ///     the comparison is on the canonical form rather than the spelling.
    /// </summary>
    private static void AssertSameLocation(string expected, string actual)
    {
        Assert.Equal(CanonicalLocation(expected), CanonicalLocation(actual));
    }

    private static string CanonicalLocation(string path)
    {
        string existing = Path.GetFullPath(path);
        List<string> unresolved = [];
        while (!Directory.Exists(existing))
        {
            unresolved.Insert(0, Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing)!;
        }

        return unresolved.Aggregate(
            SquadPhysicalRootIdentity.Resolve(existing).PhysicalPath,
            (accumulated, part) => Path.Combine(accumulated, part));
    }

    /// <summary>
    ///     The harness-relative path a fixture renders a single agent to. A global install renders relative
    ///     to its target's global root, so it takes a bare path; a project install keeps the target directory.
    /// </summary>
    private static string AgentRelativePath(SquadDeploymentScope scope, string fileName)
    {
        return scope == SquadDeploymentScope.Global
            ? $"agents/{fileName}"
            : $".codex/agents/{fileName}";
    }

    private static SortedDictionary<string, byte[]> Snapshot(string root)
    {
        SortedDictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            snapshot.Add(relativePath, ReadAllBytesAllowingWrites(path));
        }

        return snapshot;
    }

    private static SortedDictionary<string, TreeEntry> CaptureTree(string root)
    {
        SortedDictionary<string, TreeEntry> snapshot = new(StringComparer.Ordinal);
        CaptureTreeDirectory(new DirectoryInfo(root), root, snapshot);
        return snapshot;
    }

    private static void CaptureTreeDirectory(
        DirectoryInfo directory,
        string root,
        IDictionary<string, TreeEntry> snapshot)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            string relativePath = Path.GetRelativePath(root, entry.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (entry.LinkTarget is not null)
            {
                bool isDirectoryLink = OperatingSystem.IsWindows()
                    ? (entry.Attributes & FileAttributes.Directory) != 0
                    : Directory.Exists(entry.FullName);
                snapshot.Add(
                    relativePath,
                    new TreeEntry(
                        isDirectoryLink
                            ? TreeEntryKind.DirectorySymbolicLink
                            : TreeEntryKind.FileSymbolicLink,
                        null,
                        entry.LinkTarget));
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                snapshot.Add(relativePath, new TreeEntry(TreeEntryKind.Directory, null, null));
                CaptureTreeDirectory(childDirectory, root, snapshot);
                continue;
            }

            snapshot.Add(
                relativePath,
                new TreeEntry(
                    TreeEntryKind.File,
                    ReadAllBytesAllowingWrites(entry.FullName),
                    null));
        }
    }

    private static byte[] ReadAllBytesAllowingWrites(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string? path, byte[]? expectedBytes) in expected)
            Assert.True(
                actual[path].SequenceEqual(expectedBytes),
                $"File '{path}' changed. Expected {Digest(expectedBytes)}, actual {Digest(actual[path])}.");
    }

    private static void AssertTreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string? path, TreeEntry? expectedEntry) in expected)
        {
            TreeEntry actualEntry = actual[path];
            Assert.Equal(expectedEntry.Kind, actualEntry.Kind);
            Assert.Equal(expectedEntry.LinkTarget, actualEntry.LinkTarget);
            if (expectedEntry.Content is null)
            {
                Assert.Null(actualEntry.Content);
                continue;
            }

            Assert.NotNull(actualEntry.Content);
            Assert.True(
                expectedEntry.Content.SequenceEqual(actualEntry.Content),
                $"File '{path}' changed. Expected {Digest(expectedEntry.Content)}, actual {Digest(actualEntry.Content)}.");
        }
    }

    private static void RestoreTree(
        string root,
        IReadOnlyDictionary<string, TreeEntry> snapshot)
    {
        DeleteTreeContents(new DirectoryInfo(root));

        foreach ((string? relativePath, TreeEntry _) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.Directory)
                     .OrderBy(pair => PathDepth(pair.Key)))
            Directory.CreateDirectory(ToPlatformPath(root, relativePath));

        foreach ((string? relativePath, TreeEntry? entry) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.File))
        {
            string path = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Assert.IsType<byte[]>(entry.Content));
        }

        foreach ((string? relativePath, TreeEntry? entry) in snapshot.Where(pair =>
                     pair.Value.Kind is TreeEntryKind.FileSymbolicLink or
                         TreeEntryKind.DirectorySymbolicLink))
        {
            string path = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string linkTarget = Assert.IsType<string>(entry.LinkTarget);
            if (entry.Kind == TreeEntryKind.DirectorySymbolicLink)
                Directory.CreateSymbolicLink(path, linkTarget);
            else
                File.CreateSymbolicLink(path, linkTarget);
        }
    }

    private static void DeleteTreeContents(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().ToArray())
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                    Directory.Delete(entry.FullName);
                else
                    File.Delete(entry.FullName);
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DeleteTreeContents(childDirectory);
                childDirectory.Delete();
            }
            else
            {
                entry.Delete();
            }
        }
    }

    private static string ToPlatformPath(string root, string relativePath)
    {
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static int PathDepth(string relativePath)
    {
        return relativePath.Count(character => character == '/');
    }

    private static void RestoreSnapshot(
        string root,
        IReadOnlyDictionary<string, byte[]> snapshot)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(path);

        foreach (string directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            Directory.Delete(directory, false);

        foreach ((string? relativePath, byte[]? content) in snapshot)
        {
            string path = Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }

    private static string Digest(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
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
    }

    private static void AssertReceiptEqual(SquadReceipt expected, SquadReceipt actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.TargetRoot, actual.TargetRoot);
        Assert.Equal(expected.InstalledAtUtc, actual.InstalledAtUtc);
        Assert.Equal(expected.Degradations, actual.Degradations);
        Assert.Equal(expected.Files, actual.Files);
    }

    private enum TreeEntryKind
    {
        File,
        Directory,
        FileSymbolicLink,
        DirectorySymbolicLink
    }

    private sealed record TreeEntry(
        TreeEntryKind Kind,
        byte[]? Content,
        string? LinkTarget);

    private sealed record ActiveTransitionScenario(
        SquadDeploymentPlan Plan,
        string Subject);

    private sealed record ClaimMutationScenario(
        SquadDeploymentPlan Plan,
        string Subject,
        string LiveRelativePath);

    private sealed record AfterImageRestoreScenario(
        SquadDeploymentPlan Plan,
        string Subject);

    private sealed record RestoreArtifactScenario(
        SquadDeploymentPlan Plan,
        SquadTransactionStepKind MutationBoundary,
        string ArtifactPath,
        string IntentPath,
        string LiveRelativePath,
        TreeEntry ExpectedLiveAfterImage);

    private sealed class RecordingReleaseSource : ISquadReleaseSource
    {
        public int CallCount { get; private set; }

        public Task<SquadReleaseResult> DownloadAndExtractAsync(
            SquadReleaseRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new NotImplementedException();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSquadUserPaths(string applicationDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = applicationDataDirectory;

        public string HomeDirectory => Path.Combine(ApplicationDataDirectory, "home");
    }

    private sealed class FakeGlobalRootResolver(IReadOnlyDictionary<SquadTarget, string> roots)
        : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return roots.TryGetValue(target, out string? root)
                ? root
                : throw new ArgumentOutOfRangeException(nameof(target), target, "No root mapped for target.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return value;
        }
    }

    /// <summary>
    ///     Records where the durable intent, the staged bytes and the backups live while a global
    ///     transaction runs. The intent, staging and backups all belong in the home journal, never beside the
    ///     target root and never in the portable global state directory.
    /// </summary>
    private sealed class GlobalTransactionLocationObserver(
        string targetRoot,
        string stateDirectory,
        string journalDirectory) : ISquadTransactionObserver
    {
        public bool SawDurableIntentInJournal { get; private set; }

        public bool SawStagingInJournal { get; private set; }

        public bool SawBackupInJournal { get; private set; }

        public bool SawStagingOrBackupOnTargetRoot { get; private set; }

        public bool SawStagingOrBackupInState { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            SawDurableIntentInJournal |= Directory.Exists(journalDirectory) &&
                                         Directory.EnumerateFiles(
                                             journalDirectory,
                                             "intent.json",
                                             SearchOption.AllDirectories).Any();
            if (step.Kind == SquadTransactionStepKind.FileStaged)
            {
                SawStagingInJournal |= HasDirectoryNamed(journalDirectory, "staging");
                SawStagingOrBackupOnTargetRoot |= HasDirectoryNamed(targetRoot, "staging");
                SawStagingOrBackupInState |= HasDirectoryNamed(stateDirectory, "staging");
            }

            if (step.Kind == SquadTransactionStepKind.FileBackedUp)
            {
                SawBackupInJournal |= HasDirectoryNamed(journalDirectory, "backups");
                SawStagingOrBackupOnTargetRoot |= HasDirectoryNamed(targetRoot, "backups");
                SawStagingOrBackupInState |= HasDirectoryNamed(stateDirectory, "backups");
            }
        }
    }

    private sealed class BlockingObserver(SquadTransactionStepKind blockedStep) :
        ISquadTransactionObserver,
        IDisposable
    {
        public ManualResetEventSlim Reached { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Dispose()
        {
            Reached.Dispose();
            Release.Dispose();
        }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != blockedStep)
                return;

            Reached.Set();
            Release.Wait();
        }
    }

    private sealed class RecordingObserver(string root) : ISquadTransactionObserver
    {
        private readonly string _transactionDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction");

        public List<SquadTransactionStep> Steps { get; } = [];

        public bool SawIntentJournal { get; private set; }

        public bool SawStagedFile { get; private set; }

        public bool SawBackupFile { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            Steps.Add(step);
            SawIntentJournal |= File.Exists(Path.Combine(_transactionDirectory, "intent.json"));
            SawStagedFile |= Directory.Exists(Path.Combine(_transactionDirectory, "staging")) &&
                             Directory.EnumerateFiles(
                                 Path.Combine(_transactionDirectory, "staging"),
                                 "*",
                                 SearchOption.AllDirectories).Any();
            SawBackupFile |= Directory.Exists(Path.Combine(_transactionDirectory, "backups")) &&
                             Directory.EnumerateFiles(
                                 Path.Combine(_transactionDirectory, "backups"),
                                 "*",
                                 SearchOption.AllDirectories).Any();
        }
    }

    private sealed class CheckpointRecordingObserver : ISquadTransactionCheckpointObserver
    {
        public List<SquadTransactionStep> Steps { get; } = [];

        public List<SquadTransactionCheckpoint> Checkpoints { get; } = [];

        public void AfterStep(SquadTransactionStep step)
        {
            Steps.Add(step);
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            Checkpoints.Add(checkpoint);
        }
    }

    private sealed class FailingObserver(string root, int failureSequence) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, byte[]>? InterruptedSnapshot { get; private set; }

        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Sequence == failureSequence)
            {
                InterruptedSnapshot = Snapshot(root);
                InterruptedTreeSnapshot = CaptureTree(root);
                throw new InjectedSquadTransactionFailure(step.Sequence);
            }
        }
    }

    private sealed class LifecycleStepFailingObserver(
        string root,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class RestoreArtifactTamperingObserver(
        string artifactPath,
        SquadTransactionStepKind mutationBoundary) : ISquadTransactionObserver
    {
        public byte[] CorruptBytes { get; } = Encoding.UTF8.GetBytes(
            $"corrupted restore artifact after {mutationBoundary}");

        public bool MutatedArtifact { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != mutationBoundary)
                return;

            Assert.True(
                File.Exists(artifactPath),
                $"Prepared restore artifact '{artifactPath}' was not published.");
            File.WriteAllBytes(artifactPath, CorruptBytes);
            MutatedArtifact = true;
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class CheckpointFailingObserver(
        string root,
        SquadTransactionCheckpointKind checkpoint) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    private sealed class PostIntentReplacementObserver(
        string root,
        SquadStateStore store,
        SquadDeploymentScope scope,
        string relativePath,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        public byte[] ExternalBytes { get; } = Encoding.UTF8.GetBytes(
            $"external replacement after {checkpoint}");

        public string ReplacedPath => checkpoint switch
        {
            SquadTransactionStepKind.FileApplied => ToPlatformPath(root, relativePath),
            SquadTransactionStepKind.LockApplied => store.ResolveLockPath(root, scope),
            SquadTransactionStepKind.ReceiptApplied => store.ResolveReceiptPath(root, scope),
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint, null)
        };

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(ReplacedPath)!);
            File.WriteAllBytes(ReplacedPath, ExternalBytes);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class ExternalChildObserver(
        string root,
        string generatedPath,
        string externalPath) : ISquadTransactionObserver
    {
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != SquadTransactionStepKind.FileApplied ||
                !string.Equals(step.RelativePath, generatedPath, StringComparison.Ordinal))
                return;

            Write(root, externalPath, "external operator note");
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class PreparedArtifactsObserver(
        string root,
        string targetRelativePath) : ISquadTransactionCheckpointObserver
    {
        public bool ObservedPreparedCheckpoint { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            Assert.Equal("installed body", Read(root, targetRelativePath));
            string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
            string[] temporaryArtifacts = Directory.EnumerateFileSystemEntries(
                    transactionDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(temporaryArtifacts);

            string intentPath = Path.Combine(transactionDirectory, "intent.json");
            using JsonDocument journal = JsonDocument.Parse(ReadAllBytesAllowingWrites(intentPath));
            Assert.Equal("prepared", journal.RootElement.GetProperty("phase").GetString());
            JsonElement[] artifacts = journal.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.NotEmpty(artifacts);
            foreach (JsonElement artifact in artifacts)
            {
                string? relativeArtifactPath = artifact.GetProperty("path").GetString();
                Assert.False(string.IsNullOrWhiteSpace(relativeArtifactPath));
                string artifactPath = ToPlatformPath(
                    transactionDirectory,
                    Assert.IsType<string>(relativeArtifactPath));
                byte[] bytes = ReadAllBytesAllowingWrites(artifactPath);
                Assert.Equal(bytes.LongLength, artifact.GetProperty("byteLength").GetInt64());
                Assert.Equal(Digest(bytes), artifact.GetProperty("sha256").GetString());
            }

            ObservedPreparedCheckpoint = true;
        }
    }

    private sealed class StagedArtifactTamperingObserver(
        string root,
        string relativePath,
        string artifact) : ISquadTransactionCheckpointObserver
    {
        private bool _tampered;

        public bool SawAffectedApplyCheckpoint { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedApplyCheckpoint |= artifact switch
            {
                "target-file" => step.Kind == SquadTransactionStepKind.FileApplied,
                "lock-state" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt-state" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => throw new InvalidOperationException(
                    $"Unknown staged artifact contract '{artifact}'.")
            };
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!_tampered && checkpoint.Kind == SquadTransactionCheckpointKind.Prepared)
            {
                string transactionDirectory = Path.Combine(
                    root,
                    ".kyber-weave",
                    ".squad-transaction");
                string stagePath = artifact switch
                {
                    "target-file" => ToPlatformPath(
                        Path.Combine(transactionDirectory, "staging"),
                        $"codex/{relativePath}"),
                    "lock-state" => Path.Combine(
                        transactionDirectory,
                        "state-staging",
                        "lock"),
                    "receipt-state" => Path.Combine(
                        transactionDirectory,
                        "state-staging",
                        "receipt"),
                    _ => throw new InvalidOperationException(
                        $"Unknown staged artifact contract '{artifact}'.")
                };
                Assert.True(File.Exists(stagePath), $"Prepared stage '{stagePath}' was not published.");
                File.WriteAllText(
                    stagePath,
                    $"changed prepared {artifact}",
                    new UTF8Encoding(false));
                _tampered = true;
            }
        }
    }

    private sealed class LeafRaceObserver(
        string root,
        string relativePath,
        string racedNodeKind) : ISquadTransactionCheckpointObserver
    {
        private bool _raced;

        public TreeEntry? RacedEntry { get; private set; }

        public string ExternalCanaryPath { get; private set; } = string.Empty;

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (_raced || checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            string targetPath = ToPlatformPath(root, relativePath);
            DeleteLeaf(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            switch (racedNodeKind)
            {
                case "file":
                    File.WriteAllText(
                        targetPath,
                        "external raced file",
                        new UTF8Encoding(false));
                    ExternalCanaryPath = targetPath;
                    break;
                case "directory":
                    Directory.CreateDirectory(targetPath);
                    ExternalCanaryPath = Path.Combine(targetPath, "external-child.txt");
                    File.WriteAllText(
                        ExternalCanaryPath,
                        "external raced directory child",
                        new UTF8Encoding(false));
                    break;
                case "link":
                    ExternalCanaryPath = Path.Combine(root, "external-link-target.txt");
                    File.WriteAllText(
                        ExternalCanaryPath,
                        "external raced link target",
                        new UTF8Encoding(false));
                    File.CreateSymbolicLink(targetPath, ExternalCanaryPath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown raced node kind '{racedNodeKind}'.");
            }

            RacedEntry = CaptureTree(root)[relativePath];
            _raced = true;
        }

        private static void DeleteLeaf(string path)
        {
            FileInfo entry = new(path);
            if (entry.LinkTarget is not null)
            {
                File.Delete(path);
                return;
            }

            DirectoryInfo directory = new(path);
            if (directory.LinkTarget is not null)
            {
                Directory.Delete(path);
                return;
            }

            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private sealed class PreparedBoundaryCorruptionObserver(
        string root,
        string relativePath,
        string corruption) : ISquadTransactionCheckpointObserver
    {
        public bool CorruptedPreparedArtifacts { get; private set; }

        public bool SawLeafOrStateMutation { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (!CorruptedPreparedArtifacts)
                return;

            SawLeafOrStateMutation |= step.Kind is
                SquadTransactionStepKind.FileApplied or
                SquadTransactionStepKind.LockApplied or
                SquadTransactionStepKind.ReceiptApplied;
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!CorruptedPreparedArtifacts &&
                checkpoint.Kind == SquadTransactionCheckpointKind.Prepared)
            {
                string transactionDirectory = Path.Combine(
                    root,
                    ".kyber-weave",
                    ".squad-transaction");
                string stagePath = ToPlatformPath(
                    Path.Combine(transactionDirectory, "staging"),
                    $"codex/{relativePath}");
                switch (corruption)
                {
                    case "missing":
                        File.Delete(stagePath);
                        break;
                    case "mutated":
                        File.WriteAllText(
                            stagePath,
                            "mutated after prepared publication",
                            new UTF8Encoding(false));
                        break;
                    case "extra":
                        Write(
                            transactionDirectory,
                            "unexpected/closed-set-extra.bin",
                            "undeclared prepared artifact");
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown prepared-boundary corruption '{corruption}'.");
                }

                CorruptedPreparedArtifacts = true;
                return;
            }

            if (!CorruptedPreparedArtifacts)
                return;

            SawLeafOrStateMutation |= checkpoint.Kind is
                SquadTransactionCheckpointKind.ActiveTransitionWritten or
                SquadTransactionCheckpointKind.OriginalClaimed or
                SquadTransactionCheckpointKind.AfterImagePublished;
        }
    }

    private sealed class ActiveTransitionCrashObserver(
        string root,
        SquadTransactionCheckpointKind checkpoint,
        string subject) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint ||
                !string.Equals(value.RelativePath, subject, StringComparison.Ordinal))
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    private sealed class OriginalClaimStageTamperingObserver(
        string root,
        string targetRelativePath,
        string subject,
        string mutation) : ISquadTransactionCheckpointObserver
    {
        private readonly string _checkpointSubject = string.Equals(
            subject,
            "target",
            StringComparison.Ordinal)
            ? targetRelativePath
            : subject;

        public bool SawOriginalClaimed { get; private set; }

        public bool SawAffectedAfterImage { get; private set; }

        public bool SawAffectedPublicApply { get; private set; }

        public TreeEntry? MutatedStageEntry { get; private set; }

        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

        public string StageRelativePath => subject switch
        {
            "target" => $".kyber-weave/.squad-transaction/staging/codex/{targetRelativePath}",
            "lock" => ".kyber-weave/.squad-transaction/state-staging/lock",
            "receipt" => ".kyber-weave/.squad-transaction/state-staging/receipt",
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };

        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedPublicApply |= subject switch
            {
                "target" => step.Kind == SquadTransactionStepKind.FileApplied &&
                            string.Equals(
                                step.RelativePath,
                                targetRelativePath,
                                StringComparison.Ordinal),
                "lock" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => throw new InvalidOperationException(
                    $"Unknown claim-stage subject '{subject}'.")
            };
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!string.Equals(
                    checkpoint.RelativePath,
                    _checkpointSubject,
                    StringComparison.Ordinal))
                return;

            SawAffectedAfterImage |=
                checkpoint.Kind == SquadTransactionCheckpointKind.AfterImagePublished;
            if (SawOriginalClaimed ||
                checkpoint.Kind != SquadTransactionCheckpointKind.OriginalClaimed)
                return;

            SawOriginalClaimed = true;
            if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
                return;

            string stagePath = ToPlatformPath(root, StageRelativePath);
            Assert.True(
                File.Exists(stagePath),
                $"Prepared stage '{stagePath}' must exist at OriginalClaimed.");
            switch (mutation)
            {
                case "edit-bytes":
                    File.WriteAllText(
                        stagePath,
                        $"changed {subject} stage after claim",
                        new UTF8Encoding(false));
                    break;
                case "replacement-link":
                    File.Delete(stagePath);
                    File.CreateSymbolicLink(stagePath, "replacement-stage-target");
                    break;
                case "replacement-directory":
                    File.Delete(stagePath);
                    Directory.CreateDirectory(stagePath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim-stage mutation '{mutation}'.");
            }

            MutatedStageEntry = CaptureTree(root)[StageRelativePath];
        }
    }

    private sealed class OriginalClaimTamperingObserver(
        string root,
        string subject,
        string mutation) : ISquadTransactionCheckpointObserver
    {
        public bool SawOriginalClaimed { get; private set; }

        public bool SawAffectedAfterImage { get; private set; }

        public bool SawAffectedPublicApply { get; private set; }

        public string ClaimRelativePath { get; private set; } = string.Empty;

        public TreeEntry? MutatedClaimEntry { get; private set; }

        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedPublicApply |= subject switch
            {
                "lock" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => step.Kind == SquadTransactionStepKind.FileApplied &&
                     string.Equals(step.RelativePath, subject, StringComparison.Ordinal)
            };
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!string.Equals(checkpoint.RelativePath, subject, StringComparison.Ordinal))
                return;

            SawAffectedAfterImage |=
                checkpoint.Kind == SquadTransactionCheckpointKind.AfterImagePublished;
            if (checkpoint.Kind != SquadTransactionCheckpointKind.OriginalClaimed)
                return;

            SawOriginalClaimed = true;
            using JsonDocument intent = JsonDocument.Parse(ReadAllBytesAllowingWrites(IntentPath));
            JsonElement activeClaim = intent.RootElement
                .GetProperty("artifacts")
                .EnumerateArray()
                .Single(artifact =>
                    string.Equals(
                        artifact.GetProperty("role").GetString(),
                        "claimed-original",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        artifact.GetProperty("lifecycleState").GetString(),
                        "active-transition",
                        StringComparison.Ordinal));
            string artifactRelativePath = Assert.IsType<string>(
                activeClaim.GetProperty("path").GetString());
            string claimPath = ToPlatformPath(
                Path.Combine(root, ".kyber-weave", ".squad-transaction"),
                artifactRelativePath);
            ClaimRelativePath = Path.GetRelativePath(root, claimPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            switch (mutation)
            {
                case "edit-bytes":
                    File.WriteAllText(
                        claimPath,
                        $"changed {subject} claim bytes",
                        new UTF8Encoding(false));
                    break;
                case "retarget-file-link":
                    File.Delete(claimPath);
                    File.CreateSymbolicLink(claimPath, "retargeted-file-claim");
                    break;
                case "retarget-directory-link":
                    DeleteLeafEntry(claimPath);
                    Directory.CreateSymbolicLink(claimPath, "retargeted-directory-claim");
                    break;
                case "replace-directory":
                    File.Delete(claimPath);
                    Directory.CreateDirectory(claimPath);
                    break;
                case "replace-file":
                    Directory.Delete(claimPath, true);
                    File.WriteAllText(
                        claimPath,
                        $"replacement {subject} claim file",
                        new UTF8Encoding(false));
                    break;
                case "unchanged":
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim mutation '{mutation}'.");
            }

            MutatedClaimEntry = CaptureTree(root)[ClaimRelativePath];
            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    private sealed class AfterImageCrashObserver(
        string root,
        string subject) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (checkpoint.Kind != SquadTransactionCheckpointKind.AfterImagePublished ||
                !string.Equals(checkpoint.RelativePath, subject, StringComparison.Ordinal))
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Standard exception constructors required by CA1032.")]
    private sealed class InjectedSquadTransactionFailure : Exception
    {
        public InjectedSquadTransactionFailure()
        {
        }

        public InjectedSquadTransactionFailure(string message)
            : base(message)
        {
        }

        public InjectedSquadTransactionFailure(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public InjectedSquadTransactionFailure(int sequence)
            : base($"Injected failure after transaction step {sequence}.")
        {
            Sequence = sequence;
        }

        public int Sequence { get; }
    }

    private sealed class RichTransactionFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private RichTransactionFixture()
        {
            const string installedBody = "installed conductor";
            const string obsoleteBody = "installed obsolete";
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".codex", "agents"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".codex", "preserved-empty"));
            string canonicalPath = System.IO.Path.Combine(Path, "canonical", "conductor.toml");
            Write(Path, "canonical/conductor.toml", installedBody);
            string agentDirectory = System.IO.Path.Combine(Path, ".codex", "agents");
            File.CreateSymbolicLink(
                System.IO.Path.Combine(agentDirectory, "conductor.toml"),
                System.IO.Path.GetRelativePath(agentDirectory, canonicalPath));
            Write(Path, ".cursor/agents/obsolete.md", obsoleteBody);
            Receipt = Receipt(
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest(installedBody),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".cursor/agents/obsolete.md",
                    Digest(obsoleteBody),
                    "cursor",
                    false));
            WriteState(Path, Lock(), Receipt);
        }

        public string Path => _temp.Path;

        public SquadReceipt Receipt { get; }

        public void Dispose()
        {
            _temp.Dispose();
        }

        public static RichTransactionFixture Create()
        {
            return new RichTransactionFixture();
        }

        public SquadDeploymentPlan CreateUpdatePlan()
        {
            return SquadDeploymentPlan.CreateUpdate(
                Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated conductor"),
                    Rendered(".warp/roles/reviewer.md", "new reviewer", "warp")
                ],
                Receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
        }
    }

    private sealed class TransactionFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private TransactionFixture()
        {
            RelativePath = ".codex/agents/conductor.toml";
            Write(Path, RelativePath, "installed body");
            Receipt = Receipt(
                new SquadOwnedFile(
                    RelativePath,
                    Digest("installed body"),
                    "codex",
                    false));
            WriteState(Path, Lock(), Receipt);
        }

        public string Path => _temp.Path;

        public string RelativePath { get; }

        public SquadReceipt Receipt { get; }

        public void Dispose()
        {
            _temp.Dispose();
        }

        public static TransactionFixture Create()
        {
            return new TransactionFixture();
        }

        public SquadDeploymentPlan CreateUpdatePlan()
        {
            return SquadDeploymentPlan.CreateUpdate(
                Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(RelativePath, "updated body")],
                Receipt,
                [new SquadDegradation("codex", "conductor", "model-profile-narrowed")],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));
        }
    }
}
