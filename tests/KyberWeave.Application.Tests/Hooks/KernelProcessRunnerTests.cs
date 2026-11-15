using System.ComponentModel;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Hooks;

public sealed class KernelProcessRunnerTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("kw-runner-");

    public void Dispose() => directory.Delete(recursive: true);

    private static IProcessRunner Runner()
    {
        ServiceCollection services = [];
        _ = services.AddWeaveApplication();
        return services.BuildServiceProvider().GetRequiredService<IProcessRunner>();
    }

    [Fact]
    public async Task ReportsAMissingExecutableAsCommandNotFound()
    {
        FileNotFoundException ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Runner().RunAsync(["kw-no-such-program-4e1f"], directory.FullName, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Contains("command not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAMissingWorkingDirectoryByItsOwnName()
    {
        string missing = Path.Combine(directory.FullName, "gone");

        DirectoryNotFoundException ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            Runner().RunAsync(["dotnet", "--version"], missing, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Contains("working directory not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlamesTheDirectoryWhenItWasRemovedAfterTheCheck()
    {
        string missing = Path.Combine(directory.FullName, "removed-mid-start");

        Win32Exception native = new(2);
        DirectoryNotFoundException mapped = Assert.IsType<DirectoryNotFoundException>(
            KernelProcessRunner.MapMissingExecutable(native, "dotnet", missing));

        Assert.Equal($"working directory not found: {missing}", mapped.Message);
        Assert.Same(native, mapped.InnerException);
    }

    [Fact]
    public void BlamesTheCommandWhenTheDirectoryStillExists()
    {
        FileNotFoundException mapped = Assert.IsType<FileNotFoundException>(
            KernelProcessRunner.MapMissingExecutable(new Win32Exception(2), "kw-no-such-program-4e1f", directory.FullName));

        Assert.Equal("command not found: kw-no-such-program-4e1f", mapped.Message);
    }

    [Fact]
    public async Task DoesNotCallAPermissionErrorCommandNotFound()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string script = Path.Combine(directory.FullName, "noexec.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            Runner().RunAsync([script], directory.FullName, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.IsNotType<FileNotFoundException>(ex);
        Assert.DoesNotContain("command not found", ex.Message, StringComparison.Ordinal);
    }
}
