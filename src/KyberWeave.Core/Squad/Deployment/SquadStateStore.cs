using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// The registry schema. Its entries are keyed by root key, so a registry rebuilt from
    /// per-project state and one written by this code agree on identity.
    /// </summary>
    internal const string RegistrySchema = "kyber-squad.registry/v1";

    private const string RegistryFileName = "registry.json";
    private const string RegistryProjectsProperty = "projects";

    /// <summary>
    /// The home-level state directory relative to the home directory. It holds <c>global/</c>,
    /// <c>projects/&lt;root-key&gt;/</c> and <c>registry.json</c>.
    /// </summary>
    private const string HomeStateRelativeDirectory = ".kyber-weave/squad";

    private const string GlobalStateRelativeDirectory = ".kyber-weave/squad/global";
    private const string ProjectsRelativeDirectory = ".kyber-weave/squad/projects";

    /// <summary>
    /// The journal for a project transaction sits beside its root, and the global journal sits
    /// beside the home. Both are <c>.kyber-weave/.squad-transaction</c> under their own anchor.
    /// </summary>
    private const string JournalRelativePath = ".kyber-weave/" + TransactionDirectoryName;

    /// <summary>
    /// The receipt <c>targetRoot</c> a plan records before the transaction anchors it. Global
    /// receipts are anchored on the home, which only the store knows, so the planner writes this
    /// portable marker and the transaction replaces it at write time. Legacy receipts carry it too.
    /// </summary>
    internal const string UnanchoredTargetRoot = ".";

    /// <summary>
    /// The one receipt schema source (#91 / A3): project receipts stay on this schema forever,
    /// and it is also what a legacy Global receipt written before #91 carries.
    /// </summary>
    internal const string ReceiptSchemaV1 = "kyber-squad.receipt/v1";

    /// <summary>
    /// Written for Global receipts only, additive over v1 with a required <c>layout</c> field.
    /// An older CLI that predates this schema refuses it outright rather than misreading it.
    /// </summary>
    internal const string ReceiptSchemaV2 = "kyber-squad.receipt/v2";

    private static readonly JsonWriterOptions ReceiptWriterOptions = new JsonWriterOptions
    {
        Indented = true,
        NewLine = "\n"
    };

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

    internal ISquadUserPaths UserPaths => _userPaths;

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

    /// <summary>
    /// Returns the home-level Squad state directory, <c>&lt;HomeDirectory&gt;/.kyber-weave/squad</c>,
    /// which holds <c>global/</c>, <c>projects/&lt;root-key&gt;/</c> and <c>registry.json</c>.
    /// </summary>
    public string ResolveHomeStateDirectory() =>
        ResolveDirectoryUnder(ResolveHomeRoot(), HomeStateRelativeDirectory);

    /// <summary>
    /// Lists every project in the home-level registry, with a stale flag and reason for each entry
    /// that no longer matches the state on disk. A project whose receipt sits under
    /// <c>projects/&lt;key&gt;</c> but has no registry entry is rebuilt from that receipt, and the
    /// rebuilt registry is written back. A failed write surfaces to the caller rather than being hidden.
    /// </summary>
    /// <remarks>
    /// The registry is an index, never the authority for ownership. A stale entry is reported rather
    /// than dropped, so an operator can see what was registered before choosing to unregister it.
    /// </remarks>
    /// <exception cref="InvalidDataException">The registry file exists but is not a valid registry.</exception>
    public IReadOnlyList<SquadProjectRegistryEntry> ListProjects()
    {
        Dictionary<string, RegistryRecord> registry = ReadRegistry();
        bool rebuilt = false;
        foreach (string projectDirectory in EnumerateProjectStateDirectories())
        {
            string key = Path.GetFileName(projectDirectory);
            if (registry.ContainsKey(key))
                continue;

            string receiptPath = Path.Combine(projectDirectory, ReceiptFileName);
            if (!File.Exists(receiptPath))
                continue;

            SquadReceipt receipt = DeserializeReceipt(File.ReadAllText(receiptPath, Encoding.UTF8));
            registry[key] = new RegistryRecord(
                receipt.TargetRoot,
                receipt.InstalledAtUtc,
                DistinctTargets(receipt.Files));
            rebuilt = true;
        }

        if (rebuilt)
            WriteRegistry(registry);

        return registry
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => DescribeEntry(pair.Key, pair.Value))
            .ToArray();
    }

    /// <summary>Registers, or refreshes, a project root in the home-level Squad registry.</summary>
    /// <param name="root">The project root to register.</param>
    /// <param name="targets">The harness targets deployed beneath that root.</param>
    /// <param name="now">The instant recorded as the entry's last update.</param>
    /// <exception cref="IOException">The registry could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The registry could not be written.</exception>
    public void RegisterProject(string root, IReadOnlyList<string> targets, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(targets);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(root);
        Dictionary<string, RegistryRecord> registry = ReadRegistry();
        registry[identity.Key] = new RegistryRecord(
            identity.PhysicalPath,
            now.ToUniversalTime(),
            targets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        WriteRegistry(registry);
    }

    /// <summary>Removes a project root from the home-level Squad registry.</summary>
    /// <param name="root">The project root to remove. It may no longer exist, so a stale entry can still be removed.</param>
    /// <returns><see langword="true"/> when an entry was removed; <see langword="false"/> when none existed.</returns>
    public bool UnregisterProject(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string key = SquadPhysicalRootIdentity.KeyOf(
            SquadPhysicalRootIdentity.CanonicalizeAllowingMissing(root));
        Dictionary<string, RegistryRecord> registry = ReadRegistry();
        if (!registry.Remove(key))
            return false;

        WriteRegistry(registry);
        return true;
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

    /// <summary>
    /// Serializes a receipt as stable, indented JSON with portable relative paths. A Global
    /// receipt is always written on <see cref="ReceiptSchemaV2"/> — the v1 field set unchanged,
    /// plus its required <c>layout</c> — since v2 exists for Global receipts only (A3); a
    /// project receipt is written byte-identical to every receipt this store has ever produced,
    /// on <see cref="ReceiptSchemaV1"/> with no <c>layout</c> field. The write schema is derived
    /// from <see cref="SquadReceipt.Scope"/> alone, never from the schema string already on
    /// <paramref name="receipt"/> — so a receipt built directly through the record's
    /// constructor upgrades to v2 the moment it is written for Global scope.
    /// </summary>
    public string SerializeReceipt(SquadReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateReceipt(receipt);

        bool isGlobal = receipt.Scope == SquadDeploymentScope.Global;

        using MemoryStream stream = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, ReceiptWriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", isGlobal ? ReceiptSchemaV2 : ReceiptSchemaV1);
            writer.WriteString("scope", isGlobal ? "global" : "project");
            if (isGlobal)
            {
                writer.WriteString(
                    "layout",
                    receipt.Layout == SquadReceiptLayout.SingleRoot ? "single-root" : "per-target-roots");
            }

            writer.WriteString("targetRoot", receipt.TargetRoot);
            writer.WriteString(
                "installedAtUtc",
                receipt.InstalledAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WritePropertyName("degradations");
            JsonSerializer.Serialize(writer, receipt.Degradations, JsonOptions);
            writer.WritePropertyName("files");
            JsonSerializer.Serialize(writer, receipt.Files, JsonOptions);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Deserializes a Squad ownership receipt.</summary>
    public SquadReceipt DeserializeReceipt(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            ValidateReceiptJsonShape(document.RootElement);
            SquadReceipt receipt = BuildReceiptFromJson(document.RootElement);
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

    /// <summary>
    /// Builds the in-memory receipt from an already shape-validated JSON document. A v1 Global
    /// receipt carries no <c>layout</c> field, so its layout is classified from its own owned
    /// files' paths (<see cref="SquadDeploymentPlan.ClassifyGlobalLayout"/>) — the only signal
    /// #91 left behind. A v2 receipt is always Global (A3) regardless of whether its optional
    /// <c>scope</c> field was present, and its explicit <c>layout</c> field is trusted as-is.
    /// </summary>
    private static SquadReceipt BuildReceiptFromJson(JsonElement root)
    {
        string schema = root.GetProperty("schema").GetString()
            ?? throw new InvalidDataException("Squad receipt schema must be a string.");
        bool isV2 = string.Equals(schema, ReceiptSchemaV2, StringComparison.Ordinal);

        string? scopeText = isV2 ? null : root.GetProperty("scope").GetString();
        SquadDeploymentScope scope = isV2
            ? SquadDeploymentScope.Global
            : scopeText switch
            {
                "project" => SquadDeploymentScope.Project,
                "global" => SquadDeploymentScope.Global,
                _ => throw new InvalidDataException(
                    $"Squad receipt scope '{scopeText}' is not a recognized canonical scope.")
            };

        string targetRoot = root.GetProperty("targetRoot").GetString()
            ?? throw new InvalidDataException("Squad receipt targetRoot must be a string.");
        DateTimeOffset installedAtUtc = DateTimeOffset.Parse(
            root.GetProperty("installedAtUtc").GetString()
                ?? throw new InvalidDataException("Squad receipt installedAtUtc must be a string."),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        IReadOnlyList<SquadDegradation> degradations =
            root.GetProperty("degradations").Deserialize<IReadOnlyList<SquadDegradation>>(JsonOptions)
            ?? [];
        IReadOnlyList<SquadOwnedFile> files =
            root.GetProperty("files").Deserialize<IReadOnlyList<SquadOwnedFile>>(JsonOptions)
            ?? [];

        SquadReceiptLayout layout;
        if (isV2)
        {
            string? layoutText = root.GetProperty("layout").GetString();
            layout = layoutText switch
            {
                "single-root" => SquadReceiptLayout.SingleRoot,
                "per-target-roots" => SquadReceiptLayout.PerTargetRoots,
                _ => throw new InvalidDataException(
                    $"Squad receipt layout '{layoutText}' is not a recognized canonical layout.")
            };

            // An empty file list is consistent with either declared layout — there is nothing
            // to classify against. A non-empty list must agree with what its own paths
            // classify as; a persisted receipt that lies about its layout is exactly the state
            // a corrupted or hand-edited write could leave behind, and trusting it silently is
            // what let a mismatched label reach a later, resolver-supplied run (#91).
            if (files.Count > 0)
            {
                SquadReceiptLayout classified = SquadDeploymentPlan.ClassifyGlobalLayout(files);
                if (classified != layout)
                {
                    string classifiedText = classified == SquadReceiptLayout.SingleRoot
                        ? "single-root"
                        : "per-target-roots";
                    throw new InvalidDataException(
                        $"Squad receipt declares layout '{layoutText}' but its files classify as " +
                        $"'{classifiedText}'. Recover it by hand: verify or remove each file, then " +
                        "delete the receipt and lock so a fresh install can recreate them.");
                }
            }
        }
        else if (scope == SquadDeploymentScope.Global)
        {
            layout = SquadDeploymentPlan.ClassifyGlobalLayout(files);
        }
        else
        {
            layout = SquadReceiptLayout.PerTargetRoots;
        }

        return new SquadReceipt(schema, scope, targetRoot, installedAtUtc, degradations, files)
        {
            Layout = layout
        };
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

    /// <summary>
    /// The project journal lives beside its root and the global journal beside the home, so
    /// <paramref name="targetRoot"/> is ignored for global scope. Recovery and the lease both
    /// address the transaction through this directory, which is why it is independent of the
    /// state files' own location.
    /// </summary>
    internal string ResolveTransactionDirectory(string targetRoot, SquadDeploymentScope scope) =>
        scope switch
        {
            SquadDeploymentScope.Project => SquadPathPolicy.ResolveFile(
                SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath,
                JournalRelativePath),
            SquadDeploymentScope.Global => SquadPathPolicy.ResolveFile(ResolveHomeStateRoot(), JournalRelativePath),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };

    /// <summary>
    /// The directory a transaction keeps its own lease-scoped work in. For both scopes it is the
    /// journal directory, which holds the intent, the staged bytes and the backups.
    /// </summary>
    /// <remarks>
    /// A global transaction keeps its staging and backups in the home journal rather than beside the
    /// target root, so one home has one transaction directory and the portable global state directory
    /// stays free of transaction evidence. The home journal may sit on another volume from the target
    /// root; a staged file is then moved across that boundary rather than renamed within it.
    /// </remarks>
    internal string ResolveTransactionWorkDirectory(
        string targetRoot,
        SquadDeploymentScope scope) =>
        ResolveTransactionDirectory(targetRoot, scope);

    /// <summary>
    /// The directory that holds a transaction's staged and backed-up target files. Both scopes keep it in
    /// the journal, see <see cref="ResolveTransactionWorkDirectory"/>.
    /// </summary>
    internal string ResolveFileWorkDirectory(
        string targetRoot,
        SquadDeploymentScope scope) =>
        ResolveTransactionDirectory(targetRoot, scope);

    /// <summary>
    /// The state root against which a transaction's created directories are recorded and removed.
    /// A project's journal is beside its root, so the root is the authority. A global journal is
    /// beside the home, so the home is the authority.
    /// </summary>
    internal string ResolveStateAuthorityRoot(
        string targetRoot,
        SquadDeploymentScope scope) =>
        scope switch
        {
            SquadDeploymentScope.Project =>
                SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath,
            SquadDeploymentScope.Global => ResolveHomeCreationBoundary(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };

    /// <summary>
    /// The identity a transaction's lease is keyed by: the physical key of the root a project is
    /// installed at, or of the home for global state. Both use the same key space on purpose. A project
    /// is leased per physical root, so projects at distinct roots proceed independently and aliases of one
    /// root contend. A global transaction is leased on the home, which is also the global journal's
    /// anchor, so a project installed at the home root contends with global state and any other root does not.
    /// </summary>
    internal string ResolveLeaseIdentity(string targetRoot, SquadDeploymentScope scope) =>
        scope switch
        {
            SquadDeploymentScope.Project => SquadPhysicalRootIdentity.Resolve(targetRoot).Key,
            SquadDeploymentScope.Global => SquadPhysicalRootIdentity.KeyOf(ResolveHomeRoot()),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };

    /// <summary>
    /// The canonical physical home directory, which may not exist yet. It keys the global lease and is
    /// the value a global receipt records, so two spellings of one home never diverge on identity.
    /// </summary>
    internal string ResolveHomeRoot()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_userPaths.HomeDirectory);
        return SquadPhysicalRootIdentity.CanonicalizeAllowingMissing(_userPaths.HomeDirectory);
    }

    /// <summary>
    /// The home as the operator configured it, made absolute but not resolved through symlinks. Global
    /// state and its journal are placed beneath this spelling, so they read back as they were given.
    /// Project state is addressed by its root's physical identity and so stays canonical; the two
    /// spellings name the same directories, and every relative path is taken between canonical forms.
    /// </summary>
    private string ResolveHomeStateRoot()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_userPaths.HomeDirectory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(_userPaths.HomeDirectory));
    }

    /// <summary>
    /// The directory beneath which a transaction records the home directories it creates. It is the
    /// home's parent rather than the home, so a home that did not exist before the transaction is
    /// created and then removed on rollback. Nothing above the home is ever recorded or removed.
    /// </summary>
    internal string ResolveHomeCreationBoundary() =>
        Path.GetDirectoryName(ResolveHomeRoot())
            ?? throw new InvalidOperationException("The Squad home directory has no parent to anchor state on.");

    private string ResolveProjectStateDirectory(string targetRoot)
    {
        string key = SquadPhysicalRootIdentity.Resolve(targetRoot).Key;
        return ResolveDirectoryUnder(ResolveHomeRoot(), $"{ProjectsRelativeDirectory}/{key}");
    }

    private string ResolveGlobalStateDirectory() =>
        ResolveDirectoryUnder(ResolveHomeStateRoot(), GlobalStateRelativeDirectory);

    private static string ResolveDirectoryUnder(string root, string relativeDirectory)
    {
        string sentinel = SquadPathPolicy.ResolveFile(root, $"{relativeDirectory}/.state-sentinel");
        return Path.GetDirectoryName(sentinel)
            ?? throw new InvalidOperationException("Could not resolve a Squad state directory.");
    }

    private string ResolveStateFile(
        string targetRoot,
        SquadDeploymentScope scope,
        string fileName)
    {
        return scope switch
        {
            SquadDeploymentScope.Project => SquadPathPolicy.ResolveFile(
                ResolveProjectStateDirectory(targetRoot),
                fileName),
            SquadDeploymentScope.Global => SquadPathPolicy.ResolveFile(
                ResolveGlobalStateDirectory(),
                fileName),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Squad scope.")
        };
    }

    /// <summary>One registry entry as stored on disk, before stale checks are applied.</summary>
    private sealed record RegistryRecord(
        string Path,
        DateTimeOffset LastUpdatedUtc,
        IReadOnlyList<string> Targets);

    private static IReadOnlyList<string> DistinctTargets(IEnumerable<SquadOwnedFile> files) =>
        files.Select(file => file.Target).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Enumerates the per-project state directories one level deep. Symbolic links are skipped and
    /// never followed, because the walk runs over a directory a user can influence.
    /// </summary>
    private IEnumerable<string> EnumerateProjectStateDirectories()
    {
        string projectsDirectory = ResolveDirectoryUnder(ResolveHomeRoot(), ProjectsRelativeDirectory);
        if (!Directory.Exists(projectsDirectory))
            return [];

        return new DirectoryInfo(projectsDirectory)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 })
            .OfType<DirectoryInfo>()
            .Where(entry => entry.LinkTarget is null && IsRootKey(entry.Name))
            .Select(entry => entry.FullName)
            .ToArray();
    }

    private static bool IsRootKey(string value) =>
        value.Length == 64 && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private SquadProjectRegistryEntry DescribeEntry(string key, RegistryRecord record)
    {
        List<string> reasons = [];
        if (!IsRootKey(key))
        {
            reasons.Add("registered root key is malformed");
        }
        else if (string.IsNullOrEmpty(record.Path) || !Path.IsPathFullyQualified(record.Path))
        {
            reasons.Add("registered project path is not recorded");
        }
        else if (!Directory.Exists(record.Path))
        {
            reasons.Add("registered project path no longer exists");
        }
        else if (!RegisteredKeyMatchesPath(key, record.Path))
        {
            reasons.Add("registered root key no longer matches the physical key of its path");
        }

        if (IsRootKey(key) &&
            !File.Exists(Path.Combine(ResolveDirectoryUnder(ResolveHomeRoot(), $"{ProjectsRelativeDirectory}/{key}"), ReceiptFileName)))
        {
            reasons.Add("project receipt is missing");
        }

        return new SquadProjectRegistryEntry(
            key,
            record.Path,
            record.LastUpdatedUtc,
            record.Targets,
            Stale: reasons.Count > 0,
            StaleReason: reasons.Count > 0 ? string.Join("; ", reasons) : null);
    }

    private static bool RegisteredKeyMatchesPath(string key, string path)
    {
        try
        {
            return string.Equals(SquadPhysicalRootIdentity.Resolve(path).Key, key, StringComparison.Ordinal);
        }
        catch (SquadDeploymentConflictException)
        {
            return false;
        }
    }

    private string RegistryPath() =>
        Path.Combine(ResolveHomeStateDirectory(), RegistryFileName);

    /// <summary>
    /// Reads the registry. A missing file is an empty registry, because the registry is rebuildable
    /// from per-project state. A file that exists but does not parse is an error the operator must see.
    /// </summary>
    private Dictionary<string, RegistryRecord> ReadRegistry()
    {
        Dictionary<string, RegistryRecord> registry = new Dictionary<string, RegistryRecord>(StringComparer.Ordinal);
        string path = RegistryPath();
        if (!File.Exists(path))
            return registry;

        try
        {
            JsonNode document = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))
                ?? throw new InvalidDataException("Squad registry is empty.");
            if (document is not JsonObject root ||
                !string.Equals(root["schema"]?.GetValue<string>(), RegistrySchema, StringComparison.Ordinal) ||
                root[RegistryProjectsProperty] is not JsonObject projects)
            {
                throw new InvalidDataException(
                    $"Squad registry must be an object on schema '{RegistrySchema}' with a projects map.");
            }

            foreach (KeyValuePair<string, JsonNode?> entry in projects)
            {
                if (entry.Value is not JsonObject project)
                    throw new InvalidDataException($"Squad registry entry '{entry.Key}' must be an object.");

                string registeredPath = project["path"]?.GetValue<string>()
                    ?? throw new InvalidDataException($"Squad registry entry '{entry.Key}' has no path.");
                DateTimeOffset lastUpdated = DateTimeOffset.Parse(
                    project["lastUpdatedUtc"]?.GetValue<string>()
                        ?? throw new InvalidDataException($"Squad registry entry '{entry.Key}' has no lastUpdatedUtc."),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                IReadOnlyList<string> targets = project["targets"] is JsonArray targetArray
                    ? targetArray.Select(node => node?.GetValue<string>() ?? throw new InvalidDataException(
                        $"Squad registry entry '{entry.Key}' has a null target.")).ToArray()
                    : throw new InvalidDataException($"Squad registry entry '{entry.Key}' has no targets array.");
                registry[entry.Key] = new RegistryRecord(registeredPath, lastUpdated, targets);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Squad registry is not valid JSON: {exception.Message}", exception);
        }

        return registry;
    }

    /// <summary>
    /// Writes the registry to a temporary file beside it and renames that file into place, so a
    /// reader sees either the prior registry or the new one, never a partial file.
    /// </summary>
    private void WriteRegistry(IReadOnlyDictionary<string, RegistryRecord> registry)
    {
        string squadDirectory = ResolveHomeStateDirectory();
        Directory.CreateDirectory(squadDirectory);

        JsonObject projects = new JsonObject();
        foreach (KeyValuePair<string, RegistryRecord> entry in registry.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            projects[entry.Key] = new JsonObject
            {
                ["path"] = entry.Value.Path,
                ["lastUpdatedUtc"] = entry.Value.LastUpdatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["targets"] = new JsonArray(entry.Value.Targets.Select(target => (JsonNode)JsonValue.Create(target)!).ToArray())
            };
        }

        JsonObject document = new JsonObject
        {
            ["schema"] = RegistrySchema,
            [RegistryProjectsProperty] = projects
        };
        string text = document.ToJsonString(JsonOptions) + "\n";

        string temporaryPath = Path.Combine(squadDirectory, $"{RegistryFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: SquadTransaction.SyncToDisk);
            }

            File.Move(temporaryPath, Path.Combine(squadDirectory, RegistryFileName), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
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

        // Unlike the bundle and asset digests, this one is optional at the source: canonical
        // toolchain.yml pins no upstream build, so SquadApmIdentity.None is what a real
        // install produces. Requiring 64 hex here rejected every such install outright.
        // Unlike the bundle and asset digests, this one is optional at the source: canonical
        // toolchain.yml pins no upstream build, so SquadApmIdentity.None is what a real
        // install produces. Requiring 64 hex here rejected every such install outright.
        if (!string.Equals(squadLock.Apm.AssetSha256, SquadApmIdentity.Unverified, StringComparison.Ordinal))
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
        bool isKnownSchema =
            string.Equals(receipt.Schema, ReceiptSchemaV1, StringComparison.Ordinal) ||
            string.Equals(receipt.Schema, ReceiptSchemaV2, StringComparison.Ordinal);
        if (!isKnownSchema)
        {
            throw new InvalidDataException(
                $"Squad receipt schema must be '{ReceiptSchemaV1}' or '{ReceiptSchemaV2}'.");
        }

        // A receipt records the canonical absolute physical root it was written against. The
        // relative "." marker is still accepted: every receipt written before the home-level state
        // carries it, and rejecting it would strand those projects on upgrade.
        if (!string.Equals(receipt.TargetRoot, UnanchoredTargetRoot, StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(receipt.TargetRoot))
        {
            throw new InvalidDataException(
                "Squad receipt target root must be the canonical absolute physical root, " +
                "or the legacy relative marker '.'.");
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

        // Uniqueness is scoped the way the deployment engine scopes physical files. Project
        // scope writes every target beneath the one deployment root, so two targets claiming
        // one path would claim one physical file and the path alone must be unique. Global
        // scope writes beneath each target's own root, so the same relative path legitimately
        // appears once per target and only (target, path) must be unique — mirroring
        // SquadDeploymentPlan's deployment identity.
        HashSet<string> deployedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                !string.Equals(portableIdentity, normalizedPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Squad receipt file path '{file.RelativePath}' is not a portable canonical path.");
            }

            ValidateDigest(file.Sha256, "file digest");
            ValidateCanonicalTarget(file.Target, "file target");

            string deploymentIdentity = receipt.Scope == SquadDeploymentScope.Project
                ? portableIdentity
                : SquadDeploymentPlan.DeployedFileIdentity(file.Target, portableIdentity);
            if (!deployedIdentities.Add(deploymentIdentity))
            {
                throw new InvalidDataException(
                    $"Squad receipt file path '{file.RelativePath}' for target '{file.Target}' " +
                    "is not a unique portable deployment path.");
            }
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

    /// <summary>
    /// Validates the receipt's raw JSON field shape, before any object binding: schema v1 keeps
    /// today's exact field set; schema v2 is that same set with <c>scope</c> made optional
    /// (present only if the receipt was hand-authored to include it — always Global, per A3)
    /// and a required <c>layout</c> added, restricted to its two canonical tokens.
    /// </summary>
    private static void ValidateReceiptJsonShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Squad receipt must be an object.");

        string? schema = root.TryGetProperty("schema", out JsonElement schemaElement) &&
            schemaElement.ValueKind == JsonValueKind.String
            ? schemaElement.GetString()
            : null;

        if (string.Equals(schema, ReceiptSchemaV2, StringComparison.Ordinal))
        {
            ValidateReceiptJsonShapeV2(root);
        }
        else
        {
            ValidateReceiptJsonShapeV1(root);
        }

        JsonElement degradations = root.GetProperty("degradations");
        if (degradations.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad receipt degradations must be an array.");
        foreach (JsonElement degradation in degradations.EnumerateArray())
        {
            RequireExactJsonFields(
                degradation,
                ["target", "subject", "code"],
                "receipt degradation");
        }

        JsonElement files = root.GetProperty("files");
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

    private static void ValidateReceiptJsonShapeV1(JsonElement root)
    {
        RequireExactJsonFields(
            root,
            ["schema", "scope", "targetRoot", "installedAtUtc", "degradations", "files"],
            "receipt");
        RequireCanonicalJsonEnum(root, "scope", ["project", "global"], "receipt");
    }

    private static void ValidateReceiptJsonShapeV2(JsonElement root)
    {
        RequireExactJsonFields(
            root,
            ["schema", "layout", "targetRoot", "installedAtUtc", "degradations", "files"],
            "receipt",
            optional: ["scope"]);

        JsonElement layout = root.GetProperty("layout");
        string? layoutToken = layout.ValueKind == JsonValueKind.String ? layout.GetString() : null;
        if (layoutToken is not ("single-root" or "per-target-roots"))
        {
            throw new InvalidDataException(
                "Squad receipt field 'layout' must be 'single-root' or 'per-target-roots', " +
                $"but found '{layoutToken ?? "null"}'.");
        }

        if (root.TryGetProperty("scope", out JsonElement scopeElement))
        {
            string? scopeToken = scopeElement.ValueKind == JsonValueKind.String ? scopeElement.GetString() : null;
            if (!string.Equals(scopeToken, "global", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Squad receipt schema '{ReceiptSchemaV2}' is for global scope only, but found " +
                    $"scope '{scopeToken ?? "null"}'. Write a project receipt on schema " +
                    $"'{ReceiptSchemaV1}' instead.");
            }
        }
    }

    private static void RequireExactJsonFields(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string subject,
        IReadOnlyCollection<string>? optional = null)
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

        HashSet<string> allowed = new HashSet<string>(expected, StringComparer.Ordinal);
        if (optional is not null)
            allowed.UnionWith(optional);

        string[] missing = expected.Where(field => !actual.Contains(field)).ToArray();
        string[] unknown = actual.Where(field => !allowed.Contains(field)).ToArray();
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
