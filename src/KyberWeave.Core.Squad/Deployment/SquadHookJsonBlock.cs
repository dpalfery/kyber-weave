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
    Antigravity,
    Claude,
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
/// <c>kyber-weave-arbiter hook --harness &lt;h&gt;</c> or <c>kyber-weave-arbiter worker-guard --harness &lt;h&gt;</c>. Antigravity is the exception — Squad
/// owns the whole top-level <c>kyber-arbiter</c> group, since top-level keys there are group
/// names and any unknown key would read as another group.
/// </remarks>
public static class SquadHookJsonBlock
{
    private const string CommandPrefix = "kyber-weave-arbiter hook --harness ";

    /// <summary>The worker guard's command, which Squad also owns: <c>kyber-weave-arbiter worker-guard --harness &lt;h&gt;</c>.</summary>
    private const string WorkerGuardPrefix = "kyber-weave-arbiter worker-guard --harness ";

    private const string AntigravityGroupKey = "kyber-arbiter";

    private const string PreToolUseContainer = "PreToolUse";

    private const string PostToolUseContainer = "PostToolUse";

    // Factory alone fires a hook when a background subagent stops. Its launch-time PostToolUse
    // carries no worker report, so the Arbiter reads the report on this container.
    private const string SubagentStopContainer = "SubagentStop";

    private static readonly string[] PreAndPostContainers = [PreToolUseContainer, PostToolUseContainer];

    private static readonly string[] AllContainers = [PreToolUseContainer, PostToolUseContainer, SubagentStopContainer];

    // The lower-case spellings a Claude settings file held before the PascalCase fix.
    private static readonly string[] LegacyClaudeContainers = ["preToolUse", "postToolUse"];

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
            SquadHookBlockFormat.Claude => "settings.json",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };

    /// <summary>
    /// Resolves the hook-file shape of one receipt-owned block, reading the receipt that recorded
    /// it rather than a render that may no longer exist.
    /// </summary>
    /// <remarks>
    /// The recorded target decides the shape first: four of the six formats write the same bare
    /// <c>hooks.json</c> beneath their own global root, so under global scope the path names the
    /// file but cannot name the format (global block identity plan, D8). A target that owns no
    /// shared hook file falls through to the per-format portable path, which is the
    /// project- and instance-scope shape where the path alone is unique. A corrupt record —
    /// a target whose own shape names a different path than the one recorded — fails closed
    /// instead of resolving, so status, doctor and uninstall report the corrupt receipt
    /// rather than operating on the wrong shape.
    /// </remarks>
    /// <returns><see langword="true"/> when the block's shape is known; otherwise <see langword="false"/>.</returns>
    public static bool TryResolveFormat(SquadOwnedBlock block, out SquadHookBlockFormat format)
    {
        ArgumentNullException.ThrowIfNull(block);

        SquadHookBlockFormat? ownedByTarget = SquadTargetCatalog.TryResolve(block.Target, out SquadTarget target)
            ? FormatOwnedBy(target)
            : null;
        if (ownedByTarget is { } known)
        {
            string expectedPath = RelativePath(known);
            string expectedFileName = Path.GetFileName(expectedPath);
            string recordedFileName = Path.GetFileName(block.RelativePath);
            bool fileNameMatchesOwnShape =
                string.Equals(recordedFileName, expectedFileName, StringComparison.OrdinalIgnoreCase);
            bool pathMatchesOwnShape =
                string.Equals(block.RelativePath, expectedPath, StringComparison.OrdinalIgnoreCase);
            if (fileNameMatchesOwnShape && (pathMatchesOwnShape || !block.RelativePath.Contains('/', StringComparison.Ordinal)))
            {
                format = known;
                return true;
            }

            format = default;
            return false;
        }

        foreach (SquadHookBlockFormat candidate in Enum.GetValues<SquadHookBlockFormat>())
        {
            if (string.Equals(RelativePath(candidate), block.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

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

        if (format is SquadHookBlockFormat.Cursor or SquadHookBlockFormat.Claude)
        {
            if (obj["hooks"] is JsonArray cursorHooks)
            {
                return cursorHooks.Count > 0
                    && cursorHooks.All(hook => hook is JsonObject hookObj
                        && HasSignature(hookObj["command"], Harness(format)));
            }

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
    /// Replaces Squad's entries in <paramref name="filePath"/> with <paramref name="preToolUse"/>,
    /// <paramref name="postToolUse"/> and, for Factory, <paramref name="subagentStop"/>, appending
    /// them at the end of each owned container (or setting the Antigravity group key). A missing
    /// or blank file starts from the minimal document. Returns the owned entries written.
    /// </summary>
    public static IReadOnlyList<SquadHookOwnedEntry> SpliceFile(
        SquadHookBlockFormat format,
        string filePath,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse,
        IReadOnlyList<JsonNode>? subagentStop = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(preToolUse);
        ArgumentNullException.ThrowIfNull(postToolUse);
        RefuseForeignSubagentStop(format, subagentStop);

        string? existing = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        string content;
        IReadOnlyList<SquadHookOwnedEntry> owned;
        try
        {
            (content, owned) = SpliceContent(format, existing, preToolUse, postToolUse, subagentStop);
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

        JsonObject? parent = format is SquadHookBlockFormat.Cursor or SquadHookBlockFormat.Codex or SquadHookBlockFormat.Claude
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
            if (TryReanchor(format, root, claimed, entry))
            {
                continue;
            }

            drift.Add(new SquadHookDrift(entry.Pointer, entry.WasMissing ? MissingReason : ModifiedReason));
        }

        return drift;
    }

    /// <summary>
    /// Re-anchors one unanchored entry to an unclaimed index of its container, trying each spelling
    /// of that container, and claims the index it lands on. A receipt recorded before the PascalCase
    /// migration names a lower-case container that now sits under its PascalCase name, so it reads
    /// as moved rather than missing.
    /// </summary>
    private static bool TryReanchor(
        SquadHookBlockFormat format,
        JsonObject root,
        Dictionary<string, HashSet<int>> claimed,
        UnanchoredEntry entry)
    {
        foreach (string spelling in ContainerSpellings(format, entry.ContainerPointer))
        {
            if (ResolvePointer(root, spelling) is not JsonArray container)
            {
                continue;
            }

            HashSet<int> taken = ClaimedIn(claimed, spelling);
            int found = IndexOfUnclaimedDigest(container, entry.Digest, taken);
            if (found >= 0)
            {
                taken.Add(found);
                return true;
            }
        }

        return false;
    }

    /// <summary>The spellings one container pointer may be written under. Only Claude has two.</summary>
    private static string[] ContainerSpellings(SquadHookBlockFormat format, string containerPointer)
    {
        if (format != SquadHookBlockFormat.Claude)
        {
            return [containerPointer];
        }

        return containerPointer switch
        {
            "/hooks/PreToolUse" or "/hooks/preToolUse" => ["/hooks/PreToolUse", "/hooks/preToolUse"],
            "/hooks/PostToolUse" or "/hooks/postToolUse" => ["/hooks/PostToolUse", "/hooks/postToolUse"],
            _ => [containerPointer],
        };
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

    private static void RefuseForeignSubagentStop(SquadHookBlockFormat format, IReadOnlyList<JsonNode>? subagentStop)
    {
        if (format != SquadHookBlockFormat.Factory && subagentStop is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Only the Factory hook file holds SubagentStop entries; '{Harness(format)}' does not.");
        }
    }

    /// <summary>
    /// Splices Squad's entries into hook-file <paramref name="existing"/> content without
    /// touching the disk: the single implementation behind both <see cref="SpliceFile"/> and
    /// the deployment plan, so a dry run and a real install can never disagree. A missing
    /// or blank file starts from the format's minimal document. Throws
    /// <see cref="JsonException"/> or <see cref="InvalidOperationException"/> when the
    /// content is not a usable hook file, leaving the caller to decide how to report it.
    /// </summary>
    /// <remarks>
    /// <paramref name="subagentStop"/> is accepted for Factory only. Every other format keeps
    /// its two containers, so its file bytes never change; a non-empty list for any other
    /// format is a caller bug and is refused rather than silently dropped.
    /// </remarks>
    public static (string Content, IReadOnlyList<SquadHookOwnedEntry> Owned) SpliceContent(
        SquadHookBlockFormat format,
        string? existing,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse,
        IReadOnlyList<JsonNode>? subagentStop = null)
    {
        RefuseForeignSubagentStop(format, subagentStop);
        IReadOnlyList<JsonNode> stop = subagentStop ?? [];

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
            // Only Factory's SubagentStop container is optional: a file without it gets one only
            // when there is a stop entry to write, so a Factory file that never had one gains no key.
            bool hadStopContainer = root.ContainsKey(SubagentStopContainer);
            if (format == SquadHookBlockFormat.Claude)
            {
                RemoveSquadFromLegacyClaudeContainers(root, dropEmptied: true);
            }

            var containers = OwnedArrays(format, root, create: true);
            // Positional with the containers OwnedArrays returns: create: true yields every
            // container the format holds, in the order listed in RootContainerNames.
            IReadOnlyList<JsonNode>[] managed = format == SquadHookBlockFormat.Factory
                ? [preToolUse, postToolUse, stop]
                : [preToolUse, postToolUse];
            for (int i = 0; i < containers.Count; i++)
            {
                (string pointer, JsonArray array) = containers[i];
                RemoveSquadEntries(format, array);

                foreach (JsonNode entry in managed[i])
                {
                    JsonNode projected = ProjectEntry(format, entry);
                    array.Add(projected);
                    string entryPointer = $"{pointer}/{array.Count - 1}";
                    owned.Add(new SquadHookOwnedEntry(entryPointer, CanonicalDigest(projected)));
                }
            }

            if (format == SquadHookBlockFormat.Factory && stop.Count == 0 && !hadStopContainer
                && root[SubagentStopContainer] is JsonArray { Count: 0 })
            {
                root.Remove(SubagentStopContainer);
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
            if (format == SquadHookBlockFormat.Claude)
            {
                RemoveSquadFromLegacyClaudeContainers(root, dropEmptied: false);
            }

            foreach ((_, JsonArray array) in OwnedArrays(format, root, create: false))
            {
                RemoveSquadEntries(format, array);
            }
        }

        return Serialize(root);
    }

    /// <summary>
    /// Removes Squad's entries from a container's array, keeping every user entry in its place.
    /// </summary>
    private static void RemoveSquadEntries(SquadHookBlockFormat format, JsonArray array)
    {
        for (int index = array.Count - 1; index >= 0; index--)
        {
            if (IsSquadEntry(format, array[index]))
            {
                array.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// Removes Squad's entries from the lower-case <c>preToolUse</c> and <c>postToolUse</c> containers
    /// a file written before the PascalCase fix holds. Claude Code never reads those, so they are
    /// retired here. When <paramref name="dropEmptied"/> is set, a container this removal leaves
    /// empty is deleted; one still holding user entries stays as the user wrote it.
    /// </summary>
    private static void RemoveSquadFromLegacyClaudeContainers(JsonObject root, bool dropEmptied)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return;
        }

        foreach (string name in LegacyClaudeContainers)
        {
            if (hooks[name] is not JsonArray array)
            {
                continue;
            }

            bool hadEntries = array.Count > 0;
            RemoveSquadEntries(SquadHookBlockFormat.Claude, array);
            if (dropEmptied && hadEntries && array.Count == 0)
            {
                hooks.Remove(name);
            }
        }
    }

    private static void CollectOwned(JsonObject group, string prefix, List<SquadHookOwnedEntry> owned)
    {
        // Antigravity's group never holds SubagentStop, so this reads it as absent there.
        foreach (string container in AllContainers)
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
            SquadHookBlockFormat.Claude => ClaudeArrays(root, create),
            SquadHookBlockFormat.Codex => PrefixedArrays(root, "/hooks", create),
            SquadHookBlockFormat.Factory or SquadHookBlockFormat.Devin => RootArrays(format, root, create),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };
    }

    /// <summary>
    /// The containers a format's root holds, in the order Squad writes them. Devin shares the
    /// root shape with Factory but holds no SubagentStop, so only Factory gains the third name.
    /// </summary>
    private static string[] RootContainerNames(SquadHookBlockFormat format) =>
        format == SquadHookBlockFormat.Factory ? AllContainers : PreAndPostContainers;

    private static List<(string Pointer, JsonArray Array)> CursorArrays(JsonObject root, bool create)
    {
        bool hasRootArrays = root.ContainsKey("preToolUse") || root.ContainsKey("PreToolUse")
            || root.ContainsKey("postToolUse") || root.ContainsKey("PostToolUse");
        JsonObject hooks;
        string pointerPrefix;
        if (root["hooks"] is JsonObject nested)
        {
            hooks = nested;
            pointerPrefix = "/hooks/";
        }
        else if (hasRootArrays)
        {
            hooks = root;
            pointerPrefix = "/";
        }
        else if (create)
        {
            hooks = EnsureObject(root, "hooks");
            pointerPrefix = "/hooks/";
        }
        else
        {
            hooks = new JsonObject();
            pointerPrefix = "/hooks/";
        }

        List<(string Pointer, JsonArray Array)> containers = [];
        foreach ((string lowerName, string pascalName) in new[] { ("preToolUse", "PreToolUse"), ("postToolUse", "PostToolUse") })
        {
            string name = hooks.ContainsKey(pascalName) ? pascalName : lowerName;
            JsonArray? array = hooks[name] as JsonArray;
            if (array is null && hooks[name == lowerName ? pascalName : lowerName] is JsonArray alternate)
            {
                name = name == lowerName ? pascalName : lowerName;
                array = alternate;
            }

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

            containers.Add(($"{pointerPrefix}{name}", array));
        }

        return containers;
    }

    /// <summary>
    /// Claude Code reads only the PascalCase containers under <c>hooks</c>, so those are the only
    /// containers this shape owns: always the <c>hooks</c> object, always <c>PreToolUse</c> and
    /// <c>PostToolUse</c>, created when missing. A lower-case spelling a file already holds is
    /// retired by the callers, not read here.
    /// </summary>
    private static List<(string Pointer, JsonArray Array)> ClaudeArrays(JsonObject root, bool create) =>
        PrefixedArrays(root, "/hooks", create);

    private static List<(string Pointer, JsonArray Array)> PrefixedArrays(JsonObject root, string prefix, bool create)
    {
        JsonObject hooks = create ? EnsureObject(root, "hooks") : root["hooks"] as JsonObject ?? new JsonObject();
        return NamedArrays(hooks, prefix, create, PreAndPostContainers);
    }

    private static List<(string Pointer, JsonArray Array)> RootArrays(
        SquadHookBlockFormat format, JsonObject root, bool create) =>
        NamedArrays(root, string.Empty, create, RootContainerNames(format));

    private static List<(string Pointer, JsonArray Array)> NamedArrays(
        JsonObject parent, string prefix, bool create, IEnumerable<string> names)
    {
        List<(string Pointer, JsonArray Array)> containers = [];
        foreach (string name in names)
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
            SquadHookBlockFormat.Codex or SquadHookBlockFormat.Claude => new JsonObject { ["hooks"] = new JsonObject() },
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

        if (format == SquadHookBlockFormat.Cursor && obj["hooks"] is null)
        {
            JsonObject projected = new();
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

    private static bool HasSignature(JsonNode? node, string harness)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out string? command) || command is null)
        {
            return false;
        }

        if (string.Equals(command, CommandPrefix + harness, StringComparison.Ordinal)
            || string.Equals(command, CommandPrefix + harness + " --scope global", StringComparison.Ordinal)
            || string.Equals(command, WorkerGuardPrefix + harness, StringComparison.Ordinal))
        {
            return true;
        }

        int hookIndex = command.IndexOf("kyber-weave-arbiter hook", StringComparison.Ordinal);
        if (hookIndex >= 0)
        {
            string remainder = command[(hookIndex + "kyber-weave-arbiter hook".Length)..].Trim();
            if (string.Equals(remainder, "--harness " + harness, StringComparison.Ordinal)
                || string.Equals(remainder, "--harness " + harness + " --scope global", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Harness(SquadHookBlockFormat format) =>
        format switch
        {
            SquadHookBlockFormat.Cursor => "cursor",
            SquadHookBlockFormat.Claude => "claude",
            SquadHookBlockFormat.Codex => "codex",
            SquadHookBlockFormat.Factory => "factory",
            SquadHookBlockFormat.Devin => "devin",
            SquadHookBlockFormat.Antigravity => "antigravity",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown hook block format."),
        };

    /// <summary>
    /// The shape that owns <paramref name="target"/>'s hooks, or <see langword="null"/> when the
    /// target keeps no shared hook file. The inverse of <see cref="Harness"/>, kept beside it so the
    /// two cannot drift apart.
    /// </summary>
    private static SquadHookBlockFormat? FormatOwnedBy(SquadTarget target) =>
        target switch
        {
            SquadTarget.Cursor => SquadHookBlockFormat.Cursor,
            SquadTarget.Claude => SquadHookBlockFormat.Claude,
            SquadTarget.Codex => SquadHookBlockFormat.Codex,
            SquadTarget.Factory => SquadHookBlockFormat.Factory,
            SquadTarget.Devin => SquadHookBlockFormat.Devin,
            SquadTarget.Antigravity => SquadHookBlockFormat.Antigravity,
            _ => null,
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
