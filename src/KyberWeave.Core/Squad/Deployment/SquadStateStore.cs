using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Reads and writes the portable lock and ownership receipt for Squad deployments.</summary>
public sealed class SquadStateStore
{
    private const string LockFileName = "squad.lock.yml";
    private const string ReceiptFileName = "squad.receipt.json";
    private const string TransactionDirectoryName = ".squad-transaction";
    private const string LockSchema = "kyber-squad.lock/v1";
    private const string ReceiptSchema = "kyber-squad.receipt/v1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly IReadOnlySet<string> CanonicalTargetTokens =
        SquadTargetCatalog.All
            .Select(SquadTargetCatalog.GetToken)
            .ToHashSet(StringComparer.Ordinal);
    private readonly ISquadUserPaths _userPaths;

    public SquadStateStore(ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        _userPaths = userPaths;
    }

    /// <summary>Returns the state directory for the requested deployment scope.</summary>
    public string ResolveStateDirectory(string targetRoot, SquadDeploymentScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        return scope switch
        {
            SquadDeploymentScope.Project => ResolveProjectStateDirectory(targetRoot),
            SquadDeploymentScope.Global => ResolveGlobalStateDirectory(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };
    }

    /// <summary>Serializes a lock using stable, portable YAML field ordering.</summary>
    public string SerializeLock(SquadLock squadLock)
    {
        ArgumentNullException.ThrowIfNull(squadLock);
        ValidateLock(squadLock);

        StringBuilder yaml = new StringBuilder();
        AppendScalar(yaml, "schema", squadLock.Schema);
        AppendScalar(yaml, "squad-version", squadLock.SquadVersion);
        AppendScalar(yaml, "cli-version", squadLock.CliVersion);
        AppendScalar(yaml, "mcp-version", squadLock.McpVersion);
        AppendScalar(yaml, "bundle", squadLock.Bundle);
        AppendSequence(yaml, "targets", squadLock.Targets);
        AppendSequence(yaml, "exclusions", squadLock.Exclusions);
        AppendScalar(yaml, "translation", squadLock.Translation);
        AppendScalar(yaml, "bundle-digest", squadLock.BundleDigest);
        AppendScalar(yaml, "asset-digest", squadLock.AssetDigest);
        yaml.Append("apm:\n");
        AppendScalar(yaml, "version", squadLock.Apm.Version, indentation: 2);
        AppendScalar(yaml, "tag-commit", squadLock.Apm.TagCommit, indentation: 2);
        AppendScalar(yaml, "asset-sha256", squadLock.Apm.AssetSha256, indentation: 2);
        return yaml.ToString();
    }

    /// <summary>Deserializes a Squad lock from YAML.</summary>
    public SquadLock DeserializeLock(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        try
        {
            RejectYamlIndirection(yaml);
            YamlStream stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new InvalidDataException(
                    "Squad lock YAML must contain exactly one mapping document.");
            }

            RequireExactYamlFields(
                root,
                [
                    "schema", "squad-version", "cli-version", "mcp-version", "bundle",
                    "targets", "exclusions", "translation", "bundle-digest",
                    "asset-digest", "apm"
                ],
                "lock");
            YamlMappingNode apm = RequireYamlMapping(root, "apm", "lock");
            RequireExactYamlFields(
                apm,
                ["version", "tag-commit", "asset-sha256"],
                "lock apm identity");

            SquadLock squadLock = new SquadLock(
                RequireYamlScalar(root, "schema", "lock"),
                RequireYamlScalar(root, "squad-version", "lock"),
                RequireYamlScalar(root, "cli-version", "lock"),
                RequireYamlScalar(root, "mcp-version", "lock"),
                RequireYamlScalar(root, "bundle", "lock"),
                RequireYamlSequence(root, "targets", "lock"),
                RequireYamlSequence(root, "exclusions", "lock"),
                RequireYamlScalar(root, "translation", "lock"),
                RequireYamlScalar(root, "bundle-digest", "lock"),
                RequireYamlScalar(root, "asset-digest", "lock"),
                new SquadApmIdentity(
                    RequireYamlScalar(apm, "version", "lock apm identity"),
                    RequireYamlScalar(apm, "tag-commit", "lock apm identity"),
                    RequireYamlScalar(apm, "asset-sha256", "lock apm identity")));
            ValidateLock(squadLock);
            return squadLock;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is YamlException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Squad lock YAML is invalid: {exception.Message}",
                exception);
        }
    }

    /// <summary>Serializes a receipt as stable, indented JSON with portable relative paths.</summary>
    public string SerializeReceipt(SquadReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateReceipt(receipt);
        return JsonSerializer.Serialize(receipt, JsonOptions) + "\n";
    }

    /// <summary>Deserializes a Squad ownership receipt.</summary>
    public SquadReceipt DeserializeReceipt(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            ValidateReceiptJsonShape(json);
            SquadReceipt receipt = JsonSerializer.Deserialize<SquadReceipt>(json, JsonOptions)
                ?? throw new InvalidDataException("Squad receipt JSON is empty.");
            ValidateReceipt(receipt);
            return receipt;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"Squad receipt JSON is invalid: {exception.Message}",
                exception);
        }
    }

    /// <summary>Reads the receipt for a deployment, or returns <see langword="null"/> when absent.</summary>
    public SquadReceipt? ReadReceipt(string targetRoot, SquadDeploymentScope scope)
    {
        string path = ResolveStateFile(targetRoot, scope, ReceiptFileName);
        return File.Exists(path)
            ? DeserializeReceipt(File.ReadAllText(path, Encoding.UTF8))
            : null;
    }

    /// <summary>Reads the lock for a deployment, or returns <see langword="null"/> when absent.</summary>
    public SquadLock? ReadLock(string targetRoot, SquadDeploymentScope scope)
    {
        string path = ResolveStateFile(targetRoot, scope, LockFileName);
        return File.Exists(path)
            ? DeserializeLock(File.ReadAllText(path, Encoding.UTF8))
            : null;
    }

    internal string ResolveLockPath(string targetRoot, SquadDeploymentScope scope) =>
        ResolveStateFile(targetRoot, scope, LockFileName);

    internal string ResolveReceiptPath(string targetRoot, SquadDeploymentScope scope) =>
        ResolveStateFile(targetRoot, scope, ReceiptFileName);

    internal string ResolveTransactionDirectory(string targetRoot, SquadDeploymentScope scope) =>
        ResolveStateFile(targetRoot, scope, TransactionDirectoryName);

    internal string ResolveTransactionWorkDirectory(
        string targetRoot,
        SquadDeploymentScope scope) =>
        scope switch
        {
            SquadDeploymentScope.Project => ResolveTransactionDirectory(targetRoot, scope),
            SquadDeploymentScope.Global => SquadPathPolicy.ResolveFile(
                Path.GetFullPath(targetRoot),
                ".kyber-weave/.squad-transaction"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };

    internal string ResolveStateAuthorityRoot(
        string targetRoot,
        SquadDeploymentScope scope) =>
        scope switch
        {
            SquadDeploymentScope.Project =>
                SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath,
            SquadDeploymentScope.Global => Path.GetDirectoryName(
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(_userPaths.ApplicationDataDirectory)))
                ?? Path.GetFullPath(_userPaths.ApplicationDataDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };

    private string ResolveProjectStateDirectory(string targetRoot)
    {
        string root = SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath;
        string sentinel = SquadPathPolicy.ResolveFile(root, ".kyber-weave/.state-sentinel");
        return Path.GetDirectoryName(sentinel)
            ?? throw new InvalidOperationException("Could not resolve the project Squad state directory.");
    }

    private string ResolveGlobalStateDirectory()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_userPaths.ApplicationDataDirectory);
        string root = Path.GetFullPath(_userPaths.ApplicationDataDirectory);
        string sentinel = SquadPathPolicy.ResolveFile(root, "KyberWeave/squad/.state-sentinel");
        return Path.GetDirectoryName(sentinel)
            ?? throw new InvalidOperationException("Could not resolve the global Squad state directory.");
    }

    private string ResolveStateFile(
        string targetRoot,
        SquadDeploymentScope scope,
        string fileName)
    {
        return scope switch
        {
            SquadDeploymentScope.Project => SquadPathPolicy.ResolveFile(
                SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath,
                $".kyber-weave/{fileName}"),
            SquadDeploymentScope.Global => SquadPathPolicy.ResolveFile(
                ResolveGlobalStateDirectory(),
                $"roots/{GlobalRootBinding(targetRoot)}/{fileName}"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };
    }

    private static void AppendScalar(
        StringBuilder yaml,
        string name,
        string value,
        int indentation = 0)
    {
        yaml.Append(' ', indentation);
        yaml.Append(name);
        yaml.Append(": ");
        yaml.Append(ToYamlScalar(value));
        yaml.Append('\n');
    }

    private static void AppendSequence(
        StringBuilder yaml,
        string name,
        IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        yaml.Append(name);
        if (values.Count == 0)
        {
            yaml.Append(": []\n");
            return;
        }

        yaml.Append(":\n");
        foreach (string value in values)
        {
            yaml.Append("  - ");
            yaml.Append(ToYamlScalar(value));
            yaml.Append('\n');
        }
    }

    private static string ToYamlScalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/') &&
            value is not "null" and not "Null" and not "NULL" and
            not "true" and not "True" and not "TRUE" and
            not "false" and not "False" and not "FALSE" and not "~")
        {
            return value;
        }

        return JsonSerializer.Serialize(value);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NewLine = "\n",
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private static void ValidateLock(SquadLock squadLock)
    {
        if (!string.Equals(squadLock.Schema, LockSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Squad lock schema must be '{LockSchema}'.");
        }

        if (squadLock.Targets is null || squadLock.Exclusions is null)
            throw new InvalidDataException("Squad lock target and exclusion lists are required.");

        ValidateRequiredLockValue(squadLock.SquadVersion, "squad version");
        ValidateRequiredLockValue(squadLock.CliVersion, "CLI version");
        ValidateRequiredLockValue(squadLock.McpVersion, "MCP version");
        ValidateRequiredLockValue(squadLock.Bundle, "bundle");
        ValidateRequiredLockValue(squadLock.Translation, "translation");
        foreach (string target in squadLock.Targets)
            ValidateRequiredLockValue(target, "target");
        foreach (string exclusion in squadLock.Exclusions)
            ValidateRequiredLockValue(exclusion, "exclusion");

        ValidateDigest(squadLock.BundleDigest, "bundle digest");
        ValidateDigest(squadLock.AssetDigest, "asset digest");
        if (squadLock.Apm is null)
            throw new InvalidDataException("Squad lock is missing the apm identity.");

        ValidateRequiredLockValue(squadLock.Apm.Version, "APM version");
        ValidateRequiredLockValue(squadLock.Apm.TagCommit, "APM tag commit");

        ValidateDigest(squadLock.Apm.AssetSha256, "apm digest");
    }

    private static void ValidateRequiredLockValue(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Squad lock {fieldName} must be a non-blank value.");
        }
    }

    private static void ValidateReceipt(SquadReceipt receipt)
    {
        if (!string.Equals(receipt.Schema, ReceiptSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Squad receipt schema must be '{ReceiptSchema}'.");
        }

        if (!string.Equals(receipt.TargetRoot, ".", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Squad receipt target root must be the portable relative marker '.'.");
        }

        if (receipt.Files is null)
            throw new InvalidDataException("Squad receipt files are required.");
        if (receipt.Degradations is null)
            throw new InvalidDataException("Squad receipt degradations are required.");

        foreach (SquadDegradation? degradation in receipt.Degradations)
        {
            if (degradation is null)
            {
                throw new InvalidDataException(
                    "Squad receipt contains an empty degradation entry.");
            }

            ValidateCanonicalTarget(degradation.Target, "degradation target");
            ValidateRequiredReceiptValue(degradation.Subject, "degradation subject");
            ValidateRequiredReceiptValue(degradation.Code, "degradation code");
        }

        HashSet<string> portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SquadOwnedFile? file in receipt.Files)
        {
            if (file is null)
                throw new InvalidDataException("Squad receipt contains an empty file entry.");

            string normalizedPath;
            string portableIdentity;
            try
            {
                normalizedPath = SquadPathPolicy.NormalizeRelativePath(file.RelativePath);
                portableIdentity = SquadPathPolicy.GetPortableIdentity(normalizedPath);
            }
            catch (Exception exception) when (
                exception is SquadPathContainmentException or SquadDeploymentConflictException)
            {
                throw new InvalidDataException(
                    $"Squad receipt file path '{file.RelativePath}' is not portable.",
                    exception);
            }

            if (!string.Equals(normalizedPath, file.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(portableIdentity, normalizedPath, StringComparison.Ordinal) ||
                !portablePaths.Add(portableIdentity))
            {
                throw new InvalidDataException(
                    $"Squad receipt file path '{file.RelativePath}' is not a unique portable path.");
            }

            ValidateDigest(file.Sha256, "file digest");
            ValidateCanonicalTarget(file.Target, "file target");
        }
    }

    private static void ValidateCanonicalTarget(string? value, string fieldName)
    {
        if (value is null || !CanonicalTargetTokens.Contains(value))
        {
            throw new InvalidDataException(
                $"Squad receipt {fieldName} must be an exact canonical Squad target token.");
        }
    }

    private static void ValidateRequiredReceiptValue(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Squad receipt {fieldName} must be a non-blank value.");
        }
    }

    private static void ValidateDigest(string? digest, string fieldName)
    {
        if (digest is null ||
            digest.Length != 64 ||
            digest.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"Squad {fieldName} must be a lowercase SHA-256 digest.");
        }
    }

    private static void RejectYamlIndirection(string yaml)
    {
        if (yaml.Contains('&', StringComparison.Ordinal) ||
            yaml.Contains('*', StringComparison.Ordinal) ||
            yaml.Contains("<<:", StringComparison.Ordinal) ||
            yaml.Contains("!!", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Squad lock YAML does not allow anchors, aliases, merge keys, or custom tags.");
        }
    }

    private static void RequireExactYamlFields(
        YamlMappingNode mapping,
        IReadOnlyCollection<string> expected,
        string subject)
    {
        HashSet<string> actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (YamlNode keyNode in mapping.Children.Keys)
        {
            if (keyNode is not YamlScalarNode { Value: not null } key ||
                !actual.Add(key.Value))
            {
                throw new InvalidDataException(
                    $"Squad {subject} contains a duplicate or non-scalar field.");
            }
        }

        string[] missing = expected.Where(field => !actual.Contains(field)).ToArray();
        string[] unknown = actual.Where(field => !expected.Contains(field)).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"Squad {subject} has missing or unknown fields. " +
                $"Missing: {string.Join(", ", missing)}; unknown: {string.Join(", ", unknown)}.");
        }
    }

    private static YamlNode RequireYamlNode(
        YamlMappingNode mapping,
        string field,
        string subject)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(field), out YamlNode? node))
            throw new InvalidDataException($"Squad {subject} is missing field '{field}'.");
        return node;
    }

    private static string RequireYamlScalar(
        YamlMappingNode mapping,
        string field,
        string subject)
    {
        YamlNode node = RequireYamlNode(mapping, field, subject);
        if (node is not YamlScalarNode { Value: not null } scalar ||
            string.IsNullOrWhiteSpace(scalar.Value) ||
            scalar.Value is "null" or "Null" or "NULL" or "~")
        {
            throw new InvalidDataException(
                $"Squad {subject} field '{field}' must be a non-null string.");
        }

        return scalar.Value;
    }

    private static YamlMappingNode RequireYamlMapping(
        YamlMappingNode mapping,
        string field,
        string subject) =>
        RequireYamlNode(mapping, field, subject) as YamlMappingNode ??
        throw new InvalidDataException(
            $"Squad {subject} field '{field}' must be a mapping.");

    private static IReadOnlyList<string> RequireYamlSequence(
        YamlMappingNode mapping,
        string field,
        string subject)
    {
        if (RequireYamlNode(mapping, field, subject) is not YamlSequenceNode sequence)
        {
            throw new InvalidDataException(
                $"Squad {subject} field '{field}' must be a sequence.");
        }

        return sequence.Children.Select(node =>
        {
            if (node is not YamlScalarNode { Value: not null } scalar ||
                string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidDataException(
                    $"Squad {subject} field '{field}' must contain only non-null strings.");
            }

            return scalar.Value;
        }).ToArray();
    }

    private static void ValidateReceiptJsonShape(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        RequireExactJsonFields(
            document.RootElement,
            ["schema", "scope", "targetRoot", "installedAtUtc", "degradations", "files"],
            "receipt");
        RequireCanonicalJsonEnum(
            document.RootElement,
            "scope",
            ["project", "global"],
            "receipt");

        JsonElement degradations = document.RootElement.GetProperty("degradations");
        if (degradations.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad receipt degradations must be an array.");
        foreach (JsonElement degradation in degradations.EnumerateArray())
        {
            RequireExactJsonFields(
                degradation,
                ["target", "subject", "code"],
                "receipt degradation");
        }

        JsonElement files = document.RootElement.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad receipt files must be an array.");
        foreach (JsonElement file in files.EnumerateArray())
        {
            RequireExactJsonFields(
                file,
                ["relativePath", "sha256", "target", "adopted"],
                "receipt file");
        }
    }

    private static void RequireExactJsonFields(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string subject)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Squad {subject} must be an object.");

        HashSet<string> actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name))
                throw new InvalidDataException($"Squad {subject} contains duplicate field '{property.Name}'.");
            if (property.Value.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException($"Squad {subject} field '{property.Name}' cannot be null.");
        }

        string[] missing = expected.Where(field => !actual.Contains(field)).ToArray();
        string[] unknown = actual.Where(field => !expected.Contains(field)).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"Squad {subject} has missing or unknown fields. " +
                $"Missing: {string.Join(", ", missing)}; unknown: {string.Join(", ", unknown)}.");
        }
    }

    private static void RequireCanonicalJsonEnum(
        JsonElement element,
        string propertyName,
        IReadOnlyCollection<string> allowedValues,
        string subject)
    {
        JsonElement value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } token ||
            !allowedValues.Contains(token))
        {
            throw new InvalidDataException(
                $"Squad {subject} field '{propertyName}' must use its canonical enum token.");
        }
    }

    private static string GlobalRootBinding(string targetRoot)
    {
        return SquadPhysicalRootIdentity.Resolve(targetRoot).Key;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string value = reader.GetString()
                ?? throw new JsonException("Expected a UTC timestamp string.");
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

}
