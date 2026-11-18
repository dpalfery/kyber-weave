using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Deployment-state contracts: lock, receipt and state document contracts. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateTests : SquadDeploymentStateTestBase
{
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
            receipt with { Schema = SquadStateSchemas.ReceiptSchemaV2 },
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
                    ? receipt with { Schema = SquadStateSchemas.ReceiptSchemaV2 }
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
}
