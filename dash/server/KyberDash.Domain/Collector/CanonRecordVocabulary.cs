using System.Globalization;
using KyberDash.Data.Collector;

namespace KyberDash.Domain;

/// <summary>
/// The one record wire vocabulary shared by every OTLP mapping path: wire strings parse into
/// <see cref="RecordOp"/>, <see cref="RecordKind"/>, and <see cref="RecordStatus"/> values going
/// in, and format back out going out, while ingest problems share one unique-key rule.
/// </summary>
/// <remarks>
/// The relational ingest pipeline (<c>KyberDash.Application</c>), the harness span/log mappers
/// (<c>KyberDash.Data</c>), and record hydration all route through these helpers so the wire
/// strings can never disagree between layers. It lives beside <see cref="CanonValidation"/>
/// rather than in the collector DTO namespace, which the collector tests reserve for
/// schema-mapped data transfer types.
/// </remarks>
public static class CanonRecordVocabulary
{
    /// <summary>Parses a wire operation name into a <see cref="RecordOp"/>.</summary>
    /// <param name="op">Wire operation name, for example <c>llm.invoke</c>.</param>
    /// <returns>The matching operation, or <see cref="RecordOp.Unspecified"/> when unknown.</returns>
    public static RecordOp ParseRecordOp(string op) => op switch
    {
        "llm.invoke" => RecordOp.LlmInvoke,
        "tool.invoke" => RecordOp.ToolInvoke,
        "agent.invoke" => RecordOp.AgentInvoke,
        _ => RecordOp.Unspecified,
    };

    /// <summary>Formats a <see cref="RecordOp"/> as its wire operation name.</summary>
    /// <param name="op">Operation to format.</param>
    /// <returns>Wire operation name, or <c>unspecified</c> when unknown.</returns>
    public static string FormatRecordOp(RecordOp op) => op switch
    {
        RecordOp.LlmInvoke => "llm.invoke",
        RecordOp.ToolInvoke => "tool.invoke",
        RecordOp.AgentInvoke => "agent.invoke",
        _ => "unspecified",
    };

    /// <summary>Parses a wire span-kind name into a <see cref="RecordKind"/>.</summary>
    /// <param name="kind">Wire span-kind name, for example <c>server</c>.</param>
    /// <returns>The matching kind, or <see cref="RecordKind.Unspecified"/> when unknown.</returns>
    public static RecordKind ParseRecordKind(string kind) => kind switch
    {
        "internal" => RecordKind.Internal,
        "server" => RecordKind.Server,
        "client" => RecordKind.Client,
        "producer" => RecordKind.Producer,
        "consumer" => RecordKind.Consumer,
        _ => RecordKind.Unspecified,
    };

    /// <summary>Formats a <see cref="RecordKind"/> as its wire span-kind name.</summary>
    /// <param name="kind">Kind to format.</param>
    /// <returns>Wire span-kind name, or <c>unspecified</c> when unknown.</returns>
    public static string FormatRecordKind(RecordKind kind) => kind switch
    {
        RecordKind.Internal => "internal",
        RecordKind.Server => "server",
        RecordKind.Client => "client",
        RecordKind.Producer => "producer",
        RecordKind.Consumer => "consumer",
        _ => "unspecified",
    };

    /// <summary>Parses a wire status name into a <see cref="RecordStatus"/>.</summary>
    /// <param name="status">Wire status name, for example <c>error</c>.</param>
    /// <returns>The matching status, or <see cref="RecordStatus.Unspecified"/> when unknown.</returns>
    public static RecordStatus ParseRecordStatus(string status) => status switch
    {
        "ok" => RecordStatus.Ok,
        "error" => RecordStatus.Error,
        "unset" => RecordStatus.Unset,
        _ => RecordStatus.Unspecified,
    };

    /// <summary>Formats a <see cref="RecordStatus"/> as its wire status name.</summary>
    /// <param name="status">Status to format.</param>
    /// <returns>Wire status name (<c>unset</c> for <see cref="RecordStatus.Unset"/>).</returns>
    public static string FormatRecordStatus(RecordStatus status) => status switch
    {
        RecordStatus.Ok => "ok",
        RecordStatus.Error => "error",
        RecordStatus.Unspecified => "unspecified",
        _ => "unset",
    };

    /// <summary>
    /// Builds a problem key unique within <paramref name="usedKeys"/>, recording each issued key.
    /// </summary>
    /// <param name="usedKeys">Keys already issued in this batch.</param>
    /// <param name="keyPrefix">Key namespace prefix, including its separator (for example <c>otlp:</c>).</param>
    /// <param name="id">Span or log identifier the problem attaches to.</param>
    /// <param name="code">Diagnostic problem code.</param>
    /// <param name="index">Batch index disambiguating a repeated base key.</param>
    /// <returns>A problem key not already present in <paramref name="usedKeys"/>.</returns>
    public static string UniqueProblemKey(HashSet<string> usedKeys, string keyPrefix, string id, string code, int index)
    {
        string baseKey = string.Create(CultureInfo.InvariantCulture, $"{keyPrefix}{id}:{code}");
        if (usedKeys.Add(baseKey))
        {
            return baseKey;
        }

        string indexedKey = string.Create(CultureInfo.InvariantCulture, $"{baseKey}:{index}");
        _ = usedKeys.Add(indexedKey);
        return indexedKey;
    }
}
