using System.Text;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Docs.Scaffolding;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The fixture vocabulary the MCP declaration and wiring suites share: a host declaration, a file
/// written at a path inside a repository root, and a launch binary a resolver can find.
/// </summary>
/// <remarks>
/// <para>
/// These helpers were copied into five suites as the wiring feature landed. A copy is not a
/// problem in itself; five copies of the <em>same</em> decision is, because the decision then has
/// to change in five places and one of them is always missed. The launch binary is the clearest
/// case: the resolver is a file-existence check, so an empty file is enough, and the one thing
/// that differs between platforms is whether an execute bit exists. That reasoning belongs here
/// once.
/// </para>
/// <para>
/// Every helper takes the root it writes under rather than holding one. A test class owns its
/// temp directory and disposes it, so a shared helper that owned one would outlive the test that
/// made it.
/// </para>
/// </remarks>
internal static class McpWiringFixture
{
    /// <summary>
    /// A host configuration declaring exactly <paramref name="servers"/>, keyed by server name as
    /// the loader keys it.
    /// </summary>
    public static McpConfig Declare(params RequiredMcpServer[] servers)
    {
        Dictionary<string, RequiredMcpServer> declared = new(StringComparer.Ordinal);
        foreach (RequiredMcpServer server in servers)
            declared[server.Name] = server;

        return new McpConfig { RequiredServers = declared };
    }

    /// <summary>
    /// Writes an empty executable-looking file into <paramref name="directory"/> and returns the
    /// path it wrote.
    /// </summary>
    /// <remarks>
    /// A file, not a process: the resolver is a file-existence check, so an empty file proves no
    /// launch happened and is enough for a declaration to resolve. The execute bit is set where the
    /// platform has one so the fixture looks like an installed binary rather than a stray text
    /// file; on Windows the extension PATHEXT resolution looks for is used, because there a
    /// name without one would not resolve.
    /// </remarks>
    public static string WriteExecutable(string directory, string name)
    {
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllText(path, string.Empty);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Writes UTF-8 text to a repository-relative path, creating its directories.</summary>
    public static void WriteText(string root, string relativePath, string content) =>
        WriteBytes(root, relativePath, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Writes bytes to a repository-relative path, creating its directories. Bytes rather than text
    /// wherever a case asserts a file is byte-identical afterwards, so the comparison is of what the
    /// platform actually stored.
    /// </summary>
    public static void WriteBytes(string root, string relativePath, byte[] content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, content);
    }

    /// <summary>
    /// The <c>mcp:</c> declaration exactly as <c>docs init</c> appends it to a host configuration
    /// whose newline style is <paramref name="newline"/>: one blank line separating it from the
    /// operator's last byte, the block, and a single trailing newline.
    /// </summary>
    /// <remarks>
    /// The block's own text is <c>McpDeclarationBlockWriterTests</c>' subject. Referencing the same
    /// constant keeps the scaffold fixtures on what they protect — that the operator's bytes are an
    /// exact prefix and the declaration is the only thing appended after them.
    /// </remarks>
    public static string AppendedDeclaration(string newline = "\n") =>
        newline + newline +
        McpDeclarationBlockWriter.DeclarationYaml.Replace("\n", newline, StringComparison.Ordinal) +
        newline;

    /// <summary>
    /// Creates a symbolic link, or skips the calling test.
    /// </summary>
    /// <remarks>
    /// Windows refuses to create a symbolic link unless the process holds the developer-mode
    /// privilege or elevation, and the repository's Windows CI holds neither. A case that cannot be
    /// arranged on this platform is skipped with that reason rather than failed, because a red test
    /// there would say the code under test is broken when the fixture never ran.
    /// </remarks>
    public static void CreateSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip(
                $"This platform refuses to create a symbolic link ({exception.Message}), "
                    + "so a linked harness configuration cannot be arranged here.");
        }
    }
}
