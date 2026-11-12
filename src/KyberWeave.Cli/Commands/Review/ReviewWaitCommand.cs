using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Waits for a <c>review gates --out</c> run to finish and reports its result. This type is the
/// command skeleton only: it is registered so the verb parses, and it does nothing yet.
/// </summary>
/// <remarks>
/// A wait is done through marker files written by the gates run rather than by polling the process
/// table. <c>pgrep -f</c> matches the waiting shell's own command line, so a loop built on it never
/// ends, which is why the verb exists.
/// </remarks>
public sealed class ReviewWaitCommand : Command<ReviewWaitSettings>
{
    /// <inheritdoc />
    public override int Execute(
        CommandContext context, ReviewWaitSettings settings, CancellationToken cancellationToken) => 1;
}
