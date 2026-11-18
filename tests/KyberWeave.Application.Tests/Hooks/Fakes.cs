using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Hooks;

namespace KyberWeave.Application.Tests.Hooks;

internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
    private readonly HashSet<string> directories = new(StringComparer.Ordinal);
    private readonly HashSet<string> unreadable = new(StringComparer.Ordinal);

    public static string Root { get; } = Path.GetFullPath("/repo");

    public static string Abs(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    public FakeFileSystem WithFile(string relative, string content = "")
    {
        files[Abs(relative)] = content;
        return this;
    }

    public FakeFileSystem WithDirectory(string relative)
    {
        _ = directories.Add(Abs(relative));
        return this;
    }

    public FakeFileSystem WithUnreadableFile(string relative)
    {
        _ = WithFile(relative);
        _ = unreadable.Add(Abs(relative));
        return this;
    }

    public bool FileExists(string path) => files.ContainsKey(path);

    public bool DirectoryExists(string path) => directories.Contains(path);

    public string ReadAllText(string path) => unreadable.Contains(path) ? throw new UnauthorizedAccessException(path) : files[path];

    public byte[] ReadAllBytes(string path) => System.Text.Encoding.UTF8.GetBytes(ReadAllText(path));

    public IEnumerable<string> ReadLines(string path)
    {
        string[] lines = ReadAllText(path).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            lines[index] = lines[index].TrimEnd('\r');
        }

        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path));

    public long GetLength(string path) => System.Text.Encoding.UTF8.GetByteCount(ReadAllText(path));

    public DateTime GetLastWriteTimeUtc(string path) => files.ContainsKey(path) ? DateTime.UtcNow : default;

    public FileAttributes GetAttributes(string path) =>
        directories.Contains(path) ? FileAttributes.Directory
        : files.ContainsKey(path) ? FileAttributes.Normal
        : throw new FileNotFoundException(path);

    public IEnumerable<FileSystemEntry> EnumerateEntries(string directory) =>
        files.Keys.Where(f => Path.GetDirectoryName(f) == directory).Select(f => new FileSystemEntry(f, false, false))
            .Concat(directories.Where(d => Path.GetDirectoryName(d) == directory).Select(d => new FileSystemEntry(d, true, false)));

    public string? GetLinkTarget(string path) => null;

    public string? GetFinalLinkTarget(string path) => null;

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks) =>
        EnumerateFiles(directory, searchPattern, recursive);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        files.Keys.Where(f =>
            (recursive ? f.StartsWith(directory, StringComparison.Ordinal) : Path.GetDirectoryName(f) == directory)
            && (searchPattern.StartsWith('*') ? f.EndsWith(searchPattern[1..], StringComparison.Ordinal) : Path.GetFileName(f) == searchPattern));

    public IEnumerable<string> EnumerateDirectories(string directory) =>
        directories.Where(d => Path.GetDirectoryName(d) == directory);
}

internal sealed class FakeConfigSource(HooksConfig? config, Exception? failure = null) : IHooksConfigSource
{
    public HooksConfig Load(string repositoryRoot) => failure is not null ? throw failure : config!;
}

internal sealed class FakeTestClassResolver(TestClassResolution result) : ITestClassResolver
{
    public int Calls { get; private set; }

    public TestClassResolution Resolve(string repositoryRoot, string relativePath)
    {
        Calls++;
        return result;
    }
}

internal sealed class FakeClock : TimeProvider
{
    private readonly List<FakeTimer> timers = [];
    private long ticks;

    public void Advance(TimeSpan by)
    {
        ticks += (long)(by.TotalSeconds * Stopwatch.Frequency);
        foreach (FakeTimer timer in timers.Where(t => t.Due is { } due && due <= ticks).ToList())
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        FakeTimer timer = new(callback, state, ticks + (long)(dueTime.TotalSeconds * Stopwatch.Frequency));
        timers.Add(timer);
        return timer;
    }

    public override long GetTimestamp() => ticks;

    public override long TimestampFrequency => Stopwatch.Frequency;
}

internal sealed class FakeProcessRunner(FakeClock clock) : IProcessRunner
{
    public List<(IReadOnlyList<string> Argv, string Cwd, TimeSpan Timeout)> Calls { get; } = [];

    public Func<IReadOnlyList<string>, ProcessRunResult> Respond { get; set; } = _ => new ProcessRunResult(0, string.Empty, false);

    public TimeSpan CostPerRun { get; set; }

    public Task<ProcessRunResult> RunAsync(IReadOnlyList<string> argv, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((argv, workingDirectory, timeout));
        clock.Advance(CostPerRun);
        return Task.FromResult(Respond(argv));
    }
}

internal sealed class FakeTimer(TimerCallback callback, object? state, long due) : ITimer
{
    public long? Due { get; private set; } = due;

    public void Fire()
    {
        Due = null;
        callback(state);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period) => true;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
