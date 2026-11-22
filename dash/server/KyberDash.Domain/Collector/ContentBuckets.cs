using KyberDash.Data.Collector;

namespace KyberDash.Domain;

/// <summary>
/// Canonical wire-name mapping for <see cref="ContentBucket"/>: the single shared helper behind
/// the OTLP span mappers, the OTLP log mapper, and the canon ingest pipeline.
/// </summary>
public static class ContentBuckets
{
    /// <summary>
    /// Maps a collected content-part wire name to its <see cref="ContentBucket"/>, returning
    /// <see langword="false"/> and the default bucket for anything unrecognized.
    /// </summary>
    /// <param name="bucket">Wire bucket name (for example <c>"user_prompt"</c>).</param>
    /// <param name="result">Mapped bucket on success; default otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="bucket"/> names a known bucket.</returns>
    public static bool TryMap(string bucket, out ContentBucket result)
    {
        switch (bucket)
        {
            case "system_prompt":
                result = ContentBucket.SystemPrompt;
                return true;
            case "tool_definitions":
                result = ContentBucket.ToolDefinitions;
                return true;
            case "tool_definition":
                result = ContentBucket.ToolDefinition;
                return true;
            case "instruction_context":
                result = ContentBucket.InstructionContext;
                return true;
            case "conversation_history":
                result = ContentBucket.ConversationHistory;
                return true;
            case "tool_result_content":
                result = ContentBucket.ToolResultContent;
                return true;
            case "user_prompt":
                result = ContentBucket.UserPrompt;
                return true;
            case "user_message":
                result = ContentBucket.UserMessage;
                return true;
            case "reasoning":
                result = ContentBucket.Reasoning;
                return true;
            default:
                result = default;
                return false;
        }
    }
}
