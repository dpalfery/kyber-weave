using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>What the repair did to one target file.</summary>
public enum McpWiringChangeOutcome
{
    /// <summary>The file was absent and now holds the declared servers' entries.</summary>
    Created,

    /// <summary>Entries were inserted into a file that already existed.</summary>
    Inserted,

    /// <summary>Removal restored the file's pre-write bytes.</summary>
    Restored,

    /// <summary>Removal deleted a file the repair had created.</summary>
    Deleted,

    /// <summary>Nothing was written to the target, and <see cref="McpWiringFileChange.Code"/> says why.</summary>
    Refused
}

/// <summary>What one target of a repair or a removal came to.</summary>
/// <param name="Harness">The harness slug the change is about.</param>
/// <param name="Path">
/// The repository-relative file, or null when the harness has no file in the tree to write.
/// </param>
/// <param name="Outcome">What happened.</param>
/// <param name="Servers">The server names the change concerns.</param>
/// <param name="Code">The stable <c>KW-*</c> rule id, on a refusal.</param>
/// <param name="Message">One sentence describing the change or the refusal.</param>
/// <param name="Hint">The remedy, as the command or edit to make.</param>
/// <param name="Written">Whether bytes reached the target. Always false in a dry run.</param>
/// <param name="Snippet">For a harness the repair does not write: what to paste instead.</param>
public sealed record McpWiringFileChange(
    string Harness,
    string? Path,
    McpWiringChangeOutcome Outcome,
    IReadOnlyList<string> Servers,
    string? Code = null,
    string? Message = null,
    string? Hint = null,
    bool Written = false,
    string? Snippet = null);

/// <summary>Every target's outcome, in the order the targets were requested.</summary>
/// <param name="Files">One change per target.</param>
/// <param name="DryRun">Whether the report describes a run that wrote nothing.</param>
public sealed record McpWiringRepairReport(IReadOnlyList<McpWiringFileChange> Files, bool DryRun);

/// <summary>
/// Writes the declared MCP servers into the harnesses an operator names, and takes them out again.
/// </summary>
/// <remarks>
/// <para>
/// Three properties bound what this may do, and each one is a rule rather than a convenience.
/// <b>Selection is consent</b>: a named target is written even when no marker suggests the harness
/// is in use, because for the harnesses whose only repository-scope artefact is their configuration
/// file, "not in play" and "file absent" are the same condition — and the operator named it (D19,
/// 5.4). <b>Insertion is not re-serialization</b>: an existing file only ever gains bytes, through
/// <see cref="JsonServerSplice"/>, so comments, key order and formatting survive outside the insert
/// (5.6). <b>Refusal means zero bytes</b>: the whole plan is computed before anything is written, so
/// a name that is already taken, a document that cannot be read, a harness the repair does not
/// write, or a launch binary that does not resolve leaves the target exactly as it was found
/// (5.5–5.7, 6.2).
/// </para>
/// <para>
/// The entry body is composed from what the host declared — the server's name and argv — inside the
/// shape the catalog records for the harness, rather than copied from the catalog's
/// <c>kyber-weave</c> example. A copied example would wire a server nobody declared, and the check
/// would then report it as <c>KW-MCP-WIRE-002</c> (5.11, R6.2).
/// </para>
/// <para>
/// A binary that does not resolve refuses that server everywhere (6.2), because an entry whose
/// launch command cannot work turns a diagnosable absence into a harness-side startup failure that
/// blames the configuration file. Where the absent binary is Kyber-Weave's own, the refusal says the
/// installation itself is broken and names the install repair, not the harness configuration (6.3).
/// </para>
/// </remarks>
public static class McpWiringRepairer
{
    /// <summary>The repair refused a target and wrote nothing to it.</summary>
    public const string RefusalCode = "KW-MCP-WIRE-005";

    /// <summary>The install repair for this tool's own binary, quoted rather than explained.</summary>
    private const string InstallRepairCommand = "kyber-weave update";

    /// <summary>
    /// The repository-root spelling for a harness that substitutes no workspace variable. Codex is
    /// the only such harness, and its snippet is printed rather than written (DD11).
    /// </summary>
    private const string AbsoluteRepositoryPath = "/path/to/your/repo";

    private static readonly JsonDocumentOptions LenientReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Writes the declared servers into the named harnesses' repository-scope configuration.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose files are written.</param>
    /// <param name="config">The servers the repository declares.</param>
    /// <param name="resolver">Resolves a launch command to a file, without launching it.</param>
    /// <param name="targets">The harness slugs to repair. Naming a harness is the consent to write it.</param>
    /// <param name="dryRun">
    /// When true the same computation runs and the same change set is reported, but no byte is
    /// written — not even the receipt (5.10).
    /// </param>
    /// <remarks>
    /// A refused target does not stop another: the design's error table has "other targets proceed",
    /// and one unlucky file in a repository is no reason to leave the rest unwired.
    /// </remarks>
    public static McpWiringRepairReport Repair(
        string repositoryRoot,
        McpConfig config,
        LaunchCommandResolver resolver,
        IReadOnlyList<string> targets,
        bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(targets);

        IReadOnlyList<RequiredMcpServer> declared = Declared(config);

        // No declaration, no write: there is nothing to wire, and inventing a server is exactly what
        // this feature refuses to do.
        if (declared.Count == 0)
            return new McpWiringRepairReport([], dryRun);

        string root = Path.GetFullPath(repositoryRoot);
        List<McpWiringReceiptRecord> records = [.. McpWiringReceipt.Load(root).Files];
        List<McpWiringFileChange> changes = [];
        bool recorded = false;

        foreach (string target in targets)
        {
            TargetResult result = RepairTarget(root, declared, resolver, target, dryRun);
            changes.Add(result.Change);

            if (result.Record is null)
                continue;

            records.RemoveAll(record => McpWiringReceipt.SamePath(record.Path, result.Record.Path));
            records.Add(result.Record);
            recorded = true;
        }

        if (!dryRun && recorded)
            changes = ReportReceiptFailure(changes, McpWiringReceipt.Save(root, records));

        return new McpWiringRepairReport(changes, dryRun);
    }

    /// <summary>
    /// Removes what the receipt says the repair wrote, restoring an inserted file's bytes and
    /// deleting a created one.
    /// </summary>
    /// <param name="repositoryRoot">The repository whose files are restored.</param>
    /// <param name="targets">The harness slugs to remove.</param>
    /// <remarks>
    /// Removal proves its case before it touches anything: no record, a file that is gone, or a
    /// current digest that differs from the recorded post-write digest is drift, and drift is
    /// refused rather than restored over (5.9). There is no dry run here — the operation either
    /// restores bytes it can prove, or it does nothing.
    /// </remarks>
    public static McpWiringRepairReport Unwire(string repositoryRoot, IReadOnlyList<string> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(targets);

        string root = Path.GetFullPath(repositoryRoot);
        List<McpWiringReceiptRecord> records = [.. McpWiringReceipt.Load(root).Files];
        List<McpWiringFileChange> changes = [];
        bool changed = false;

        foreach (string target in targets)
        {
            (McpWiringFileChange change, bool removed) = UnwireTarget(root, records, target);
            changes.Add(change);
            changed |= removed;
        }

        if (changed)
            changes = ReportReceiptFailure(changes, McpWiringReceipt.Save(root, records));

        return new McpWiringRepairReport(changes, DryRun: false);
    }

    private static (McpWiringFileChange Change, bool Removed) UnwireTarget(
        string root,
        List<McpWiringReceiptRecord> records,
        string harness)
    {
        (HarnessMcpEntry? row, string? path, McpWiringFileChange? refusal) = Resolve(harness);

        if (refusal is not null)
        {
            // A harness this tool does not know is a usage error (005). A harness that has no file
            // in the tree simply has no record to remove, which is the drift refusal (006): removal
            // is only ever refused for want of proof, and "no file" is the largest want of proof.
            return (refusal.Code == RefusalCode && row is not null
                ? refusal with
                {
                    Code = McpWiringReceipt.DriftCode,
                    Message = $"{harness} has no receipt record, because the repair can never write it: "
                        + "its MCP servers are repository settings rather than a file in the tree",
                    Snippet = null
                }
                : refusal,
                false);
        }

        if (row is null || path is null)
            return (NoTargetRefusal(harness), false);

        McpWiringReceiptRecord? record = records.Find(
            candidate => McpWiringReceipt.SamePath(candidate.Path, path));

        if (ContainedPath(root, path) is not { } fullPath)
        {
            return (Refused(
                harness,
                path,
                record?.Servers ?? [],
                McpWiringReceipt.DriftCode,
                $"{path} does not resolve inside the repository, so nothing was removed: a record has to name a file "
                    + "in the tree this command was given",
                $"Report this: {McpWiringReceipt.RelativePath} names '{record?.Path ?? path}', and a path that leaves "
                    + "the repository cannot be restored from.").Change,
                false);
        }

        // Removal restores the bytes the receipt captured, so through a link it would write those
        // bytes into whichever file the link names — outside the repository, and over a file this
        // repository does not own (2.6, 5.2). The guard comes before the record lookup and the
        // digest read, because it is the more specific fact: an operator whose file is a link needs
        // to hear that, not "there is no record" or "restore it from version control" — advice that
        // would have them editing the linked file by hand.
        McpLinkProbe probe = McpConfigurationLink.Probe(root, fullPath);

        if (probe.Obstruction == McpLinkObstruction.Denied)
        {
            return (Refused(
                harness,
                path,
                record?.Servers ?? [],
                McpWiringReceipt.DriftCode,
                $"{McpConfigurationLink.Describe(path, probe)}, so nothing was removed: restoring recorded bytes "
                    + "over a file this process cannot read would be a guess",
                $"Check the permissions on {path} and on every directory above it, then run the removal again; the "
                    + "receipt still records the write.").Change,
                false);
        }

        if (probe.Obstruction == McpLinkObstruction.Link)
        {
            return (Refused(
                harness,
                path,
                record?.Servers ?? [],
                McpWiringReceipt.DriftCode,
                $"{McpConfigurationLink.Describe(path, probe)}, so nothing was removed: restoring the recorded "
                    + "bytes through it would write outside the repository",
                $"Replace {path} with a real file inside the repository — copy its content out of the linked file, "
                    + "delete the link, and leave the copy in place — then run the removal again. Nothing was written.").Change,
                false);
        }

        if (record is null)
        {
            return (Refused(
                harness,
                path,
                [],
                McpWiringReceipt.DriftCode,
                $"no receipt record for {path}: Kyber-Weave cannot prove it wrote there, so nothing was removed",
                $"The receipt at {McpWiringReceipt.RelativePath} records what the repair wrote. Remove the entry from "
                    + $"{path} by hand, or with git revert, and leave the rest of the file intact.").Change,
                false);
        }

        // The read answers for the file's existence as well as its content: File.Exists reports a file
        // this process may not reach as absent, and "gone" is a different refusal from "unreadable".
        byte[] current;

        try
        {
            current = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return (MissingOnRemoval(harness, path, record), false);
        }
        catch (IOException exception)
        {
            return (UnreadableOnRemoval(harness, path, record, exception.Message), false);
        }
        catch (UnauthorizedAccessException exception)
        {
            return (UnreadableOnRemoval(harness, path, record, exception.Message), false);
        }

        string digest = Digest(current);

        if (!string.Equals(digest, record.PostSha256, StringComparison.OrdinalIgnoreCase))
        {
            return (Refused(
                harness,
                path,
                record.Servers,
                McpWiringReceipt.DriftCode,
                $"{path} changed after the repair, so restoring over the change would discard someone else's edit",
                $"The receipt recorded {record.PostSha256} and the file now digests {digest}. Decide which version to "
                    + "keep by hand; the receipt still records what was written.").Change,
                false);
        }

        bool restored = record.Mode == McpWiringWriteMode.Inserted;
        byte[]? preWrite = restored ? PreWriteContent(record) : null;

        if (restored && preWrite is null)
        {
            return (Refused(
                harness,
                path,
                record.Servers,
                McpWiringReceipt.DriftCode,
                $"the receipt records no usable pre-write content for {path}, so the file cannot be restored",
                $"Remove the entry from {path} by hand, or restore the file with git revert.").Change,
                false);
        }

        try
        {
            if (preWrite is null)
                File.Delete(fullPath);
            else
                AtomicFile.WriteAllBytes(fullPath, preWrite);
        }
        catch (IOException exception)
        {
            return (NotRemovable(harness, path, record, exception.Message), false);
        }
        catch (UnauthorizedAccessException exception)
        {
            return (NotRemovable(harness, path, record, exception.Message), false);
        }

        records.RemoveAll(candidate => McpWiringReceipt.SamePath(candidate.Path, path));

        return (new McpWiringFileChange(
            harness,
            path,
            restored ? McpWiringChangeOutcome.Restored : McpWiringChangeOutcome.Deleted,
            record.Servers,
            Message: restored
                ? $"{path} was restored to the bytes the repair found there"
                : $"{path} was deleted, because the repair created it",
            Written: true),
            true);
    }

    private static TargetResult RepairTarget(
        string root,
        IReadOnlyList<RequiredMcpServer> declared,
        LaunchCommandResolver resolver,
        string harness,
        bool dryRun)
    {
        (HarnessMcpEntry? row, string? path, McpWiringFileChange? refusal) = Resolve(harness);

        if (refusal is not null)
            return new TargetResult(refusal, null);

        if (row is null || path is null)
            return new TargetResult(NoTargetRefusal(harness), null);

        if (row.Fixability != McpWiringFixability.Fixable)
        {
            return Refused(
                harness,
                path,
                [],
                RefusalCode,
                $"Kyber-Weave does not write {harness} configuration: {NotWritableReason(row)}, so nothing was written to {path}",
                NotWritableHint(row, path),
                Snippet(row, declared));
        }

        if (ContainedPath(root, path) is not { } fullPath)
        {
            return Refused(
                harness,
                path,
                [],
                RefusalCode,
                $"{path} does not resolve inside the repository, so nothing was written: a repository-scope file has "
                    + "to name a path in the tree this command was given",
                $"Report this: the harness catalog names '{path}' for {harness}, and a path that leaves the repository "
                    + "cannot be wired.");
        }

        // A link in the tree makes this repository-scope path name a file somebody else owns, and
        // reading or writing through it leaves the repository entirely (2.6, 5.2, ADR 0051). The
        // refusal comes before the read so that not one byte of the target is opened. The same
        // position answers the denial case, which is the other way this path can prove nothing about
        // the file it names.
        McpLinkProbe probe = McpConfigurationLink.Probe(root, fullPath);

        if (probe.Obstruction == McpLinkObstruction.Denied)
        {
            return Refused(
                harness,
                path,
                [],
                RefusalCode,
                $"{McpConfigurationLink.Describe(path, probe)}, so nothing was read or written: a file this process "
                    + "cannot inspect cannot be told from one that is absent",
                $"Check the permissions on {path} and on every directory above it, then run the wire command again. "
                    + "Kyber-Weave never creates a file over one it could not read.");
        }

        if (probe.Obstruction == McpLinkObstruction.Link)
        {
            return Refused(
                harness,
                path,
                [],
                RefusalCode,
                $"{McpConfigurationLink.Describe(path, probe)}, so nothing was read or written: a repository-scope "
                    + "configuration cannot be a link, because the bytes would land in whichever file it names",
                $"Replace {path} with a real file inside the repository — copy its content out of the linked file, "
                    + "delete the link, and leave the copy in place — then run the wire command again. Kyber-Weave "
                    + "writes repository-scope files only, and a write through a link leaves the repository.");
        }

        // The read answers for the file's existence as well as its content: a pre-check by
        // File.Exists cannot tell a denied file from an absent one, and "absent" is what selects the
        // create path below.
        byte[]? original;

        try
        {
            original = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            original = null;
        }
        catch (IOException exception)
        {
            return Refused(
                harness,
                path,
                [],
                RefusalCode,
                $"{path} could not be read: {exception.Message}",
                $"Check that {path} is a readable file, then run the wire command again.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Refused(
                harness,
                path,
                [],
                RefusalCode,
                $"{path} could not be read: {exception.Message}",
                $"Check the permissions on {path}, then run the wire command again.");
        }

        if (original is not null)
        {
            // The document has to be one this harness could load before anything is planned: a
            // splice into a foreign shape produces a file that harness silently ignores (5.7).
            string? unwritable = DescribeUnwritable(original, row.ServerKeyPath);

            if (unwritable is not null)
            {
                return Refused(
                    harness,
                    path,
                    [],
                    RefusalCode,
                    $"{path} cannot be wired: {unwritable}",
                    $"Fix {path} so it holds a JSON object with the server map at "
                        + $"'{string.Join('.', row.ServerKeyPath)}', or restore it from version control; "
                        + "the repair never rewrites a document it could not read.");
            }

            foreach (RequiredMcpServer server in declared)
            {
                if (!HoldsServer(original, row.ServerKeyPath, server.Name))
                    continue;

                return Refused(
                    harness,
                    path,
                    [server.Name],
                    RefusalCode,
                    $"{path} already holds an entry named '{server.Name}', which the repair never modifies, merges, or overwrites",
                    $"Remove the existing '{server.Name}' entry from {path}, or edit its launch command by hand, and "
                        + "run the wire command again. Nothing was written.");
            }
        }

        List<RequiredMcpServer> unresolvable = [.. declared.Where(server => !Resolves(resolver, server))];
        List<RequiredMcpServer> writable = [.. declared.Where(server => Resolves(resolver, server))];

        if (writable.Count == 0)
        {
            return Refused(
                harness,
                path,
                [.. unresolvable.Select(server => server.Name)],
                RefusalCode,
                BinaryAbsenceMessage(unresolvable, path),
                BinaryAbsenceHint(unresolvable));
        }

        string[] written = [.. writable.Select(server => server.Name)];
        bool created = original is null;
        byte[] planned;

        try
        {
            planned = original is null
                ? CreatedDocument(row, writable)
                : InsertedDocument(original, row, writable);
        }
        catch (JsonException exception)
        {
            return Refused(
                harness,
                path,
                written,
                RefusalCode,
                $"{path} could not be wired: {exception.Message}",
                $"Nothing was written. If the entry could not be built, report this: the catalog's template for "
                    + $"{harness} is not the shape this writer needs.");
        }

        if (dryRun)
        {
            return new TargetResult(
                new McpWiringFileChange(
                    harness,
                    path,
                    created ? McpWiringChangeOutcome.Created : McpWiringChangeOutcome.Inserted,
                    written,
                    Message: SkippedNote(unresolvable, path)),
                null);
        }

        try
        {
            AtomicFile.WriteAllBytes(fullPath, planned);
        }
        catch (IOException exception)
        {
            return Refused(
                harness,
                path,
                written,
                RefusalCode,
                $"{path} could not be written: {exception.Message}",
                $"Kyber-Weave writes repository-scope files only; check that {path} is a writable file path rather "
                    + "than a directory, then run the wire command again.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Refused(
                harness,
                path,
                written,
                RefusalCode,
                $"{path} could not be written: {exception.Message}",
                $"Check the permissions on {path} and its directory, then run the wire command again.");
        }

        byte[] preWrite = original ?? [];
        McpWiringReceiptRecord record = new(
            path,
            created ? McpWiringWriteMode.Created : McpWiringWriteMode.Inserted,
            written,
            created ? null : Digest(preWrite),
            Digest(planned),
            DateTimeOffset.UtcNow,
            created ? null : Convert.ToBase64String(preWrite));

        return new TargetResult(
            new McpWiringFileChange(
                harness,
                path,
                created ? McpWiringChangeOutcome.Created : McpWiringChangeOutcome.Inserted,
                written,
                Message: SkippedNote(unresolvable, path),
                Written: true),
            record);
    }

    /// <summary>
    /// Resolves a target to its catalog row and the file it names, or to the refusal that stops it
    /// before any file is considered.
    /// </summary>
    private static (HarnessMcpEntry? Row, string? Path, McpWiringFileChange? Refusal) Resolve(string harness)
    {
        if (string.IsNullOrWhiteSpace(harness))
            return (null, null, NoTargetRefusal(string.Empty));

        HarnessMcpEntry? row = HarnessMcpCatalog.Find(harness);

        if (row is null)
            return (null, null, NoTargetRefusal(harness));

        if (row.Shape == McpConfigurationShape.RepositorySettings || row.Paths.Count == 0)
        {
            return (row, null, Refused(
                harness,
                null,
                [],
                RefusalCode,
                $"{harness} has no repository-scope file: its MCP servers are repository settings, not a file in the tree",
                "Add the server in the repository's Copilot MCP configuration under 'mcpServers' (type and tools are "
                    + "required), and add the install step to .github/workflows/copilot-setup-steps.yml: the coding "
                    + "agent reads MCP configuration from repository settings only. The DocGraph MCP runbook records the form.").Change);
        }

        return (row, TargetPath(row), null);
    }

    private static McpWiringFileChange NoTargetRefusal(string harness)
    {
        string named = string.IsNullOrEmpty(harness)
            ? "no target was named"
            : $"'{harness}' is not a harness this tool knows";

        return Refused(
            harness,
            null,
            [],
            RefusalCode,
            named,
            "Targets are harness slugs from the catalog: "
                + string.Join(", ", HarnessMcpCatalog.Rows.Select(row => row.Harness))).Change;
    }

    /// <summary>
    /// The file to write. Every writable row names exactly one repository-scope file, which is also
    /// the file creation uses when none exists yet.
    /// </summary>
    private static string TargetPath(HarnessMcpEntry row)
    {
        foreach (string candidate in row.Paths)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        throw new InvalidOperationException($"{row.Harness} is writable but carries no configuration path");
    }

    /// <summary>
    /// Reads the document's structure and reports why it cannot be written into, or null when it can.
    /// </summary>
    private static string? DescribeUnwritable(byte[] document, IReadOnlyList<string> serverKeyPath)
    {
        using JsonDocument? parsed = ReadJson(document, out string? error);

        if (parsed is null)
            return $"it is not valid JSON ({error})";

        return TryFindContainer(parsed.RootElement, serverKeyPath, out _, out string? reason) ? null : reason;
    }

    private static bool HoldsServer(byte[] document, IReadOnlyList<string> serverKeyPath, string name)
    {
        using JsonDocument? parsed = ReadJson(document, out _);

        if (parsed is null)
            return false;

        return TryFindContainer(parsed.RootElement, serverKeyPath, out JsonElement container, out _)
            && container.ValueKind == JsonValueKind.Object
            && container.TryGetProperty(name, out _);
    }

    /// <summary>
    /// Walks the key path to the server map. A missing key is writable — the splice inserts the path
    /// — while a key that is present and is not an object is the structurally foreign refusal (5.7).
    /// </summary>
    private static bool TryFindContainer(
        JsonElement root,
        IReadOnlyList<string> serverKeyPath,
        out JsonElement container,
        out string? reason)
    {
        container = default;
        reason = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            reason = "its root is not a JSON object";
            return false;
        }

        JsonElement current = root;

        foreach (string key in serverKeyPath)
        {
            if (!current.TryGetProperty(key, out JsonElement next))
                return true;

            if (next.ValueKind != JsonValueKind.Object)
            {
                reason = $"its '{key}' key is not a JSON object";
                return false;
            }

            current = next;
        }

        container = current;
        return true;
    }

    private static JsonDocument? ReadJson(byte[] document, out string? error)
    {
        error = null;
        int bomLength = JsonServerSplice.ByteOrderMarkLength(document);

        try
        {
            return JsonDocument.Parse(document.AsMemory(bomLength), LenientReadOptions);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>Inserts one entry per declared server into an existing document.</summary>
    private static byte[] InsertedDocument(
        byte[] document,
        HarnessMcpEntry row,
        IReadOnlyList<RequiredMcpServer> servers)
    {
        byte[] planned = document;

        foreach (RequiredMcpServer server in servers)
        {
            JsonSplicePlan plan = JsonServerSplice.Plan(
                planned,
                row.ServerKeyPath,
                server.Name,
                ComposeEntry(row, server).ToJsonString());

            // A refusal here cannot follow the checks above unless the document changed under us, in
            // which case the caller must not write anything rather than write half a request.
            if (plan.Outcome != JsonSpliceOutcome.Inserted)
                throw new JsonException(plan.Message);

            planned = plan.Document;
        }

        return planned;
    }

    /// <summary>
    /// Builds the document a repair creates when the target file is absent: the catalog's canonical
    /// document — the runbook's example, including whatever skeleton the harness needs, such as
    /// opencode's <c>$schema</c> — with its example entry replaced by the servers actually declared,
    /// so a created file holds the declared servers and nothing else (5.4).
    /// </summary>
    private static byte[] CreatedDocument(HarnessMcpEntry row, IReadOnlyList<RequiredMcpServer> servers)
    {
        if (row.CanonicalDocument is null || JsonNode.Parse(row.CanonicalDocument) is not JsonObject root)
        {
            throw new JsonException(
                $"the catalog marks {row.Harness} writable but carries no usable canonical document for it");
        }

        JsonObject container = Container(root, row.ServerKeyPath);
        container.Clear();

        foreach (RequiredMcpServer server in servers)
            container[server.Name] = ComposeEntry(row, server);

        return Encoding.UTF8.GetBytes(JsonServerSplice.RenderDocument(root) + "\n");
    }

    /// <summary>
    /// The map the declared servers are written into, created along the key path where it is absent.
    /// </summary>
    /// <remarks>
    /// A key that holds something other than an object is a document this writer does not
    /// understand. Replacing the value would discard what the template — and so the operator whose
    /// file was created from it — put there, without a word, and produce a file that harness cannot
    /// load. The template is data, so the wrong shape stops the write and the refusal names the
    /// catalog; a JSON null is nothing rather than a value, and is treated as an absent key.
    /// </remarks>
    internal static JsonObject Container(JsonObject root, IReadOnlyList<string> serverKeyPath)
    {
        JsonObject current = root;

        foreach (string key in serverKeyPath)
        {
            if (current[key] is JsonObject existing)
            {
                current = existing;
                continue;
            }

            if (current[key] is { } unusable)
            {
                throw new JsonException(
                    $"its '{key}' key holds a {unusable.GetValueKind()} rather than the map of servers");
            }

            JsonObject created = new();
            current[key] = created;
            current = created;
        }

        return current;
    }

    /// <summary>
    /// Composes one server's entry in a harness's documented shape, from the declared name and argv.
    /// </summary>
    /// <remarks>
    /// The template supplies what the declaration cannot: which keys the harness reads
    /// (<c>type</c>, <c>enabled</c>), the name of its environment key (<c>env</c> for most,
    /// <c>environment</c> for opencode), and the harness's own spelling of the repository root — the
    /// value its environment block carries in the runbook's example. Everything that describes
    /// <em>this</em> server — its name and its argv — comes from the declaration, so a declared
    /// <c>codegraph</c> is wired as <c>codegraph</c> and not as the catalog's example.
    /// </remarks>
    private static JsonObject ComposeEntry(HarnessMcpEntry row, RequiredMcpServer server)
    {
        JsonObject template = TemplateEntry(row);
        string pathToken = row.RepositoryRootToken;
        JsonObject entry = new();

        foreach (KeyValuePair<string, JsonNode?> property in template)
        {
            entry[property.Key] = property.Key switch
            {
                "command" => Command(row, server, pathToken),
                "args" => Arguments(server.Command.Skip(1), pathToken),
                _ when HarnessMcpEntry.IsEnvironmentKey(property.Key) && property.Value is JsonObject environment
                    => Environment(environment, row.RepositoryRootToken, pathToken),
                _ => property.Value?.DeepClone()
            };
        }

        return entry;
    }

    /// <summary>
    /// The launch command as this harness spells it: split into <c>command</c> and <c>args</c> for
    /// every harness but opencode, which holds the executable and its arguments in one array (5.11).
    /// </summary>
    private static JsonNode? Command(HarnessMcpEntry row, RequiredMcpServer server, string pathToken) =>
        row.CommandShape == McpEntryCommandShape.SingleArray
            ? Arguments(server.Command, pathToken)
            : JsonValue.Create(server.Command[0]);

    private static JsonArray Arguments(IEnumerable<string> tokens, string pathToken)
    {
        JsonArray array = [];

        foreach (string token in tokens)
            array.Add(JsonValue.Create(Substitute(token, pathToken)));

        return array;
    }

    /// <summary>
    /// The entry's environment block, with the harness's own spelling of the repository root
    /// rewritten and every other value left as the template holds it.
    /// </summary>
    /// <remarks>
    /// An environment block describes the harness's world, not this tool's relation to the
    /// repository: a template carries <c>KYBER_WEAVE_REPO_ROOT</c> and may carry anything else the
    /// harness needs. Stamping the root into every entry collapsed such a template into copies of
    /// one value, so only the entries that hold the root token are rewritten — and an entry without
    /// it is the harness's to define.
    /// </remarks>
    internal static JsonObject Environment(JsonObject template, string rootToken, string pathToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathToken);

        JsonObject environment = new();

        foreach (KeyValuePair<string, JsonNode?> setting in template)
        {
            environment[setting.Key] = setting.Value is JsonValue value
                && value.TryGetValue(out string? text)
                && text.Contains(rootToken, StringComparison.Ordinal)
                    ? JsonValue.Create(text.Replace(rootToken, pathToken, StringComparison.Ordinal))
                    : setting.Value?.DeepClone();
        }

        return environment;
    }

    /// <summary>
    /// Rewrites a repository-root token into the harness's own spelling. The declaration writes the
    /// root as <c>.</c>, which every harness that substitutes nothing keeps and the ones that
    /// substitute a workspace variable replace; an absolute path is left alone, because substituting
    /// it would change what it means.
    /// </summary>
    private static string Substitute(string token, string pathToken) =>
        string.Equals(token, JsonServerSplice.RepositoryRootToken, StringComparison.Ordinal)
            ? pathToken
            : token;

    private static JsonObject TemplateEntry(HarnessMcpEntry row)
    {
        if (row.CanonicalEntry is null || JsonNode.Parse(row.CanonicalEntry) is not JsonObject template)
        {
            throw new JsonException(
                $"the catalog marks {row.Harness} writable but carries no usable entry template for it");
        }

        return template;
    }

    /// <summary>
    /// What to paste for a harness the repair does not write. A harness whose entry body is not
    /// pinned gets the exact place the entry goes and the command that launches, never an invented
    /// body (DD11, DD12).
    /// </summary>
    private static string? Snippet(HarnessMcpEntry row, IReadOnlyList<RequiredMcpServer> servers)
    {
        if (row.Paths.Count == 0 || servers.Count == 0)
            return null;

        List<string> snippets =
        [
            .. servers.Select(server => row.Shape == McpConfigurationShape.Toml
                ? TomlSnippet(row.ServerKeyPath, server)
                : JsonSnippet(row.ServerKeyPath, server.Name))
        ];

        return string.Join("\n\n", snippets);
    }

    /// <summary>
    /// The table header and body to paste for a harness with no writer, with every key spelled so the
    /// header cannot be broken by the name inside it.
    /// </summary>
    /// <remarks>
    /// The header is a dotted key path wrapped in brackets, which means the text between them has
    /// TOML's own syntax: a segment holding a dot would become a nested table, and one holding a
    /// bracket, a quote or a newline would close the header and inject whatever followed it into the
    /// file the operator pastes. Every segment is therefore a TOML key rather than raw text, which
    /// keeps a name like <c>a.b</c> or <c>with space</c> pasteable instead of turning the whole
    /// snippet into a refusal.
    /// </remarks>
    internal static string TomlSnippet(IReadOnlyList<string> serverKeyPath, RequiredMcpServer server)
    {
        string table = string.Join('.', serverKeyPath.Select(TomlKey)) + "." + TomlKey(server.Name);
        string arguments = string.Join(
            ", ",
            server.Command.Skip(1).Select(token => Literal(Substitute(token, AbsoluteRepositoryPath))));

        return $"[{table}]\ncommand = {Literal(Executable(server))}\nargs = [{arguments}]";
    }

    /// <summary>
    /// A TOML key: bare where the name allows it, quoted where it does not.
    /// </summary>
    /// <remarks>
    /// A quoted key is TOML's own form for a name that is not bare, and its basic-string escapes are
    /// the ones <see cref="JsonSerializer"/> already writes — quotes, backslashes and control
    /// characters — so the serialized form is a valid TOML basic string and one escaping rule covers
    /// both formats.
    /// </remarks>
    private static string TomlKey(string key) => IsBareTomlKey(key) ? key : Literal(key);

    /// <summary>
    /// Whether a key may appear unquoted, which is what keeps the snippet reading like the runbook's
    /// example: ASCII letters and digits, underscores and hyphens.
    /// </summary>
    private static bool IsBareTomlKey(string key) =>
        key.Length > 0
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string JsonSnippet(IReadOnlyList<string> serverKeyPath, string serverName)
    {
        JsonObject root = new();
        JsonObject current = root;

        foreach (string key in serverKeyPath)
        {
            JsonObject child = new();
            current[key] = child;
            current = child;
        }

        current[serverName] = new JsonObject();

        return JsonServerSplice.RenderDocument(root);
    }

    /// <summary>
    /// Escaping for the text a snippet puts inside TOML, which is JSON's own escaping with the
    /// HTML-safe substitutions switched off.
    /// </summary>
    /// <remarks>
    /// The default encoder writes a quote as <c>\u0022</c>, which TOML accepts and nobody wants to
    /// read in the snippet they are pasting into a configuration file. The relaxed encoder writes
    /// <c>\"</c> — TOML's own spelling of the same character — and still escapes backslashes and
    /// control characters, so one `Literal` serves both formats.
    /// </remarks>
    private static readonly JsonSerializerOptions LiteralOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Literal(string value) => JsonSerializer.Serialize(value, LiteralOptions);

    private static string NotWritableReason(HarnessMcpEntry row) =>
        row.Shape switch
        {
            McpConfigurationShape.Toml =>
                "no TOML writer ships in this version, because a hand-rolled one risks the duplicate-table "
                    + "corruption the write bounds exist to prevent",
            McpConfigurationShape.RepositorySettings =>
                "its configuration is repository settings rather than a file in the tree",
            _ =>
                "its entry body is not pinned in this repository, so writing one risks a configuration the "
                    + "harness cannot load"
        };

    private static string NotWritableHint(HarnessMcpEntry row, string path) =>
        row.Shape == McpConfigurationShape.Toml
            ? $"Paste the snippet into {path} with an absolute repository path: this harness substitutes no "
                + "workspace variable. The DocGraph MCP runbook records the form."
            : $"Paste the snippet into {path} and complete the entry body from the DocGraph MCP runbook.";

    private static string BinaryAbsenceMessage(IReadOnlyList<RequiredMcpServer> unresolvable, string path)
    {
        foreach (RequiredMcpServer server in unresolvable)
        {
            if (server.Provenance != McpProvenance.FirstParty)
                continue;

            return $"the Kyber-Weave installation itself is broken: '{Executable(server)}' is not on the search "
                + $"path, so no harness could launch it and nothing was written to {path}";
        }

        List<string> described =
        [
            .. unresolvable.Select(server => server.Command.Count == 0
                ? $"'{server.Name}' declares no launch command"
                : $"'{server.Name}' launches '{Executable(server)}', which is not on the search path")
        ];

        return string.Join("; ", described) + $", so nothing was written to {path}";
    }

    private static string BinaryAbsenceHint(IReadOnlyList<RequiredMcpServer> unresolvable)
    {
        foreach (RequiredMcpServer server in unresolvable)
        {
            if (server.Provenance == McpProvenance.FirstParty)
                return $"The harness configuration is not what is broken; repair the installation with: {InstallRepairCommand}";
        }

        List<string> hints =
        [
            .. unresolvable.Select(server => string.IsNullOrWhiteSpace(server.Install)
                ? $"Install '{Executable(server)}' yourself and make sure it is on the search path; "
                    + "Kyber-Weave never installs external binaries."
                : $"Install '{Executable(server)}' yourself; Kyber-Weave never installs external binaries: {server.Install}")
        ];

        return string.Join(" ", hints);
    }

    /// <summary>
    /// Names a server that was declared but not written because its binary does not resolve. The
    /// file is still written for the servers that can launch, and this keeps the omission visible
    /// rather than letting a partial write read as a complete one (6.2).
    /// </summary>
    private static string? SkippedNote(IReadOnlyList<RequiredMcpServer> skipped, string path)
    {
        if (skipped.Count == 0)
            return null;

        return string.Join(
            "; ",
            skipped.Select(server =>
                $"'{server.Name}' was not written to {path} because '{Executable(server)}' is not on the search path"));
    }

    private static byte[]? PreWriteContent(McpWiringReceiptRecord record)
    {
        if (string.IsNullOrEmpty(record.PreContent) || string.IsNullOrEmpty(record.PreSha256))
            return null;

        try
        {
            byte[] content = Convert.FromBase64String(record.PreContent);

            // The bytes are checked against the digest the receipt recorded before anything is
            // restored: a record whose payload no longer matches its own digest proves nothing.
            return string.Equals(Digest(content), record.PreSha256, StringComparison.OrdinalIgnoreCase)
                ? content
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The refusal for a record whose file is no longer on disk.</summary>
    private static McpWiringFileChange MissingOnRemoval(
        string harness,
        string path,
        McpWiringReceiptRecord record) =>
        Refused(
            harness,
            path,
            record.Servers,
            McpWiringReceipt.DriftCode,
            $"{path} is gone, so the state the receipt recorded cannot be restored",
            $"Replace {path} from version control and run the removal again; the receipt still records the write.").Change;

    private static McpWiringFileChange UnreadableOnRemoval(
        string harness,
        string path,
        McpWiringReceiptRecord record,
        string reason) =>
        Refused(
            harness,
            path,
            record.Servers,
            McpWiringReceipt.DriftCode,
            $"{path} could not be read: {reason}",
            $"Check the permissions on {path}, then run the removal again; the receipt still records the write.").Change;

    private static McpWiringFileChange NotRemovable(
        string harness,
        string path,
        McpWiringReceiptRecord record,
        string reason) =>
        Refused(
            harness,
            path,
            record.Servers,
            McpWiringReceipt.DriftCode,
            $"{path} could not be removed: {reason}",
            $"Check the permissions on {path} and its directory, then run the removal again.").Change;

    private static IReadOnlyList<RequiredMcpServer> Declared(McpConfig config) =>
        [.. config.RequiredServers.Values.OrderBy(server => server.Name, StringComparer.Ordinal)];

    private static bool Resolves(LaunchCommandResolver resolver, RequiredMcpServer server) =>
        server.Command.Count > 0 &&
        !string.IsNullOrWhiteSpace(server.Command[0]) &&
        resolver.Resolve(server.Command[0]) is not null;

    private static string Executable(RequiredMcpServer server) =>
        server.Command.Count > 0 ? server.Command[0] : "(nothing)";

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    /// The absolute path of a repository-relative file, or null when the name does not resolve inside
    /// the repository.
    /// </summary>
    /// <remarks>
    /// Every target path passes through here, so a name that is absolute or climbs out of the tree is
    /// refused in one place rather than in each caller. The catalog's rows are this repository's own
    /// constant and a receipt's paths are validated where the receipt is read, so this is a last line
    /// rather than a first: it is the one place where a defect in either would reach the filesystem,
    /// and the one place the containment claim is made.
    /// </remarks>
    internal static string? ContainedPath(string root, string relativePath) =>
        McpWiringReceipt.RepositoryRelative(relativePath)
            ? Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))
            : null;

    /// <summary>
    /// Keeps a write's audit note visible when the receipt could not be written. The files are
    /// already written at that point, so throwing would lose the report of what did happen, and
    /// silence would leave an operator with files that <c>mcp-unwire</c> will refuse to undo.
    /// </summary>
    private static List<McpWiringFileChange> ReportReceiptFailure(
        List<McpWiringFileChange> changes,
        string? failure)
    {
        if (failure is null)
            return changes;

        const string Note =
            "the change was made, but the receipt could not be recorded, so the removal path will refuse to undo it";

        return
        [
            .. changes.Select(change => change.Written
                ? change with
                {
                    Message = string.IsNullOrEmpty(change.Message)
                        ? $"{Note}: {failure}"
                        : $"{change.Message}; {Note}: {failure}"
                }
                : change)
        ];
    }

    private static TargetResult Refused(
        string harness,
        string? path,
        IReadOnlyList<string> servers,
        string code,
        string message,
        string? hint = null,
        string? snippet = null) =>
        new(
            new McpWiringFileChange(
                harness,
                path,
                McpWiringChangeOutcome.Refused,
                servers,
                code,
                message,
                hint,
                Written: false,
                snippet),
            null);

    /// <summary>What repairing one target came to, and the receipt record it earns.</summary>
    private readonly record struct TargetResult(McpWiringFileChange Change, McpWiringReceiptRecord? Record);
}
