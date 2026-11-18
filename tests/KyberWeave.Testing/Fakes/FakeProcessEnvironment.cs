using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>A process environment a test configures: its directories, its platform facts and its variables.</summary>
/// <remarks>
/// Every value has a safe default that touches nothing real, so a unit test that only cares about one fact
/// states that fact and gets the rest from the defaults. No value is read from the running host.
/// </remarks>
public sealed class FakeProcessEnvironment : IProcessEnvironment
{
    private readonly Func<string> _homeDirectory;
    private readonly Func<string, string?>? _readVariable;

    /// <summary>Creates a fake over the values given, and the defaults for the rest.</summary>
    /// <param name="currentDirectory">The directory the process is said to have started in.</param>
    /// <param name="homeDirectory">The operator's home directory.</param>
    /// <param name="tempDirectory">The temporary directory.</param>
    /// <param name="userName">The user running the process.</param>
    /// <param name="machineName">The machine running the process.</param>
    /// <param name="operatingSystem">The operating-system family.</param>
    /// <param name="isFileSystemCaseInsensitive">Whether the file system ignores case in names.</param>
    /// <param name="isInputRedirected">Whether standard input is redirected.</param>
    /// <param name="readVariable">Reads an environment variable; null answers every variable with null.</param>
    public FakeProcessEnvironment(
        string currentDirectory = "/kw/work",
        string homeDirectory = "/kw/home",
        string tempDirectory = "/kw/temp",
        string userName = "kw-user",
        string machineName = "kw-machine",
        OperatingSystemKind operatingSystem = OperatingSystemKind.Unknown,
        bool isFileSystemCaseInsensitive = false,
        bool isInputRedirected = true,
        Func<string, string?>? readVariable = null)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(tempDirectory);
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(machineName);
        CurrentDirectory = currentDirectory;
        _homeDirectory = () => homeDirectory;
        TempDirectory = tempDirectory;
        UserName = userName;
        MachineName = machineName;
        OperatingSystem = operatingSystem;
        IsFileSystemCaseInsensitive = isFileSystemCaseInsensitive;
        IsInputRedirected = isInputRedirected;
        _readVariable = readVariable;
    }

    /// <summary>Creates a fake whose home is supplied as a delegate, read on each access.</summary>
    /// <param name="homeDirectory">Gets the home directory.</param>
    /// <param name="variables">Reads an environment variable; null answers every variable with null.</param>
    public FakeProcessEnvironment(Func<string> homeDirectory, Func<string, string?>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);
        _homeDirectory = homeDirectory;
        CurrentDirectory = "/kw/work";
        TempDirectory = "/kw/temp";
        UserName = "kw-user";
        MachineName = "kw-machine";
        OperatingSystem = OperatingSystemKind.Unknown;
        IsFileSystemCaseInsensitive = false;
        IsInputRedirected = true;
        _readVariable = variables;
    }

    /// <inheritdoc />
    public string CurrentDirectory { get; }

    /// <inheritdoc />
    public string HomeDirectory => _homeDirectory();

    /// <inheritdoc />
    public string TempDirectory { get; }

    /// <inheritdoc />
    public OperatingSystemKind OperatingSystem { get; }

    /// <inheritdoc />
    public string UserName { get; }

    /// <inheritdoc />
    public string MachineName { get; }

    /// <inheritdoc />
    public bool IsFileSystemCaseInsensitive { get; }

    /// <inheritdoc />
    public bool IsInputRedirected { get; }

    /// <inheritdoc />
    public string? GetVariable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length == 0 ? null : _readVariable?.Invoke(name);
    }
}
