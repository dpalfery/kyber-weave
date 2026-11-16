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
}
