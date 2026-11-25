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

    /// <summary>
    /// The config entry and the rendered agent entry launch the same server only while both read one shared
    /// executable name and one shared argument; a literal in either would let them drift apart.
    /// </summary>
    [Fact]
    public void Entry_LaunchesTheSharedExecutableWithTheSharedArgument()
    {
        Assert.Empty(Ensure());

        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        JsonElement entry = document.RootElement.GetProperty("mcpServers").GetProperty("kw-docs");
        Assert.Equal(SquadDocsServerCommand.Executable, entry.GetProperty("command").GetString());
        Assert.Equal(
            [SquadDocsServerCommand.AnyRepoArgument],
            entry.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()));
        Assert.Equal("--any-repo", SquadDocsServerCommand.AnyRepoArgument);
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

    /// <summary>
    /// The size probe and the read are two calls, so the file can grow between them; the cap holds on the bytes the
    /// read returned, not only on the length the probe reported.
    /// </summary>
    [Fact]
    public void FileThatGrowsPastOneMebibyteAfterTheSizeProbe_IsRefused()
    {
        WriteConfig("{ \"mcpServers\": {}, \"pad\": \"" + new string('a', 1024 * 1024) + "\" }");
        _files.ReportLength(16);

        string warning = Assert.Single(Ensure());

        Assert.Contains(_configPath, warning, StringComparison.Ordinal);
        Assert.Contains("1 MiB", warning, StringComparison.Ordinal);
        Assert.Equal(0, _files.Writes);
    }

    /// <summary>
    /// A server another program adds between the read and the write is kept: the change is re-planned from the bytes
    /// the file holds now, so the write never puts back the document as it was first read.
    /// </summary>
    [Fact]
    public void ConcurrentEditBeforeTheWrite_IsKeptAndTheEntryIsAdded()
    {
        WriteConfig("{ \"mcpServers\": { \"first\": { \"command\": \"a\" } } }");
        _files.AfterRead(1, () => WriteConfig(
            "{ \"mcpServers\": { \"first\": { \"command\": \"a\" }, \"added\": { \"command\": \"b\" } } }"));

        Assert.Empty(Ensure());

        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        JsonElement servers = document.RootElement.GetProperty("mcpServers");
        Assert.Equal(
            ["added", "first", "kw-docs"],
            servers.EnumerateObject().Select(server => server.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, _files.Writes);
    }

    /// <summary>A concurrent edit that itself adds the entry leaves nothing to write.</summary>
    [Fact]
    public void ConcurrentEditThatAddsTheEntry_WritesNothing()
    {
        WriteConfig("{ \"mcpServers\": {} }");
        byte[] theirs = Encoding.UTF8.GetBytes(
            "{ \"mcpServers\": { \"kw-docs\": { \"command\": \"x\", \"args\": [\"--any-repo\"] } } }");
        _files.AfterRead(1, () => WriteConfig(theirs));

        Assert.Empty(Ensure());

        Assert.Equal(0, _files.Writes);
        Assert.Equal(theirs, _state.Files.ReadAllBytes(_configPath));
    }

    /// <summary>A file that keeps changing under the update is left alone with one warning rather than overwritten.</summary>
    [Fact]
    public void FileThatKeepsChangingBeforeTheWrite_WritesNothingAndWarnsOnce()
    {
        WriteConfig("{ \"mcpServers\": {} }");
        _files.AfterEveryRead(read => WriteConfig(
            "{ \"mcpServers\": { \"s" + read.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\": {} } }"));

        string warning = Assert.Single(Ensure());

        Assert.Contains(_configPath, warning, StringComparison.Ordinal);
        Assert.Contains("changed", warning, StringComparison.Ordinal);
        Assert.Equal(0, _files.Writes);
    }

    /// <summary>A file that appears between the first look and the write is planned again from its bytes.</summary>
    [Fact]
    public void FileCreatedBeforeTheWrite_IsKeptAndTheEntryIsAdded()
    {
        _state.Files.CreateDirectory(_agyRoot);
        _files.AfterExistenceCheck(1, () => WriteConfig("{ \"mcpServers\": { \"theirs\": {} } }"));

        Assert.Empty(Ensure());

        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        Assert.True(document.RootElement.GetProperty("mcpServers").TryGetProperty("theirs", out _));
        Assert.True(document.RootElement.GetProperty("mcpServers").TryGetProperty("kw-docs", out _));
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

    [Fact]
    public async Task GlobalInstall_WithAResolvedPath_PassesItToTheRenderAndTheConfigEntry()
    {
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(_source, _state.Files);
        string resolved = SeedExecutable();
        RecordingRenderer renderer = new(new AntigravityRenderer());

        SquadLifecycleResult result = await NewService(
            release,
            renderer,
            name => name == "kyber-weave-mcp" ? resolved : null).InstallAsync(
            new SquadInstallRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Antigravity], Version: Version));

        Assert.True(result.Success);
        Assert.Equal(resolved, renderer.LastRequest?.DocsServerCommand);
        Assert.Equal(resolved, ConfigKwDocsCommand());
        Assert.DoesNotContain(result.Warnings ?? [], warning => warning.Contains("PATH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GlobalUpdate_WithAResolvedPath_PassesItToTheRenderAndTheConfigEntry()
    {
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(_source, _state.Files);
        string resolved = SeedExecutable();
        RecordingRenderer renderer = new(new AntigravityRenderer());
        SquadLifecycleService service = NewService(release, renderer, _ => resolved);
        await service.InstallAsync(
            new SquadInstallRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Antigravity], Version: Version));
        _state.Files.DeleteFile(_configPath);

        SquadLifecycleResult result = await service.UpdateAsync(
            new SquadUpdateRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Antigravity], Version: Version));

        Assert.True(result.Success);
        Assert.Equal(resolved, renderer.LastRequest?.DocsServerCommand);
        Assert.Equal(resolved, ConfigKwDocsCommand());
    }

    [Fact]
    public async Task GlobalInstall_WithAnUnresolvedPath_AddsOneWarningAndTheBareCommand()
    {
        using FakeSquadReleaseSource release = FakeSquadReleaseSource.FromDirectory(_source, _state.Files);
        RecordingRenderer renderer = new(new AntigravityRenderer());

        SquadLifecycleResult result = await NewService(release, renderer, _ => null).InstallAsync(
            new SquadInstallRequest(_home, SquadDeploymentScope.Global, [SquadTarget.Antigravity], Version: Version));

        Assert.True(result.Success);
        Assert.Null(renderer.LastRequest?.DocsServerCommand);
        Assert.Equal(1, (result.Warnings ?? []).Count(warning => warning.Contains("kyber-weave-mcp", StringComparison.Ordinal)
            && warning.Contains("PATH", StringComparison.Ordinal)));
        Assert.Equal("kyber-weave-mcp", ConfigKwDocsCommand());
    }

    private string ConfigKwDocsCommand()
    {
        using JsonDocument document = JsonDocument.Parse(_state.Files.ReadAllBytes(_configPath));
        return document.RootElement.GetProperty("mcpServers").GetProperty("kw-docs").GetProperty("command").GetString()
            ?? string.Empty;
    }

    /// <summary>Creates the <c>kyber-weave-mcp</c> a PATH lookup names, since the lookup's answer must be a file that exists.</summary>
    private string SeedExecutable()
    {
        string path = Path.Combine(_state.Files.Root, "tools", "kyber-weave-mcp");
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllBytes(path, [0x7F]);
        return path;
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

    private SquadLifecycleService NewService(
        ISquadReleaseSource release,
        ISquadRenderer renderer,
        Func<string, string?>? findExecutable = null) =>
        SquadTestCommands.InMemoryLifecycle(
            _state,
            release,
            new SquadRendererRegistry([renderer], new InMemorySquadServices(_state.Files).RenderInputReader()),
            new SquadGlobalRoots(_ => null, _home),
            findExecutable: findExecutable);

    /// <summary>Forwards every render to the real renderer and keeps the last request it was given.</summary>
    private sealed class RecordingRenderer(ISquadRenderer inner) : ISquadRenderer
    {
        public SquadRenderRequest? LastRequest { get; private set; }

        public IReadOnlyCollection<SquadTarget> SupportedTargets => inner.SupportedTargets;

        public Task<SquadRenderResult> RenderAsync(SquadRenderRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return inner.RenderAsync(request, cancellationToken);
        }
    }

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
        private long? _reportedLength;
        private readonly Dictionary<int, Action> _afterRead = [];
        private readonly Dictionary<int, Action> _afterExists = [];
        private Action<int>? _afterEveryRead;
        private int _existenceChecks;

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public void FailOn(string operation, Exception failure)
        {
            _failing = operation;
            _failure = failure;
        }

        /// <summary>Makes the size probe report <paramref name="length"/> whatever the file holds.</summary>
        public void ReportLength(long length) => _reportedLength = length;

        /// <summary>Runs <paramref name="change"/> right after the <paramref name="read"/>th byte read returns.</summary>
        public void AfterRead(int read, Action change) => _afterRead[read] = change;

        /// <summary>Runs <paramref name="change"/> after every byte read, given the read's number.</summary>
        public void AfterEveryRead(Action<int> change) => _afterEveryRead = change;

        /// <summary>Runs <paramref name="change"/> right after the <paramref name="check"/>th existence check returns.</summary>
        public void AfterExistenceCheck(int check, Action change) => _afterExists[check] = change;

        public bool FileExists(string path)
        {
            bool exists = inner.FileExists(path);
            _existenceChecks++;
            if (_afterExists.Remove(_existenceChecks, out Action? change))
                change();
            return exists;
        }

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public string ReadAllText(string path)
        {
            Fail("read");
            Reads++;
            return inner.ReadAllText(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            Fail("read");
            Reads++;
            byte[] bytes = inner.ReadAllBytes(path);
            if (_afterRead.Remove(Reads, out Action? change))
                change();
            _afterEveryRead?.Invoke(Reads);
            return bytes;
        }

        public long GetLength(string path)
        {
            Fail("length");
            return _reportedLength ?? inner.GetLength(path);
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
