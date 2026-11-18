using System.Text;
using KyberWeave.Application.Utilities;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.StatusLine;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Utilities;

/// <summary>Reading the status-line product tree through a fake file set, with the product root resolved through the path seam.</summary>
public sealed class StatusLineProductArtifactSourceTests
{
    private const string Work = "/kw/work";

    private readonly InMemoryFileSystem _disk = new();
    private readonly FilesFake _files = new();

    private StatusLineProductArtifactSource Source(string productRoot) =>
        new(_files, new FakePathResolver(_disk), new FakeProcessEnvironment(currentDirectory: Work), productRoot);

    [Fact]
    public void ARelativeProductRootIsReadAgainstTheWorkingDirectory()
    {
        _files.Add("/kw/work/product/claude/statusline.sh", "echo");

        IReadOnlyList<StatusLineDeploymentFile> loaded = Source("product").Load(StatusLineTarget.Claude);

        StatusLineDeploymentFile file = Assert.Single(loaded);
        Assert.Equal("statusline.sh", file.RelativePath);
    }

    [Fact]
    public void ManifestsAreNotDeployedAndNestedFilesKeepTheirRelativePath()
    {
        _files.Add("/kw/product/pi/manifest.json", "{}");
        _files.Add("/kw/product/pi/MANIFEST.yml", "a: b");
        _files.Add("/kw/product/pi/ext/helper.ts", "export {}");
        _files.Add("/kw/product/pi/index.ts", "import './ext/helper'");

        IReadOnlyList<StatusLineDeploymentFile> loaded = Source("/kw/product").Load(StatusLineTarget.Pi);

        Assert.Equal(["ext/helper.ts", "index.ts"], loaded.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AMissingTargetDirectoryIsReported()
    {
        _ = Assert.Throws<DirectoryNotFoundException>(() => Source("/kw/product").Load(StatusLineTarget.Claude));
    }

    private sealed class FilesFake : IStatusLineFiles
    {
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);

        public void Add(string path, string text) => _content[path] = Encoding.UTF8.GetBytes(text);

        public bool FileExists(string path) => _content.ContainsKey(path);

        public bool DirectoryExists(string path) =>
            _content.Keys.Any(k => k.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal));

        public byte[] ReadAllBytes(string path) => _content[path];

        public string? ReadTextOrNull(string path) => _content.TryGetValue(path, out byte[]? bytes) ? Encoding.UTF8.GetString(bytes) : null;

        public IEnumerable<string> EnumerateRegularFiles(string directory) =>
            _content.Keys.Where(k => k.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal));

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void WriteAllBytes(string path, ReadOnlySpan<byte> content) => throw new NotSupportedException();

        public void WriteTextAtomically(string path, string text) => throw new NotSupportedException();

        public void DeleteFile(string path) => throw new NotSupportedException();

        public bool IsDirectoryEmpty(string path) => throw new NotSupportedException();

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public bool IsLinkOrReparsePoint(string path) => false;

        public bool HasUserExecuteBit(string path) => false;

        public void MarkExecutable(string path) => throw new NotSupportedException();
    }
}
