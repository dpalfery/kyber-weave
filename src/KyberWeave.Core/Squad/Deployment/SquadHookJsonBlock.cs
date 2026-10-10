using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The shared hook-file shape owned through a JSON block splice.</summary>
public enum SquadHookBlockFormat
{
    Cursor,
    Codex,
    Factory,
    Devin,
    Antigravity
}

/// <summary>One managed entry written by a splice: its JSON pointer and canonical digest.</summary>
/// <remarks>
/// The member is named <c>Location</c> rather than <c>Pointer</c> because the latter trips
/// CA1720 (<c>System.Reflection.Pointer</c>); the value is still an RFC 6901 JSON pointer.
/// </remarks>
public sealed record SquadHookOwnedEntry(string Location, string Digest);

/// <summary>One owned entry whose current file content no longer matches its record.</summary>
public sealed record SquadHookDrift(string Location, string Reason);

/// <summary>
/// Splices Squad's hook entries into the shared hook files the user also owns, without
/// disturbing the user's entries.
/// </summary>
/// <remarks>
/// JSON hook files carry no comments, so markers in the style of the Config Reg block cannot
/// be used (D25). Ownership is by command signature instead:
/// <c>kyber-weave-arbiter hook --harness &lt;h&gt;</c>. Antigravity is the exception — Squad
/// owns the whole top-level <c>kyber-arbiter</c> group, since top-level keys there are group
/// names and any unknown key would read as another group.
/// </remarks>
public static class SquadHookJsonBlock
{
    private const string CommandPrefix = "kyber-weave-arbiter hook --harness ";

    private const string AntigravityGroupKey = "kyber-arbiter";

    private const string MissingReason = "missing";

    private const string ModifiedReason = "modified";

    // The relaxed encoder keeps the user's own text byte-faithful in the file (&&, quotes,
    // angle brackets and non-ASCII would otherwise be rewritten as \uXXXX escapes). The
    // digest stays on the default encoder inside CanonicalDigest, so recorded digests
    // never depend on how the file was written.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The hook file owned through this format, as a portable relative path.</summary>
    public static string RelativePath(SquadHookBlockFormat format) =>
        format switch
        {
            SquadHookBlockFormat.Cursor => ".cursor/hooks.json",
            SquadHookBlockFormat.Codex => ".codex/hooks.json",
            SquadHookBlockFormat.Factory => ".factory/hooks.json",
            SquadHookBlockFormat.Devin => ".devin/hooks.v1.json",
            SquadHookBlockFormat.Antigravity => ".agents/hooks.json",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };

    /// <summary>Whether a hook entry (or group) is one of Squad's, by command signature.</summary>
    /// <remarks>
    /// Cursor entries are flat and carry their own command. Codex, Factory and Devin entries
    /// are matcher groups, Squad's only when every hook in the group carries the signature —
    /// a group mixing Squad and user hooks stays user content. Antigravity is owned by key,
    /// so any group value presented for that format is Squad's.
    /// </remarks>
    public static bool IsSquadEntry(SquadHookBlockFormat format, JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return false;
        }

        if (format == SquadHookBlockFormat.Antigravity)
        {
            return true;
        }

        if (format == SquadHookBlockFormat.Cursor)
        {
            return HasSignature(obj["command"], Harness(format));
        }

        return obj["hooks"] is JsonArray hooks
            && hooks.Count > 0
            && hooks.All(hook => hook is JsonObject hookObj && HasSignature(hookObj["command"], Harness(format)));
    }

    /// <summary>The canonical digest of one owned entry: SHA-256 of its compact JSON.</summary>
    public static string CanonicalDigest(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    /// <summary>
    /// Replaces Squad's entries in <paramref name="filePath"/> with <paramref name="preToolUse"/>
    /// and <paramref name="postToolUse"/>, appending them at the end of each owned container
    /// (or setting the Antigravity group key). A missing or blank file starts from the minimal
    /// document. Returns the owned entries written.
    /// </summary>
    public static IReadOnlyList<SquadHookOwnedEntry> SpliceFile(
        SquadHookBlockFormat format,
        string filePath,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(preToolUse);
        ArgumentNullException.ThrowIfNull(postToolUse);

        string? existing = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        string content;
        IReadOnlyList<SquadHookOwnedEntry> owned;
        try
        {
            (content, owned) = SpliceContent(format, existing, preToolUse, postToolUse);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Squad hook splice failed: '{filePath}' does not parse as a hook file for {Harness(format)}.",
                ex);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, content);
        return owned;
    }

    /// <summary>
    /// Removes Squad's entries from <paramref name="filePath"/>, restoring the user's content.
    /// A missing file needs no work; an unparsable file fails and is left untouched.
    /// </summary>
    public static void RemoveFile(SquadHookBlockFormat format, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return;
        }

        string existing = File.ReadAllText(filePath);
        string content;
        try
        {
            content = RemoveContent(format, existing);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Squad hook removal failed: '{filePath}' does not parse as a hook file for {Harness(format)}.",
                ex);
        }

        File.WriteAllText(filePath, content);
    }

    /// <summary>Whether any hook remains anywhere in <paramref name="json"/>.</summary>
    /// <remarks>
    /// This looks beyond Squad's owned containers on purpose: uninstall deletes the file only
    /// when no hook at all remains, and the user's hooks may live beside Squad's — notably in
    /// another Antigravity group. Non-array values (like a group's <c>enabled</c> flag) are
    /// not hooks and are ignored.
    /// </remarks>
    public static bool IsEmpty(SquadHookBlockFormat format, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        JsonObject root = ParseObject(json);
        if (format == SquadHookBlockFormat.Antigravity)
        {
            foreach ((_, JsonNode? value) in root)
            {
                if (value is JsonArray array && array.Count > 0)
                {
                    return false;
                }

                if (value is JsonObject group && group.Any(child => child.Value is JsonArray childArray && childArray.Count > 0))
                {
                    return false;
                }
            }

            return true;
        }

        JsonObject? parent = format == SquadHookBlockFormat.Cursor || format == SquadHookBlockFormat.Codex
            ? root["hooks"] as JsonObject
            : root;
        if (parent is null)
        {
            return true;
        }

        return parent.All(child => child.Value is not JsonArray array || array.Count == 0);
    }

    /// <summary>
    /// Reports owned entries whose digest differs from <paramref name="expectedDigests"/>, or
    /// which are missing from <paramref name="currentJson"/> (Req 8.4).
    /// </summary>
    public static IReadOnlyList<SquadHookDrift> FindDrift(
        SquadHookBlockFormat format,
        string currentJson,
        IReadOnlyDictionary<string, string> expectedDigests)
    {
        ArgumentNullException.ThrowIfNull(expectedDigests);
        _ = RelativePath(format);

        JsonObject root = ParseObject(currentJson);
        List<SquadHookDrift> drift = [];
        Dictionary<string, HashSet<int>> claimed = new(StringComparer.Ordinal);
        List<UnanchoredEntry> unanchored = [];

        // Pass one claims every recorded index whose digest still matches where it was
        // recorded. Anchoring all of them before any re-anchor is what keeps two
        // identical twins from covering for each other: interleaved, the surviving twin
        // claimed the slot its edited sibling was recorded at, and the edit read as a
        // move rather than as damage.
        foreach ((string pointer, string digest) in expectedDigests)
        {
            JsonNode? node = ResolvePointer(root, pointer);
            bool hasContainer = TrySplitPointer(pointer, out string containerPointer, out int recordedIndex);
            if (node is not null
                && string.Equals(CanonicalDigest(node), digest, StringComparison.Ordinal))
            {
                if (hasContainer)
                {
                    ClaimedIn(claimed, containerPointer).Add(recordedIndex);
                }

                continue;
            }

            if (hasContainer)
            {
                unanchored.Add(new UnanchoredEntry(pointer, containerPointer, digest, node is null));
                continue;
            }

            drift.Add(new SquadHookDrift(pointer, node is null ? MissingReason : ModifiedReason));
        }

        // Pass two re-anchors only what pass one left unaccounted for, and only against
        // indices no pass-one claim took. A user who inserts a hook ahead of Squad's
        // shifts every entry, and reading that as drift would also stop `squad update`
        // from ever refreshing them.
        foreach (UnanchoredEntry entry in unanchored)
        {
            HashSet<int> taken = ClaimedIn(claimed, entry.ContainerPointer);
            int found = ResolvePointer(root, entry.ContainerPointer) is JsonArray container
                ? IndexOfUnclaimedDigest(container, entry.Digest, taken)
                : -1;
            if (found >= 0)
            {
                taken.Add(found);
                continue;
            }

            drift.Add(new SquadHookDrift(entry.Pointer, entry.WasMissing ? MissingReason : ModifiedReason));
        }

        return drift;
    }

    /// <summary>A recorded entry whose digest did not match the index it was recorded at.</summary>
    private readonly record struct UnanchoredEntry(
        string Pointer,
        string ContainerPointer,
        string Digest,
        bool WasMissing);

    private static HashSet<int> ClaimedIn(Dictionary<string, HashSet<int>> claimed, string containerPointer)
    {
        if (!claimed.TryGetValue(containerPointer, out HashSet<int>? taken))
        {
            taken = [];
            claimed[containerPointer] = taken;
        }

        return taken;
    }

    private static int IndexOfUnclaimedDigest(JsonArray container, string digest, HashSet<int> taken)
    {
        for (int index = 0; index < container.Count; index++)
        {
            if (!taken.Contains(index)
                && container[index] is JsonNode candidate
                && string.Equals(CanonicalDigest(candidate), digest, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TrySplitPointer(string pointer, out string containerPointer, out int index)
    {
        int slash = pointer.LastIndexOf('/');
        if (slash > 0
            && int.TryParse(
                pointer.AsSpan(slash + 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out index))
        {
            containerPointer = pointer[..slash];
            return true;
        }

        containerPointer = string.Empty;
        index = -1;
        return false;
    }

    /// <summary>
    /// Splices Squad's entries into hook-file <paramref name="existing"/> content without
    /// touching the disk: the single implementation behind both <see cref="SpliceFile"/> and
    /// the deployment plan, so a dry run and a real install can never disagree. A missing
    /// or blank file starts from the format's minimal document. Throws
    /// <see cref="JsonException"/> or <see cref="InvalidOperationException"/> when the
    /// content is not a usable hook file, leaving the caller to decide how to report it.
    /// </summary>
    public static (string Content, IReadOnlyList<SquadHookOwnedEntry> Owned) SpliceContent(
        SquadHookBlockFormat format,
        string? existing,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse)
    {
        JsonObject root = ParseOrMinimal(format, existing);
        List<SquadHookOwnedEntry> owned = [];

        if (format == SquadHookBlockFormat.Antigravity)
        {
            root.Remove(AntigravityGroupKey);
            if (preToolUse.Count > 0 || postToolUse.Count > 0)
            {
                var group = new JsonObject
                {
                    ["PreToolUse"] = ProjectedArray(format, preToolUse),
                    ["PostToolUse"] = ProjectedArray(format, postToolUse),
                };
                root[AntigravityGroupKey] = group;
                CollectOwned(group, $"/{AntigravityGroupKey}", owned);
            }
        }
        else
        {
            var containers = OwnedArrays(format, root, create: true);
            var managed = new[] { preToolUse, postToolUse };
            for (int i = 0; i < containers.Count; i++)
            {
                (string pointer, JsonArray array) = containers[i];
                for (int index = array.Count - 1; index >= 0; index--)
                {
                    if (IsSquadEntry(format, array[index]))
                    {
                        array.RemoveAt(index);
                    }
                }

                foreach (JsonNode entry in managed[i])
                {
                    JsonNode projected = ProjectEntry(format, entry);
                    array.Add(projected);
                    string entryPointer = $"{pointer}/{array.Count - 1}";
                    owned.Add(new SquadHookOwnedEntry(entryPointer, CanonicalDigest(projected)));
                }
            }

            if (format == SquadHookBlockFormat.Cursor && !root.ContainsKey("version"))
            {
                root["version"] = 1;
            }
        }

        return (Serialize(root), owned);
    }

    /// <summary>
    /// Removes Squad's entries from hook-file <paramref name="existing"/> content without
    /// touching the disk, restoring the user's content. Same failure contract as
    /// <see cref="SpliceContent"/>.
    /// </summary>
    public static string RemoveContent(SquadHookBlockFormat format, string? existing)
    {
        JsonObject root = ParseOrMinimal(format, existing);

        if (format == SquadHookBlockFormat.Antigravity)
        {
            root.Remove(AntigravityGroupKey);
        }
        else
        {
            foreach ((_, JsonArray array) in OwnedArrays(format, root, create: false))
            {
                for (int index = array.Count - 1; index >= 0; index--)
                {
                    if (IsSquadEntry(format, array[index]))
                    {
                        array.RemoveAt(index);
                    }
                }
            }
        }

        return Serialize(root);
    }

    private static void CollectOwned(JsonObject group, string prefix, List<SquadHookOwnedEntry> owned)
    {
        foreach (string container in new[] { "PreToolUse", "PostToolUse" })
        {
            if (group[container] is JsonArray array)
            {
                for (int index = 0; index < array.Count; index++)
                {
                    JsonNode node = array[index]!;
                    owned.Add(new SquadHookOwnedEntry($"{prefix}/{container}/{index}", CanonicalDigest(node)));
                }
            }
        }
    }

    private static List<(string Pointer, JsonArray Array)> OwnedArrays(
        SquadHookBlockFormat format, JsonObject root, bool create)
    {
        return format switch
        {
            SquadHookBlockFormat.Cursor => CursorArrays(root, create),
            SquadHookBlockFormat.Codex => PrefixedArrays(root, "/hooks", create),
            SquadHookBlockFormat.Factory or SquadHookBlockFormat.Devin => RootArrays(root, create),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };
    }

    private static List<(string Pointer, JsonArray Array)> CursorArrays(JsonObject root, bool create)
    {
        JsonObject hooks = create ? EnsureObject(root, "hooks") : root["hooks"] as JsonObject ?? new JsonObject();
        List<(string Pointer, JsonArray Array)> containers = [];
        foreach (string name in new[] { "preToolUse", "postToolUse" })
        {
            JsonArray? array = hooks[name] as JsonArray;
            if (array is null)
            {
                if (!create)
                {
                    continue;
                }

                if (hooks[name] is not null)
                {
                    throw new InvalidOperationException($"Hook container '{name}' must be a JSON array.");
                }

                array = [];
                hooks[name] = array;
            }

            containers.Add(($"/hooks/{name}", array));
        }

        return containers;
    }

    private static List<(string Pointer, JsonArray Array)> PrefixedArrays(JsonObject root, string prefix, bool create)
    {
        JsonObject hooks = create ? EnsureObject(root, "hooks") : root["hooks"] as JsonObject ?? new JsonObject();
        return NamedArrays(hooks, prefix, create);
    }

    private static List<(string Pointer, JsonArray Array)> RootArrays(JsonObject root, bool create) =>
        NamedArrays(root, string.Empty, create);

    private static List<(string Pointer, JsonArray Array)> NamedArrays(JsonObject parent, string prefix, bool create)
    {
        List<(string Pointer, JsonArray Array)> containers = [];
        foreach (string name in new[] { "PreToolUse", "PostToolUse" })
        {
            JsonArray? array = parent[name] as JsonArray;
            if (array is null)
            {
                if (!create)
                {
                    continue;
                }

                if (parent[name] is not null)
                {
                    throw new InvalidOperationException($"Hook container '{name}' must be a JSON array.");
                }

                array = [];
                parent[name] = array;
            }

            containers.Add(($"{prefix}/{name}", array));
        }

        return containers;
    }

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        if (parent[name] is not null)
        {
            throw new InvalidOperationException($"Hook section '{name}' must be a JSON object.");
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonObject ParseOrMinimal(SquadHookBlockFormat format, string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return MinimalDocument(format);
        }

        return ParseObject(existing);
    }

    private static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("A hook file must be a JSON object at its root.");
    }

    private static JsonObject MinimalDocument(SquadHookBlockFormat format) =>
        format switch
        {
            SquadHookBlockFormat.Cursor => new JsonObject { ["version"] = 1, ["hooks"] = new JsonObject() },
            SquadHookBlockFormat.Codex => new JsonObject { ["hooks"] = new JsonObject() },
            SquadHookBlockFormat.Factory or SquadHookBlockFormat.Devin or SquadHookBlockFormat.Antigravity => new JsonObject(),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };

    /// <summary>Copies only the documented fields of a managed entry, dropping anything else.</summary>
    /// <remarks>
    /// The renderer may pass through richer shapes; the file carries only what the harness
    /// documents, and never a sentinel key (D25).
    /// </remarks>
    private static JsonNode ProjectEntry(SquadHookBlockFormat format, JsonNode entry)
    {
        if (entry is not JsonObject obj)
        {
            throw new InvalidOperationException("A managed hook entry must be a JSON object.");
        }

        if (format == SquadHookBlockFormat.Cursor)
        {
            var projected = new JsonObject();
            foreach (string field in new[] { "command", "matcher", "timeout", "failClosed" })
            {
                if (obj[field] is JsonNode value)
                {
                    projected[field] = value.DeepClone();
                }
            }

            return projected;
        }

        var group = new JsonObject();
        if (obj["matcher"] is JsonNode matcher)
        {
            group["matcher"] = matcher.DeepClone();
        }

        if (obj["hooks"] is JsonArray hooks)
        {
            var projectedHooks = new JsonArray();
            foreach (JsonNode? hook in hooks)
            {
                if (hook is not JsonObject hookObj)
                {
                    throw new InvalidOperationException("A managed hook entry must hold JSON objects.");
                }

                var projectedHook = new JsonObject();
                foreach (string field in new[] { "type", "command", "timeout" })
                {
                    if (hookObj[field] is JsonNode value)
                    {
                        projectedHook[field] = value.DeepClone();
                    }
                }

                projectedHooks.Add(projectedHook);
            }

            group["hooks"] = projectedHooks;
        }
        else if (obj["hooks"] is not null)
        {
            throw new InvalidOperationException("A managed matcher group must hold a 'hooks' array.");
        }

        return group;
    }

    private static JsonArray ProjectedArray(SquadHookBlockFormat format, IReadOnlyList<JsonNode> entries)
    {
        var array = new JsonArray();
        foreach (JsonNode entry in entries)
        {
            array.Add(ProjectEntry(format, entry));
        }

        return array;
    }

    private static JsonNode? ResolvePointer(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        foreach (string raw in pointer.Split('/').Skip(1))
        {
            string segment = raw
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray arr when int.TryParse(
                    segment,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int index)
                    && index >= 0 && index < arr.Count => arr[index],
                _ => null,
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static bool HasSignature(JsonNode? node, string harness) =>
        node is JsonValue value
        && value.TryGetValue<string>(out string? command)
        && command is not null
        && command.StartsWith(CommandPrefix + harness, StringComparison.Ordinal);

    private static string Harness(SquadHookBlockFormat format) =>
        format switch
        {
            SquadHookBlockFormat.Cursor => "cursor",
            SquadHookBlockFormat.Codex => "codex",
            SquadHookBlockFormat.Factory => "factory",
            SquadHookBlockFormat.Devin => "devin",
            SquadHookBlockFormat.Antigravity => "antigravity",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };

    /// <summary>
    /// Parses and re-serializes hook-file content exactly as a splice or removal would, so a
    /// caller can tell "nothing of Squad's was in this file" apart from a real rewrite.
    /// </summary>
    public static string NormalizeContent(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Serialize(ParseObject(json));
    }

    private static string Serialize(JsonNode root) => root.ToJsonString(Indented) + "\n";
}
