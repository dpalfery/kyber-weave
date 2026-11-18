using System.Diagnostics;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     Runs <c>squad run</c> through the real instance ensure, so the zero-files guarantee is proven against a real
///     deployment rather than a stand-in.
///     Acceptance criterion 7.2: Zero files written into project tree.
/// </summary>
public sealed class SquadRunCommandRealEnsureTests : IDisposable
{
    private readonly string _projectDir;
    private readonly SquadModelPresetSelectionStore _selectionStore;
    private readonly TempDirectory _temp = new();
    private readonly FakeSquadUserPaths _userPaths;

    public SquadRunCommandRealEnsureTests()
    {
        string home = Path.Combine(_temp.Path, "user-home");
        Directory.CreateDirectory(home);
        _projectDir = Path.Combine(_temp.Path, "project");
        Directory.CreateDirectory(_projectDir);
        _userPaths = new FakeSquadUserPaths(home);
        Directory.CreateDirectory(_userPaths.HomeDirectory);
        _selectionStore = RealSquadSelectionStore.Create(_userPaths);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void SuccessfulRunLeavesProjectSourceTreeCompletelyUnmodified()
    {
        string sourceFile = Path.Combine(_projectDir, "Program.cs");
        File.WriteAllText(sourceFile, "// Original content", Encoding.UTF8);
        string[] entriesBefore = Directory.GetFileSystemEntries(_projectDir, "*", SearchOption.AllDirectories);

        WritePreset("fast");
        _selectionStore.Set(_projectDir, SquadTarget.Claude, "fast");
        SquadRunCommand command = CreateCommand(
            _ => 0,
            name => $"/usr/bin/{name}");

        SquadRunSettings settings = new()
        {
            Harness = "claude",
            Args = ["run"]
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(null!, settings));

        Assert.Equal(0, execution.Result);
        string[] entriesAfter = Directory.GetFileSystemEntries(_projectDir, "*", SearchOption.AllDirectories);
        Assert.Equal(entriesBefore, entriesAfter);
        Assert.Equal("// Original content", File.ReadAllText(sourceFile, Encoding.UTF8));
    }

    private void WritePreset(string name, string? yaml = null)
    {
        string presetsDir = Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        string presetPath = Path.Combine(presetsDir, $"{name}.yml");
        string content = yaml ?? """
                                 schema: kyber-squad.model-preset/v1
                                 profiles:
                                   general:
                                     default: gpt-4o-mini
                                     claude: gpt-4o-mini
                                     codex: gpt-4o-mini
                                     copilot: gpt-4o-mini
                                     pi: gpt-4o-mini
                                 """;
        File.WriteAllText(presetPath, content, Encoding.UTF8);
    }

    private SquadRunCommand CreateCommand(
        Func<ProcessStartInfo, int>? runner = null,
        Func<string, string?>? findOnPath = null)
    {
        return SquadTestCommands.Run(
            _userPaths,
            _selectionStore,
            runner ?? (_ => 0),
            _ => null,
            findOnPath ?? (name => $"/usr/local/bin/{name}"),
            _projectDir);
    }
}
