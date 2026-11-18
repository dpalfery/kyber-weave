using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// <para>
/// On macOS the default root lives in <c>kw-test-temp</c> beside the per-user temp directory
/// (<c>getconf DARWIN_USER_TEMP_DIR</c>), both inside the per-user folder under
/// <c>/private/var/folders</c>, the tree this machine's endpoint-security software skips. The
/// folder is a sibling of the temp directory rather than a child: canonicalisation reads every
/// ancestor, and the shared temp directory held ~1,700 entries against four in its parent. That
/// path is longer than a Unix domain socket can carry, so the platform's control pipe is moved
/// to <see cref="ShortPipeDirectory" />, whose path is short enough for the 103-byte socket
/// limit. Linux keeps <c>/tmp</c>; Windows has no <c>TMPDIR</c> and is left alone.
/// </para>
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
    /// <summary>The environment variable that moves the root; unset means the platform default.</summary>
    internal const string OverrideVariable = "KW_TEST_TEMP_ROOT";

    /// <summary>
    ///     The variable Microsoft.Testing.Platform reads to place its control pipe outside the test
    ///     temp root, which is what makes a root longer than <see cref="MaxRootLength" /> usable.
    /// </summary>
    internal const string PipeDirectoryVariable = "TESTINGPLATFORM_PIPE_DIRECTORY";

    /// <summary>
    ///     The short directory the control pipe moves to when the test root cannot carry it. The
    ///     pipe name plus this directory is far below the <see cref="UnixSocketPathLimit" />.
    /// </summary>
    internal const string ShortPipeDirectory = "/tmp";

    /// <summary>The longest path a Unix domain socket can name.</summary>
    internal const int UnixSocketPathLimit = 103;

    /// <summary>The length of <c>MONITORTOHOST_&lt;32 hex&gt;</c>, the platform's pipe name.</summary>
    internal const int PipeNameLength = 46;

    /// <summary>
    ///     The longest root a Unix domain socket path can carry the platform's pipe name under:
    ///     the <see cref="UnixSocketPathLimit" />, minus the separator and the
    ///     <see cref="PipeNameLength" />-character <c>MONITORTOHOST_&lt;32 hex&gt;</c> name.
    /// </summary>
    internal const int MaxRootLength = UnixSocketPathLimit - 1 - PipeNameLength;

    private const int DefaultKeyLength = 16;
    private const int ShortKeyLength = 8;
    private const int DarwinUserTempDirectoryName = 65537;

    /// <summary>
    ///     The folder the macOS root lives in: a sibling of the per-user temp directory, created
    ///     by the first run, so the shared temp directory is not an ancestor to list.
    /// </summary>
    private const string MacBaseFolderName = "kw-test-temp";

    internal static void RedirectTemp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string? overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        bool defaultMacRoot = OperatingSystem.IsMacOS() && string.IsNullOrWhiteSpace(overridePath);

        // The macOS default base is inside the tree the endpoint scanner skips, but it is longer
        // than the socket budget. Move the pipe now, before the platform reads its path.
        if (defaultMacRoot &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PipeDirectoryVariable)))
        {
            Environment.SetEnvironmentVariable(PipeDirectoryVariable, ShortPipeDirectory);
        }

        bool pipeConfigured =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PipeDirectoryVariable));
        string defaultBasePath = DefaultBasePath(
            OperatingSystem.IsMacOS(),
            OperatingSystem.IsMacOS() ? MacUserTempDirectory() : "/tmp");

        string root = ResolveRoot(
            overridePath,
            CheckoutKey(),
            Environment.ProcessId,
            socketBudgetApplies: !pipeConfigured,
            defaultBasePath);

        // A run that was killed (kill -9 or a timeout) leaves its per-process folder behind.
        string? parent = Path.GetDirectoryName(root);
        if (parent is not null)
        {
            RemoveDeadSiblings(parent, Environment.ProcessId, IsProcessAlive, minAge: TimeSpan.FromMinutes(5));
        }

        // A recycled PID may find the previous run's leftovers (kill -9 skips the exit cleanup).
        TryDeleteDirectory(root);

        Directory.CreateDirectory(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("TMPDIR", root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            TryDeleteDirectory(root);
            if (parent is not null)
            {
                TryDeleteEmptyDirectory(parent);
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
    ///     The base directory the root sits under when no override is set: a dedicated folder
    ///     beside the per-user temp directory on macOS, and <c>/tmp</c> everywhere else.
    /// </summary>
    /// <param name="isMacOS">Whether the current platform is macOS.</param>
    /// <param name="darwinUserTempDirectory">
    ///     What <c>getconf DARWIN_USER_TEMP_DIR</c> returned; ignored off macOS.
    /// </param>
    /// <returns>An absolute base path without a trailing separator.</returns>
    internal static string DefaultBasePath(bool isMacOS, string darwinUserTempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(darwinUserTempDirectory);

        string tempDirectory = Path.TrimEndingDirectorySeparator(darwinUserTempDirectory);
        if (!isMacOS)
        {
            return "/tmp";
        }

        // A parent that is missing or the filesystem root leaves nowhere separate to put the
        // folder, so the temp directory itself stays the base.
        string? perUserFolder = Path.GetDirectoryName(tempDirectory);
        return string.IsNullOrEmpty(perUserFolder) ||
               string.Equals(perUserFolder, Path.GetPathRoot(tempDirectory), StringComparison.Ordinal)
            ? tempDirectory
            : Path.Combine(perUserFolder, MacBaseFolderName);
    }

    /// <summary>
    /// The temp root this process should use: <c>&lt;base&gt;/kw-&lt;key&gt;/&lt;pid&gt;</c>,
    /// where the base is the platform default, <c>KW_TEST_TEMP_ROOT</c> when it is set.
    /// </summary>
    /// <param name="overridePath">The configured override, or null when it is unset.</param>
    /// <param name="checkoutKey">The checkout's hex key, as returned by <see cref="CheckoutKey" />.</param>
    /// <param name="processId">The process the root belongs to.</param>
    /// <param name="socketBudgetApplies">
    ///     Whether the platform's control pipe still lives under this root. When it does not, the
    ///     socket path limit no longer constrains the root's length.
    /// </param>
    /// <param name="defaultBasePath">
    ///     The base to use when no override is set. Null or blank means <c>/tmp</c>.
    /// </param>
    /// <returns>An absolute path inside the length budget.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The override is not an absolute path, or no spelling of it fits the socket budget.
    /// </exception>
    internal static string ResolveRoot(
        string? overridePath,
        string checkoutKey,
        int processId,
        bool socketBudgetApplies = true,
        string? defaultBasePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutKey);
        if (checkoutKey.Length < ShortKeyLength)
        {
            throw new InvalidOperationException(
                $"Checkout key '{checkoutKey}' is shorter than the minimum {ShortKeyLength} characters.");
        }

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
        else if (!string.IsNullOrWhiteSpace(defaultBasePath))
        {
            basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultBasePath));
        }

        foreach (int keyLength in new[] { DefaultKeyLength, ShortKeyLength })
        {
            if (checkoutKey.Length < keyLength)
            {
                continue;
            }

            string root = Path.Combine(basePath, "kw-" + checkoutKey[..keyLength], pid);
            if (!socketBudgetApplies || root.Length <= MaxRootLength)
            {
                return root;
            }
        }

        throw new InvalidOperationException(
            $"{OverrideVariable}='{overridePath}' leaves no temp root under {MaxRootLength} characters: " +
            $"a Unix socket path must carry the platform's pipe name under {UnixSocketPathLimit} bytes. Set " +
            $"{PipeDirectoryVariable} to move the pipe out of the temp root, or choose a shorter path " +
            $"(the base must be at most {MaxRootLength - "/kw-".Length - ShortKeyLength - 1 - pid.Length} characters).");
    }

    /// <summary>
    ///     Deletes the per-process folders under <paramref name="parentDirectory" /> whose process
    ///     is gone, so a killed run cannot make the parent grow from run to run.
    /// </summary>
    /// <param name="parentDirectory">The checkout's <c>kw-&lt;key&gt;</c> directory.</param>
    /// <param name="ownProcessId">This process's folder, which is left alone.</param>
    /// <param name="isProcessAlive">Answers whether a PID still names a running process.</param>
    /// <param name="minAge">
    ///     Optional minimum age a sibling folder must have before deletion, preventing a recycled-PID
    ///     race from removing a freshly created concurrent run's folder.
    /// </param>
    internal static void RemoveDeadSiblings(
        string parentDirectory,
        int ownProcessId,
        Func<int, bool> isProcessAlive,
        TimeSpan? minAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentNullException.ThrowIfNull(isProcessAlive);

        if (!Directory.Exists(parentDirectory))
        {
            return;
        }

        foreach (string sibling in Directory.EnumerateDirectories(parentDirectory))
        {
            if (!int.TryParse(
                    Path.GetFileName(sibling),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int processId))
            {
                continue;
            }

            if (processId == ownProcessId || isProcessAlive(processId))
            {
                continue;
            }

            if (minAge is { } threshold)
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(sibling) < threshold)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            // Re-check liveness right before deletion so a PID recycled during the age check is not removed.
            if (isProcessAlive(processId))
            {
                continue;
            }

            TryDeleteDirectory(sibling);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string MacUserTempDirectory()
    {
        nuint length = Confstr(DarwinUserTempDirectoryName, null, 0);
        if (length > 0 && length <= 4096)
        {
            byte[] buffer = new byte[(int)length];
            if (Confstr(DarwinUserTempDirectoryName, buffer, length) == length)
            {
                string path = Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
                if (Path.IsPathFullyQualified(path))
                {
                    return Path.TrimEndingDirectorySeparator(path);
                }
            }
        }

        string? tmpdir = Environment.GetEnvironmentVariable("TMPDIR");
        return !string.IsNullOrWhiteSpace(tmpdir) && Path.IsPathFullyQualified(tmpdir)
            ? Path.TrimEndingDirectorySeparator(tmpdir)
            : "/tmp";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Nothing to clear, or a leftover we cannot remove: leave it. This runs in a module
            // initializer, where a throw would fail every test before any of them start.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // A sibling run still has its folder here, or the directory is already gone.
        }
    }

    // The source-generated equivalent needs AllowUnsafeBlocks for one call; plain interop does not.
#pragma warning disable SYSLIB1054
    [DllImport("libSystem.dylib", EntryPoint = "confstr")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nuint Confstr(int name, byte[]? buffer, nuint length);
#pragma warning restore SYSLIB1054
}
