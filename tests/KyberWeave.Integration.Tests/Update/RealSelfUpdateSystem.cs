using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Data.Update;

namespace KyberWeave.Testing.Update;

/// <summary>The self-update system over the real disk and real processes, for a test that runs a whole update against a scratch install directory.</summary>
public static class RealSelfUpdateSystem
{
    /// <summary>Creates the system over the production adapters.</summary>
    /// <returns>The system.</returns>
    public static ISelfUpdateSystem Create()
    {
        PhysicalFileSystem disk = new();
        return new SelfUpdateSystem(
            disk,
            new PhysicalPathResolver(disk),
            new KernelProcessRunner(),
            new ProcessConsoleLauncher(),
            new UnixUpdateFileModes());
    }
}
