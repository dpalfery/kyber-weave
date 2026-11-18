using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests;

/// <summary>
/// Reads and arranges the files of an in-memory <see cref="DocFixture"/> by checkout-relative path, so a unit test
/// states a plan, a task file or a folder without composing a platform path or touching the disk.
/// </summary>
internal static class DocFixtureMemoryExtensions
{
    /// <summary>The platform path of <paramref name="relative"/> under the fixture root.</summary>
    internal static string FullPath(this DocFixture fixture, string relative) =>
        Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The text of the checkout-relative file.</summary>
    internal static string ReadText(this DocFixture fixture, string relative) =>
        fixture.Store.ReadAllText(fixture.FullPath(relative));

    /// <summary>Whether the checkout-relative file exists.</summary>
    internal static bool Exists(this DocFixture fixture, string relative) =>
        fixture.Store.FileExists(fixture.FullPath(relative));

    /// <summary>Creates the checkout-relative directory, in memory or on disk per the fixture mode.</summary>
    internal static DocFixture EnsureDirectory(this DocFixture fixture, string relative)
    {
        if (fixture.Memory is not null)
        {
            fixture.Memory.CreateDirectory(fixture.FullPath(relative));
        }
        else
        {
            _ = Directory.CreateDirectory(fixture.FullPath(relative));
        }

        return fixture;
    }
}

/// <summary>
/// The container the real host builds, with every file, path and environment port pointing at an in-memory
/// <see cref="DocFixture"/>, for a unit test of a command or tool that composes services.
/// </summary>
internal static class DocsMemoryServicesD
{
    /// <summary>The container over <paramref name="fixture"/>; <paramref name="configure"/> runs after the memory ports are set.</summary>
    internal static IServiceProvider Services(DocFixture fixture, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.Memory is null)
        {
            throw new InvalidOperationException("The fixture must be in memory: DocFixture.CreateInMemory().");
        }

        return DocsTestCommands.Services(services =>
        {
            _ = services.AddSingleton<IFileSystem>(fixture.Memory);
            _ = services.AddSingleton<IWritableFileSystem>(fixture.Memory);
            _ = services.AddSingleton(fixture.Store);
            _ = services.AddSingleton(fixture.Paths);
            _ = services.AddSingleton(fixture.Environment);
            configure?.Invoke(services);
        });
    }
}
