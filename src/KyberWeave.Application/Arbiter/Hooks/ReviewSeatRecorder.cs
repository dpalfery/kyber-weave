using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Saves a review lens dispatch's reply as its seat file, so a reviewer that cannot write files still leaves
/// the folder <c>review verdict</c> reads.
/// </summary>
/// <remarks>
/// The rules are in the architecture document, section "Review seat files". Nothing here throws for a refused or
/// failed write: the hook logs it and the dispatch continues.
/// </remarks>
public sealed partial class ReviewSeatRecorder(IWritableFileSystem files, IPathResolver paths)
{
    /// <summary>Saves the reply of one returned dispatch when it is a review seat dispatch.</summary>
    /// <param name="prompt">The dispatch prompt, with its <c>Packet:</c> line and, when it survived, its <c>LENS:</c> header.</param>
    /// <param name="toolOutput">The harness's tool result for the return, raw JSON or plain text.</param>
    /// <param name="context">The hook invocation: its log, repository root and run context.</param>
    public void Record(string prompt, string toolOutput, HookContext context)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(toolOutput);
        ArgumentNullException.ThrowIfNull(context);

        Match packet = PacketRegex().Match(prompt);
        if (!packet.Success)
        {
            return;
        }

        string packetPath = packet.Groups["path"].Value.Trim().Replace('\\', '/');
        Match shape = PacketPathRegex().Match(packetPath);
        if (!shape.Success)
        {
            return;
        }

        string lens = shape.Groups["lens"].Value;
        if (HeaderBlock.Parse(prompt).Headers.TryGetValue(DispatchHeaders.Lens, out string? declared)
            && !string.Equals(declared, lens, StringComparison.Ordinal))
        {
            context.Log.WriteLine(
                $"Arbiter: review seat not saved: the dispatch's LENS header '{declared}' differs from its packet '{lens}.md'.");
            return;
        }

        // With no active run, a relative path resolves against whichever checkout the hook process started in, which
        // may not be the worktree the review runs in; a seat written there is one the verdict never reads.
        if (!Path.IsPathFullyQualified(packetPath) && context.RunContext?.ActiveRun is null)
        {
            context.Log.WriteLine(
                $"Arbiter: review seat '{lens}' not saved: the dispatch names a relative packet path and no run is active to resolve it against.");
            return;
        }

        string? reply = ReplyText(toolOutput);
        if (string.IsNullOrWhiteSpace(reply))
        {
            context.Log.WriteLine($"Arbiter: review seat '{lens}' not saved: the return has no reply text.");
            return;
        }

        // Claude Code's delivery notice says where the report went; it is not the report, and saving it would leave
        // the verdict a seat with no findings section to read.
        if (ClaudeDeliveryWrapper.IsWrapper(reply))
        {
            context.Log.WriteLine(
                $"Arbiter: review seat '{lens}' not saved: the return is only the harness's delivery notice, not the report.");
            return;
        }

        string baseDirectory = context.RunContext?.Worktree ?? context.RepoRoot;
        string seatsFolder = paths.GetFullPath(shape.Groups["cycle"].Value + "/seats", baseDirectory);
        string seatFile = paths.GetFullPath(lens + ".md", seatsFolder);
        if (!InsideRunRoots(seatsFolder, context))
        {
            context.Log.WriteLine(
                $"Arbiter: review seat '{lens}' not saved: '{seatsFolder}' is outside the repository and its worktrees.");
            return;
        }

        if (files.GetLinkTarget(seatFile) is not null)
        {
            context.Log.WriteLine($"Arbiter: review seat '{lens}' not saved: '{seatFile}' is a link.");
            return;
        }

        reply = reply.Replace("\r\n", "\n", StringComparison.Ordinal);
        string text = reply;
        if (files.FileExists(seatFile))
        {
            string existing = files.ReadAllText(seatFile).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (AlreadyHolds(existing, reply))
            {
                context.Log.WriteLine($"Arbiter: review seat '{lens}' already holds this reply; not appended again.");
                return;
            }

            // A seat that holds only a delivery notice, saved before the notice was recognised, holds no reply to keep.
            text = existing.Length == 0 || ClaudeDeliveryWrapper.IsWrapper(existing)
                ? reply
                : existing.TrimEnd('\n') + "\n\n" + reply;
        }

        files.CreateDirectory(seatsFolder);
        files.WriteAllTextAtomic(seatFile, text);
        context.Log.WriteLine($"Arbiter: review seat '{lens}' saved to '{seatFile}'.");
    }

    /// <summary>The largest host-supplied tool result, in characters, this recorder parses.</summary>
    private const int MaxToolOutputCharacters = 262_144;

    /// <summary>The reply inside a tool result: the Copilot CLI result text, the text blocks of a content list, or plain text.</summary>
    /// <param name="toolOutput">The raw tool result.</param>
    /// <returns>The reply, or null when the result holds none.</returns>
    internal static string? ReplyText(string toolOutput)
    {
        // A host-supplied payload is bounded before it is scanned or parsed, so an oversize
        // result is refused rather than copied into a seat file that then grows without limit.
        if (toolOutput.Length > MaxToolOutputCharacters)
        {
            return null;
        }

        string trimmed = toolOutput.TrimStart();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] is not ('{' or '[' or '"' or '-' or >= '0' and <= '9' or 't' or 'f' or 'n'))
        {
            return toolOutput;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(toolOutput);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString();
            }

            // An array, a number, a boolean or null is the envelope's shape, not a reply a
            // reviewer wrote, so it writes no seat file.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (string name in (string[])["textResultForLlm", "result", "output", "text", "response"])
            {
                if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            if (root.TryGetProperty("content", out JsonElement content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                if (content.ValueKind == JsonValueKind.Array)
                {
                    StringBuilder joined = new();
                    foreach (JsonElement block in content.EnumerateArray())
                    {
                        if (block.ValueKind == JsonValueKind.Object
                            && block.TryGetProperty("text", out JsonElement text)
                            && text.ValueKind == JsonValueKind.String)
                        {
                            _ = joined.Length > 0 ? joined.Append("\n\n") : joined;
                            _ = joined.Append(text.GetString());
                        }
                    }

                    return joined.Length > 0 ? joined.ToString() : null;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return toolOutput;
        }
    }

    /// <summary>
    /// Whether the file already ends with this exact reply, as one of its blank-line-separated
    /// entries. A reply that is merely a substring of another entry is a different reply and is
    /// appended, so an exact-match rule never silently drops one.
    /// </summary>
    /// <param name="existing">The seat file's current text.</param>
    /// <param name="reply">The reply about to be saved.</param>
    /// <returns>True when saving it again would repeat an entry the file already holds.</returns>
    private static bool AlreadyHolds(string existing, string reply) =>
        string.Equals(existing, reply, StringComparison.Ordinal)
        || existing.EndsWith("\n\n" + reply, StringComparison.Ordinal);

    private bool InsideRunRoots(string folder, HookContext context)
    {
        List<string> roots = [context.RepoRoot];
        if (context.RunContext is { } run)
        {
            roots.Add(run.RepositoryRoot);
            roots.Add(run.Worktree);
            roots.AddRange(run.Worktrees);
        }

        return roots.Any(root => !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && paths.IsWithin(root, folder));
    }

    [GeneratedRegex(@"^[ \t]*Packet:[ \t]*(?<path>\S.*?)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PacketRegex();

    [GeneratedRegex(@"^(?<cycle>(?:.*/)?review/cycle-\d+)/packets/(?<lens>[a-z0-9][a-z0-9-]*)\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex PacketPathRegex();
}
