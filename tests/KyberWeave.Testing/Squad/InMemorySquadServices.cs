using KyberWeave.Application.Squad.Packaging;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console;
using KyberWeave.Data.Squad.Packaging;

namespace KyberWeave.Testing.Squad;

/// <summary>The Squad source loader and render-input reader over an in-memory file system, for a test that needs no disk.</summary>
/// <param name="files">The in-memory file system the Squad tree lives in.</param>
public sealed class InMemorySquadServices(InMemoryFileSystem files)
{
    private readonly FakePathResolver _paths = new(files);
    // The working directory is the in-memory root, so it is fully qualified on every platform (Windows refuses '/kw/work').
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: files.Root, isFileSystemCaseInsensitive: !files.CaseSensitive);

    /// <summary>Creates the source loader over the in-memory tree.</summary>
    public SquadSourceLoader SourceLoader() => new(files, _paths, _environment);

    /// <summary>Loads the Squad source under a root from the in-memory tree.</summary>
    /// <param name="root">The Squad source root.</param>
    public SquadSource Load(string root) => SourceLoader().Load(root);

    /// <summary>Creates the model preset validator over the in-memory tree.</summary>
    public SquadModelPresetValidator PresetValidator() => new(files);

    /// <summary>Creates the render input reader over the in-memory tree; no environment variable is set.</summary>
    public FileSquadRenderInputReader RenderInputReader() => new(
        SourceLoader(),
        new CopilotMcpServerProbe(files),
        new SquadOperatorChannelOverlayFile(files),
        new SquadWorkerBaselineFile(files, _paths, _environment),
        files,
        _environment);

    /// <summary>Creates the companion skills lister over the in-memory tree.</summary>
    /// <param name="workingDirectory">The directory the process is in, which a relative companion root resolves against.</param>
    public SquadCompanionSkills CompanionSkills(string workingDirectory) =>
        new(files, _paths, new FakeProcessEnvironment(currentDirectory: workingDirectory, isFileSystemCaseInsensitive: !files.CaseSensitive));

    /// <summary>Creates the packer that reads the in-memory tree and writes its archives there.</summary>
    /// <param name="workingDirectory">The directory the process is in.</param>
    public SquadPacker Packer(string workingDirectory) => new(files, CompanionSkills(workingDirectory));

    /// <summary>Creates <c>squad pack</c> over the in-memory tree, run from a working directory, writing to a console.</summary>
    /// <param name="workingDirectory">The directory the command is run from.</param>
    /// <param name="console">Where the command writes.</param>
    public SquadPackCommand PackCommand(string workingDirectory, IAnsiConsole console)
    {
        FakeProcessEnvironment environment = new(
            currentDirectory: workingDirectory,
            isFileSystemCaseInsensitive: !files.CaseSensitive);
        SquadCompanionSkills companions = new(files, _paths, environment);
        return new SquadPackCommand(
            new SquadPackService(
                environment,
                _paths,
                new SquadPackSourceLocator(files, _paths, environment),
                new FileSquadPackager(
                    new SquadPacker(files, companions),
                    SourceLoader(),
                    companions,
                    files,
                    environment,
                    new FakeIdGenerator())),
            console);
    }
}
