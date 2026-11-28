using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Data.RunStart;

/// <summary>Keeps the run records as one JSON file per branch under the operator's home.</summary>
/// <param name="files">The file system the records live on.</param>
/// <param name="branches">Reads the branch a recorded worktree has checked out.</param>
/// <param name="paths">Resolves the directories two records are compared by.</param>
/// <param name="environment">Supplies the platform and the current directory those comparisons use.</param>
public sealed class FileRunStateStore(
    IWritableFileSystem files,
    ICheckedOutBranchReader branches,
    IPathResolver paths,
    IProcessEnvironment environment) : IRunStateStore
{
    /// <inheritdoc />
    public void Write(string homeDirectory, string branch, string baseRef, RunStartPlace? place = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string path = RunBaseStateFormat.StatePath(homeDirectory, branch);
        if (Path.GetDirectoryName(path) is not { } directory)
        {
            throw new InvalidOperationException($"Cannot determine the state directory for '{path}'.");
        }

        files.CreateDirectory(directory);
        files.WriteAllText(path, RunBaseStateFormat.Serialize(baseRef, branch, place));
    }

    /// <inheritdoc />
    public bool TryReadBaseRef(string homeDirectory, string branch, out string? baseRef)
    {
        baseRef = null;
        string path;
        try
        {
            path = RunBaseStateFormat.StatePath(homeDirectory, branch);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string json;
        try
        {
            json = files.ReadAllText(path);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }

        return RunBaseStateFormat.TryReadBaseRef(json, out baseRef);
    }

    /// <inheritdoc />
    public bool? HasLiveRun(string homeDirectory, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        string directory = RunBaseStateFormat.RunsDirectory(homeDirectory);
        List<string> recordFiles;
        try
        {
            if (!files.DirectoryExists(directory))
            {
                return false;
            }

            recordFiles = [.. files.EnumerateFiles(directory, "*" + RunBaseStateFormat.StateExtension, recursive: false)];
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }

        string wanted = RunBaseStateFormat.NormalizeDirectory(repository, paths, environment);
        foreach (string file in recordFiles)
        {
            string json;
            try
            {
                json = files.ReadAllText(file);
            }
            catch (Exception exception) when (FileFailure.Is(exception))
            {
                return null;
            }

            if (RunBaseStateFormat.TryReadRecord(json, out RunRecord? record)
                && record is not null
                && string.Equals(RunBaseStateFormat.NormalizeDirectory(record.Repository, paths, environment), wanted, RunBaseStateFormat.PathComparison(environment))
                && files.DirectoryExists(record.Worktree)
                && string.Equals(branches.Read(record.Worktree), record.Branch, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRecord>? ReadRecords(string homeDirectory)
    {
        string directory = RunBaseStateFormat.RunsDirectory(homeDirectory);
        List<RunRecord> records = [];
        try
        {
            if (!files.DirectoryExists(directory))
            {
                return records;
            }

            foreach (string file in files.EnumerateFiles(directory, "*" + RunBaseStateFormat.StateExtension, recursive: false))
            {
                if (RunBaseStateFormat.TryReadRecord(files.ReadAllText(file), out RunRecord? record) && record is not null)
                {
                    records.Add(record);
                }
            }
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }

        return records;
    }
}
