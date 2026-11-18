using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the real <see cref="GitFacts"/> over the real adapters for the tests whose subject is what git reports.
/// The class itself takes its seams, so every test that does not need real git injects fakes instead.
/// </summary>
internal static class RealGitFacts
{
    /// <summary>The git facts over the kernel's process runner and the real file system.</summary>
    internal static GitFacts Create() => new(new KernelProcessRunner(), new PhysicalFileSystem());
}
