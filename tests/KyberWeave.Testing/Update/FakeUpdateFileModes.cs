using KyberWeave.Cli.Update;

namespace KyberWeave.Testing.Update;

/// <summary>Permission bits in a unit test: a table of modes by path, so a copy or an extraction can be asserted to carry the executable bit.</summary>
public sealed class FakeUpdateFileModes : IUpdateFileModes
{
    private readonly Dictionary<string, UnixFileMode> _modes = new(StringComparer.Ordinal);

    /// <summary>Gets every path whose mode was set, with the mode.</summary>
    public IReadOnlyDictionary<string, UnixFileMode> Modes => _modes;

    /// <inheritdoc />
    public UnixFileMode? Get(string path) => _modes.TryGetValue(path, out UnixFileMode mode) ? mode : null;

    /// <inheritdoc />
    public void Set(string path, UnixFileMode mode) => _modes[path] = mode;
}
