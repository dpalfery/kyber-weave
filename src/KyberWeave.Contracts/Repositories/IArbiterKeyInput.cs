namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the TypeSafe key from the operator: from standard input, or from a masked prompt.</summary>
/// <remarks>The value is never echoed. A host with no console to prompt on answers null.</remarks>
public interface IArbiterKeyInput
{
    /// <summary>Reads one line of the key from standard input.</summary>
    /// <returns>The line, or null when standard input is closed.</returns>
    string? ReadFromStandardInput();

    /// <summary>Prompts for the key with input hidden.</summary>
    /// <returns>The key, or null when there is no interactive console to prompt on.</returns>
    string? PromptHidden();
}
