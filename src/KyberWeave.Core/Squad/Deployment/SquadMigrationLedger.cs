using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// The record of which legacy sources a home has already migrated or imported, so that a one-shot migration stays
/// one-shot after the home state it created is uninstalled.
/// </summary>
/// <remarks>
/// The migration keeps every legacy file for an older CLI, so those files cannot say whether a migration has already
/// run. Once the home copy is uninstalled, the next command would copy the legacy state back. Each entry here is a
/// legacy global root key or a project root key whose copy or import has been written. A missing ledger is an empty
/// one. A ledger that does not parse, or does not match the schema, is read as empty; the caller reports it, and the
/// next successful write replaces it with a valid ledger.
/// </remarks>
internal sealed record SquadMigrationLedger(
    IReadOnlyList<string> GlobalMigratedFrom,
    IReadOnlyList<string> ProjectsImported)
{
    /// <summary>The schema identifier written into every ledger.</summary>
    internal const string Schema = "kyber-squad.migration-ledger/v1";

    /// <summary>The file name, beside the home registry.</summary>
    internal const string FileName = "migration-ledger.json";

    private const string GlobalProperty = "globalMigratedFrom";
    private const string ProjectsProperty = "projectsImported";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n"
    };

    /// <summary>A ledger that records nothing.</summary>
    internal static SquadMigrationLedger Empty { get; } = new([], []);

    /// <summary>Whether the legacy global root with this key has already been copied into the home.</summary>
    internal bool HasGlobalMigration(string legacyKey) =>
        GlobalMigratedFrom.Contains(legacyKey, StringComparer.Ordinal);

    /// <summary>Whether the in-repo state of the project with this root key has already been imported.</summary>
    internal bool HasProjectImport(string rootKey) =>
        ProjectsImported.Contains(rootKey, StringComparer.Ordinal);

    /// <summary>Returns this ledger with one more global migration recorded.</summary>
    internal SquadMigrationLedger WithGlobalMigration(string legacyKey) =>
        this with { GlobalMigratedFrom = Record(GlobalMigratedFrom, legacyKey) };

    /// <summary>Returns this ledger with one more project import recorded.</summary>
    internal SquadMigrationLedger WithProjectImport(string rootKey) =>
        this with { ProjectsImported = Record(ProjectsImported, rootKey) };

    /// <summary>
    /// Reads the ledger at <paramref name="path"/>. A missing file is an empty ledger. A file that cannot be read as a
    /// ledger is an empty ledger, and <paramref name="unreadableReason"/> says why.
    /// </summary>
    internal static SquadMigrationLedger Read(string path, out string? unreadableReason)
    {
        unreadableReason = null;
        if (!File.Exists(path))
            return Empty;

        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or InvalidOperationException or FormatException
                or UnauthorizedAccessException)
        {
            // An access denial is the same outcome as a parse failure here: the ledger is unusable, so it counts as
            // empty and the caller reports the reason. Letting it escape would fail a command over a warning-level fault.
            unreadableReason = exception.Message;
            return Empty;
        }
    }

    /// <summary>The ledger as the bytes that are written. Keys are sorted, so one record always writes one file.</summary>
    internal string Serialize()
    {
        JsonObject document = new()
        {
            ["schema"] = Schema,
            [GlobalProperty] = ToJsonArray(GlobalMigratedFrom),
            [ProjectsProperty] = ToJsonArray(ProjectsImported)
        };
        return document.ToJsonString(WriteOptions) + "\n";
    }

    private static SquadMigrationLedger Parse(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject root ||
            !string.Equals(root["schema"]?.GetValue<string>(), Schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The ledger must be an object on schema '{Schema}'.");
        }

        return new SquadMigrationLedger(ReadKeys(root, GlobalProperty), ReadKeys(root, ProjectsProperty));
    }

    private static IReadOnlyList<string> ReadKeys(JsonObject root, string property)
    {
        if (root[property] is not JsonArray entries)
            throw new InvalidDataException($"The ledger's '{property}' must be an array.");

        return entries
            .Select(entry => entry?.GetValue<string>()
                ?? throw new InvalidDataException($"The ledger's '{property}' holds a null entry."))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> Record(IReadOnlyList<string> keys, string key) =>
        keys.Contains(key, StringComparer.Ordinal)
            ? keys
            : keys.Append(key).Order(StringComparer.Ordinal).ToArray();

    private static JsonArray ToJsonArray(IEnumerable<string> keys) =>
        new(keys.Select(key => (JsonNode)JsonValue.Create(key)!).ToArray());
}
