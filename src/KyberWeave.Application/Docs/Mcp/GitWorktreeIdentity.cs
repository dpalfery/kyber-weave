using System.Globalization;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Mcp;

namespace KyberWeave.Data.Docs.Mcp;

/// <summary>Reads a working tree's identity from <c>git rev-parse</c> through the process seam, bounded by a five-second timeout.</summary>
public sealed class GitWorktreeIdentity(IProcessRunner process, IPathResolver paths, IProcessEnvironment environment)
    : IGitWorktreeIdentity
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<GitWorktreeRoots> TryRootsAsync(string directory, CancellationToken cancellationToken = default)
    {
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
            ProcessRunResult result = await process.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
            {
                return new GitWorktreeRoots(
                    null,
                    null,
                    $"The process did not exit within {GitTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
            }

            if (result.ExitCode != 0)
            {
                return new GitWorktreeRoots(null, null, CorpusProvenance.FirstLine(result.StandardError));
            }

            string[] lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                return new GitWorktreeRoots(null, null, "git reported no working tree");
            }

            string topLevel = paths.GetFullPath(lines[0].Trim(), environment.CurrentDirectory);
            string commonDirectory = paths.GetFullPath(lines[1].Trim(), environment.CurrentDirectory);
            return new GitWorktreeRoots(topLevel, commonDirectory, null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or FileNotFoundException
            or DirectoryNotFoundException
            or InvalidOperationException)
        {
            return new GitWorktreeRoots(null, null, exception.Message);
        }
    }
}
