using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Fakes;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
/// Pins how a global deployment that includes Antigravity inserts the any-repo <c>kw-docs</c> entry into
/// Antigravity's user MCP config: insertion only, every other byte kept, malformed files refused, and no
/// warning that quotes the file. Each fact runs over the in-memory file system; nothing touches the real home.
/// </summary>
public sealed class AntigravityMcpConfigurationTests
{
    private const string Version = "1.2.3";
    private const string SecretMarker = "SECRET-MARKER-7f3a";
    private const string ContainerOpen = "\"mcpServers\": {";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly InMemorySquadState _state = new();
    private readonly string _home;
    private readonly string _project;
    private readonly string _source;
    private readonly string _agyRoot;
    private readonly string _configPath;
    private readonly RecordingFiles _files;

    public AntigravityMcpConfigurationTests()
    {
        _home = _state.UserPaths.HomeDirectory;
        _project = Path.Combine(_state.Files.Root, "work", "project");
        _source = Path.Combine(_state.Files.Root, "release", "kyber-squad");
        _state.Files.CreateDirectory(_home);
        _state.Files.CreateDirectory(_project);
        _ = CanonicalSquadTree.CopyInto(_state.Files, _source);
        _agyRoot = new SquadGlobalRoots(_ => null, _home).ResolveGlobalRoot(SquadTarget.Antigravity);
        _configPath = Path.Combine(_agyRoot, "mcp_config.json");
        _files = new RecordingFiles(new InMemorySquadFileAccess(_state.Files));
    }

    [Fact]
    public void MissingFile_IsCreatedWithOnlyTheEntry()
    {
        _state.Files.CreateDirectory(_agyRoot);

        Assert.Empty(Ensure());

        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        JsonElement servers = document.RootElement.GetProperty("mcpServers");
        JsonProperty entry = Assert.Single(servers.EnumerateObject());
        Assert.Equal("kw-docs", entry.Name);
        Assert.Equal("kyber-weave-mcp", entry.Value.GetProperty("command").GetString());
        Assert.Equal(["--any-repo"], entry.Value.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()));
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void MissingDirectory_IsCreated()
    {
        Assert.False(_state.Files.DirectoryExists(_agyRoot));

        Assert.Empty(Ensure());

        Assert.True(_state.Files.FileExists(_configPath));
    }

    [Fact]
    public void FileWithOtherServers_InsertsTheEntryAndKeepsEveryOtherByte()
    {
        string text =
            "{\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"other\": { \"command\": \"x\", \"env\": { \"KEY\": \""
            + SecretMarker + "\" } }\n  },\n  \"tail\": [1, 2]\n}\n";
        byte[] before = Encoding.UTF8.GetBytes(text);
        WriteConfig(before);

        Assert.Empty(Ensure());

        AssertInsertedAt(before, text.IndexOf(ContainerOpen, StringComparison.Ordinal) + ContainerOpen.Length);
    }

    [Fact]
    public void FileWithByteOrderMark_InsertsTheEntryAndKeepsEveryOtherByte()
    {
        string text = "{\n  \"mcpServers\": {\n    \"other\": { \"command\": \"x\" }\n  }\n}\n";
        byte[] before = [.. Bom, .. Encoding.UTF8.GetBytes(text)];
        WriteConfig(before);

        Assert.Empty(Ensure());

        AssertInsertedAt(before, Bom.Length + text.IndexOf(ContainerOpen, StringComparison.Ordinal) + ContainerOpen.Length);
        Assert.Equal(Bom, _state.Files.ReadAllBytes(_configPath)[..3]);
    }

    [Fact]
    public void RootWithoutServersKey_InsertsTheKey()
    {
        byte[] before = Encoding.UTF8.GetBytes("{\n  \"theme\": \"dark\"\n}\n");
        WriteConfig(before);

        Assert.Empty(Ensure());

        AssertInsertedAt(before, 1);
        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        Assert.Equal("dark", document.RootElement.GetProperty("theme").GetString());
        Assert.True(document.RootElement.GetProperty("mcpServers").TryGetProperty("kw-docs", out _));
    }

    [Fact]
    public void EmptyServers_InsertsWithNoStrayComma()
    {
        string text = "{ \"mcpServers\": {} }";
        byte[] before = Encoding.UTF8.GetBytes(text);
        WriteConfig(before);

        Assert.Empty(Ensure());

        AssertInsertedAt(before, text.IndexOf(ContainerOpen, StringComparison.Ordinal) + ContainerOpen.Length);
        // A strict parse refuses a trailing comma, so success proves there is none.
        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        Assert.Single(document.RootElement.GetProperty("mcpServers").EnumerateObject());
    }

    [Fact]
    public void FileWithCommentsAndTrailingComma_InsertsTheEntryAndKeepsEveryOtherByte()
    {
        string text =
            "{\n  // the servers this editor starts\n  \"mcpServers\": {\n    \"other\": { \"command\": \"x\" },\n  },\n}\n";
        byte[] before = Encoding.UTF8.GetBytes(text);
        WriteConfig(before);

        Assert.Empty(Ensure());

        AssertInsertedAt(before, text.IndexOf(ContainerOpen, StringComparison.Ordinal) + ContainerOpen.Length);
    }

    [Fact]
    public void ExistingEntryWithAnyRepo_WritesNothingAndDoesNotWarn()
    {
        WriteConfig("""{ "mcpServers": { "kw-docs": { "command": "elsewhere", "args": ["--any-repo"] } } }""");

        Assert.Empty(Ensure());

        Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public void ExistingEntryWithoutAnyRepo_WritesNothingAndWarnsOnce()
    {
        byte[] before = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"kw-docs\": { \"command\": \"x\", \"args\": [\"--repo-root\", \"" + SecretMarker + "\"] } } }");
        WriteConfig(before);

        string warning = Assert.Single(Ensure());

        Assert.Contains(_configPath, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, warning, StringComparison.Ordinal);
        Assert.Equal(0, _files.Writes);
        Assert.Equal(before, _state.Files.ReadAllBytes(_configPath));
    }

    [Theory]
    [InlineData("""{ "mcpServers": { "kw-docs": "not an object" } }""")]
    [InlineData("""{ "mcpServers": { "kw-docs": { "command": "x" } } }""")]
    [InlineData("""{ "mcpServers": { "kw-docs": { "command": "x", "args": "--any-repo" } } }""")]
    [InlineData("""{ "mcpServers": { "kw-docs": { "command": "x", "args": [1, true, null] } } }""")]
    public void ExistingEntryWithUnusableArgs_WarnsOnceWithoutThrowing(string text)
    {
        WriteConfig(text);

        Assert.Single(Ensure());
        Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public void CommentedByteOrderMarkFileWithEntry_WritesNothingAndDoesNotThrow()
    {
        WriteConfig([.. Bom, .. Encoding.UTF8.GetBytes(
            "{\n  /* keep */\n  \"mcpServers\": {\n    // ours\n    \"kw-docs\": { \"command\": \"x\", \"args\": [\"--any-repo\",], },\n  },\n}\n")]);

        Assert.Empty(Ensure());

        Assert.Equal(0, _files.Writes);
    }

    [Theory]
    [InlineData("{ \"mcpServers\": { \"other\": { \"env\": { \"KEY\": \"" + SecretMarker + "\" } }")]
    [InlineData("[ \"" + SecretMarker + "\" ]")]
    [InlineData("{ \"mcpServers\": [ \"" + SecretMarker + "\" ] }")]
    public void UnusableDocument_WritesNothingAndWarnsOnceWithoutQuotingIt(string text)
    {
        WriteConfig(text);

        string warning = Assert.Single(Ensure());

        Assert.Contains(_configPath, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, warning, StringComparison.Ordinal);
        Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public void FileOverOneMebibyte_IsRefusedWithoutBeingRead()
    {
        byte[] before = Encoding.UTF8.GetBytes("{ \"mcpServers\": {}, \"pad\": \"" + new string('a', 1024 * 1024) + "\" }");
        WriteConfig(before);

        string warning = Assert.Single(Ensure());

        Assert.Contains(_configPath, warning, StringComparison.Ordinal);
        Assert.Equal(0, _files.Reads);
        Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public void FileOfExactlyOneMebibyte_IsRead()
    {
        string prefix = "{ \"mcpServers\": {}, \"pad\": \"";
        string suffix = "\" }";
        WriteConfig(prefix + new string('a', (1024 * 1024) - prefix.Length - suffix.Length) + suffix);

        Assert.Empty(Ensure());

        Assert.Equal(1, _files.Writes);
    }

    public static TheoryData<string, Exception> Failures => new()
    {
        { "length", new IOException(SecretMarker) },
        { "length", new UnauthorizedAccessException(SecretMarker) },
        { "read", new IOException(SecretMarker) },
        { "read", new UnauthorizedAccessException(SecretMarker) },
        { "write", new IOException(SecretMarker) },
        { "write", new UnauthorizedAccessException(SecretMarker) },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void FailingFileAccess_WarnsOnceWithFixedText(string operation, Exception failure)
    {
        WriteConfig("{ \"mcpServers\": {} }");
        _files.FailOn(operation, failure);

        string warning = Assert.Single(Ensure());

        Assert.Contains(_configPath, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, warning, StringComparison.Ordinal);
        Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public void DryRun_WritesNothingAndNotesTheAddition()
    {
        string note = Assert.Single(Ensure(dryRun: true));

        Assert.Contains("would add", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_configPath, note, StringComparison.Ordinal);
        Assert.Equal(0, _files.Writes);
        Assert.False(_state.Files.FileExists(_configPath));
    }

    [Fact]
    public void SecondRun_WritesNothing()
    {
        Assert.Empty(Ensure());
        byte[] first = _state.Files.ReadAllBytes(_configPath);

        Assert.Empty(Ensure());

        Assert.Equal(1, _files.Writes);
        Assert.Equal(first, _state.Files.ReadAllBytes(_configPath));
    }

    [Fact]
    public void ResolvedCommand_IsWrittenAsGiven()
    {
        string command = Path.Combine(_state.Files.Root, "tools", "kyber-weave-mcp");

        Assert.Empty(Ensure(command: command));

        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        Assert.Equal(command, document.RootElement.GetProperty("mcpServers").GetProperty("kw-docs").GetProperty("command").GetString());
    }

    [Theory]
    [InlineData(SquadDeploymentScope.Project, SquadTarget.Antigravity)]
    [InlineData(SquadDeploymentScope.Instance, SquadTarget.Antigravity)]
    [InlineData(SquadDeploymentScope.Global, SquadTarget.Factory)]
    public void OutOfScope_WritesNothingAndDoesNotResolveTheRoot(SquadDeploymentScope scope, SquadTarget target)
    {
        Assert.Empty(AntigravityMcpConfiguration.Ensure(_files, [target], scope, new ThrowingRoots(), null, dryRun: false));

        Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public void NoRootResolver_WritesNothing()
    {
        Assert.Empty(AntigravityMcpConfiguration.Ensure(
            _files, [SquadTarget.Antigravity], SquadDeploymentScope.Global, null, null, dryRun: false));

        Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public async Task GlobalInstall_WithAntigravity_InsertsTheEntry()
    {
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(_source, _state.Files);

        SquadLifecycleResult result = await NewService(release, new AntigravityRenderer()).InstallAsync(
            new SquadInstallRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Antigravity], Version: Version));

        Assert.True(result.Success);
        AssertEntryWritten();
    }

    [Fact]
    public async Task GlobalUpdate_WithAntigravity_InsertsTheEntry()
    {
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(_source, _state.Files);
        SquadLifecycleService service = NewService(release, new AntigravityRenderer());
        await service.InstallAsync(
            new SquadInstallRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Antigravity], Version: Version));
        _state.Files.DeleteFile(_configPath);

        SquadLifecycleResult result = await service.UpdateAsync(
            new SquadUpdateRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Antigravity], Version: Version));

        Assert.True(result.Success);
        AssertEntryWritten();
    }

    [Fact]
    public async Task ProjectInstall_WithAntigravity_WritesNothing()
    {
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(_source, _state.Files);

        SquadLifecycleResult result = await NewService(release, new AntigravityRenderer()).InstallAsync(
            new SquadInstallRequest(_project, SquadDeploymentScope.Project, [SquadTarget.Antigravity], Version: Version));

        Assert.True(result.Success);
        Assert.False(_state.Files.FileExists(_configPath));
    }

    [Fact]
    public async Task GlobalInstall_WithoutAntigravity_WritesNothing()
    {
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(_source, _state.Files);

        SquadLifecycleResult result = await NewService(release, new FactoryRenderer()).InstallAsync(
            new SquadInstallRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Factory], Version: Version));

        Assert.True(result.Success);
        Assert.False(_state.Files.FileExists(_configPath));
    }

    private IReadOnlyList<string> Ensure(bool dryRun = false, string? command = null) =>
        AntigravityMcpConfiguration.Ensure(
            _files,
            [SquadTarget.Claude, SquadTarget.Antigravity],
            SquadDeploymentScope.Global,
            new SquadGlobalRoots(_ => null, _home),
            command,
            dryRun);

    private void WriteConfig(string text) => WriteConfig(Encoding.UTF8.GetBytes(text));

    private void WriteConfig(byte[] bytes)
    {
        _state.Files.CreateDirectory(_agyRoot);
        _state.Files.WriteAllBytes(_configPath, bytes);
    }

    /// <summary>
    /// Asserts the file is <c>before[..offset] + inserted + before[offset..]</c> on raw bytes, and that the inserted
    /// span holds the entry. The offset comes from the fixture, never from the splice under test.
    /// </summary>
    private void AssertInsertedAt(byte[] before, int offset)
    {
        byte[] after = _state.Files.ReadAllBytes(_configPath);
        int insertedLength = after.Length - before.Length;
        Assert.True(insertedLength > 0);
        Assert.Equal(before[..offset], after[..offset]);
        Assert.Equal(before[offset..], after[(offset + insertedLength)..]);
        string inserted = Encoding.UTF8.GetString(after, offset, insertedLength);
        Assert.Contains("\"kw-docs\"", inserted, StringComparison.Ordinal);
        Assert.Contains("\"--any-repo\"", inserted, StringComparison.Ordinal);
        Assert.Equal(1, _files.Writes);
    }

    private void AssertEntryWritten()
    {
        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        JsonElement entry = document.RootElement.GetProperty("mcpServers").GetProperty("kw-docs");
        Assert.Equal("kyber-weave-mcp", entry.GetProperty("command").GetString());
        Assert.Equal(["--any-repo"], entry.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()));
    }

    private SquadLifecycleService NewService(ISquadReleaseSource release, ISquadRenderer renderer) =>
        SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            new SquadRendererRegistry([renderer], new InMemorySquadServices(_state.Files).RenderInputReader()),
            new SquadGlobalRoots(_ => null, _home));

    /// <summary>A resolver that fails the test if the code asks it for a root.</summary>
    private sealed class ThrowingRoots : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) =>
            throw new InvalidOperationException("the root was resolved before the scope, target and resolver checks.");
    }

    /// <summary>Counts reads and writes over the in-memory files, and throws on the one operation a test names.</summary>
    private sealed class RecordingFiles(ISquadFileAccess inner) : ISquadFileAccess
    {
        private string? _failing;
        private Exception? _failure;

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public void FailOn(string operation, Exception failure)
        {
            _failing = operation;
            _failure = failure;
        }

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public string ReadAllText(string path)
        {
            Reads++;
            return inner.ReadAllText(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            Fail("read");
            Reads++;
            return inner.ReadAllBytes(path);
        }

        public long GetLength(string path)
        {
            Fail("length");
            return inner.GetLength(path);
        }

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

        public string NewScratchDirectoryPath(string prefix) => inner.NewScratchDirectoryPath(prefix);

        public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory) =>
            inner.EnumerateRealDirectories(directory);

        public string? FindCheckoutSquadSourceRoot() => inner.FindCheckoutSquadSourceRoot();

        public void WriteAllBytesAtomic(string path, byte[] contents)
        {
            Fail("write");
            inner.WriteAllBytesAtomic(path, contents);
            Writes++;
        }

        private void Fail(string operation)
        {
            if (operation == _failing && _failure is not null)
                throw _failure;
        }
    }
}
