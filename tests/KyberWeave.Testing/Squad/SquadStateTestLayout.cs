using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// Where the Squad state store and migration keep and find their files, and the receipts and locks a test
/// writes there, over any file system the store itself was built over.
/// </summary>
/// <remarks>
/// Receipts and locks are written through the store's own serializers, so the fixtures carry the shapes the
/// reader accepts. A legacy receipt carries the <c>"."</c> target root the store writes before it anchors one.
/// </remarks>
public sealed class SquadStateTestLayout
{
    /// <summary>The instant every receipt a test writes records as its install time.</summary>
    public static readonly DateTimeOffset InstalledAt = new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    private readonly IWritableFileSystem _files;
    private readonly IFileSystemPathSemantics _semantics;
    private readonly ISquadStateStore _store;

    /// <summary>Creates the layout over a store and the file system it reads.</summary>
    /// <param name="store">The store whose receipts and locks the layout serializes.</param>
    /// <param name="files">The file system the setup writes through, which must not count against a fault plan.</param>
    /// <param name="semantics">The path semantics the store resolves roots with.</param>
    public SquadStateTestLayout(ISquadStateStore store, IWritableFileSystem files, IFileSystemPathSemantics semantics)
    {
        _store = store;
        _files = files;
        _semantics = semantics;
    }

    /// <summary>Gets the user's home directory.</summary>
    public string Home => _store.UserPaths.HomeDirectory;

    /// <summary>Gets the user's application-data directory, which held the legacy global state.</summary>
    public string ApplicationData => _store.UserPaths.ApplicationDataDirectory;

    /// <summary>Gets the canonical spelling of a path that exists.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The physical path.</returns>
    public string PhysicalOf(string path) => SquadPhysicalRootIdentity.Resolve(path, _semantics).PhysicalPath;

    /// <summary>Gets the key a legacy directory and a registry entry are named by.</summary>
    /// <param name="root">An existing root.</param>
    /// <returns>The root key.</returns>
    public string KeyOf(string root) => SquadPhysicalRootIdentity.Resolve(root, _semantics).Key;

    /// <summary>Gets the legacy per-user directory that held a root's global state.</summary>
    /// <param name="root">The root the legacy state was installed from.</param>
    /// <returns>The directory.</returns>
    public string LegacyGlobalDirectory(string root) =>
        Path.Combine(ApplicationData, "KyberWeave", "squad", "roots", KeyOf(root));

    /// <summary>Gets the home-level global state directory.</summary>
    /// <returns>The directory.</returns>
    public string HomeGlobalDirectory() => Path.Combine(PhysicalOf(Home), ".kyber-weave", "squad", "global");

    /// <summary>Gets the home-level state directory of one project.</summary>
    /// <param name="root">The project root.</param>
    /// <returns>The directory.</returns>
    public string HomeProjectDirectory(string root) =>
        Path.Combine(PhysicalOf(Home), ".kyber-weave", "squad", "projects", KeyOf(root));

    /// <summary>Gets the migration ledger path.</summary>
    /// <returns>The file.</returns>
    public string LedgerPath() => Path.Combine(PhysicalOf(Home), ".kyber-weave", "squad", "migration-ledger.json");

    /// <summary>Gets the registry path.</summary>
    /// <returns>The file.</returns>
    public string RegistryPath() => Path.Combine(PhysicalOf(Home), ".kyber-weave", "squad", "registry.json");

    /// <summary>Writes the legacy global receipt and lock for a root.</summary>
    /// <param name="root">The root the legacy state was installed from.</param>
    /// <param name="receipt">The receipt.</param>
    /// <param name="squadLock">The lock.</param>
    public void WriteLegacyGlobal(string root, SquadReceipt receipt, SquadLock squadLock)
    {
        string directory = LegacyGlobalDirectory(root);
        WriteText(Path.Combine(directory, "squad.receipt.json"), _store.SerializeReceipt(receipt));
        WriteText(Path.Combine(directory, "squad.lock.yml"), _store.SerializeLock(squadLock));
    }

    /// <summary>
    /// Writes the in-repo project state a pre-home install left behind: the receipt and lock under
    /// <c>.kyber-weave/</c>, and the one harness file the receipt owns.
    /// </summary>
    /// <param name="repo">The project root.</param>
    /// <param name="squadLock">The lock.</param>
    public void WriteInRepoState(string repo, SquadLock squadLock)
    {
        WriteText(Path.Combine(repo, ".kyber-weave", "squad.lock.yml"), _store.SerializeLock(squadLock));
        WriteText(
            Path.Combine(repo, ".kyber-weave", "squad.receipt.json"),
            _store.SerializeReceipt(ProjectReceipt(OwnedFile(".codex/agents/alpha.toml", "alpha"))));
        WriteText(Path.Combine(repo, ".codex", "agents", "alpha.toml"), "alpha");
    }

    /// <summary>Writes a text file, creating its directory.</summary>
    /// <param name="path">The file.</param>
    /// <param name="content">The text.</param>
    public void WriteText(string path, string content)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    /// <summary>Reads a receipt's target root as raw JSON.</summary>
    /// <param name="receiptPath">The receipt file.</param>
    /// <returns>The recorded target root.</returns>
    public string ReadTargetRoot(string receiptPath) =>
        JsonNode.Parse(_files.ReadAllText(receiptPath))!.AsObject()["targetRoot"]!.GetValue<string>();

    /// <summary>A global receipt whose owned files use bare, per-target paths.</summary>
    /// <returns>The receipt.</returns>
    public static SquadReceipt PerTargetRootsReceipt() =>
        GlobalReceipt(SquadReceiptLayout.PerTargetRoots, OwnedFile("agents/alpha.toml", "alpha"));

    /// <summary>A global receipt a pre-#91 single-root install recorded.</summary>
    /// <returns>The receipt.</returns>
    public static SquadReceipt SingleRootReceipt() =>
        GlobalReceipt(SquadReceiptLayout.SingleRoot, OwnedFile(".codex/agents/alpha.toml", "alpha"));

    /// <summary>A global receipt.</summary>
    /// <param name="layout">How the owned files are laid out.</param>
    /// <param name="files">The owned files.</param>
    /// <returns>The receipt.</returns>
    public static SquadReceipt GlobalReceipt(SquadReceiptLayout layout, params SquadOwnedFile[] files) =>
        new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            files)
        {
            Layout = layout
        };

    /// <summary>A project receipt.</summary>
    /// <param name="files">The owned files.</param>
    /// <returns>The receipt.</returns>
    public static SquadReceipt ProjectReceipt(params SquadOwnedFile[] files) =>
        new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            files);

    /// <summary>One file a receipt owns.</summary>
    /// <param name="relativePath">The file's path under its target root.</param>
    /// <param name="content">The content the digest is taken over.</param>
    /// <returns>The owned file.</returns>
    public static SquadOwnedFile OwnedFile(string relativePath, string content) =>
        new(relativePath, Digest(content), "codex", Adopted: false);

    /// <summary>A lock.</summary>
    /// <param name="version">The version every version field records.</param>
    /// <returns>The lock.</returns>
    public static SquadLock Lock(string version = "1.2.3") =>
        new(
            SquadStateSchemas.LockSchemaV1,
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

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
