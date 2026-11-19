using System.Reflection;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Testing.Squad;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Where <c>squad pack</c> looks for the canonical source and what it writes, over the in-memory file system:
/// the markers must sit in the working directory itself, no parent is searched, no embedded copy exists, and a
/// valid root packs every format.
/// </summary>
public sealed class SquadPackCommandSourceTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeProcessEnvironment _environment;
    private readonly SquadPackSourceLocator _locator;
    private readonly InMemorySquadServices _services;
    private readonly string _work;

    public SquadPackCommandSourceTests()
    {
        _ansi.Profile.Width = 200;
        _work = Path.Combine(_files.Root, "work");
        _files.CreateDirectory(_work);
        _environment = new FakeProcessEnvironment(currentDirectory: _work, isFileSystemCaseInsensitive: !_files.CaseSensitive);
        _locator = new SquadPackSourceLocator(_files, new FakePathResolver(_files), _environment);
        _services = new InMemorySquadServices(_files);
    }

    [Fact]
    public void Pack_OutsideRepositoryRoot_FailsWithMaintainerGuidanceBeforeSideEffects()
    {
        string emptyDir = Path.Combine(_work, "empty-dir");
        _files.CreateDirectory(emptyDir);
        Assert.Null(_locator.Resolve(emptyDir));

        string slnOnlyDir = Path.Combine(_work, "sln-only");
        _files.CreateDirectory(slnOnlyDir);
        _files.WriteAllText(Path.Combine(slnOnlyDir, "KyberWeave.sln"), "Microsoft Visual Studio Solution File");
        Assert.Null(_locator.Resolve(slnOnlyDir));

        string squadYmlOnlyDir = Path.Combine(_work, "squad-yml-only");
        _files.CreateDirectory(Path.Combine(squadYmlOnlyDir, "products", "kyber-squad"));
        _files.WriteAllText(Path.Combine(squadYmlOnlyDir, "products", "kyber-squad", "squad.yml"), "schema: kyber-squad.squad/v1");
        Assert.Null(_locator.Resolve(squadYmlOnlyDir));

        string dirWithoutSquadYml = Path.Combine(_work, "dir-without-squad-yml");
        _files.CreateDirectory(Path.Combine(dirWithoutSquadYml, "products", "kyber-squad"));
        _files.WriteAllText(Path.Combine(dirWithoutSquadYml, "KyberWeave.sln"), "solution");
        Assert.Null(_locator.Resolve(dirWithoutSquadYml));
    }

    [Fact]
    public void Pack_DoesNotSearchParentDownloadOrUseEmbeddedSource()
    {
        string repo = CreateRepository("parent-repo");
        string childDir = Path.Combine(repo, "src", "KyberWeave.Cli");
        _files.CreateDirectory(childDir);

        Assert.Null(_locator.Resolve(childDir));

        Assembly cliAssembly = typeof(KyberWeave.Cli.CliApplication).Assembly;
        Assembly coreAssembly = typeof(KyberWeave.Core.Diagnostics.DiagnosticReport).Assembly;
        Assembly squadAssembly = typeof(SquadSource).Assembly;

        string[] cliResources = cliAssembly.GetManifestResourceNames();
        string[] coreResources = coreAssembly.GetManifestResourceNames();
        string[] squadResources = squadAssembly.GetManifestResourceNames();

        Assert.DoesNotContain(cliResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
                                                    name.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase) ||
                                                    name.EndsWith("squad.yml", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(coreResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
                                                     name.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase) ||
                                                     name.EndsWith("squad.yml", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(squadResources, name => name.Contains("products", StringComparison.OrdinalIgnoreCase) ||
                                                      name.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase) ||
                                                      name.EndsWith("squad.yml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_RepositoryRoot_UsesTrackedCanonicalSource()
    {
        string repo = CreateRepository("valid-repo");

        string? resolved = _locator.Resolve(repo);

        Assert.NotNull(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(repo, "products", "kyber-squad")),
            Path.GetFullPath(resolved));
    }

    [Theory]
    [InlineData("apm")]
    [InlineData("plugins")]
    [InlineData("all")]
    public void Pack_OutsideRepositoryRoot_ExitsNonZeroWithGuidanceAndNoSideEffects(string format)
    {
        string workingDir = Path.Combine(_work, "outside-root");
        _files.CreateDirectory(workingDir);
        string outDir = Path.Combine(_work, "pack-out");

        FakeProcessExecutor executor = new();
        SquadPackCommand command = _services.PackCommand(workingDir, _ansi);

        int exitCode = command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = format,
                Out = outDir
            });

        string output = Output();
        Assert.Equal(1, exitCode);
        Assert.Contains("KyberWeave.sln", output, StringComparison.Ordinal);
        Assert.Contains("products/kyber-squad/squad.yml", output, StringComparison.Ordinal);
        Assert.Contains("squad install", output, StringComparison.Ordinal);
        Assert.False(_files.DirectoryExists(outDir));
        Assert.Empty(executor.Calls);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("other")]
    public void Pack_InvalidFormatToken_ExitsTwo(string format)
    {
        SquadPackCommand command = _services.PackCommand(_work, _ansi);

        int exitCode = command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = format,
                Out = Path.Combine(_work, "out")
            });

        Assert.Equal(2, exitCode);
        Assert.Contains("Invalid pack format", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pack_RepositoryRoot_UsesTrackedCanonicalSourceAndSucceeds()
    {
        string repoPath = CreateRepository("packable-repo");
        string outDir = Path.Combine(_work, "pack-output");

        FakeProcessExecutor executor = new();
        SquadPackCommand command = _services.PackCommand(repoPath, _ansi);

        int exitCode = command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = "all",
                Out = outDir
            });

        Assert.Equal(0, exitCode);
        Assert.True(_files.DirectoryExists(outDir));
        Assert.Contains(
            _files.EnumerateFiles(outDir, "kyber-squad-*.zip", recursive: false),
            path => !Path.GetFileName(path).StartsWith("kyber-squad-plugin-", StringComparison.Ordinal));
        Assert.Contains(_files.EnumerateFiles(outDir, "kyber-squad-plugin-*.zip", recursive: false), _ => true);
        Assert.True(_files.FileExists(Path.Combine(outDir, "SHA256SUMS.txt")));
        Assert.Empty(executor.Calls);
    }

    private string CreateRepository(string name)
    {
        string repo = Path.Combine(_work, name);
        _files.CreateDirectory(repo);
        _files.WriteAllText(Path.Combine(repo, "KyberWeave.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00");
        CanonicalSquadTree.CopyInto(_files, Path.Combine(repo, "products", "kyber-squad"));
        return repo;
    }

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}
