using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using YamlDotNet.Core;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one fake for <see cref="IKyberWeaveConfigRepository"/>: the same reads and the same failure results as
/// <c>FileKyberWeaveConfigRepository</c>, answered from a dictionary of file text rather than from disk.
/// </summary>
/// <remarks>
/// A test arranges a repository by writing the config text it would have written to disk, so the parse and merge are
/// the shipped ones and only the file system is replaced. The paths it reports are full paths over
/// <see cref="Path.GetTempPath"/>; no directory is created and nothing is read.
/// </remarks>
public sealed class FakeKyberWeaveConfigRepository : IKyberWeaveConfigRepository
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _readFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly List<string> _roots = [];

    /// <summary>Creates the fake, with its tree probe answering from the files and directories it is given.</summary>
    public FakeKyberWeaveConfigRepository() => Tree = new ArrangedTreeProbe(this);

    /// <summary>
    /// Answers what the file system says about a gate's <c>working-directory</c>. It reads this fake's own arrangement, so a
    /// directory a test created exists here exactly as it would on disk.
    /// </summary>
    public IRepositoryTreeProbe Tree { get; }

    /// <summary>The repository roots <see cref="Load"/> and <see cref="TryLoad"/> were asked for, in call order.</summary>
    public IReadOnlyList<string> LoadedRoots => _roots;

    /// <summary>The config text the fake holds, by full path.</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>Adds or replaces the text at <paramref name="fullPath"/>.</summary>
    /// <param name="fullPath">The full path of the file.</param>
    /// <param name="yaml">The text the file holds.</param>
    public void WriteFile(string fullPath, string yaml) => _files[Path.GetFullPath(fullPath)] = yaml;

    /// <summary>Creates a directory, so a gate's <c>working-directory</c> can name one that exists.</summary>
    /// <param name="fullPath">The full path of the directory.</param>
    public void CreateDirectory(string fullPath) => _directories.Add(FullDirectory(fullPath));

    /// <summary>Writes a repository's <c>.kyber-weave/kyber-weave.yml</c>.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="yaml">The config text.</param>
    public void WriteRepositoryConfig(string repoRoot, string yaml) =>
        WriteFile(RepositoryConfigPath(repoRoot), yaml);

    /// <summary>Writes a legacy repository-root <c>kyber-weave.yml</c>.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="yaml">The config text.</param>
    public void WriteLegacyConfig(string repoRoot, string yaml) =>
        WriteFile(Path.Combine(Path.GetFullPath(repoRoot), KyberWeaveYamlParser.DefaultFileName), yaml);

    /// <summary>Writes the per-user config file under <paramref name="home"/>.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <param name="yaml">The config text.</param>
    public void WriteUserConfig(string home, string yaml) => WriteFile(UserConfigPath(home), yaml);

    /// <summary>The full path of a repository's <c>.kyber-weave/kyber-weave.yml</c>.</summary>
    /// <param name="repoRoot">The repository root.</param>
    public static string RepositoryConfigPath(string repoRoot) => Path.Combine(
        Path.GetFullPath(repoRoot),
        KyberWeaveYamlParser.DefaultDirectoryName,
        KyberWeaveYamlParser.DefaultFileName);

    /// <summary>The full path of a repository's legacy root <c>kyber-weave.yml</c>.</summary>
    /// <param name="repoRoot">The repository root.</param>
    public static string LegacyConfigPath(string repoRoot) =>
        Path.Combine(Path.GetFullPath(repoRoot), KyberWeaveYamlParser.DefaultFileName);

    /// <summary>The full path of the per-user config file under <paramref name="home"/>.</summary>
    /// <param name="home">The operator's home directory.</param>
    public static string UserConfigPath(string home) => UserHostConfigPath.Of(Path.GetFullPath(home));

    /// <summary>
    /// Makes every read of <paramref name="fullPath"/> fail until the returned scope is disposed, so a test can reach
    /// the result the real repository reports for a file that exists but cannot be read.
    /// </summary>
    /// <param name="fullPath">The full path of a file the fake holds.</param>
    /// <param name="error">The failure the read throws; an <see cref="IOException"/> when null.</param>
    /// <returns>A scope that restores the read when disposed.</returns>
    public IDisposable FailReads(string fullPath, Exception? error = null)
    {
        string path = Path.GetFullPath(fullPath);
        _readFailures[path] = error ?? new IOException($"The fake could not read '{path}'.");
        return new ReadFailureScope(this, path);
    }

    /// <inheritdoc />
    public KyberWeaveConfig Load(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);
        _roots.Add(root);

        string? path = ResolveConfigPath(root, configPath);
        return path is null ? KyberWeaveConfig.ProductDefaults : FromFile(path);
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);
        _roots.Add(root);
        string? path = null;

        try
        {
            path = ResolveConfigPath(root, configPath);
            if (path is null)
                return KyberWeaveConfigLoadResultFactory.Ok(KyberWeaveConfig.ProductDefaults, null);

            return KyberWeaveConfigLoadResultFactory.Ok(FromFile(path), path);
        }
        catch (YamlException ex)
        {
            return KyberWeaveConfigLoadResultFactory.Fail(ex.Message, path ?? configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return KyberWeaveConfigLoadResultFactory.Fail(ex.Message, path ?? configPath);
        }
    }

    /// <inheritdoc />
    public string? FindConfigPath(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        string defaultPath = RepositoryConfigPath(repoRoot);
        if (_files.ContainsKey(defaultPath))
            return defaultPath;

        string legacyPath = LegacyConfigPath(repoRoot);
        return _files.ContainsKey(legacyPath) ? legacyPath : null;
    }

    /// <inheritdoc />
    public KyberWeaveConfig LoadUserConfig(string homeDirectory, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string root = Path.GetFullPath(homeDirectory);
        string? path = ResolveUserConfigPath(root, configPath);
        if (path is null)
            return KyberWeaveConfig.ProductDefaults;

        if (!_files.ContainsKey(path))
        {
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                throw new FileNotFoundException(
                    $"Kyber-Weave config file not found: {path}",
                    path);
            }

            return KyberWeaveConfig.ProductDefaults;
        }

        return KyberWeaveConfigAssembler.FromUserYaml(ReadFile(path));
    }

    /// <inheritdoc />
    public KyberWeaveConfigLoadResult TryLoadUserConfig(string homeDirectory, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string root = Path.GetFullPath(homeDirectory);
        string? path = ResolveUserConfigPath(root, configPath);
        if (path is null)
            return KyberWeaveConfigLoadResultFactory.Ok(KyberWeaveConfig.ProductDefaults, path);

        if (!_files.ContainsKey(path))
        {
            return string.IsNullOrWhiteSpace(configPath)
                ? KyberWeaveConfigLoadResultFactory.Ok(KyberWeaveConfig.ProductDefaults, path)
                : KyberWeaveConfigLoadResultFactory.Fail($"Kyber-Weave config file not found: {path}", path);
        }

        try
        {
            return KyberWeaveConfigLoadResultFactory.Ok(KyberWeaveConfigAssembler.FromUserYaml(ReadFile(path)), path);
        }
        catch (YamlException ex)
        {
            return KyberWeaveConfigLoadResultFactory.Fail(ex.Message, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KyberWeaveConfigLoadResultFactory.Fail(ex.Message, path);
        }
    }

    private string? ResolveConfigPath(string repoRoot, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return FindConfigPath(repoRoot);

        string explicitPath = Path.IsPathRooted(configPath)
            ? configPath
            : Path.Combine(repoRoot, configPath);
        if (!_files.ContainsKey(Path.GetFullPath(explicitPath)))
        {
            throw new FileNotFoundException(
                $"Kyber-Weave config file not found: {explicitPath}",
                explicitPath);
        }

        return explicitPath;
    }

    private static string? ResolveUserConfigPath(string homeDirectory, string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            return Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(homeDirectory, configPath);
        }

        return UserHostConfigPath.Of(homeDirectory);
    }

    private KyberWeaveConfig FromFile(string path)
    {
        string yaml = ReadFile(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetFullPath(path);
        string root = Path.GetFileName(directory) == KyberWeaveYamlParser.DefaultDirectoryName
            ? Path.GetDirectoryName(directory) ?? directory
            : directory;

        return KyberWeaveConfigAssembler.FromRepositoryYaml(
            yaml,
            root,
            new ReviewWorkingDirectoryFacts(Tree.DirectoryExists, Tree.LeadsOutsideThroughSymlink),
            ReadText);
    }

    private string? ReadText(string path)
    {
        string full = Path.GetFullPath(path);
        ThrowIfUnreadable(full);
        return _files.TryGetValue(full, out string? text) ? text : null;
    }

    private string ReadFile(string path)
    {
        string full = Path.GetFullPath(path);
        ThrowIfUnreadable(full);
        return _files[full];
    }

    private bool DirectoryExists(string path)
    {
        string directory = FullDirectory(path);
        return _directories.Contains(directory)
            || _files.Keys.Any(file => Path.GetDirectoryName(file) is string parent && IsUnder(directory, parent));
    }

    private static bool IsUnder(string directory, string parent) =>
        string.Equals(parent, directory, StringComparison.Ordinal)
        || parent.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string FullDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private void ThrowIfUnreadable(string fullPath)
    {
        if (_readFailures.TryGetValue(fullPath, out Exception? error))
            throw error;
    }

    private sealed class ReadFailureScope(FakeKyberWeaveConfigRepository owner, string path) : IDisposable
    {
        public void Dispose() => owner._readFailures.Remove(path);
    }

    /// <summary>The tree probe over this fake's own files and directories, so it answers as the real adapter would.</summary>
    private sealed class ArrangedTreeProbe(FakeKyberWeaveConfigRepository owner) : IRepositoryTreeProbe
    {
        public RepositoryRootAccess ProbeRoot(string root) => new(owner.DirectoryExists(root));

        public bool DirectoryExists(string path) => owner.DirectoryExists(path);

        public bool LeadsOutsideThroughSymlink(string root, string path) => false;
    }
}
