using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Mcp;
using KyberWeave.Core.Processes;

namespace KyberWeave.Data.Docs.Mcp;

/// <summary>Reads a working tree's identity from <c>git rev-parse</c>, bounded by a five-second timeout.</summary>
public sealed class GitWorktreeIdentity : IGitWorktreeIdentity
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public bool TryRoots(string directory, out string? topLevel, out string? commonDirectory, out string? failure)
    {
        topLevel = null;
        commonDirectory = null;
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
            if (result.ExitCode != 0)
            {
                failure = CorpusProvenance.FirstLine(result.StandardError);
                return false;
            }

            string[] lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                failure = "git reported no working tree";
                return false;
            }

            topLevel = Path.GetFullPath(lines[0].Trim());
            commonDirectory = Path.GetFullPath(lines[1].Trim());
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or TimeoutException)
        {
            failure = exception.Message;
            return false;
        }
    }
}
