using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>The Squad source loader and render-input reader over an in-memory file system, for a test that needs no disk.</summary>
/// <param name="files">The in-memory file system the Squad tree lives in.</param>
public sealed class InMemorySquadServices(InMemoryFileSystem files)
{
    private readonly FakePathResolver _paths = new(files);
    private readonly FakeProcessEnvironment _environment = new(isFileSystemCaseInsensitive: !files.CaseSensitive);

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
}
