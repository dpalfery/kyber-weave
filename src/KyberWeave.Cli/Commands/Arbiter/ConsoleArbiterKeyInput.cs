using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Reads the TypeSafe key from the console: from standard input, or from a masked prompt.</summary>
/// <param name="console">The console the masked prompt reads from.</param>
/// <param name="standard">The console seam the standard-input line is read through.</param>
/// <remarks>
/// This is the console adapter of `IArbiterKeyInput`: it is the only class above the seams that reads a key from a
/// person, and it reaches both resources through the seams it is given, so a test scripts the key instead of typing it.
/// </remarks>
public sealed class ConsoleArbiterKeyInput(IAnsiConsole console, IConsole standard) : IArbiterKeyInput
{
    /// <inheritdoc />
    public string? ReadFromStandardInput() => standard.ReadLine();

    /// <inheritdoc />
    public string? PromptHidden()
    {
        try
        {
            return console.Prompt(
                new TextPrompt<string>("TypeSafe API key (input hidden, never echoed):").Secret());
        }
        catch (InvalidOperationException)
        {
            // Non-interactive console: there is no key to read.
            return null;
        }
    }
}
