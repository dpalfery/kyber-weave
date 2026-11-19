using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        if (HeaderBlock.Parse(prompt).Headers.TryGetValue("LENS", out string? declared)
            && !string.Equals(declared, lens, StringComparison.Ordinal))
        {
            context.Log.WriteLine(
                $"Arbiter: review seat not saved: the dispatch's LENS header '{declared}' differs from its packet '{lens}.md'.");
            return;
        }

        string? reply = ReplyText(toolOutput);
        if (string.IsNullOrWhiteSpace(reply))
        {
            context.Log.WriteLine($"Arbiter: review seat '{lens}' not saved: the return has no reply text.");
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

        string text = reply;
        if (files.FileExists(seatFile))
        {
            string existing = files.ReadAllText(seatFile);
            if (existing.Contains(reply, StringComparison.Ordinal))
            {
                context.Log.WriteLine($"Arbiter: review seat '{lens}' already holds this reply; not appended again.");
                return;
            }

            text = existing.Length == 0
                ? reply
                : existing + (existing.EndsWith('\n') ? "\n" : "\n\n") + reply;
        }

        files.CreateDirectory(seatsFolder);
        files.WriteAllTextAtomic(seatFile, text);
        context.Log.WriteLine($"Arbiter: review seat '{lens}' saved to '{seatFile}'.");
    }

    /// <summary>The reply inside a tool result: the Copilot CLI result text, the text blocks of a content list, or plain text.</summary>
    /// <param name="toolOutput">The raw tool result.</param>
    /// <returns>The reply, or null when the result holds none.</returns>
    internal static string? ReplyText(string toolOutput)
    {
        string trimmed = toolOutput.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '"'))
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

            if (root.ValueKind != JsonValueKind.Object)
            {
                return toolOutput;
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
