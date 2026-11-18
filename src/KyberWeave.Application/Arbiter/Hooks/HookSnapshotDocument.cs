using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// One pre-dispatch snapshot. <see cref="PromptHash"/> carries the SHA-256 hex of the
/// host prompt, never the prompt itself, so the store holds no host payload. Entries
/// written before hashing carry a null hash and simply never match; entries older
/// than the retention window are pruned on the next record.
/// </summary>
/// <param name="CallId">The harness tool-call id of the dispatch, when it had one.</param>
/// <param name="Target">The dispatched agent.</param>
/// <param name="PromptHash">The SHA-256 hex of the dispatch prompt.</param>
/// <param name="Head">The head the snapshot was taken at.</param>
/// <param name="RecordedAt">When the snapshot was recorded.</param>
/// <param name="BlobIds">The blob id of each changed path.</param>
/// <param name="Statuses">The status of each changed path.</param>
internal sealed record StoredSnapshot(
    string? CallId,
    string? Target,
    string? PromptHash,
    string Head,
    DateTimeOffset RecordedAt,
    Dictionary<string, string>? BlobIds,
    Dictionary<string, string>? Statuses);

/// <summary>The snapshot document's pure half: how entries are keyed, matched, retained, read and written as JSON.</summary>
internal static class HookSnapshotDocument
{
    /// <summary>The most snapshots one document keeps; the oldest are dropped first.</summary>
    internal const int MaxStoredSnapshots = 256;

    /// <summary>How long a snapshot is kept before the next record prunes it.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(4);

    /// <summary>Reads a document's entries; an empty or corrupt document has none.</summary>
    /// <param name="json">The document's text.</param>
    internal static List<StoredSnapshot> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<StoredSnapshot>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes a document's entries as JSON.</summary>
    /// <param name="entries">The entries.</param>
    internal static string Serialize(List<StoredSnapshot> entries) => JsonSerializer.Serialize(entries);

    /// <summary>
    /// Finds the entry a post-dispatch event pairs with: the latest with its tool-call id, else the latest with the
    /// same target and prompt hash. There is no target-only fallback: without a tool-call id or a prompt there is no
    /// pairing key, and handing a dispatch another dispatch's baseline is a guess.
    /// </summary>
    /// <param name="entries">The stored entries.</param>
    /// <param name="target">The dispatched agent.</param>
    /// <param name="toolCallId">The harness tool-call id, when it has one.</param>
    /// <param name="prompt">The dispatch prompt.</param>
    /// <returns>The entry's index, or -1.</returns>
    internal static int FindMatchingIndex(List<StoredSnapshot> entries, string? target, string? toolCallId, string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(entries[i].CallId, toolCallId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            string promptHash = HashPrompt(prompt);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(entries[i].Target, target, StringComparison.Ordinal)
                    && string.Equals(entries[i].PromptHash, promptHash, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Streams the prompt through the hash in chunks: the full byte form is never allocated, yet every byte still keys
    /// the match, so same-prefix prompts with different tails cannot share a key.
    /// </summary>
    /// <param name="prompt">The dispatch prompt.</param>
    internal static string HashPrompt(string prompt)
    {
        const int charsPerChunk = 2048;
        Span<byte> byteBuffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount(charsPerChunk)];
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Encoder encoder = Encoding.UTF8.GetEncoder();
        ReadOnlySpan<char> remaining = prompt.AsSpan();
        while (!remaining.IsEmpty)
        {
            // The final chunk flushes, so a split surrogate always resolves; a
            // non-final chunk holds a full window, so progress is guaranteed.
            int take = Math.Min(remaining.Length, charsPerChunk);
            encoder.Convert(
                remaining[..take],
                byteBuffer,
                take == remaining.Length,
                out int charsUsed,
                out int bytesUsed,
                out _);
            if (bytesUsed > 0)
            {
                sha.AppendData(byteBuffer[..bytesUsed]);
            }

            remaining = remaining[charsUsed..];
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
