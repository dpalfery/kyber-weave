using KyberWeave.Contracts.Repositories;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Reads the TypeSafe key from the console: from standard input, or from a masked prompt.</summary>
public sealed class ConsoleArbiterKeyInput : IArbiterKeyInput
{
    /// <inheritdoc />
    public string? ReadFromStandardInput() => Console.In.ReadLine();

    /// <inheritdoc />
    public string? PromptHidden()
    {
        try
        {
            return AnsiConsole.Prompt(
                new TextPrompt<string>("TypeSafe API key (input hidden, never echoed):").Secret());
        }
        catch (InvalidOperationException)
        {
            // Non-interactive console: there is no key to read.
            return null;
        }
    }
}
