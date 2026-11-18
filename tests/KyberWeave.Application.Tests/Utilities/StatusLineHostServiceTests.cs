using KyberWeave.Application.Utilities;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Utilities;

/// <summary>Looking for a program on PATH through the file and environment seams.</summary>
public sealed class StatusLineHostServiceTests
{
    private const string Dangling = "/kw/dangling";
    private const string Real = "/kw/real";

    private readonly PathFiles _files = new(dangling: "/kw/dangling/agy", executable: "/kw/real/agy");

    private StatusLineHostService Service(string path) =>
        new(
            _files,
            new FakeProcessEnvironment(readVariable: name => name == "PATH" ? path : null),
            null!,
            null!,
            null!);

    [Fact]
    public void ADanglingLinkInAPathEntryIsNotFoundAndTheSearchContinues()
    {
        Assert.True(Service($"{Dangling}{Path.PathSeparator}{Real}").IsOnPath("agy"));
    }

    [Fact]
    public void ADanglingLinkAloneIsNotOnPath()
    {
        Assert.False(Service(Dangling).IsOnPath("agy"));
    }

    private sealed class PathFiles(string dangling, string executable) : IStatusLineFiles
    {
        public bool FileExists(string path) => path == dangling || path == executable;

        public bool HasUserExecuteBit(string path) =>
            path == executable ? true : throw new FileNotFoundException("The link target is missing.", path);

        public bool DirectoryExists(string path) => throw new NotSupportedException();

        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

        public string? ReadTextOrNull(string path) => throw new NotSupportedException();

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void WriteAllBytes(string path, ReadOnlySpan<byte> content) => throw new NotSupportedException();

        public void WriteTextAtomically(string path, string text) => throw new NotSupportedException();

        public void DeleteFile(string path) => throw new NotSupportedException();

        public bool IsDirectoryEmpty(string path) => throw new NotSupportedException();

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public bool IsLinkOrReparsePoint(string path) => throw new NotSupportedException();

        public void MarkExecutable(string path) => throw new NotSupportedException();

        public IEnumerable<string> EnumerateRegularFiles(string directory) => throw new NotSupportedException();
    }
}
