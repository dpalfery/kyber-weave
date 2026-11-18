using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Deployment-state contracts: lock, receipt and state document contracts. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStateTests : SquadStateDocumentFixtures
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
        InMemorySquadState state = new(applicationDataDirectory: Path.GetFullPath("/kw/TOP-SECRET-USER-PATH"));
        SquadStateStore store = state.Store;
        string kwRoot = Path.GetDirectoryName(state.UserPaths.ApplicationDataDirectory)!;
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
        Assert.DoesNotContain(kwRoot, lockYaml, StringComparison.Ordinal);
        Assert.DoesNotContain(kwRoot, receiptJson, StringComparison.Ordinal);
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        InMemorySquadState state = new();
        SquadStateStore store = state.Store;
        string physicalRoot = Path.Combine(state.Files.Root, "physical-root");
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;

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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
        SquadLock invalid = ReplaceRequiredLockValue(Lock(), field, blankValue);
        string? emitted = null;

        Exception? exception = Record.Exception(() => emitted = store.SerializeLock(invalid));

        Assert.IsType<InvalidDataException>(exception);
        Assert.Null(emitted);
    }


    [Fact]
    public void SerializeReceiptNullBlankOrNoncanonicalNestedValuesAreRejected()
    {
        SquadStateStore store = new InMemorySquadState().Store;
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
        SquadStateStore store = new InMemorySquadState().Store;
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
