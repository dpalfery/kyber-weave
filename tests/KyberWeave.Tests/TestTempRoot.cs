using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Tests;

/// <summary>
/// Points the test process, and every child it spawns, at a small private temp root instead of
/// the shared system one.
/// </summary>
/// <remarks>
/// Squad path canonicalisation lists every ancestor of a path, so the cost of each lifecycle
/// operation scales with how many entries sit beside the test directory. The system temp
/// directory is shared with every other tool on the machine and was measured at ~119k entries,
/// which made the Squad and Utilities classes several times slower. A per-checkout root is
/// small by construction and keeps concurrent worktrees from listing each other's leftovers.
/// The root must stay short: Microsoft.Testing.Platform creates a named pipe at
/// <c>$TMPDIR/&lt;32 hex&gt;</c> and a Unix domain socket path is limited to 103 bytes, so a root
/// inside a deep checkout (every AO worktree) crashes <c>dotnet test</c> before any test runs.
/// The redirect runs when the test assembly loads, which is before the pipe is created in the
/// <c>dotnet test</c> flow (the pipe path, and so this length limit, follows <c>TMPDIR</c>).
/// Windows is left alone: it has no <c>TMPDIR</c> and does not canonicalise this way.
/// </remarks>
internal static class TestTempRoot
{
    // 103-byte socket limit, minus "/" and the 32-character pipe name, with headroom.
    private const int MaxRootLength = 60;

    [ModuleInitializer]
    internal static void RedirectTemp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(KyberWeaveTestPaths.ToolRoot));
        string checkoutKey = Convert.ToHexStringLower(hash.AsSpan(0, 8));
        string root = Path.Combine(
            "/tmp",
            "kw-" + checkoutKey,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (root.Length > MaxRootLength)
        {
            return;
        }

        // A recycled PID may find the previous run's leftovers (kill -9 skips the exit cleanup).
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Nothing to clear, or a leftover we cannot remove: leave it. This runs in a module
            // initializer, where a throw would fail every test before any of them start.
        }

        Directory.CreateDirectory(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("TMPDIR", root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leftover under /tmp is small and cleared by the OS.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: never fail a finished test run over cleanup.
            }
        };
    }
}
