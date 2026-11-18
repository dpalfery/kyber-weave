using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A global root that cannot be resolved leaves a target without a hook path instead of failing the caller. Regression
/// test for the unreadable-root case in the Kilo review of PR #527.
/// </summary>
public sealed class ArbiterGlobalHookPathsRootFailureTests
{
    private readonly string _home = Path.Combine(new InMemoryFileSystem().Root, "home");

    [Fact]
    public void ResolveGlobalHookPath_WhenGlobalRootsThrowsArgumentException_ReturnsNullWithoutThrowing()
    {
        ThrowingGlobalRoots throwingRoots = new(new ArgumentException("Invalid or relative root override"));
        string? result = ArbiterGlobalHookPaths.Resolve(SquadTarget.Cursor, _home, globalRoots: throwingRoots);
        Assert.Null(result);

        Func<string, string?> envWithRelativePath = varName => varName == "CURSOR_CONFIG_DIR" ? "relative/path" : null;
        string? envResult = ArbiterGlobalHookPaths.Resolve(SquadTarget.Cursor, _home, environmentReader: envWithRelativePath);
        Assert.Null(envResult);
    }

    private sealed class ThrowingGlobalRoots(Exception exception) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => throw exception;
    }
}
