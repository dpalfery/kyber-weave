using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Review;

/// <summary>
/// The decision-model answers one review cycle already got, keyed by rule, subject and a digest of the text asked
/// about, so <c>review verdict --candidates</c> and the verdict run that follows it give the same answer for one call.
/// </summary>
/// <remarks>A changed finding text changes the digest, so a stale answer is never reused.</remarks>
internal sealed class ReviewDecisionCache
{
    private readonly string _path;
    private readonly Dictionary<string, CachedAnswer> _entries;
    private bool _dirty;

    private ReviewDecisionCache(string path, Dictionary<string, CachedAnswer> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>Reads the cache file; a missing or unreadable file starts an empty cache.</summary>
    public static ReviewDecisionCache Load(string path)
    {
        Dictionary<string, CachedAnswer> entries = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, CachedAnswer>>(File.ReadAllText(path)) is { } read)
            {
                entries = new Dictionary<string, CachedAnswer>(read, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable cache only costs the questions again.
        }

        return new ReviewDecisionCache(path, entries);
    }

    public bool TryGet(ArbiterRule rule, ReviewQuestion question, [NotNullWhen(true)] out ReviewAnswer? answer)
    {
        answer = _entries.TryGetValue(Key(rule, question), out CachedAnswer? hit)
            ? new ReviewAnswer(hit.Answer, hit.Score, hit.Threshold)
            : null;
        return answer is not null;
    }

    public void Put(ArbiterRule rule, ReviewQuestion question, ReviewAnswer answer)
    {
        _entries[Key(rule, question)] = new CachedAnswer(answer.Answer, answer.Score, answer.Threshold);
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept: the next run asks again.
        }
    }

    private static string Key(ArbiterRule rule, ReviewQuestion question)
    {
        StringBuilder text = new();
        foreach ((string name, object? value) in question.Facts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            text.Append(name).Append('=').Append(value is null ? string.Empty : JsonSerializer.Serialize(value)).Append('\n');
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        return $"{rule.Id}|{question.Subject}|{digest}";
    }

    internal sealed record CachedAnswer(string Answer, double? Score, double? Threshold);
}
