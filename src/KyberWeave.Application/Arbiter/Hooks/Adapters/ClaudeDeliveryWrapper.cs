namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>
/// Recognises the notice Claude Code puts in an <c>Agent</c> tool result when the sub-agent already delivered its
/// report through a <c>SubagentHandback</c> call: the result then says where the report went and that it "is not
/// repeated here", and holds no report of its own.
/// </summary>
/// <remarks>
/// Copied from the shape of <see cref="FactoryTranscript.IsBackgroundLaunch(string)"/>, with one deliberate
/// difference: Factory's launch notice only has to open the result, while this notice must be the whole reply. A
/// real report that quotes the notice — a reviewer explaining the defect this recogniser exists for, say — starts or
/// ends with something else and is kept, so a report is never dropped as a wrapper.
/// </remarks>
public static class ClaudeDeliveryWrapper
{
    /// <summary>The words the notice opens with.</summary>
    public const string Prefix = "This agent's report was delivered to you as a message from";

    /// <summary>The words the notice closes with.</summary>
    public const string Suffix = "it is not repeated here.";

    /// <summary>Whether a reply is the delivery notice and nothing else.</summary>
    /// <param name="reply">The reply text, or null when the result held none.</param>
    /// <returns>True when the trimmed reply opens with <see cref="Prefix"/> and closes with <see cref="Suffix"/>.</returns>
    public static bool IsWrapper(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        string trimmed = reply.Trim().TrimStart('﻿').Trim();
        return trimmed.Length >= Prefix.Length + Suffix.Length
            && trimmed.StartsWith(Prefix, StringComparison.Ordinal)
            && trimmed.EndsWith(Suffix, StringComparison.Ordinal);
    }
}
