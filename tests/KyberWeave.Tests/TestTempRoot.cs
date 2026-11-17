using System.Globalization;
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
/// <para>
/// <c>KW_TEST_TEMP_ROOT</c> moves the root under an absolute path the caller names, which is how
/// a measurement puts the suite on a filesystem an endpoint-security scanner does not read or on
/// a RAM disk. The one-checkout-component shape (<c>kw-&lt;key&gt;/&lt;pid&gt;</c>) is kept so
/// concurrent runs still cannot see each other, and the same length budget applies: an override
/// that cannot fit the socket path fails loudly rather than silently running somewhere else.
/// </para>
/// </remarks>
internal static class TestTempRoot
{
    /// <summary>The environment variable that moves the root; unset means <c>/tmp</c>.</summary>
    internal const string OverrideVariable = "KW_TEST_TEMP_ROOT";

    /// <summary>
    ///     The longest root a Unix domain socket path can carry the platform's pipe name under:
    ///     103 bytes, minus the separator and the 46-character <c>MONITORTOHOST_&lt;32 hex&gt;</c>
    ///     name Microsoft.Testing.Platform creates.
    /// </summary>
    internal const int MaxRootLength = 56;

    /// <summary>
    ///     The variable Microsoft.Testing.Platform reads to place its control pipe outside the test
    ///     temp root, which is what makes a root longer than <see cref="MaxRootLength" /> usable.
    /// </summary>
    internal const string PipeDirectoryVariable = "TESTINGPLATFORM_PIPE_DIRECTORY";

    private const int DefaultKeyLength = 16;
    private const int ShortKeyLength = 8;

    [ModuleInitializer]
    internal static void RedirectTemp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string root = ResolveRoot(
            Environment.GetEnvironmentVariable(OverrideVariable),
            CheckoutKey(),
            Environment.ProcessId,
            socketBudgetApplies: string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable(PipeDirectoryVariable)));

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

    /// <summary>The SHA-256 prefix that names one checkout's root.</summary>
    /// <returns>The lower-case hex key.</returns>
    internal static string CheckoutKey()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(KyberWeaveTestPaths.ToolRoot));
        return Convert.ToHexStringLower(hash.AsSpan(0, DefaultKeyLength / 2));
    }

    /// <summary>
    /// The temp root this process should use: <c>/tmp/kw-&lt;key&gt;/&lt;pid&gt;</c> by default,
    /// or the same shape under <c>KW_TEST_TEMP_ROOT</c> when it is set.
    /// </summary>
    /// <param name="overridePath">The configured override, or null when it is unset.</param>
    /// <param name="checkoutKey">The checkout's hex key, as returned by <see cref="CheckoutKey" />.</param>
    /// <param name="processId">The process the root belongs to.</param>
    /// <param name="socketBudgetApplies">
    ///     Whether the platform's control pipe still lives under this root. When it does not, the
    ///     socket path limit no longer constrains the root's length.
    /// </param>
    /// <returns>An absolute path inside the length budget.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The override is not an absolute path, or no spelling of it fits the socket budget.
    /// </exception>
    internal static string ResolveRoot(
        string? overridePath,
        string checkoutKey,
        int processId,
        bool socketBudgetApplies = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutKey);
        string pid = processId.ToString(CultureInfo.InvariantCulture);

        string basePath = "/tmp";
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!Path.IsPathFullyQualified(overridePath))
            {
                throw new InvalidOperationException(
                    $"{OverrideVariable} must be an absolute path; '{overridePath}' is not.");
            }

            basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(overridePath));
        }

        foreach (int keyLength in new[] { DefaultKeyLength, ShortKeyLength })
        {
            string root = Path.Combine(basePath, "kw-" + checkoutKey[..keyLength], pid);
            if (!socketBudgetApplies || root.Length <= MaxRootLength)
            {
                return root;
            }
        }

        throw new InvalidOperationException(
            $"{OverrideVariable}='{overridePath}' leaves no temp root under {MaxRootLength} characters: " +
            $"a Unix socket path must carry the platform's pipe name under 103 bytes. Set " +
            $"{PipeDirectoryVariable} to move the pipe out of the temp root, or choose a shorter path " +
            $"(the base must be at most {MaxRootLength - "/kw-".Length - ShortKeyLength - 1 - pid.Length} characters).");
    }
}
