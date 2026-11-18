using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Where the KyberDash executable is looked for, over an in-memory disk and a fake environment.</summary>
public sealed class KyberDashLocatorTests
{
    private readonly InMemoryFileSystem _files = new();
    private string? _path = string.Empty;
    private string? _processPath = "/opt/kw/kyber-weave";
    private OperatingSystemKind _kind = OperatingSystemKind.Linux;

    [Fact]
    public void Locate_BesideTheRunningBinary_WinsOverPath()
    {
        Put("/opt/kw/kyberdash");
        Put("/usr/bin/kyberdash");
        _path = "/usr/bin";

        Assert.Equal("/opt/kw/kyberdash", Locator().Locate());
    }

    [Fact]
    public void Locate_NotBeside_SearchesEachPathEntryInOrder()
    {
        Put("/b/kyberdash");
        _path = string.Join(Path.PathSeparator, "/a", "/b", "/c");

        Assert.Equal(Path.Combine("/b", "kyberdash"), Locator().Locate());
    }

    [Fact]
    public void Locate_OnWindows_LooksForTheExeName()
    {
        _kind = OperatingSystemKind.Windows;
        Put("/opt/kw/kyberdash.exe");

        Assert.Equal(Path.Combine("/opt/kw", "kyberdash.exe"), Locator().Locate());
    }

    [Fact]
    public void Locate_NoProcessPathAndNoPath_ReturnsNull()
    {
        _processPath = null;
        _path = null;

        Assert.Null(Locator().Locate());
    }

    [Fact]
    public void Locate_PathEntryThePlatformRejects_IsSkipped()
    {
        Put("/b/kyberdash");
        _path = string.Join(Path.PathSeparator, "bad\0entry", "/b");

        Assert.Equal(Path.Combine("/b", "kyberdash"), Locator().Locate());
    }

    private void Put(string path)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, string.Empty);
    }

    private KyberDashLocator Locator() => new(
        new FakeProcessEnvironment(operatingSystem: _kind, readVariable: name => name == "PATH" ? _path : null),
        _files,
        () => _processPath);
}
