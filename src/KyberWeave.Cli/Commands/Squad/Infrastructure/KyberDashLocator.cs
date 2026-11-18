using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
///     Finds the <c>kyberdash</c> executable beside the running <c>kyber-weave</c>, then on <c>PATH</c>.
/// </summary>
/// <param name="environment">Names the operating system and the <c>PATH</c> variable.</param>
/// <param name="files">Says whether a candidate file exists.</param>
/// <param name="processPath">Gives the full path of the running <c>kyber-weave</c> binary, or null when it is unknown.</param>
internal sealed class KyberDashLocator(IProcessEnvironment environment, IFileSystem files, Func<string?> processPath)
{
    private readonly IProcessEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly IFileSystem _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly Func<string?> _processPath = processPath ?? throw new ArgumentNullException(nameof(processPath));

    /// <summary>Looks beside the running binary, then along <c>PATH</c>.</summary>
    /// <returns>The full path of <c>kyberdash</c>, or null when it is installed in neither place.</returns>
    public string? Locate()
    {
        string fileName = _environment.OperatingSystem == OperatingSystemKind.Windows ? "kyberdash.exe" : "kyberdash";

        string? installDirectory = Path.GetDirectoryName(_processPath());
        if (!string.IsNullOrEmpty(installDirectory))
        {
            string beside = Path.Combine(installDirectory, fileName);
            if (_files.FileExists(beside))
                return beside;
        }

        string? path = _environment.GetVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try
            {
                string candidate = Path.Combine(directory, fileName);
                if (_files.FileExists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters Path.Combine rejects cannot hold kyberdash; keep searching.
            }

        return null;
    }
}
