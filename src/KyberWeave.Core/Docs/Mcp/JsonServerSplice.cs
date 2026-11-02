using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>What a planned splice does to a document.</summary>
public enum JsonSpliceOutcome
{
    /// <summary>The entry is added, and <see cref="JsonSplicePlan.Document"/> is the result.</summary>
    Inserted,

    /// <summary>
    /// An entry with the server's name is already in the document. The plan carries no change:
    /// an existing entry is never merged, modified, or overwritten (5.5).
    /// </summary>
    ServerAlreadyPresent,

    /// <summary>The document could not be read as JSON, even leniently.</summary>
    MalformedDocument,

    /// <summary>
    /// The document is JSON, but not a document this harness could load: the root, or the
    /// server container along the key path, is not an object. Writing into it would replace a
    /// diagnosable refusal with a harness that silently starts with no tools (5.7).
    /// </summary>
    ForeignDocument
}

/// <summary>Where a planned insertion lands.</summary>
public enum JsonInsertionPoint
{
    /// <summary>The server container is absent, so the remaining key path is inserted too.</summary>
    AbsentContainer,

    /// <summary>The container exists and holds nothing, so the entry goes in with no comma.</summary>
    EmptyContainer,

    /// <summary>The container exists and holds other entries, so the entry goes in with a comma.</summary>
    NonEmptyContainer
}

/// <summary>One bounded change to a document: the bytes, and where they go.</summary>
/// <param name="Outcome">Whether a change is planned, and why not when it is not.</param>
/// <param name="InsertionPoint">Where the insertion lands, for an inserted plan.</param>
/// <param name="InsertOffset">
/// The byte index at which <paramref name="InsertedBytes"/> begins, so the result recomposes as
/// <c>input[..offset] + inserted + input[offset..]</c>. <c>-1</c> on a refusal.
/// </param>
/// <param name="InsertedBytes">The bytes the change adds. Empty on a refusal.</param>
/// <param name="Document">
/// The spliced document, or — on a refusal — the input, byte for byte. A refusal that mutates on
/// the way out is a partial write wearing a refusal's clothes.
/// </param>
/// <param name="Message">What was refused and why, for a refusal.</param>
/// <remarks>
/// The two byte arrays are the point of the type rather than an oversight: this operation is
/// defined on bytes, the caller hands <see cref="Document"/> straight to the writer, and a refusal
/// must hand back the very input it was given so that "nothing was written" is provable by identity.
/// A collection interface in their place would copy at every access, in the one place where the
/// guarantee is about which bytes exist.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1819:Properties should not return arrays",
    Justification = "The document's bytes are the operation's result, and each is read once and written once.")]
public sealed record JsonSplicePlan(
    JsonSpliceOutcome Outcome,
    JsonInsertionPoint InsertionPoint,
    int InsertOffset,
    byte[] InsertedBytes,
    byte[] Document,
    string? Message = null);

/// <summary>
/// Computes the one bounded insertion that wires a server into a harness's JSON configuration,
/// without re-serializing the document.
/// </summary>
/// <remarks>
/// <para>
/// Every write this feature makes is a byte splice: the document is read for its structure, the
/// byte offset of the target container is located, and the entry is inserted at that offset. No
/// harness document is ever parsed into a model and written back out, because the bytes around the
/// insert are the operator's — their comments, their key order, their trailing commas, their
/// byte-order mark — and a re-serialization silently rewrites all of it (5.6).
/// </para>
/// <para>
/// The read is lenient (<see cref="JsonCommentHandling.Skip"/> and
/// <see cref="JsonReaderOptions.AllowTrailingCommas"/>), because a commented or comma-tolerant file
/// is what editors leave behind and is a valid target for a harness. Lenient on the read side does
/// not mean lenient on the write side: the inserted text is formatted to the same 2-space indents
/// the runbook examples use, so a hand-edited document keeps looking hand-edited.
/// </para>
/// <para>
/// The plan is computed in full before a caller writes anything, which is what makes "no partial
/// writes" hold: a document that cannot be wired produces a refusal and the untouched input, never
/// a half-applied change.
/// </para>
/// </remarks>
public static class JsonServerSplice
{
    /// <summary>The indent unit of every document this feature formats.</summary>
    private const int IndentWidth = 2;

    /// <summary>
    /// What the declaration writes where a repository root belongs: the runbook's "this repository"
    /// spelling. It is the token a harness that substitutes a workspace variable has to receive in
    /// its own spelling instead (5.11).
    /// </summary>
    internal const string RepositoryRootToken = ".";

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Plans the insertion of one entry into a JSON document.</summary>
    /// <param name="document">The document's bytes, as read from disk.</param>
    /// <param name="serverKeyPath">The key path from the root to the map of servers.</param>
    /// <param name="serverName">The name the entry is keyed by.</param>
    /// <param name="entryJson">The entry body, as a JSON object.</param>
    /// <returns>
    /// The plan: either the inserted bytes and the document they produce, or the refusal and the
    /// input unchanged.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The key path is empty, or <paramref name="entryJson"/> is not a JSON object — a body that is
    /// malformed JSON is that same refusal, not a parse failure escaping to the caller.
    /// </exception>
    public static JsonSplicePlan Plan(
        byte[] document,
        IReadOnlyList<string> serverKeyPath,
        string serverName,
        string entryJson)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(serverKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryJson);

        if (serverKeyPath.Count == 0)
        {
            throw new ArgumentException(
                "a server container is reached by at least one key, so the key path cannot be empty.",
                nameof(serverKeyPath));
        }

        if (ParseEntry(entryJson) is not JsonObject entry)
        {
            throw new ArgumentException(
                $"an entry body must be a JSON object, but '{entryJson}' is not one.",
                nameof(entryJson));
        }

        int bomLength = ByteOrderMarkLength(document);
        Layout layout = Locate(document.AsSpan(bomLength), serverKeyPath, serverName);

        switch (layout.Kind)
        {
            case LayoutKind.Malformed:
                return Refuse(JsonSpliceOutcome.MalformedDocument, document, layout.Message);
            case LayoutKind.Foreign:
                return Refuse(JsonSpliceOutcome.ForeignDocument, document, layout.Message);
            case LayoutKind.ServerAlreadyPresent:
                return Refuse(
                    JsonSpliceOutcome.ServerAlreadyPresent,
                    document,
                    $"the document already holds an entry named '{serverName}'; the repair never "
                        + "modifies, merges, or overwrites an existing entry");
            default:
                break;
        }

        // The member is indented one level inside the object it is spliced into, which is `level`
        // keys below the root. The closing brace of that object sits at the object's own indent.
        int memberIndent = IndentWidth * (layout.Level + 1);
        string member = Member(layout.RemainingPath, 0, serverName, entry, memberIndent);

        // An empty object's closing brace follows its opening brace immediately, so the inserted
        // text supplies the newline and the closing brace's indent and no comma is needed. A
        // non-empty object already has a first member with its own leading newline, so the
        // inserted member ends with the comma that separates the two.
        string text = layout.ParentEmpty
            ? "\n" + member + "\n" + Indent(IndentWidth * layout.Level)
            : "\n" + member + ",";

        byte[] insertedBytes = Encoding.UTF8.GetBytes(text);
        int offset = bomLength + layout.Brace + 1;

        byte[] spliced = [.. document.AsSpan(0, offset), .. insertedBytes, .. document.AsSpan(offset)];

        return new JsonSplicePlan(
            JsonSpliceOutcome.Inserted,
            layout.Point,
            offset,
            insertedBytes,
            spliced);
    }

    /// <summary>
    /// The length of the leading byte-order mark, or zero. It matters to every caller that reads
    /// this document as JSON, because the reader sees the mark as an invalid token.
    /// </summary>
    internal static int ByteOrderMarkLength(ReadOnlySpan<byte> document) =>
        document.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;

    /// <summary>
    /// Renders a JSON document the way this feature formats every document it creates, so a created
    /// file and an inserted entry are formatted by one set of rules rather than two.
    /// </summary>
    internal static string RenderDocument(JsonNode? document) => Render(document, indent: 0);

    /// <summary>
    /// The parsed entry body, or null when it is valid JSON that is not an object.
    /// </summary>
    /// <remarks>
    /// A body that is not JSON at all is the caller's argument being wrong in exactly the way the
    /// contract names, so <see cref="JsonException"/> is translated rather than escaping as itself:
    /// the caller passed a string this method could not read, and the refusal it needs to handle is
    /// the same one a well-formed non-object produces.
    /// </remarks>
    /// <exception cref="ArgumentException">The body cannot be parsed as JSON.</exception>
    private static JsonNode? ParseEntry(string entryJson)
    {
        try
        {
            return JsonNode.Parse(entryJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"an entry body must be a JSON object, but '{entryJson}' cannot be read as JSON: {exception.Message}",
                nameof(entryJson),
                exception);
        }
    }

    private static JsonSplicePlan Refuse(JsonSpliceOutcome outcome, byte[] document, string? message) =>
        new(outcome, JsonInsertionPoint.AbsentContainer, -1, [], document, message);

    /// <summary>
    /// Walks the document to the server container and reports where an insertion would land, or why
    /// there is none.
    /// </summary>
    private static Layout Locate(ReadOnlySpan<byte> body, IReadOnlyList<string> keyPath, string serverName)
    {
        try
        {
            Utf8JsonReader reader = new(
                body,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            if (!reader.Read())
                return Malformed("the document is empty");

            if (reader.TokenType != JsonTokenType.StartObject)
                return Foreign("its root is not a JSON object");

            int brace = checked((int)reader.TokenStartIndex);
            int level = 0;

            while (true)
            {
                PropertyScan scan = ScanProperty(ref reader, keyPath[level]);

                if (!scan.Found)
                {
                    // The key path stops inside this object, so the rest of the chain is inserted
                    // here. For a one-key path that object is the root, which is the ordinary case.
                    Drain(ref reader);

                    return new Layout(
                        LayoutKind.Absent,
                        JsonInsertionPoint.AbsentContainer,
                        brace,
                        scan.Empty,
                        [.. keyPath.Skip(level)],
                        level,
                        null);
                }

                if (scan.TokenType != JsonTokenType.StartObject)
                    return Foreign($"'{keyPath[level]}' is not a JSON object");

                brace = scan.Start;
                level++;

                if (level < keyPath.Count)
                    continue;

                PropertyScan named = ScanProperty(ref reader, serverName);

                // The rest of the document is read even once the answer is known: a document with
                // trailing data is not one the harness can load, so it is malformed rather than a
                // splice target with a tail nobody looked at.
                Drain(ref reader);

                return named.Found
                    ? new Layout(LayoutKind.ServerAlreadyPresent, default, -1, false, [], level, null)
                    : new Layout(
                        named.Empty ? LayoutKind.EmptyContainer : LayoutKind.NonEmptyContainer,
                        named.Empty ? JsonInsertionPoint.EmptyContainer : JsonInsertionPoint.NonEmptyContainer,
                        brace,
                        named.Empty,
                        [],
                        level,
                        null);
            }
        }
        catch (JsonException exception)
        {
            return Malformed(exception.Message);
        }
    }

    /// <summary>
    /// Advances the reader through one object looking for a property name, and reports whether the
    /// object was empty and — when found — where the value starts.
    /// </summary>
    private static PropertyScan ScanProperty(ref Utf8JsonReader reader, string name)
    {
        bool empty = true;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new PropertyScan(Found: false, empty, JsonTokenType.None, Start: -1);

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException($"expected a property name in a JSON object, found {reader.TokenType}");

            empty = false;

            if (!string.Equals(reader.GetString(), name, StringComparison.Ordinal))
            {
                reader.Skip();
                continue;
            }

            if (!reader.Read())
                throw new JsonException($"the document ended after the property name '{name}'");

            return new PropertyScan(Found: true, Empty: false, reader.TokenType, checked((int)reader.TokenStartIndex));
        }

        throw new JsonException("the document ended before its JSON object closed");
    }

    /// <summary>Reads the remaining document, so a trailing-garbage document is caught.</summary>
    private static void Drain(ref Utf8JsonReader reader)
    {
        while (reader.Read())
        {
        }
    }

    /// <summary>
    /// Builds one member — the entry, wrapped in whatever key path remains — at a given indent.
    /// </summary>
    private static string Member(
        IReadOnlyList<string> path,
        int index,
        string serverName,
        JsonNode entry,
        int indent)
    {
        if (index == path.Count)
            return Indent(indent) + Quote(serverName) + ": " + Render(entry, indent);

        string inner = Member(path, index + 1, serverName, entry, indent + IndentWidth);

        return Indent(indent) + Quote(path[index]) + ": {\n" + inner + "\n" + Indent(indent) + "}";
    }

    /// <summary>
    /// Renders a value at an indent, keeping an array of scalars on one line — the shape the
    /// runbook's examples use for <c>args</c> — and putting anything nested on its own lines.
    /// </summary>
    private static string Render(JsonNode? node, int indent)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject value:
                return RenderObject(value, indent);
            case JsonArray value:
                return RenderArray(value, indent);
            default:
                return node.ToJsonString();
        }
    }

    private static string RenderObject(JsonObject value, int indent)
    {
        if (value.Count == 0)
            return "{}";

        int inner = indent + IndentWidth;
        StringBuilder builder = new();
        builder.Append("{\n");

        bool first = true;
        foreach (KeyValuePair<string, JsonNode?> property in value)
        {
            if (!first)
                builder.Append(",\n");

            first = false;
            builder.Append(Indent(inner))
                .Append(Quote(property.Key))
                .Append(": ")
                .Append(Render(property.Value, inner));
        }

        return builder.Append('\n').Append(Indent(indent)).Append('}').ToString();
    }

    private static string RenderArray(JsonArray value, int indent)
    {
        if (value.Count == 0)
            return "[]";

        if (value.All(item => item is JsonValue))
            return "[" + string.Join(", ", value.Select(item => Render(item, indent))) + "]";

        int inner = indent + IndentWidth;
        StringBuilder builder = new();
        builder.Append("[\n");

        bool first = true;
        foreach (JsonNode? item in value)
        {
            if (!first)
                builder.Append(",\n");

            first = false;
            builder.Append(Indent(inner)).Append(Render(item, inner));
        }

        return builder.Append('\n').Append(Indent(indent)).Append(']').ToString();
    }

    private static string Indent(int width) => new(' ', width);

    /// <summary>
    /// A JSON string literal. <c>JsonSerializer</c> owns the escaping so a server name or an
    /// argument containing a quote cannot break out of the document.
    /// </summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static Layout Malformed(string? message) =>
        new(LayoutKind.Malformed, default, -1, false, [], 0, $"it is not valid JSON: {message}");

    private static Layout Foreign(string reason) =>
        new(
            LayoutKind.Foreign,
            default,
            -1,
            false,
            [],
            0,
            $"it is not a document this harness could load: {reason}");

    /// <summary>What the walk established about where an insertion goes.</summary>
    private enum LayoutKind
    {
        /// <summary>The container is absent and the remaining key path is inserted.</summary>
        Absent,

        /// <summary>The container exists and holds nothing.</summary>
        EmptyContainer,

        /// <summary>The container exists and holds at least one entry.</summary>
        NonEmptyContainer,

        /// <summary>An entry with the server's name is already there.</summary>
        ServerAlreadyPresent,

        /// <summary>The document cannot be read as JSON.</summary>
        Malformed,

        /// <summary>The document is JSON but not one this harness could load.</summary>
        Foreign
    }

    /// <summary>The located insertion point, or the refusal.</summary>
    /// <param name="Kind">What the walk found.</param>
    /// <param name="Point">The insertion point, for a writable kind.</param>
    /// <param name="Brace">The body-relative index of the <c>{</c> the insertion follows.</param>
    /// <param name="ParentEmpty">Whether that object holds nothing, so no comma is needed.</param>
    /// <param name="RemainingPath">The key path still to insert, from the object's members down.</param>
    /// <param name="Level">How many keys were matched, which fixes the insertion's indent.</param>
    /// <param name="Message">Why a refusal was made.</param>
    private readonly record struct Layout(
        LayoutKind Kind,
        JsonInsertionPoint Point,
        int Brace,
        bool ParentEmpty,
        IReadOnlyList<string> RemainingPath,
        int Level,
        string? Message);

    /// <summary>What scanning one object for a property established.</summary>
    /// <param name="Found">Whether the property is there.</param>
    /// <param name="Empty">Whether the object held no properties at all.</param>
    /// <param name="TokenType">The token the property's value starts with, when found.</param>
    /// <param name="Start">The value's body-relative start offset, when found.</param>
    private readonly record struct PropertyScan(bool Found, bool Empty, JsonTokenType TokenType, int Start);
}
