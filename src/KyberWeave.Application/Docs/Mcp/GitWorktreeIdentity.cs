using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Mcp;

namespace KyberWeave.Data.Docs.Mcp;

/// <summary>Reads a working tree's identity from <c>git rev-parse</c> through the process seam, bounded by a five-second timeout.</summary>
public sealed class GitWorktreeIdentity(IProcessRunner process) : IGitWorktreeIdentity
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public bool TryRoots(string directory, out string? topLevel, out string? commonDirectory, out string? failure)
    {
        topLevel = null;
        commonDirectory = null;
        ProcessRunRequest request = new()
        {
            Program = "git",
            Arguments = ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir"],
            WorkingDirectory = directory,
            Timeout = GitTimeout,
            StandardInput = string.Empty,
        };

        try
        {
            ProcessRunResult result = process.RunAsync(request).GetAwaiter().GetResult();
            if (result.TimedOut)
            {
                failure = $"The process did not exit within {GitTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.";
                return false;
            }

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
            or FileNotFoundException
            or DirectoryNotFoundException
            or InvalidOperationException)
        {
            failure = exception.Message;
            return false;
        }
    }
}
