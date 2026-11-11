using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter.Stores;

/// <summary>Size and retention limits for the run stores, from <c>arbiter.stores</c>.</summary>
/// <param name="MaxItemBytes">The largest single item, in UTF-8 bytes. An item exactly at the limit is kept.</param>
/// <param name="RetainDays">How many days a store directory is kept after its last write.</param>
public sealed record ArbiterStoreOptions(int MaxItemBytes = 16384, int RetainDays = 14);

/// <summary>What an append did with its item.</summary>
public enum ArbiterStoreStatus
{
    /// <summary>The item was appended.</summary>
    Stored,

    /// <summary>The item was over the byte limit. It is refused, never shortened to fit.</summary>
    TooLarge,

    /// <summary>The store could not be written. Reported, never thrown.</summary>
    Unavailable,
}

/// <summary>
/// Holds the text of review findings and worker failures under <c>&lt;arbiter-dir&gt;/stores/</c>,
/// outside the decision log and the ledger, so a rule can read a finding back without the decision
/// log ever carrying its text.
/// </summary>
/// <remarks>
/// <para>
/// A review store is keyed by the digest of its <c>REVIEW_RUN</c> id, and a failure store by the digest of
/// the plan file and its digest (or of the session, when there is no plan). Each item is one JSON line,
/// so an item's newlines are escaped and the exact text comes back on read.
/// </para>
/// <para>
/// Retention is best-effort. Each append first removes sibling store directories whose last write is
/// older than <c>RetainDays</c>, never the directory being appended to. A sibling that cannot be removed
/// is left for a later append, because the append itself is what the caller depends on.
/// </para>
/// </remarks>
public sealed class ArbiterRunStore
{
    private const string StoresDirectoryName = "stores";
    private const string FindingsFileName = "findings.jsonl";
    private const string FailuresFileName = "failures.jsonl";

    private readonly string _storesDirectory;
    private readonly ArbiterStoreOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a store rooted at <c>&lt;arbiter-dir&gt;/stores</c>.</summary>
    /// <param name="arbiterDirectory">The Arbiter directory the stores sit under.</param>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; the system clock when null.</param>
    public ArbiterRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(arbiterDirectory);
        _storesDirectory = Path.Combine(arbiterDirectory, StoresDirectoryName);
        _options = options ?? new ArbiterStoreOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Appends one finding's text to the store of <paramref name="reviewRun"/>.</summary>
    /// <param name="reviewRun">The <c>REVIEW_RUN</c> id the finding belongs to.</param>
    /// <param name="text">The finding's text, stored as given.</param>
    /// <param name="ct">Cancels the append.</param>
    /// <returns>Whether the item was stored, refused for size, or the store was unavailable.</returns>
    public Task<ArbiterStoreStatus> AppendFindingAsync(string reviewRun, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewRun);
        ArgumentNullException.ThrowIfNull(text);
        string directory = Path.Combine(_storesDirectory, "review-" + Digest(reviewRun));
        return AppendAsync(directory, FindingsFileName, text, ct);
    }

    /// <summary>
    /// Appends one worker failure's text to the store of its plan, or of its session when no plan is known.
    /// </summary>
    /// <param name="planFile">The plan file the failure belongs to, or null.</param>
    /// <param name="planDigest">The digest of that plan file, or null.</param>
    /// <param name="session">The session that failed, used as the key when there is no plan.</param>
    /// <param name="text">The failure's text, stored as given.</param>
    /// <param name="ct">Cancels the append.</param>
    /// <returns>Whether the item was stored, refused for size, or the store was unavailable.</returns>
    public Task<ArbiterStoreStatus> AppendFailureAsync(
        string? planFile,
        string? planDigest,
        string? session,
        string text,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        string key = planFile is not null
            ? planFile + (planDigest ?? string.Empty)
            : session ?? string.Empty;
        string directory = Path.Combine(_storesDirectory, "run-" + Digest(key));
        return AppendAsync(directory, FailuresFileName, text, ct);
    }

    /// <summary>Reads the findings stored for <paramref name="reviewRun"/>, oldest first.</summary>
    /// <remarks>
    /// Returns an empty list when the store is missing or unreadable. A line that does not parse is
    /// skipped rather than failing the whole read.
    /// </remarks>
    /// <param name="reviewRun">The <c>REVIEW_RUN</c> id to read.</param>
    /// <returns>The stored texts, in append order.</returns>
    public IReadOnlyList<string> ReadFindings(string reviewRun)
    {
        ArgumentNullException.ThrowIfNull(reviewRun);
        string path = Path.Combine(_storesDirectory, "review-" + Digest(reviewRun), FindingsFileName);
        return ReadItems(path);
    }

    private async Task<ArbiterStoreStatus> AppendAsync(string directory, string fileName, string text, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(text) > _options.MaxItemBytes)
        {
            return ArbiterStoreStatus.TooLarge;
        }

        DateTimeOffset now = _clock();
        try
        {
            SweepExpired(directory, now);
            string line = ArbiterJson.Serialize(new StoredItem(now, text));
            await ArbiterLogFiles.AppendLineAsync(directory, fileName, line, ct);
            return ArbiterStoreStatus.Stored;
        }
        catch (IOException)
        {
            return ArbiterStoreStatus.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return ArbiterStoreStatus.Unavailable;
        }
    }

    /// <summary>
    /// Deletes sibling store directories last written more than <c>RetainDays</c> before <paramref name="now"/>.
    /// Walks one level at a time and skips symbolic links, so a link cannot send the delete outside the stores.
    /// </summary>
    private void SweepExpired(string keepDirectory, DateTimeOffset now)
    {
        if (!Directory.Exists(_storesDirectory))
        {
            return;
        }

        TimeSpan retain = TimeSpan.FromDays(_options.RetainDays);
        string keep = Path.GetFullPath(keepDirectory);
        DirectoryInfo root = new(_storesDirectory);
        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0 };
        foreach (FileSystemInfo entry in root.EnumerateFileSystemInfos("*", options))
        {
            if (entry.LinkTarget is not null
                || entry is not DirectoryInfo directory
                || string.Equals(Path.GetFullPath(directory.FullName), keep, StringComparison.Ordinal))
            {
                continue;
            }

            if (now - directory.LastWriteTimeUtc <= retain)
            {
                continue;
            }

            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Left for a later append; see the type remarks.
            }
            catch (UnauthorizedAccessException)
            {
                // Left for a later append; see the type remarks.
            }
        }
    }

    private static IReadOnlyList<string> ReadItems(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            List<string> items = [];
            foreach (string line in File.ReadLines(path))
            {
                StoredItem? item = TryDeserialize(line);
                if (item is not null)
                {
                    items.Add(item.Text);
                }
            }

            return items;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static StoredItem? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredItem>(line, ArbiterJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>One stored line: when it was appended and its exact text.</summary>
    /// <param name="At">When the item was appended.</param>
    /// <param name="Text">The item's text, exactly as appended.</param>
    private sealed record StoredItem(DateTimeOffset At, string Text);
}
