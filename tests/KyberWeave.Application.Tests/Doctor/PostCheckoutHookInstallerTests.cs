using System.Diagnostics;
using KyberWeave.Application.Doctor;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Kernel.FileSystem;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Doctor;

/// <summary>Where the installer puts the hook and when it refuses, over scripted git answers, fake hook files and the path seam.</summary>
public sealed class PostCheckoutHookInstallerTests
{
    private const string Root = "/kw/repo";

    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _present = new(StringComparer.Ordinal)
    {
        "/kw/repo/.kyber-weave/kyber-weave.yml",
    };

    private PostCheckoutHookInstaller Installer() =>
        new(
            new Hooks(_written),
            new Disk(_present),
            new Settings(),
            new FakePathResolver(new InMemoryFileSystem()),
            new FakeProcessEnvironment(currentDirectory: "/kw/work"));

    private static ScriptedProcessExecutor Git(string hooksAnswer, string? hooksPath = null, string tracked = "") =>
        new(info =>
        {
            string arguments = string.Join(' ', info.ArgumentList);
            return arguments switch
            {
                "rev-parse --git-path hooks" => new ProcessResult(0, hooksAnswer + "\n", string.Empty),
                "config --get core.hooksPath" => hooksPath is null
                    ? new ProcessResult(1, string.Empty, string.Empty)
                    : new ProcessResult(0, hooksPath + "\n", string.Empty),
                _ when arguments.StartsWith("ls-files", StringComparison.Ordinal) => new ProcessResult(0, tracked, string.Empty),
                _ => new ProcessResult(1, string.Empty, string.Empty),
            };
        });

    [Fact]
    public void ARelativeHooksAnswerIsResolvedAgainstTheRepositoryRoot()
    {
        DoctorCheck? check = Installer().Ensure(Root, Git(".git/hooks"));

        Assert.True(check?.Passed);
        Assert.Contains("/kw/repo/.git/hooks/post-checkout", _written.Keys);
    }

    [Fact]
    public void AHooksFolderOutsideTheRepositoryIsUsedWithoutAskingGitWhetherItIsTracked()
    {
        ScriptedProcessExecutor git = Git("../shared/hooks");

        _ = Installer().Ensure(Root, git);

        Assert.Contains("/kw/shared/hooks/post-checkout", _written.Keys);
        Assert.DoesNotContain(git.Calls, c => c.ArgumentList.Contains("ls-files"));
    }

    [Fact]
    public void ATrackedRedirectedHooksFolderInsideTheRepositoryIsRefused()
    {
        DoctorCheck? check = Installer().Ensure(Root, Git("hooks", hooksPath: "hooks", tracked: "hooks/pre-commit\n"));

        Assert.StartsWith("warning: core.hooksPath points at 'hooks'", check?.Reason, StringComparison.Ordinal);
        Assert.Empty(_written);
    }

    private sealed class Hooks(Dictionary<string, string> written) : IGitHookFiles
    {
        public bool Exists(string path) => written.ContainsKey(path);

        public bool IsExecutable(string path) => true;

        public string ReadAllText(string path) => written[path];

        public void Write(string path, string text, bool makeExecutable) => written[path] = text;

        public void Delete(string path) => written.Remove(path);
    }

    private sealed class Disk(HashSet<string> present) : IDoctorFileSystem
    {
        public bool FileExists(string path) => present.Contains(path);

        public bool IsPresentFile(string path) => present.Contains(path);

        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

        public bool DirectoryExists(string path) => path == "/kw/repo/docs";

        public string ReadAllText(string path) => "ontology: {}\n";

        public IReadOnlyList<DoctorDirectoryEntry> ListEntries(string directory) => [];

        public string ReadTail(string path, long maxBytes, out bool firstLineIsPartial)
        {
            firstLineIsPartial = false;
            return string.Empty;
        }
    }

    private sealed class Settings : IRepositoryToolSettings
    {
        public RepositoryToolSettings Read(string repositoryRoot) => new();
    }
}
