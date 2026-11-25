using System.ComponentModel;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Data.RunStart;

/// <summary>Asks git which branch a checkout has checked out.</summary>
public sealed class GitCheckedOutBranchReader(IGitCommandRunner git) : ICheckedOutBranchReader
{
    /// <inheritdoc />
    public string? Read(string workingDirectory)
    {
        try
        {
            GitOutcome outcome = git.Run(workingDirectory, ["branch", "--show-current"]);
            string branch = outcome.Succeeded ? outcome.StandardOutput.Trim() : string.Empty;
            return string.IsNullOrWhiteSpace(branch) ? null : branch;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same question <c>run integrate</c> asks, with the same exit-code reading: 0 is an ancestor, 1 is not, and
    /// anything else is git failing to answer.
    /// </remarks>
    public bool? IsAncestor(string workingDirectory, string ancestor, string descendant)
    {
        try
        {
            GitOutcome outcome = git.Run(
                workingDirectory,
                ["-C", workingDirectory, "merge-base", "--is-ancestor", "--end-of-options", ancestor, descendant]);
            return outcome.ExitCode switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }
}
