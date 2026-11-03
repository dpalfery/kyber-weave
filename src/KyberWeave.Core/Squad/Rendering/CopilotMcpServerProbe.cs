using System.Text.Json;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Answers whether the Copilot CLI user MCP config declares a named server, for the operator channel gate.
/// </summary>
/// <remarks>
/// The answer is honest rather than optimistic: any failure to establish that the server is declared
/// (missing file, unreadable file, oversized file, malformed JSON, no <c>mcpServers</c> object) returns
/// <c>false</c>. A tool entry the agent cannot call is worse than a missing overlay, so absence is the
/// safe default.
///
/// Opening a FIFO blocks until a writer appears, and .NET exposes no file-type API that tells a FIFO from a
/// regular file without opening it. POSIX special files (FIFO, device, socket) all report zero bytes, and a
/// valid config is never empty. So the probe resolves a symlink to its final target, refuses any zero-length
/// target before opening it, and reads at most <see cref="MaxConfigBytes"/> bytes. A symlink is followed; its
/// target must itself be a regular file.
/// </remarks>
internal static class CopilotMcpServerProbe
{
    internal const string McpConfigFileName = "mcp-config.json";

    /// <summary>Upper bound on the config size the probe will read. Real MCP configs are a few kilobytes.</summary>
    internal const long MaxConfigBytes = 1024 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Returns <c>true</c> only when <c>&lt;copilotRoot&gt;/mcp-config.json</c> resolves to a non-empty, bounded
    /// regular file holding valid JSON whose <c>mcpServers</c> object contains <paramref name="serverName"/>
    /// as an exact key.
    /// </summary>
    internal static bool HasServer(string copilotRoot, string serverName)
    {
        string path = Path.Combine(copilotRoot, McpConfigFileName);
        try
        {
            FileInfo link = new(path);
            FileSystemInfo? resolved = link.LinkTarget is null
                ? link
                : link.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not FileInfo file || !file.Exists)
            {
                return false;
            }

            // Refused before any open: a zero-length target is a special file or an empty config, and a
            // special file must never be opened.
            if (file.Length is 0 || file.Length > MaxConfigBytes)
            {
                return false;
            }

            byte[] buffer = new byte[MaxConfigBytes + 1];
            int total = 0;
            using (FileStream stream = file.OpenRead())
            {
                // Bounded by the buffer, so a file that grows after the length check still cannot be read whole.
                int read;
                while (total < buffer.Length &&
                       (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                {
                    total += read;
                }
            }

            if (total > MaxConfigBytes)
            {
                return false;
            }

            // A UTF-8 byte-order mark is valid in a config file, and the byte overload of JsonDocument.Parse
            // rejects it, so it is skipped here rather than failing the probe.
            int start = total >= Utf8Bom.Length && buffer.AsSpan(0, Utf8Bom.Length).SequenceEqual(Utf8Bom)
                ? Utf8Bom.Length
                : 0;
            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(start, total - start));
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("mcpServers", out JsonElement servers)
                   && servers.ValueKind == JsonValueKind.Object
                   && servers.TryGetProperty(serverName, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
