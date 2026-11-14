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
/// Holds the text of review findings, worker failures and the tails of worker returns under <c>&lt;arbiter-dir&gt;/stores/</c>,
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
    private const string ReturnsFileName = "returns.jsonl";

    /// <summary>
    /// The most bytes of a plan's returns file one read looks at, taken from the end. An active plan's file is never
    /// swept, so reading all of it would make every evaluation cost more as the plan ages; the newest returns are
    /// the only ones a caller wants.
    /// </summary>
    internal const long MaxReturnsReadBytes = 1024 * 1024;

    /// <summary>
    /// The most bytes of a plan's failures file one read looks at, taken from the end. The only consumer takes the
    /// latest stored failure of one cluster, so the newest items are all it needs; a cluster whose last failure is
    /// older than the window reads as a first failure.
    /// </summary>
    internal const long MaxFailuresReadBytes = 1024 * 1024;

    /// <summary>
    /// The most bytes of a review run's findings file one read looks at, taken from the end. The consumers look up
    /// the stored item of one finding and the few findings stored just before it, and a refutation runs soon after
    /// its finding is stored, so the newest items are the ones that matter. Sized at 2 MiB, enough for about 130
    /// items of the largest permitted size and many times that at a typical size.
    /// </summary>
    internal const long MaxFindingsReadBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The longest retention the sweep honours: ten years. A larger configured value is clamped to this, because a
    /// span past the largest representable time span cannot be formed, and the append must still store its item.
    /// </summary>
    private const int MaxRetainDays = 3650;

    /// <summary>The failure-store key for a failure recorded with no session at all.</summary>
    private const string AbsentSessionKey = "\0absent-session";

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
        return AppendAsync(ReviewDirectory(reviewRun), FindingsFileName, text, ct);
    }

    /// <summary>
    /// Appends one refuter's verdict on a finding to the store of <paramref name="reviewRun"/>, keyed by the
    /// finding's slug. It is written beside the findings as a line that is not itself a finding.
    /// </summary>
    /// <param name="reviewRun">The <c>REVIEW_RUN</c> id the finding belongs to.</param>
    /// <param name="findingSlug">The bare slug of the refuted finding (the part after <c>&lt;lens&gt;/</c>).</param>
    /// <param name="verdict">What the refuter's return said.</param>
    /// <param name="ct">Cancels the append.</param>
    /// <returns>Whether the item was stored, refused for size, or the store was unavailable.</returns>
    public Task<ArbiterStoreStatus> AppendRefutationOutcomeAsync(
        string reviewRun,
        string findingSlug,
        RefutationVerdict verdict,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewRun);
        ArgumentNullException.ThrowIfNull(findingSlug);
        string text = "refutation-of: " + findingSlug + "\nverdict: " + RefutationOutcome.Word(verdict);
        return AppendAsync(ReviewDirectory(reviewRun), FindingsFileName, text, ct);
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
        string directory = FailureDirectory(planFile, planDigest, session);
        return AppendAsync(directory, FailuresFileName, text, ct);
    }

    /// <summary>
    /// Reads the worker failures stored for a plan, or for a session when no plan is known, oldest first.
    /// </summary>
    /// <remarks>
    /// Returns an empty list when the store is missing or unreadable. The key is the one
    /// <see cref="AppendFailureAsync"/> writes under, so a read finds what an append wrote.
    /// </remarks>
    /// <param name="planFile">The plan file the failures belong to, or null.</param>
    /// <param name="planDigest">The digest of that plan file, or null.</param>
    /// <param name="session">The session that failed, used as the key when there is no plan.</param>
    /// <returns>The stored texts, in append order.</returns>
    public IReadOnlyList<string> ReadFailures(string? planFile, string? planDigest, string? session)
    {
        string path = Path.Combine(FailureDirectory(planFile, planDigest, session), FailuresFileName);
        return ReadItems(path, MaxFailuresReadBytes);
    }

    /// <summary>
    /// Appends the tail of one worker's return to the store of its plan, so a later dispatch's model question can
    /// read what the worker reported rather than only the markers the ledger parsed from it.
    /// </summary>
    /// <param name="planFile">The plan file the return's dispatch named.</param>
    /// <param name="text">The stored text: the task, the target and the report tail.</param>
    /// <param name="ct">Cancels the append.</param>
    /// <returns>Whether the item was stored, refused for size, or the store was unavailable.</returns>
    public Task<ArbiterStoreStatus> AppendReturnAsync(string planFile, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(planFile);
        ArgumentNullException.ThrowIfNull(text);
        return AppendAsync(ReturnsDirectory(planFile), ReturnsFileName, text, ct);
    }

    /// <summary>Reads the return tails stored for a plan, oldest first; empty when none or unreadable.</summary>
    /// <param name="planFile">The plan file the returns belong to.</param>
    /// <returns>The stored texts, in append order.</returns>
    public IReadOnlyList<string> ReadReturns(string planFile)
    {
        ArgumentNullException.ThrowIfNull(planFile);
        return ReadItems(Path.Combine(ReturnsDirectory(planFile), ReturnsFileName), MaxReturnsReadBytes);
    }

    /// <summary>Keyed by the plan file alone, so an amended plan still sees the returns made before the amendment.</summary>
    private string ReturnsDirectory(string planFile)
    {
        string normalized = planFile.Trim().Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return Path.Combine(_storesDirectory, "returns-" + Digest(normalized));
    }

    private string FailureDirectory(string? planFile, string? planDigest, string? session)
    {
        // An absent session and an empty one are different callers, so they key different stores. A NUL cannot
        // appear in a session id from argv or a header, so the absent key never collides with a real session.
        string key = planFile is not null
            ? planFile + (planDigest ?? string.Empty)
            : session ?? AbsentSessionKey;
        return Path.Combine(_storesDirectory, "run-" + Digest(key));
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
        string path = Path.Combine(ReviewDirectory(reviewRun), FindingsFileName);
        return ReadItems(path, MaxFindingsReadBytes);
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

        // Clamped before the conversion: a value past the representable span would throw, and the append must still store.
        TimeSpan retain = TimeSpan.FromDays(Math.Clamp(_options.RetainDays, 1, MaxRetainDays));
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

            if (now - NewestItemTimeUtc(directory) <= retain)
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

    /// <summary>
    /// The time a store directory was last written to, read from its newest file. A directory's own time moves only
    /// when an entry is created or removed, so an active run appended to in place would look old and be swept
    /// mid-review. The directory's time is the fallback for a directory that holds no file.
    /// </summary>
    private static DateTime NewestItemTimeUtc(DirectoryInfo directory)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0 };
        DateTime? newest = null;
        foreach (FileInfo file in directory.EnumerateFiles("*", options))
        {
            if (file.LinkTarget is not null)
            {
                continue;
            }

            if (newest is not { } current || file.LastWriteTimeUtc > current)
            {
                newest = file.LastWriteTimeUtc;
            }
        }

        return newest ?? directory.LastWriteTimeUtc;
    }

    /// <summary>
    /// Reads the items of <paramref name="path"/>, looking only at its last <paramref name="tailBytes"/> bytes. There
    /// is no default: every caller names the window its consumers need, so a new caller cannot read a file that only
    /// grows in full.
    /// </summary>
    /// <remarks>
    /// When the file is longer than the window, the read starts at the window's first byte. That byte starts an item
    /// only when the byte before it is a newline (an item is one line, its own newlines escaped); otherwise the
    /// first line is the end of an item cut by the seek and is dropped, so a whole item is never lost.
    /// A missing file reads as empty.
    /// </remarks>
    internal static IReadOnlyList<string> ReadItems(string path, long tailBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tailBytes, 1);
        try
        {
            List<string> items = [];
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool partialFirstLine = false;
            if (stream.Length > tailBytes)
            {
                stream.Seek(stream.Length - tailBytes - 1, SeekOrigin.Begin);
                partialFirstLine = stream.ReadByte() != '\n';
            }

            using StreamReader reader = new(stream);
            if (partialFirstLine)
            {
                // The seek landed mid-line (and possibly mid-character); the fragment is not an item.
                reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
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
            // Includes a file that is absent when opened; a store that cannot be read has no items to offer.
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

    /// <summary>
    /// The directory a review run's findings are stored under, keyed by the digest of the run id. The digest is the
    /// whole key, so no <c>REVIEW_RUN</c> header value, whatever its characters or length, can name a path outside the
    /// stores.
    /// </summary>
    private string ReviewDirectory(string reviewRun) =>
        Path.Combine(_storesDirectory, "review-" + Digest(reviewRun));

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>One stored line: when it was appended and its exact text.</summary>
    /// <param name="At">When the item was appended.</param>
    /// <param name="Text">The item's text, exactly as appended.</param>
    private sealed record StoredItem(DateTimeOffset At, string Text);
}
