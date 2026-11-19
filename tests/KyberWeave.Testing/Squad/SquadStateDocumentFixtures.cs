using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The lock, receipt and home-file documents the deployment-state tests serialize, corrupt and compare, with no file
/// system. The integration and unit deployment-state test classes derive from it so the fixtures have one home.
/// </summary>
public abstract class SquadStateDocumentFixtures
{
    internal const string GuidanceFile = SquadResponseGuidanceDeployer.FileName;

    internal static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    internal static SquadLock ReplaceRequiredLockValue(
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

    internal static string CorruptLockDocument(string yaml, string corruption)
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

    internal static string CorruptReceiptDocument(string json, string corruption)
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

    internal static string CorruptJournalDocument(string json, string corruption)
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

    internal static string InsertBeforeRootClose(string json, string property)
    {
        int rootClose = json.LastIndexOf('}');
        Assert.True(rootClose >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return json.Insert(rootClose, $",{newline}{property}{newline}");
    }

    internal static string InsertDuplicateReceiptFile(string json)
    {
        int insertion = json.LastIndexOf("  ]", StringComparison.Ordinal);
        Assert.True(insertion >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string duplicate =
            $",{newline}    {{{newline}      \"relativePath\": \".codex/agents/Conductor.toml\",{newline}      \"sha256\": \"{Digest("conductor")}\",{newline}      \"target\": \"codex\",{newline}      \"adopted\": false{newline}    }}";
        return json.Insert(insertion + 3, duplicate);
    }

    internal static SquadHomeFile GuidanceHomeFile()
    {
        return new SquadHomeFile(GuidanceFile, Digest("guidance"));
    }

    internal static SquadReceipt GlobalReceiptWithHomeFiles(params SquadHomeFile[] homeFiles)
    {
        return new SquadReceipt(
            SquadStateSchemas.ReceiptSchemaV2,
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [])
        {
            HomeFiles = homeFiles
        };
    }

    internal static string HomeFileJson(string relativePath, string sha256)
    {
        return $$"""{"relativePath":"{{relativePath}}","sha256":"{{sha256}}"}""";
    }

    /// <summary>
    ///     A receipt that breaks exactly one rule of the home-file record, built from a valid global receipt
    ///     so the only thing wrong with it is the thing the case names.
    /// </summary>
    internal static SquadReceipt ReceiptWithInvalidHomeFiles(string invalidCase)
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
    internal static string ReplaceHomeFiles(string json, string homeFilesJson)
    {
        int start = json.IndexOf("\"homeFiles\":", StringComparison.Ordinal);
        int rootClose = json.LastIndexOf('}');
        Assert.True(start >= 0 && rootClose > start, "The fixture receipt must end with its homeFiles property.");
        return $"{json[..start]}\"homeFiles\": {homeFilesJson}\n{json[rootClose..]}";
    }

    internal static SquadLock Lock(string version = "1.2.3")
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

    internal static SquadReceipt Receipt(params SquadOwnedFile[] files)
    {
        return new SquadReceipt(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            files);
    }

    internal static string Digest(string content)
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
    internal static string SerializeAsLegacyV1Json(SquadReceipt receipt)
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

    internal static string Digest(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    internal static void AssertLockEqual(SquadLock expected, SquadLock actual)
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

    internal static void AssertReceiptEqual(SquadReceipt expected, SquadReceipt actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.TargetRoot, actual.TargetRoot);
        Assert.Equal(expected.InstalledAtUtc, actual.InstalledAtUtc);
        Assert.Equal(expected.Degradations, actual.Degradations);
        Assert.Equal(expected.Files, actual.Files);
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Case variance is the subject under test. The lowercase variant is "
                        + "one of the inputs being asserted on, not a normalization to be made safe.")]
    internal static IReadOnlyList<string> NoncanonicalCaseVariants(string canonical)
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

    internal static void AssertCreatedDirectoryAuthorityIsCanonical(JsonArray createdDirectories)
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

    internal static void CorruptCreatedDirectoryAuthority(
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

    internal static string ToggleAsciiCase(string value)
    {
        return new string(value.Select(character =>
            char.IsAsciiLetterUpper(character)
                ? char.ToLowerInvariant(character)
                : char.IsAsciiLetterLower(character)
                    ? char.ToUpperInvariant(character)
                    : character).ToArray());
    }
}
